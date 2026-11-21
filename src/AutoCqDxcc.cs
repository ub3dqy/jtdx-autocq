using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace JtdxAutoCq
{
    public sealed class DxccCountry
    {
        public readonly string Prefix, Name;

        public DxccCountry(string prefix, string name)
        { Prefix = prefix; Name = name; }
    }

    public sealed class DxccCountries
    {
        readonly Dictionary<string, DxccCountry> exact = new Dictionary<string, DxccCountry>();
        readonly Dictionary<string, DxccCountry> prefixes = new Dictionary<string, DxccCountry>();
        readonly List<DxccCountry> countries = new List<DxccCountry>();
        static readonly Regex Alias = new Regex(@"^=?[A-Z0-9/]+", RegexOptions.CultureInvariant);
        static readonly Regex CallArea = new Regex(@"^([2-9]?[A-Z]{1,2})\d{1,4}", RegexOptions.CultureInvariant);

        public IEnumerable<DxccCountry> Countries { get { return countries; } }

        public static DxccCountries Load(string path)
        { return Parse(File.ReadLines(path)); }

        internal static DxccCountries Parse(IEnumerable<string> lines)
        {
            var result = new DxccCountries();
            DxccCountry country = null;
            foreach (string line in lines)
            {
                if (String.IsNullOrWhiteSpace(line)) continue;
                if (!Char.IsWhiteSpace(line[0]))
                {
                    string[] fields = line.Split(':');
                    country = fields.Length >= 9 ? new DxccCountry(fields[7].Trim().ToUpperInvariant(), fields[0].Trim()) : null;
                    if (country != null) result.countries.Add(country);
                    continue;
                }
                if (country == null) continue;
                foreach (string alias in line.Split(','))
                {
                    string key = Alias.Match(alias.Trim()).Value;
                    if (key.Length == 0) continue;
                    if (key[0] == '=') result.exact[key.Substring(1)] = country;
                    else result.prefixes[key] = country;
                }
            }
            if (result.countries.Count == 0 || result.prefixes.Count == 0)
                throw new InvalidDataException("В справочнике cty.dat нет стран DXCC.");
            return result;
        }

        public DxccCountry Find(string callsign)
        {
            string call = Callsign.Clean(callsign);
            DxccCountry country;
            if (exact.TryGetValue(call, out country)) return country;

            string[] parts = call.Split('/');
            if (parts.Skip(1).Any(part => part == "MM" || part == "AM")) return null;
            string operatingPrefix = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part == "P" || part == "M" || part == "A" || part == "QRP" || part == "LH") continue;
                if (part.Length > 0 && part.All(Char.IsDigit))
                {
                    Match area = CallArea.Match(operatingPrefix);
                    operatingPrefix = (area.Success ? area.Groups[1].Value : operatingPrefix) + part;
                }
                else if (part.Length < operatingPrefix.Length && prefixes.ContainsKey(part))
                    operatingPrefix = part;
            }
            for (int length = operatingPrefix.Length; length > 0; length--)
            {
                string key = operatingPrefix.Substring(0, length);
                if (key == "KG4" && operatingPrefix.Length != 5) key = "AA";
                if (prefixes.TryGetValue(key, out country)) return country;
            }
            return null;
        }
    }
}
