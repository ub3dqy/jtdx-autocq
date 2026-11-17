using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace JtdxAutoCq
{
    public sealed class MonitorTargets
    {
        readonly HashSet<string> countries;
        readonly HashSet<string> calls;
        readonly Dictionary<string, Regex> masks;
        readonly HashSet<string> worked = new HashSet<string>();
        readonly Func<string, string> dxcc;
        readonly bool onePerBand;

        public MonitorTargets(AutoCqOptions options, Func<string, string> dxccForCall, IEnumerable<string> log)
        {
            countries = new HashSet<string>(options.MonitorDxcc, StringComparer.OrdinalIgnoreCase);
            calls = new HashSet<string>(options.MonitorCalls);
            masks = options.MonitorMasks.ToDictionary(mask => mask, mask =>
                new Regex("\\A" + Regex.Escape(mask).Replace(@"\*", "[A-Z0-9/]*").Replace(@"\?", "[A-Z0-9/]") + "\\z",
                    RegexOptions.CultureInvariant));
            dxcc = dxccForCall;
            onePerBand = options.MonitorOnePerBand;
            if (onePerBand && log != null)
                foreach (string call in log) Complete(call);
        }

        public static string[] NormalizeCalls(IEnumerable<string> values)
        { return (values ?? new string[0]).Select(Callsign.Require).Distinct().ToArray(); }

        public static string[] NormalizeMasks(IEnumerable<string> values)
        {
            return (values ?? new string[0]).Select(value =>
            {
                string mask = Callsign.Clean(value);
                if (!Regex.IsMatch(mask, @"\A[A-Z0-9/*?]{1,30}\z", RegexOptions.CultureInvariant)
                    || !mask.Any(c => c == '*' || c == '?'))
                    throw new InvalidOperationException("Укажите маску позывного со знаками * или ?, например JA* или K?ABC.");
                // Consecutive stars have the same meaning as one star.
                return Regex.Replace(mask, @"\*+", "*");
            }).Distinct().ToArray();
        }

        IEnumerable<string> Matching(string call)
        {
            call = Callsign.Clean(call);
            if (calls.Contains(call)) yield return "call/" + call;
            if (countries.Count > 0)
            {
                string country = dxcc(call);
                if (country != null && countries.Contains(country)) yield return "dxcc/" + country.ToUpperInvariant();
            }
            foreach (KeyValuePair<string, Regex> mask in masks)
                if (mask.Value.IsMatch(call)) yield return "mask/" + mask.Key;
        }

        public bool CanTry(string call)
        { return Matching(call).Any(key => !onePerBand || !worked.Contains(key)); }

        public void Complete(string call)
        {
            if (onePerBand)
                foreach (string key in Matching(call)) worked.Add(key);
        }
    }

    public static class JtdxAdif
    {
        // ADIF amateur band boundaries in MHz.
        static readonly string[] Bands = { "2190m","630m","560m","160m","80m","60m","40m","30m","20m","17m","15m","12m",
            "10m","8m","6m","5m","4m","2m","1.25m","70cm","33cm","23cm","13cm","9cm","6cm","3cm","1.25cm","6mm",
            "4mm","2.5mm","2mm","1mm","submm" };
        static readonly double[,] Ranges = {
            {0.1357,0.1378},{0.472,0.479},{0.501,0.504},{1.8,2},{3.5,4},{5.06,5.45},{7,7.3},{10.1,10.15},{14,14.35},
            {18.068,18.168},{21,21.45},{24.89,24.99},{28,29.7},{40,45},{50,54},{54.000001,69.9},{70,71},{144,148},
            {222,225},{420,450},{902,928},{1240,1300},{2300,2450},{3300,3500},{5650,5925},{10000,10500},
            {24000,24250},{47000,47200},{75500,81000},{119980,120020},{142000,149000},{241000,250000},{300000,7500000}
        };
        static readonly Regex Tag = new Regex(@"<([A-Z_][A-Z_0-9]*)(?::([0-9]+)(?::[A-Z])?)?>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static string Band(double frequencyHz)
        {
            double mhz = frequencyHz / 1000000;
            for (int i = 0; i < Bands.Length; ++i)
                if (mhz >= Ranges[i, 0] && mhz <= Ranges[i, 1]) return Bands[i];
            return null;
        }

        public static HashSet<string> WorkedCalls(string file, string band, string ownCall, string mode = null)
        {
            if (!File.Exists(file)) return new HashSet<string>();
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
                return WorkedCallsFromText(reader.ReadToEnd(), band, ownCall, mode);
        }

        internal static HashSet<string> WorkedCallsFromText(string text, string band, string ownCall, string mode = null)
        {
            var worked = new HashSet<string>();
            if (String.IsNullOrEmpty(band)) return worked;
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (Match tag = Tag.Match(text); tag.Success;)
            {
                string name = tag.Groups[1].Value.ToUpperInvariant();
                int next = tag.Index + tag.Length;
                if (name == "EOH" || name == "EOR")
                {
                    string call = Callsign.Parse(Read(fields, "CALL"));
                    string station = Callsign.Clean(Read(fields, "STATION_CALLSIGN"));
                    string recordBand = Read(fields, "BAND");
                    string recordMode = Read(fields, "SUBMODE");
                    if (recordMode.Length == 0) recordMode = Read(fields, "MODE");
                    double mhz;
                    if (recordBand.Length == 0 && Double.TryParse(Read(fields, "FREQ"), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out mhz)) recordBand = Band(mhz * 1000000);
                    if (name == "EOR" && call != null && String.Equals(band, recordBand, StringComparison.OrdinalIgnoreCase)
                        && (mode == null || String.Equals(mode, recordMode, StringComparison.OrdinalIgnoreCase))
                        && (station.Length == 0 || station == ownCall)
                        && !String.Equals(Read(fields, "SWL"), "Y", StringComparison.OrdinalIgnoreCase))
                        worked.Add(call);
                    fields.Clear();
                }
                else if (tag.Groups[2].Success)
                {
                    int count;
                    if (!Int32.TryParse(tag.Groups[2].Value, out count) || count > text.Length - next) break;
                    if (name == "CALL" || name == "BAND" || name == "FREQ" || name == "STATION_CALLSIGN" || name == "SWL"
                        || name == "MODE" || name == "SUBMODE")
                        fields[name] = text.Substring(next, count).Trim();
                    next += count;
                }
                tag = Tag.Match(text, next);
            }
            return worked;
        }

        static string Read(Dictionary<string, string> fields, string key)
        {
            string value;
            return fields.TryGetValue(key, out value) ? value : "";
        }
    }
}
