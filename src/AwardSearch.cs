using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace JtdxAutoCq
{
    internal sealed class AwardSearch
    {
        readonly AwardCriteria[] awards;
        readonly string own;
        readonly Func<string, string> dxcc;
        string stationLocator;

        internal AwardSearch(AwardCatalog catalog, string own, string band, string mode, Func<string, string> dxcc,
            string stationLocator = null)
        {
            catalog.CheckCallsign(own);
            this.own = Callsign.Clean(own);
            this.dxcc = dxcc;
            SetOperatorLocator(stationLocator);
            awards = catalog.Programs.Where(program => program.Selected && !program.ClosedByIssuance && program.HasSearchRules)
                .Select(program => new AwardCriteria(program.Copy(), band, mode)).ToArray();
        }

        internal void SetOperatorLocator(string locator)
        { if (stationLocator != locator) stationLocator = AwardLocator.Full(locator); }

        internal bool NeedsDxcc { get { return awards.Any(award => award.NeedsDxcc); } }
        internal bool NeedsOperatorLocator(DateTime now)
        { return stationLocator == null && awards.Any(award => award.AllowsContext(now) && award.MinimumDistanceKm > 0); }
        internal int Available(DateTime now)
        { return awards.Count(award => award.AllowsContext(now) && (award.MinimumDistanceKm == 0 || stationLocator != null)); }

        IEnumerable<AwardCriteria> Matches(string receivedCall, DateTime now, string grid)
        {
            string call = Callsign.Parse(receivedCall);
            if (call == null || call == own) return Enumerable.Empty<AwardCriteria>();
            string country = NeedsDxcc && dxcc != null ? dxcc(call) : null;
            return awards.Where(award => award.AllowsContext(now) && award.Matches(call, country, now, grid, stationLocator));
        }

        internal int MatchCount(string call, DateTime now, string grid = null)
        { return Matches(call, now, grid).Count(); }

        internal string Reason(string call, DateTime now, string grid = null)
        {
            string[] names = Matches(call, now, grid).Select(award => award.Title).ToArray();
            return names.Length == 0 ? "Условия выбранных дипломов не подходят для этой станции на текущем диапазоне и виде работы."
                : "Подходит для " + String.Join(", ", names) + ".";
        }
    }

    internal sealed class AwardCriteria
    {
        static readonly HashSet<string> MarathonStations = Calls("UE7DX UE36DX UE36L R2026DX R036DX UE4DX UE6DX UE3DX UE0DX R036Z UE36O");
        static readonly HashSet<string> MuromStations = Calls("U4MIR UA9CES RW9CC R2AKN R4CQ RA3TD RA6F LZ1ZF AC1ZF LZ07RN");
        static readonly HashSet<string> Hf = Calls("160m 80m 60m 40m 30m 20m 17m 15m 12m 10m");
        static readonly HashSet<string> Vhf = Calls("6m 4m 2m");
        static readonly HashSet<string> BelowHf = Calls("2190m 630m 560m");
        readonly AwardProgram program;
        readonly HamlogAward template;
        readonly HamlogRulePlan plan;
        readonly HamlogContactCriteria contacts;
        readonly HashSet<string> members;
        readonly HashSet<string> special;
        readonly DateTime? bonusFrom, bonusUntil;
        readonly DateTime? from, until;
        readonly bool contextAllowed;
        readonly bool dxccRequired;
        readonly AwardGoal[] goals;
        readonly Dictionary<string, Regex> masks;
        readonly string[] namedGroups, areas;

        internal string Title { get { return program.Title; } }
        internal int MinimumDistanceKm { get { return contacts == null ? 0 : contacts.MinimumDistanceKm; } }
        internal bool NeedsDxcc { get { return dxccRequired; } }

        internal AwardCriteria(AwardProgram program, string band, string mode)
        {
            this.program = program;
            template = program.Template;
            plan = template == null ? null : template.DocumentPlan;
            contacts = plan == null ? null : plan.Contacts;
            from = program.From;
            until = program.Until;
            goals = program.UsesLocalTargets ? program.Goals.Where(goal =>
                (goal.Band.Length == 0 || goal.Band == band) && (goal.Mode.Length == 0 || goal.Mode == mode)).ToArray() : new AwardGoal[0];
            masks = goals.Where(goal => goal.Kind == "mask").Select(goal => goal.Value).Distinct()
                .ToDictionary(mask => mask, MonitorTargets.MaskPattern);
            if (program.UsesLocalTargets)
            {
                contextAllowed = goals.Length != 0;
                dxccRequired = goals.Any(goal => goal.Kind == "dxcc");
                return;
            }

            contextAllowed = template != null && template.HasSearchRules && JtdxAdif.BandNames.Contains(band)
                && (template.Bands.Length == 0 || template.Bands.Contains(band))
                && (contacts == null || contacts.AllowsBandMode(band, mode))
                && (plan == null || !plan.Rcpw || !BelowHf.Contains(band))
                && (plan == null || !plan.Club || Hf.Contains(band) || !plan.HfOnly && Vhf.Contains(band))
                && (template != HamlogAward.Murom || !BelowHf.Contains(band));
            if (plan == null)
            {
                dxccRequired = template != null && (template.MonitoringRegion != null || template.Rule == HamlogRule.Grid
                    || template == HamlogAward.Estonia4 || template == HamlogAward.Estonia5
                    || template.Rule == HamlogRule.Vysotsky || template.Rule == HamlogRule.Marathon);
                return;
            }

            special = new HashSet<string>(plan.Calls);
            namedGroups = plan.BonusGroups.Where(group => !group.Area).SelectMany(group => group.Targets).ToArray();
            areas = plan.BonusGroups.Where(group => group.Area).SelectMany(group => group.Targets).ToArray();
            members = plan.Club ? HamlogAwardRosters.Sky : HamlogAwardRosters.For(plan.Rcpw ? "rcpw" : contacts == null ? null : contacts.Roster);
            bonusFrom = contacts == null ? null : AwardCatalog.ReadTime(contacts.BonusFromUtc);
            bonusUntil = AwardCatalog.ReadTime(plan.Rcpw ? plan.BonusUntilUtc : contacts == null ? null : contacts.BonusUntilUtc);
            dxccRequired = plan.RdaOnly || !String.IsNullOrEmpty(plan.SearchRegion) || areas.Length != 0 || contacts != null
                && (contacts.Countries.Length != 0 || contacts.RestrictsCounties || contacts.Roster == "arck-dx"
                    || contacts.Additional.Any(requirement => requirement.ByCounty && requirement.CountyCodes.Length != 0));
        }

        internal bool AllowsContext(DateTime now)
        { return contextAllowed && !program.ClosedByIssuance && (!from.HasValue || from <= now) && (!until.HasValue || now < until); }

        internal bool Matches(string call, string country, DateTime now, string grid, string stationLocator)
        {
            if (program.UsesLocalTargets)
                return goals.Any(goal => goal.Kind == "call" ? goal.Value == call
                    : goal.Kind == "dxcc" ? goal.Value == country : masks[goal.Value].IsMatch(call));
            if (plan == null) return MatchesBuiltIn(call, country, grid);
            if (MinimumDistanceKm > 0) return AwardLocator.FartherThan(stationLocator, grid, MinimumDistanceKm);
            if (plan.RdaOnly) return RussianArea(EmptyRegion(plan.SearchRegion), call, country);
            if (plan.Locators && AwardLocator.Parse(grid) == null) return false;
            string named = contacts == null ? call : contacts.NamedCall(call);
            if (special.Contains(named))
            {
                if (plan.Rcpw) return plan.RcpwSpecialAfterActivity || now < bonusUntil;
                if (!bonusFrom.HasValue || now >= bonusFrom && now < bonusUntil) return true;
            }
            if (namedGroups.Contains(named) || areas.Any(area => MatchesArea(area, call, country))) return true;
            if (members != null && (members.Contains(call) || members.Contains(named))
                && (contacts == null || contacts.Roster != "arck-dx" || country != null && !RussianEntity(country))) return true;
            if (contacts == null) return false;
            return contacts.AllStations || contacts.Countries.Contains(country)
                || contacts.RestrictsCounties && RussianArea(EmptyRegion(plan.SearchRegion) ?? contacts.CountyPrefix, call, country)
                || contacts.Additional.Any(requirement => requirement.ByCounty
                    ? requirement.CountyCodes.Any(county => MatchesArea(county, call, country)) : requirement.Station(call) != null);
        }

        bool MatchesBuiltIn(string call, string country, string grid)
        {
            if (template.MonitoringRegion != null) return RussianArea(template.MonitoringRegion, call, country);
            switch (template.Rule)
            {
                case HamlogRule.Grid: return RussianEntity(country) && AwardLocator.Parse(grid) != null;
                case HamlogRule.Stations:
                    return template == HamlogAward.Romania ? call == template.Station
                        : country == "ES" && call.IndexOf('/') < 0 && call.StartsWith(template.Station, StringComparison.Ordinal);
                case HamlogRule.Bands: return call == template.Station;
                case HamlogRule.Vysotsky: return HamlogAwardRosters.Arck.Contains(call) || RussianArea("MA", call, country);
                case HamlogRule.Arck35: return HamlogAwardRosters.Arck.Contains(call);
                case HamlogRule.Marathon:
                    return MarathonStations.Contains(call) || HamlogAwardRosters.Miller.Contains(call) || RussianArea("RO", call, country);
                default: return template == HamlogAward.Murom && (MuromStations.Contains(call) || HamlogAwardRosters.Sky.Contains(call));
            }
        }

        static bool MatchesArea(string area, string call, string country)
        {
            string region = HamlogAwardAreas.Region(area);
            return region != null ? RussianArea(region, call, country) : HamlogAwardAreas.Matches(area, call, country);
        }

        static bool RussianArea(string region, string call, string country)
        { return HamlogAwardAreas.Russian(country) && HamlogAwardAreas.MatchesMonitoringRegion(region, call, country); }
        static bool RussianEntity(string country) { return HamlogAwardAreas.Russian(country) || country == "R1FJ"; }
        static string EmptyRegion(string region) { return String.IsNullOrEmpty(region) ? null : region; }
        static HashSet<string> Calls(string calls) { return new HashSet<string>(calls.Split(' '), StringComparer.Ordinal); }
    }
}
