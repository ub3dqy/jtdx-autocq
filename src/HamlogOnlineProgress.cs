using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace JtdxAutoCq
{
    internal static class AwardLocator
    {
        static readonly Regex Pattern = new Regex(@"\A[A-R]{2}[0-9]{2}(?:[A-X]{2})?\z", RegexOptions.CultureInvariant);

        internal static string Parse(string value)
        {
            string grid = (value ?? "").Trim().ToUpperInvariant();
            return grid != "RR73" && Pattern.IsMatch(grid) ? grid.Substring(0, 4) : null;
        }
    }

    internal sealed class HamlogOnlineProgress
    {
        static readonly Regex CountyCode = new Regex(@"\A[A-Z]{2}-[0-9]{2}\z", RegexOptions.CultureInvariant);
        static readonly HashSet<string> RussianDxcc = new HashSet<string> { "UA", "UA2", "UA9", "R1FJ" };
        static readonly HashSet<string> TulaSeries = new HashSet<string> { "2P", "3P", "5P" };
        static readonly HashSet<string> TatarstanSeries = new HashSet<string> { "4P", "4Q", "4R" };
        static readonly HashSet<string> MoscowSeries = new HashSet<string> { "2A", "2B", "2C", "3A", "3B", "3C", "5A", "5B", "5C" };
        static readonly HashSet<string> RostovSeries = new HashSet<string> { "6L", "6M", "6N", "7L", "7M", "7N" };
        static readonly HashSet<string> Specials = new HashSet<string>(
            "UE7DX UE36DX UE36L R2026DX R036DX UE4DX UE6DX UE3DX UE0DX R036Z UE36O".Split(' '));

        readonly HamlogAward award;
        readonly string own;
        readonly Func<string, string> dxcc;
        readonly HashSet<string> worked = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> counties = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> confirmedCounties = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string, HashSet<string>> reportedCounties = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        readonly DateTime? from, until;
        int pending;

        internal readonly HamlogStanding Standing;
        internal bool NeedsCounties { get { return award.Rule == HamlogRule.Tatarstan && counties.Count < 25; } }
        internal int CountyCount { get { return confirmedCounties.Count; } }
        internal decimal Confirmed
        { get { return Standing == null ? 0 : award.Rule == HamlogRule.Bryansk ? confirmedCounties.Count : Standing.Points; } }
        internal int Required { get { return Standing == null ? award.Required : Standing.Required; } }
        internal bool Complete { get { return Standing != null && Confirmed >= Required && (award.Rule != HamlogRule.Tatarstan || CountyCount >= 25); } }
        internal string Unit
        { get { return award.Rule == HamlogRule.Grid ? "квадратов" : award.Rule == HamlogRule.Bryansk ? "районов DIGI" : award.Rule == HamlogRule.Bands ? "диапазонов" : "очк."; } }

        internal HamlogOnlineProgress(AwardProgram program, LogbookHistory history, string own, Func<string, string> dxcc)
        {
            award = program.Template;
            from = program.From;
            until = program.Until;
            this.own = Callsign.Clean(own);
            this.dxcc = dxcc;
            Standing = program.Hamlog;
            if (Standing == null || Standing.Callsign != this.own || Standing.CheckedUtc == DateTime.MinValue
                || Standing.Required <= 0 || Standing.Credits == null || Standing.ConfirmedSquares == null
                || award.Rule == HamlogRule.Grid && Standing.ConfirmedSquares.Length != Standing.Points)
            {
                Standing = null;
                return;
            }

            foreach (HamlogCredit credit in Standing.Credits)
            {
                string county = AwardCounty(credit.County);
                RememberCounty(credit.Call, credit.Locator, county);
                string mode = HamlogPointProgress.ModeGroup(credit.Mode);
                if (award.Rule == HamlogRule.Bryansk && mode != "DIGI") continue;
                if (county != null) confirmedCounties.Add(county);
                string key = Key(credit.Call, credit.Band, mode, credit.Locator, county);
                if (key != null) worked.Add(key);
            }
            counties.UnionWith(confirmedCounties);
            worked.UnionWith(Standing.ConfirmedSquares);

            // For band/mode repeats, remember already logged exchanges without adding them to HAMLOG's totals.
            foreach (LogbookQso row in history.Records)
            {
                if (!row.BelongsTo(this.own)) continue;
                RememberCounty(row.Call, row.Locator, AwardCounty(row.County));
            }
            if (award.Rule == HamlogRule.Tula || award.Rule == HamlogRule.Tatarstan
                || award.Rule == HamlogRule.Marathon || award.Rule == HamlogRule.Vysotsky || award.Rule == HamlogRule.Arck35)
                foreach (LogbookQso row in history.Records)
                    if (row.BelongsTo(this.own)) Reserve(row, false);
        }

        string AwardCounty(string value)
        {
            if (!CountyCode.IsMatch(value ?? "")) return null;
            if (award.Rule == HamlogRule.Tatarstan && (!value.StartsWith("TA-", StringComparison.Ordinal)
                || String.CompareOrdinal(value, "TA-01") < 0 || String.CompareOrdinal(value, "TA-63") > 0)) return null;
            return award.Rule == HamlogRule.Bryansk && !value.StartsWith("BR-", StringComparison.Ordinal) ? null : value;
        }

        void RememberCounty(string call, string grid, string county)
        {
            grid = AwardLocator.Parse(grid);
            if (grid == null || county == null || String.IsNullOrEmpty(call)) return;
            string key = call + "|" + grid;
            HashSet<string> values;
            if (!reportedCounties.TryGetValue(key, out values)) reportedCounties.Add(key, values = new HashSet<string>());
            values.Add(county);
        }

        string CountyAt(string call, string grid)
        {
            HashSet<string> values;
            return grid != null && reportedCounties.TryGetValue(call + "|" + grid, out values) && values.Count == 1 ? values.First() : null;
        }

        bool InRegion(string call, string county, string prefix, HashSet<string> series)
        {
            if (county != null) return county.StartsWith(prefix, StringComparison.Ordinal);
            return HamlogAwardAreas.MatchesRussianSeries(call, dxcc(call), series);
        }

        bool Within(DateTime time)
        { return (!from.HasValue || time >= from.Value) && (!until.HasValue || time < until.Value); }

        internal bool Available(string band, string mode, DateTime time)
        {
            if (Standing == null || Complete || !Within(time) || String.IsNullOrEmpty(band)) return false;
            if (award.Rule == HamlogRule.Bands) return award.Bands.Contains(band) && !worked.Contains(band);
            return Confirmed + pending < Required || NeedsCounties;
        }

        string Key(string call, string band, string modeGroup, string grid, string county)
        {
            switch (award.Rule)
            {
                case HamlogRule.Grid: return grid;
                case HamlogRule.Bryansk: return county;
                case HamlogRule.Stations:
                case HamlogRule.EventStation:
                case HamlogRule.Arck35: return call;
                case HamlogRule.Bands: return band;
                default: return call + "|" + band + "|" + modeGroup;
            }
        }

        int Value(string call, string band, string grid, string county)
        {
            switch (award.Rule)
            {
                case HamlogRule.Grid:
                    return grid != null && RussianDxcc.Contains(dxcc(call)) ? 1 : 0;
                case HamlogRule.Bryansk:
                    return county != null && county.StartsWith("BR-", StringComparison.Ordinal) ? 1 : 0;
                case HamlogRule.Stations:
                    return (award == HamlogAward.Romania ? call == award.Station
                        : call.IndexOf('/') < 0 && call.StartsWith(award.Station, StringComparison.Ordinal) && dxcc(call) == "ES") ? 1 : 0;
                case HamlogRule.Bands:
                    return call == award.Station && award.Bands.Contains(band) ? 1 : 0;
                case HamlogRule.EventStation:
                    return call == award.Station ? 1 : 0;
                case HamlogRule.Tula:
                    return InRegion(call, county, "TL-", TulaSeries) ? 1 : 0;
                case HamlogRule.Tatarstan:
                    return InRegion(call, county, "TA-", TatarstanSeries) ? 1 : 0;
                case HamlogRule.Vysotsky:
                    return HamlogAwardRosters.Arck.Contains(call) || InRegion(call, county, "MA-", MoscowSeries) ? 1 : 0;
                case HamlogRule.Arck35:
                    return HamlogAwardRosters.Arck.Contains(call) ? 1 : 0;
                case HamlogRule.Marathon:
                    if (Specials.Contains(call)) return 50;
                    if (HamlogAwardRosters.Miller.Contains(call)) return 25;
                    return InRegion(call, county, "RO-", RostovSeries) ? 10 : 0;
                default:
                    return 0;
            }
        }

        internal bool Needs(string call, string band, string mode, DateTime now, string grid)
        {
            if (!Available(band, mode, now) || call == own) return false;
            grid = AwardLocator.Parse(grid);
            string county = CountyAt(call, grid);
            if (award.Rule == HamlogRule.Tatarstan && Confirmed + pending >= Required && (county == null || counties.Contains(county)))
                return false;
            string key = Key(call, band, HamlogPointProgress.ModeGroup(mode), grid, county);
            return key != null && !worked.Contains(key) && Value(call, band, grid, county) > 0;
        }

        internal void Record(LogbookQso contact)
        { Reserve(contact, true); }

        void Reserve(LogbookQso contact, bool duringSession)
        {
            DateTime? time = contact.StartedUtc ?? contact.EndedUtc;
            if (Standing == null || !time.HasValue || !Within(time.Value) || contact.Call == own || String.IsNullOrEmpty(contact.Band))
                return;
            string group = HamlogPointProgress.ModeGroup(contact.Mode);
            if (group != "CW" && group != "SSB" && group != "DIGI" || award.Rule == HamlogRule.Bryansk && group != "DIGI")
                return;
            string grid = AwardLocator.Parse(contact.Locator);
            string county = AwardCounty(contact.County) ?? CountyAt(contact.Call, grid);
            int points = Value(contact.Call, contact.Band, grid, county);
            string key = Key(contact.Call, contact.Band, group, grid, county);
            if (points == 0 || key == null || !worked.Add(key) || !duringSession) return;
            pending += points;
            if (award.Rule == HamlogRule.Tatarstan && county != null) counties.Add(county);
        }
    }
}
