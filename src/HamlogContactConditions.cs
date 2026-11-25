using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace JtdxAutoCq
{
    internal static class HamlogCalendar
    {
        internal static int MonthDay(string value)
        {
            DateTime parsed;
            if (!DateTime.TryParseExact("2000-" + value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out parsed))
                throw new InvalidDataException("Для ежегодной даты укажите месяц и день (ММ-ДД).");
            return parsed.Month * 100 + parsed.Day;
        }

    }

    public sealed class HamlogContactRequirement
    {
        internal const string Counties = "counties", CountyContacts = "county-contacts", Stations = "stations";
        public string Kind, Roster;
        public int Required;
        public bool ReportedByHamlog, DistinctStations, PortableStations;
        public string[] Calls = new string[0];
        public string[] CountyCodes = new string[0];

        internal bool ByCounty { get { return Kind == Counties || Kind == CountyContacts; } }
        internal bool UsesRoster { get { return !String.IsNullOrEmpty(Roster); } }

        internal void Validate(HamlogContactCriteria criteria)
        {
            Calls = (Calls ?? new string[0]).Select(Callsign.Parse).Distinct(StringComparer.Ordinal).ToArray();
            CountyCodes = (CountyCodes ?? new string[0]).Distinct(StringComparer.Ordinal).ToArray();
            HashSet<string> roster = UsesRoster ? HamlogAwardRosters.For(Roster) : null;
            if (Required < 1 || Required > 1000
                || UsesRoster && (Kind != Stations || !ReportedByHamlog || !DistinctStations || PortableStations || roster == null)
                || (ByCounty ? Calls.Length != 0 || DistinctStations || PortableStations || CountyCodes.Length > 99
                    || CountyCodes.Length == 0 && (!criteria.RestrictsCounties || Kind == CountyContacts)
                    || CountyCodes.Any(code => !HamlogRdaCode.IsCounty(code)
                        || criteria.RestrictsCounties && !criteria.InCountyScope(code, allowRetired: true))
                    || Kind == Counties && Required > (CountyCodes.Length != 0 ? CountyCodes.Length
                        : criteria.UsableCountyCount)
                    : Kind != Stations || CountyCodes.Length != 0 || !UsesRoster && Calls.Length == 0
                        || Calls.Length > 240 || Calls.Any(call => call == null)
                        || DistinctStations && Required > (roster == null ? Calls.Length : roster.Union(Calls).Count())))
                throw new InvalidDataException("Проверьте обязательные районы и позывные диплома.");
        }

        internal bool SameAs(HamlogContactRequirement other)
        {
            return other != null && Kind == other.Kind && Required == other.Required
                && ReportedByHamlog == other.ReportedByHamlog && DistinctStations == other.DistinctStations
                && PortableStations == other.PortableStations && (Roster ?? "") == (other.Roster ?? "") && Calls.SequenceEqual(other.Calls)
                && CountyCodes.SequenceEqual(other.CountyCodes);
        }

        internal string Station(string call)
        {
            if (Calls.Contains(call) || UsesRoster && HamlogAwardRosters.For(Roster).Contains(call)) return call;
            if (!PortableStations || String.IsNullOrEmpty(call)) return null;
            string original = Callsign.BaseCall(call);
            return Calls.Contains(original) ? original : null;
        }

    }

    public sealed class HamlogPointAdjustment
    {
        public int Factor = 1, Add;
        public string FromUtc, UntilUtc, AnnualFrom, AnnualUntil;
        public string[] Bands = new string[0];
        public string[] PortableSuffixes = new string[0];

        internal bool Annual { get { return !String.IsNullOrEmpty(AnnualFrom) || !String.IsNullOrEmpty(AnnualUntil); } }

        internal void Validate()
        {
            Bands = (Bands ?? new string[0]).Distinct(StringComparer.Ordinal).ToArray();
            PortableSuffixes = (PortableSuffixes ?? new string[0]).Distinct(StringComparer.Ordinal).ToArray();
            DateTime? from = AwardCatalog.ReadTime(FromUtc), until = AwardCatalog.ReadTime(UntilUtc, true);
            if (Factor < 1 || Factor > 5 || Add < 0 || Add > 1000 || Factor == 1 && Add == 0
                || from.HasValue != until.HasValue || from.HasValue && from >= until
                || Annual && from.HasValue || !from.HasValue && !Annual && Bands.Length == 0 && PortableSuffixes.Length == 0
                || Bands.Any(band => !JtdxAdif.BandNames.Contains(band)) || PortableSuffixes.Any(suffix => suffix != "/P" && suffix != "/M"))
                throw new InvalidDataException("Проверьте условия повышения очков.");
            if (Annual)
            {
                HamlogCalendar.MonthDay(AnnualFrom);
                HamlogCalendar.MonthDay(AnnualUntil);
            }
        }

        internal bool SameAs(HamlogPointAdjustment other)
        {
            return other != null && Factor == other.Factor && Add == other.Add
                && (FromUtc ?? "") == (other.FromUtc ?? "") && (UntilUtc ?? "") == (other.UntilUtc ?? "")
                && (AnnualFrom ?? "") == (other.AnnualFrom ?? "") && (AnnualUntil ?? "") == (other.AnnualUntil ?? "")
                && Bands.SequenceEqual(other.Bands) && PortableSuffixes.SequenceEqual(other.PortableSuffixes);
        }
    }
}
