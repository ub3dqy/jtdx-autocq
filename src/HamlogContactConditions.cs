using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JtdxAutoCq
{
    public sealed class HamlogContactRequirement
    {
        internal const string Counties = "counties", CountyContacts = "county-contacts", Stations = "stations";
        public string Kind;
        public int Required;
        public bool ReportedByHamlog, DistinctStations, PortableStations;
        public string[] Calls = new string[0];
        public string[] CountyCodes = new string[0];

        internal bool ByCounty { get { return Kind == Counties || Kind == CountyContacts; } }
        internal string Label { get { return Kind == Counties ? "районы RDA" : Kind == CountyContacts
            ? "связи с обязательным районом" : DistinctStations ? "разные обязательные станции" : "связи с обязательными станциями"; } }

        internal void Validate(HamlogContactCriteria criteria)
        {
            Calls = (Calls ?? new string[0]).Select(Callsign.Parse).Distinct(StringComparer.Ordinal).ToArray();
            CountyCodes = (CountyCodes ?? new string[0]).Distinct(StringComparer.Ordinal).ToArray();
            if (Required < 1 || Required > 1000
                || (ByCounty ? Calls.Length != 0 || DistinctStations || PortableStations || CountyCodes.Length > 99
                    || CountyCodes.Length == 0 && (!criteria.RestrictsCounties || Kind == CountyContacts)
                    || CountyCodes.Any(code => !HamlogCountyIndex.IsCounty(code)
                        || criteria.RestrictsCounties && !criteria.InCountyScope(code, allowRetired: true))
                    || Kind == Counties && Required > (CountyCodes.Length != 0 ? CountyCodes.Length
                        : criteria.UsableCountyCount)
                    : Kind != Stations || CountyCodes.Length != 0 || Calls.Length == 0 || Calls.Length > 240 || Calls.Any(call => call == null)
                        || DistinctStations && Required > Calls.Length))
                throw new InvalidDataException("Проверьте обязательные районы и позывные диплома.");
        }

        internal bool SameAs(HamlogContactRequirement other)
        {
            return other != null && Kind == other.Kind && Required == other.Required
                && ReportedByHamlog == other.ReportedByHamlog && DistinctStations == other.DistinctStations
                && PortableStations == other.PortableStations && Calls.SequenceEqual(other.Calls)
                && CountyCodes.SequenceEqual(other.CountyCodes);
        }

        internal int Count(HamlogCredit[] credits)
        {
            return Kind == Counties ? credits.Where(Matches).Select(row => row.County).Distinct().Count() : credits.Count(Matches);
        }

        internal string Station(string call)
        {
            if (Calls.Contains(call)) return call;
            if (!PortableStations || String.IsNullOrEmpty(call)) return null;
            string original = Callsign.BaseCall(call);
            return Calls.Contains(original) ? original : null;
        }

        internal bool Matches(HamlogCredit row) { return !row.Observation && Matches(row.Call, row.County); }

        internal bool Matches(string call, string county)
        { return ByCounty ? HamlogCountyIndex.IsCounty(county) && (CountyCodes.Length == 0 || CountyCodes.Contains(county)) : Station(call) != null; }
    }

    internal sealed class HamlogRequirementProgress
    {
        readonly HamlogContactRequirement rule;
        readonly HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        int projected;
        internal readonly int Confirmed;
        internal int Required { get { return rule.Required; } }
        internal string Label { get { return rule.Label; } }
        internal bool Enough { get { return projected >= Required; } }

        internal HamlogRequirementProgress(HamlogContactRequirement rule, IEnumerable<HamlogCredit> confirmed)
        {
            this.rule = rule;
            foreach (HamlogCredit credit in confirmed) Record(credit);
            Confirmed = projected;
        }

        internal bool Needs(string call, string county)
        {
            return !Enough && rule.Matches(call, county)
                && !seen.Contains(Key(call, county));
        }

        internal void Record(HamlogCredit credit)
        {
            if (!rule.Matches(credit)) return;
            string key = Key(credit.Call, credit.County);
            if (key == null || seen.Add(key)) projected++;
        }

        string Key(string call, string county)
        { return rule.Kind == HamlogContactRequirement.Counties ? county : rule.DistinctStations ? rule.Station(call) : null; }
    }

    public sealed class HamlogPointAdjustment
    {
        public int Factor = 1, Add;
        public string FromUtc, UntilUtc;
        public string[] Bands = new string[0];
        Tuple<DateTime?, DateTime?> window;

        Tuple<DateTime?, DateTime?> Window
        {
            get
            {
                return window ?? (window = Tuple.Create(AwardCatalog.ReadTime(FromUtc), AwardCatalog.ReadTime(UntilUtc, true)));
            }
        }

        internal void Validate()
        {
            Bands = (Bands ?? new string[0]).Distinct(StringComparer.Ordinal).ToArray();
            window = null;
            DateTime? from = Window.Item1, until = Window.Item2;
            if (Factor < 1 || Factor > 5 || Add < 0 || Add > 1000 || Factor == 1 && Add == 0
                || from.HasValue != until.HasValue || from.HasValue && from >= until
                || !from.HasValue && Bands.Length == 0 || Bands.Any(band => !JtdxAdif.BandNames.Contains(band)))
                throw new InvalidDataException("Проверьте условия повышения очков.");
        }

        internal bool SameAs(HamlogPointAdjustment other)
        {
            return other != null && Factor == other.Factor && Add == other.Add
                && (FromUtc ?? "") == (other.FromUtc ?? "") && (UntilUtc ?? "") == (other.UntilUtc ?? "")
                && Bands.SequenceEqual(other.Bands);
        }

        internal decimal Apply(decimal points, string band, DateTime at)
        {
            var range = Window;
            return points > 0 && (Bands.Length == 0 || Bands.Contains(band))
                && (!range.Item1.HasValue || at >= range.Item1.Value && at < range.Item2.Value) ? points * Factor + Add : points;
        }

        internal DateTime[] Boundaries(DateTime withinDay)
        {
            var range = Window;
            return new[] { range.Item1, range.Item2 }.Where(at => at.HasValue && at.Value > withinDay && at.Value < withinDay.AddDays(1))
                .Select(at => at.Value).ToArray();
        }

    }
}
