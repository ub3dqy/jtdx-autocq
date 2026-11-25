using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace JtdxAutoCq
{
    public sealed class HamlogRulePlan
    {
        public string Kind, FromUtc, UntilUtc, BonusUntilUtc, SearchRegion;
        public int Required, MemberPoints, ExtraPoints, AlternativeRequired, RcpwOnlineAfterPoints, RcpwSpecialPoints;
        public bool HfOnly, VhfBonus, NoMultipliers, RcpwDoubleDistrictZero, RcpwSpecialAfterActivity, ClubMembersMayHunt;
        public bool ClubHamlogModeRates, ClubHamlogNo160mBonus;
        public string[] Calls = new string[0];
        public string[] ClubOnlyActivatorAreas = new string[0];
        public HamlogBonusGroup[] BonusGroups = new HamlogBonusGroup[0];
        public HamlogContactCriteria Contacts;
        internal bool Club { get { return Kind == "sky-hunter"; } }
        internal bool Rcpw { get { return Kind == "rcpw-hunter"; } }
        internal bool ContactAward { get { return Kind == "contact-points" && Contacts != null; } }
        internal bool RdaOnly { get { return Kind == "rda-search"; } }
        internal string MonitoringRegion
        {
            get
            {
                if (!String.IsNullOrEmpty(SearchRegion)) return SearchRegion;
                if (!ContactAward || Contacts.Countries.Length != 0 || !String.IsNullOrEmpty(Contacts.Roster)) return null;
                if (Contacts.RestrictsCounties) return HamlogAwardAreas.Region(Contacts.CountyPrefix);
                string[] regions = BonusGroups.Where(group => group.Area).SelectMany(group => group.Targets)
                    .Select(HamlogAwardAreas.Region).Distinct().ToArray();
                return regions.Length == 1 ? regions[0] : null;
            }
        }
        internal bool GeographicSearch
        {
            get { return RdaOnly || MonitoringRegion != null || ContactAward && (Contacts.RestrictsCounties
                || Contacts.Additional.Any(condition => condition.ByCounty)
                || BonusGroups.Any(group => group.Area && group.Targets.Any(HamlogCountyIndex.IsCounty))); }
        }
        internal bool Districts { get { return ContactAward && Contacts.Repeat == HamlogContactCriteria.District; } }
        internal bool Locators { get { return ContactAward && Contacts.Repeat == HamlogContactCriteria.Locator; } }
        internal bool UsesRcpwRoster { get { return Rcpw || ContactAward && Contacts.Roster == "rcpw"; } }
        internal bool Points { get { return Club || Rcpw || ContactAward; } }
        internal bool AllStations { get { return ContactAward && Contacts.AllStations; } }
        internal bool MultiLevel { get { return AllStations || ContactAward && Contacts.Levels != null && Contacts.Levels.Length > 1; } }
        internal int MaxRequired { get { return AllStations ? 100000 : 10000; } }
        internal bool Requires(int value)
        {
            return value == Required || (Rcpw || ContactAward) && AlternativeRequired > 0 && value == AlternativeRequired
                || AllStations && value >= AlternativeRequired && value <= Required
                || ContactAward && Contacts.Levels != null && Contacts.Levels.Contains(value);
        }

        internal bool SameAs(HamlogRulePlan other)
        {
            if (other == null || Kind != other.Kind || Required != other.Required || MemberPoints != other.MemberPoints
                || ExtraPoints != other.ExtraPoints || AlternativeRequired != other.AlternativeRequired
                || RcpwOnlineAfterPoints != other.RcpwOnlineAfterPoints || RcpwSpecialPoints != other.RcpwSpecialPoints
                || RcpwDoubleDistrictZero != other.RcpwDoubleDistrictZero || RcpwSpecialAfterActivity != other.RcpwSpecialAfterActivity
                || HfOnly != other.HfOnly || VhfBonus != other.VhfBonus || NoMultipliers != other.NoMultipliers
                || ClubMembersMayHunt != other.ClubMembersMayHunt
                || !ClubOnlyActivatorAreas.SequenceEqual(other.ClubOnlyActivatorAreas)
                || (SearchRegion ?? "") != (other.SearchRegion ?? "") || ClubHamlogModeRates != other.ClubHamlogModeRates
                || ClubHamlogNo160mBonus != other.ClubHamlogNo160mBonus
                || (FromUtc ?? "") != (other.FromUtc ?? "")
                || (UntilUtc ?? "") != (other.UntilUtc ?? "") || (BonusUntilUtc ?? "") != (other.BonusUntilUtc ?? "")
                || !Calls.SequenceEqual(other.Calls) || (Contacts == null ? other.Contacts != null : !Contacts.SameAs(other.Contacts))) return false;
            var groups = other.BonusGroups ?? new HamlogBonusGroup[0];
            return BonusGroups.Length == groups.Length && BonusGroups.Zip(groups, (a, b) =>
                a.Area == b.Area && a.Points == b.Points && a.Targets.SequenceEqual(b.Targets)).All(equal => equal);
        }

        internal void Validate()
        {
            FromUtc = (FromUtc ?? "").Trim();
            UntilUtc = (UntilUtc ?? "").Trim();
            SearchRegion = (SearchRegion ?? "").Trim();
            if (SearchRegion.Length != 0 && HamlogAwardAreas.Region(SearchRegion) != SearchRegion)
                throw new InvalidDataException("Проверьте код области для поиска.");
            DateTime? from = AwardCatalog.ReadTime(FromUtc), until = AwardCatalog.ReadTime(UntilUtc, true);
            if (from.HasValue && until.HasValue && from >= until)
                throw new InvalidDataException("Дата окончания должна быть позже даты начала.");
            if (RdaOnly)
            {
                if (Required < 1 || Required > 10000)
                    throw new InvalidDataException("Проверьте порог зачёта HAMLOG.");
                return;
            }
            if (!Points && Kind != "single-station")
                throw new InvalidDataException("Неизвестный способ зачёта диплома.");
            if (RcpwOnlineAfterPoints != 0 && (!Rcpw || RcpwOnlineAfterPoints < MemberPoints || RcpwOnlineAfterPoints > ExtraPoints)
                || !Rcpw && (RcpwSpecialPoints != 0 || RcpwDoubleDistrictZero || RcpwSpecialAfterActivity))
                throw new InvalidDataException("Проверьте подтверждённую ставку HAMLOG после дней активности.");
            if (Calls == null || Calls.Length > 80
                || Calls.Length == 0 && !Rcpw && !Club && !ContactAward)
                throw new InvalidDataException("Укажите позывные станций.");
            Calls = Calls.Select(Callsign.Parse).Distinct(StringComparer.Ordinal).ToArray();
            if (Calls.Any(call => call == null))
                throw new InvalidDataException("Проверьте позывные в списке.");
            ClubOnlyActivatorAreas = (ClubOnlyActivatorAreas ?? new string[0]).Distinct(StringComparer.Ordinal).ToArray();
            if (!Club && (ClubOnlyActivatorAreas.Length != 0 || ClubMembersMayHunt || ClubHamlogModeRates || ClubHamlogNo160mBonus)
                || ClubOnlyActivatorAreas.Any(area => !HamlogAwardAreas.Known(area)))
                throw new InvalidDataException("Проверьте территории активаторов клуба.");
            if (Points ? Required < 1 || Required > MaxRequired || MemberPoints < 1 || MemberPoints > 1000
                || ExtraPoints < 1 || ExtraPoints > 1000 || (HfOnly || NoMultipliers) && VhfBonus
                : Required != 1 || Calls.Length != 1)
                throw new InvalidDataException("Проверьте число очков или позывных в условиях диплома.");
            BonusGroups = BonusGroups ?? new HamlogBonusGroup[0];
            if (BonusGroups.Length > 12 || !Club && !ContactAward && BonusGroups.Length != 0)
                throw new InvalidDataException("Проверьте дополнительные группы станций.");
            if (Club && Calls.Length == 0 && BonusGroups.Length == 0)
                throw new InvalidDataException("Укажите бонусный позывной или территорию.");
            if (ContactAward)
            {
                if (AllStations ? AlternativeRequired < 1 || AlternativeRequired >= Required
                    : AlternativeRequired < 0 || AlternativeRequired > Required || MultiLevel && AlternativeRequired != 0)
                    throw new InvalidDataException("Проверьте дополнительный порог зачёта.");
                Contacts.Validate(Required, Calls, BonusGroups.Length != 0);
                if ((Districts || Locators) && (MemberPoints != 1 || ExtraPoints != 1 || AlternativeRequired != 0))
                    throw new InvalidDataException("Для этого диплома каждый новый район или квадрат даёт одну отметку.");
            }
            var named = new HashSet<string>(Calls, StringComparer.Ordinal);
            var areas = new HashSet<string>(StringComparer.Ordinal);
            foreach (HamlogBonusGroup group in BonusGroups)
            {
                if (group == null || group.Points < 1 || group.Points > 1000 || group.Targets == null || group.Targets.Length == 0
                    || (group.Area ? group.Targets.Any(area => !HamlogAwardAreas.Known(area) || !areas.Add(area))
                        : group.Targets.Any(call => String.IsNullOrEmpty(call) || Callsign.Parse(call) != call || !named.Add(call))))
                    throw new InvalidDataException("Проверьте очки, позывные и районы в дополнительных группах.");
            }
            if (Rcpw)
            {
                BonusUntilUtc = (BonusUntilUtc ?? "").Trim();
                DateTime? bonusUntil = AwardCatalog.ReadTime(BonusUntilUtc);
                if (!from.HasValue || bonusUntil < from || !bonusUntil.HasValue && Calls.Length != 0
                    || bonusUntil == from && (ExtraPoints != MemberPoints || Calls.Length != 0)
                    || until.HasValue && bonusUntil > until
                    || AlternativeRequired < 1 || AlternativeRequired > Required || ExtraPoints < MemberPoints
                    || (Calls.Length == 0 ? RcpwSpecialPoints != 0 || RcpwSpecialAfterActivity : RcpwSpecialPoints < ExtraPoints || RcpwSpecialPoints > 1000)
                    || HfOnly || VhfBonus)
                    throw new InvalidDataException("Проверьте границу дней активности и пороги зачёта RCPW.");
            }
        }

        internal int RcpwFactor(string own) { return RcpwDoubleDistrictZero && HamlogAwardAreas.IsZeroDistrict(own) ? 2 : 1; }
    }

    public sealed class HamlogBonusGroup
    {
        public bool Area;
        public int Points;
        public string[] Targets = new string[0];
    }

    public sealed class HamlogContactCriteria
    {
        internal const string BandMode = "band-mode", DayBandMode = "day-band-mode", EveryQso = "every-qso",
            Call = "call", Band = "band", Day = "day", District = "district", Locator = "locator";
        static readonly Regex RdrcDigital = new Regex(@"\A(?:CONTESTIA?|DOMINO(?:EX|F)?|FT8|FT4|(?:FM|FSK|PSK)?HELL(?:80)?|HFSK|JT65|JT9|MFSK\d*|MT63|OLIVIA(?: [0-9]+/[0-9]+)?|(?:F|Q)?PSK(?:[A-Z]*\d+[A-Z]*\d*)?|ROS|RTTY(?:M)?|SIM(?:31)?|SSTV|T10|THOR|THROB)\z",
            RegexOptions.CultureInvariant);
        public string Repeat, BonusFromUtc, BonusUntilUtc, Roster, CountyPrefix, ShortcutDay;
        public int LastCounty, ShortcutContacts, RepeatDayOffsetMinutes, MinimumDistanceKm;
        public bool GroupModes, PortableAliases, PortableRegionalCalls, AcceptObserverCredit, ActivatorRoster, MultiplyByCountyCount, CreditBeforePublication, AllStations;
        public bool HamlogRosterCredit;
        public string[] ActivatorAreas = new string[0];
        public string[] Countries = new string[0];
        public string[] Bands = new string[0];
        public string[] Modes = new string[0];
        public int[] Levels = new int[0];
        public int[] RetiredCounties = new int[0];
        public int[] EligibleCounties = new int[0];
        public HamlogContactRequirement[] Additional = new HamlogContactRequirement[0];
        public HamlogPointAdjustment[] Adjustments = new HamlogPointAdjustment[0];

        internal bool RestrictsCounties { get { return !String.IsNullOrEmpty(CountyPrefix); } }

        internal void Validate(int required, string[] calls, bool areaGroups)
        {
            if (Repeat != BandMode && Repeat != DayBandMode && Repeat != EveryQso && Repeat != Call && Repeat != Band
                && Repeat != Day && Repeat != District && Repeat != Locator)
                throw new InvalidDataException("Проверьте условия повторных связей.");
            Countries = (Countries ?? new string[0]).Distinct(StringComparer.Ordinal).ToArray();
            Bands = (Bands ?? new string[0]).Distinct(StringComparer.Ordinal).ToArray();
            Modes = (Modes ?? new string[0]).Distinct(StringComparer.Ordinal).ToArray();
            ActivatorAreas = (ActivatorAreas ?? new string[0]).Distinct(StringComparer.Ordinal).ToArray();
            Levels = (Levels ?? new int[0]).Distinct().OrderBy(level => level).ToArray();
            RetiredCounties = (RetiredCounties ?? new int[0]).Distinct().OrderBy(code => code).ToArray();
            EligibleCounties = (EligibleCounties ?? new int[0]).Distinct().OrderBy(code => code).ToArray();
            Additional = Additional ?? new HamlogContactRequirement[0];
            Adjustments = Adjustments ?? new HamlogPointAdjustment[0];
            if (Countries.Any(country => !Regex.IsMatch(country ?? "", @"\A[A-Z0-9]{1,8}\z"))
                || Bands.Any(band => !JtdxAdif.BandNames.Contains(band))
                || Modes.Any(mode => mode != "DIGI" && mode != "SSB" && mode != "CW")
                || MinimumDistanceKm == 0 && !AllStations && !RestrictsCounties && Countries.Length == 0
                    && calls.Length == 0 && String.IsNullOrEmpty(Roster) && !areaGroups
                || !String.IsNullOrEmpty(Roster) && Roster != "rcpw" && HamlogAwardRosters.For(Roster) == null
                || ActivatorRoster && String.IsNullOrEmpty(Roster) || ActivatorAreas.Any(area => !HamlogAwardAreas.Known(area))
                || PortableAliases && (calls.Length == 0 && String.IsNullOrEmpty(Roster) || calls.Any(call => call.Contains("/")))
                || Levels.Length != 0 && (Levels.First() < 1 || Levels.Last() != required))
                throw new InvalidDataException("Проверьте территории, диапазоны и степени диплома.");
            if (MinimumDistanceKm < 0 || MinimumDistanceKm > 20000
                || MinimumDistanceKm > 0 && (AllStations || RestrictsCounties || Countries.Length != 0 || calls.Length != 0
                    || !String.IsNullOrEmpty(Roster) || areaGroups || Repeat != BandMode || GroupModes))
                throw new InvalidDataException("Проверьте расстояние и правила повторов. Для диплома дальних связей учитываются отдельные цифровые виды.");
            if (HamlogRosterCredit && calls.Length == 0)
                throw new InvalidDataException("Укажите опубликованные позывные для сверки клубного зачёта.");
            if (AllStations && (Repeat != BandMode || RestrictsCounties || Countries.Length != 0 || calls.Length != 0 || areaGroups
                || !String.IsNullOrEmpty(Roster) || ActivatorAreas.Length != 0 || Levels.Length != 0 || Additional.Length != 0
                || Adjustments.Length != 0 || ShortcutContacts != 0))
                throw new InvalidDataException("Для серии QSO подходят станции всех стран. Пороги берутся из HAMLOG.");
            if (RestrictsCounties && (!Regex.IsMatch(CountyPrefix, @"\A[A-Z]{2}\z") || LastCounty < 1 || LastCounty > 99
                || RetiredCounties.Concat(EligibleCounties).Any(code => code < 1 || code > LastCounty))
                || !RestrictsCounties && (LastCounty != 0 || RetiredCounties.Length != 0 || EligibleCounties.Length != 0)
                || Repeat == District && (!RestrictsCounties || UsableCountyCount < required
                    || Countries.Length != 0 || calls.Length != 0 || !String.IsNullOrEmpty(Roster) || areaGroups
                    || Additional.Length != 0 || Adjustments.Length != 0)
                || MultiplyByCountyCount && (!RestrictsCounties || Repeat != BandMode || calls.Length != 0 || Countries.Length != 0
                    || !String.IsNullOrEmpty(Roster) || Additional.Length != 0 || Adjustments.Length != 0)
                || Repeat == Locator && (RestrictsCounties || Countries.Length == 0 || calls.Length != 0
                    || areaGroups || !String.IsNullOrEmpty(Roster) || Additional.Length != 0 || Adjustments.Length != 0))
                throw new InvalidDataException("Проверьте код области и список районов RDA.");
            if (Additional.Length > 2 || Additional.Any(condition => condition == null)
                || Additional.Where(condition => condition.ReportedByHamlog)
                    .GroupBy(condition => condition.Kind == HamlogContactRequirement.Counties).Any(group => group.Count() > 1)
                || Adjustments.Length > 8 || Adjustments.Any(adjustment => adjustment == null))
                throw new InvalidDataException("Проверьте дополнительные условия диплома.");
            if (CreditBeforePublication && (Repeat == District || Repeat == Locator))
                throw new InvalidDataException("Проверьте дату начала зачёта.");
            if (RepeatDayOffsetMinutes < -720 || RepeatDayOffsetMinutes > 840
                || RepeatDayOffsetMinutes != 0 && Repeat != Day && Repeat != DayBandMode
                || ShortcutContacts < 0 || ShortcutContacts > 1000 || String.IsNullOrEmpty(ShortcutDay) != (ShortcutContacts == 0)
                || ShortcutContacts != 0 && (Additional.Length != 0 || Repeat == District || Repeat == Locator || MultiplyByCountyCount))
                throw new InvalidDataException("Проверьте время повтора и альтернативный зачёт.");
            if (ShortcutContacts != 0) HamlogCalendar.MonthDay(ShortcutDay);
            foreach (HamlogContactRequirement condition in Additional) condition.Validate(this);
            foreach (HamlogPointAdjustment adjustment in Adjustments) adjustment.Validate();
            DateTime? from = AwardCatalog.ReadTime(BonusFromUtc), until = AwardCatalog.ReadTime(BonusUntilUtc);
            if (from.HasValue != until.HasValue || from.HasValue && from >= until)
                throw new InvalidDataException("Проверьте начало и окончание периода для специальных позывных.");
        }

        internal bool SameAs(HamlogContactCriteria other)
        {
            return other != null && Repeat == other.Repeat && GroupModes == other.GroupModes && PortableAliases == other.PortableAliases
                && PortableRegionalCalls == other.PortableRegionalCalls && RepeatDayOffsetMinutes == other.RepeatDayOffsetMinutes
                && (ShortcutDay ?? "") == (other.ShortcutDay ?? "") && ShortcutContacts == other.ShortcutContacts
                && AcceptObserverCredit == other.AcceptObserverCredit
                && ActivatorRoster == other.ActivatorRoster && ActivatorAreas.SequenceEqual(other.ActivatorAreas)
                && MultiplyByCountyCount == other.MultiplyByCountyCount && EligibleCounties.SequenceEqual(other.EligibleCounties)
                && CreditBeforePublication == other.CreditBeforePublication && AllStations == other.AllStations
                && MinimumDistanceKm == other.MinimumDistanceKm
                && HamlogRosterCredit == other.HamlogRosterCredit
                && (Roster ?? "") == (other.Roster ?? "")
                && (CountyPrefix ?? "") == (other.CountyPrefix ?? "") && LastCounty == other.LastCounty
                && RetiredCounties.SequenceEqual(other.RetiredCounties)
                && Additional.Length == other.Additional.Length && Additional.Zip(other.Additional, (first, second) => first.SameAs(second)).All(same => same)
                && Adjustments.Length == other.Adjustments.Length && Adjustments.Zip(other.Adjustments, (first, second) => first.SameAs(second)).All(same => same)
                && (BonusFromUtc ?? "") == (other.BonusFromUtc ?? "") && (BonusUntilUtc ?? "") == (other.BonusUntilUtc ?? "")
                && Countries.SequenceEqual(other.Countries) && Bands.SequenceEqual(other.Bands)
                && Modes.SequenceEqual(other.Modes) && Levels.SequenceEqual(other.Levels);
        }

        internal string NamedCall(string call) { return PortableAliases ? Callsign.BaseCall(call) : call; }

        internal string ContactKey(string call, string band, string mode)
        { return call + "|" + band + "|" + (MinimumDistanceKm > 0 ? RdrcMode(mode)
            : GroupModes ? HamlogPointProgress.ModeGroup(mode) : (mode ?? "").ToUpperInvariant()); }

        internal decimal Adjust(decimal points, string band, DateTime time, string call)
        {
            foreach (HamlogPointAdjustment adjustment in Adjustments) points = adjustment.Apply(points, band, time, call);
            return points;
        }

        internal bool MatchesPoints(int points, HamlogCredit credit)
        {
            if (Adjustments.Length == 0) return points == credit.Points;
            DateTime day = credit.DayUtc.Date;
            return new[] { day }.Concat(Adjustments.SelectMany(adjustment => adjustment.Boundaries(day)))
                .Any(time => Adjust(points, credit.Band, time, credit.Call) == credit.Points);
        }

        internal bool AllowsBandMode(string band, string mode)
        {
            string group = HamlogPointProgress.ModeGroup(mode);
            return !String.IsNullOrEmpty(group) && JtdxAdif.BandNames.Contains(band)
                && (Bands.Length == 0 || Bands.Contains(band)) && (Modes.Length == 0 || Modes.Contains(group))
                && (MinimumDistanceKm == 0 || RdrcMode(mode) != null);
        }

        static string RdrcMode(string mode)
        {
            mode = (mode ?? "").Trim().ToUpperInvariant();
            if (!RdrcDigital.IsMatch(mode)) return null;
            if (mode == "FT4" || mode.StartsWith("MFSK", StringComparison.Ordinal)) return "MFSK";
            if (mode.Contains("PSK")) return "PSK";
            if (mode.StartsWith("DOMINO", StringComparison.Ordinal)) return "DOMINO";
            if (mode.StartsWith("CONTESTI", StringComparison.Ordinal)) return "CONTESTIA";
            if (mode.Contains("HELL") || mode == "HFSK") return "HELL";
            if (mode.StartsWith("OLIVIA", StringComparison.Ordinal)) return "OLIVIA";
            if (mode == "SIM31") return "SIM";
            return mode == "RTTYM" ? "RTTY" : mode;
        }

        internal bool IsActivatorArea(string call, Func<string, string> dxcc)
        {
            if (ActivatorAreas.Length == 0 || dxcc == null || String.IsNullOrEmpty(call)) return false;
            string entity = dxcc(call);
            return ActivatorAreas.Any(area => HamlogAwardAreas.Matches(area, call, entity));
        }

        internal int UsableCountyCount
        {
            get { return EligibleCounties.Length == 0 ? LastCounty - RetiredCounties.Length
                : EligibleCounties.Except(RetiredCounties).Count(); }
        }

        internal string DistrictCode(string call, string county, bool allowRetired = false)
        {
            return !String.IsNullOrEmpty(call) && !call.EndsWith("/AM", StringComparison.Ordinal)
                && InCountyScope(county, allowRetired) ? county : null;
        }

        internal bool InCountyScope(string county, bool allowRetired)
        {
            if (county == null || county.Length != 5 || !county.StartsWith(CountyPrefix + "-", StringComparison.Ordinal)) return false;
            int code;
            return Int32.TryParse(county.Substring(3), NumberStyles.None, CultureInfo.InvariantCulture, out code)
                && code > 0 && code <= LastCounty && (EligibleCounties.Length == 0 || EligibleCounties.Contains(code))
                && (allowRetired || !RetiredCounties.Contains(code));
        }
    }

    internal static class HamlogPlanCompiler
    {
        internal const int Version = 10;
        const string Day = @"\d{1,2} [а-яё]+ \d{4}";
        const string Points = @"\s*(?:[-–—]\s*)?(?<count>\d{1,4})\s*оч(?:ков|ка|ко)\.?";
        static readonly Regex Sky = Pattern(
            @"Связи на дипломы? засчитываются с (?<from>" + Day + @")\s*г\. Срок действия диплома\s*[-–—]\s*постоянный\.\s*"
            + @"(?:Засчитываются радиосвязи с радиолюбителями|Приглашаются радиолюбители) всех стран мира на диапазонах 1[,.]8\s*[-–—]\s*28\s*MHz(?<vhf> и УКВ)?\.\s*"
            + @"Необходимо набрать" + Points.Replace("count", "required")
            + @"\s*За радиосвязи с (?:Поч[её]тными членами клуба «Рыцари неба»|временными специальными позывными) (?<calls>[^.]{8,580}?) (?:начисляется|начисляются)"
            + Points.Replace("count", "special")
            + @"\s*За QSO [cс] членами клуба «Рыцари неба» (?:начисляется|начисляются)" + Points.Replace("count", "member")
            + @"\s*Для перечисленных выше корреспондентов вводится множитель для следующих видов излучения:\s*"
            + @"(?:За QSO\s+)?CW\s*[-–—:]\s*2,?\s+SSB\s*[-–—:]\s*1[,.]5,?\s+DIGI\s*[-–—:]\s*1\s+"
            + @"За QSO на диапазоне 160 метров(?<vhfbonus> и УКВ)?\s*[-–—]\s*1[,.]5\s*"
            + @"Повторные связи с одним и тем же членом клуба в одни сутки засчитываются на разных диапазонах,?\s*а на одном\s*[-–—]\s*"
            + @"разными видами излучения \(все DIGI считаются за один вид\)\.\s*Диплом выда[её]тся");
        static readonly Regex Calls = new Regex(@"(?<![A-Z0-9/-])[A-Z0-9]{1,5}\d[A-Z]{1,5}(?:/[A-Z0-9]{1,7})?(?![A-Z0-9/-])",
            RegexOptions.CultureInvariant);
        const string Hf = @"1[,.]8\s*[-–—]\s*28\s*MHz";
        const string ClockStart = @"(?:00[:.]00\s*UTC\s*)?";
        const string ClockEnd = @"(?:23[:.]59\s*UTC\s*)?";
        const string ShortDate = @"\d{1,2}\.\d{1,2}\.";
        const string RuleDate = @"(?:" + ShortDate + @"\d{4}|" + Day + @")";
        const string RcpwRepeat = @"Повторные связи засчитываются на разных КВ \(УКВ\) диапазонах, на одном диапазоне\s*[-–—]+\s*"
            + @"разными видами модуляции \(\s*все виды DIGI считаются за один\)\.";
        const string RcpwThreshold = @"Для получения диплома соискателям необходимо набрать" + Points
            + @"\s*за работу [cс] членами клуба RCPW на любых разреш[её]нных диапазонах любыми видами модуляции \(все виды DIGI считаются за один\)\.\s*"
            + @"А для радиолюбителей нулевого района и находящихся за пределами России необходимо набрать";
        const string RcpwRoster = @"Список членов клуба RCPW можно посмотреть на сайте rcpw\.ru\.\s*";
        static readonly Regex SkyBands = Pattern(@"на диапазонах\s*" + Hf + @"(?<vhf>\.?\s+и\s+УКВ)?");
        static readonly Regex SkyExtended = Pattern(
            @"Связи на дипломы? засчитываются с (?<from>" + Day + @")\s*г(?:ода)?\."
            + @"(?:\s*Срок действия диплома\s*[-–—]?\s*постоянный\.)?\s*"
            + @"(?:(?:Засчитываются радиосвязи с радиолюбителями|Приглашаются радиолюбители) всех стран мира на диапазонах " + Hf + @"(?:\.?\s+и\s+УКВ)?\.\s*)?"
            + @"Необходимо набрать" + Points.Replace("count", "required")
            + @"(?<rows>\s*За (?:радиосвязи|QSO)\s+[cс](?:о)?\s+.{1,1600}?)\s*"
            + @"Для перечисленных выше корреспондентов вводится множитель для следующих видов излучения:\s*"
            + @"(?:За QSO\s+)?CW\s*[-–—:]\s*2,?\s+SSB\s*[-–—:]\s*1[,.]5,?\s+DIGI\s*[-–—:]\s*1\s+"
            + @"За QSO на диапазоне 160 метров(?<vhfbonus>(?:,| и)\s*УКВ)?\s*[-–—]\s*1[,.]5\s*"
            + @"Повторные связи с одним и тем же членом клуба в одни сутки засчитываются на разных\s*диапазонах,?\s*а на одном\s*[-–—]?\s*"
            + @"разными видами излучения\s*\(все DIGI счита(?:ются|ть) за один вид\)\.\s*"
            + @"(?:Диплом выда[её]тся|Радиолюбители активаторы клуба|Члены клуба)");
        static readonly Regex SkyRow = Pattern(@"\G\s*(?:[•]\s*)?За (?:радиосвязи|QSO)\s+[cс](?:о)?\s+(?<target>[^.!?]+?)\s*"
            + @"(?:(?:начисляется|начисляются)|[-–—])" + Points + @"\s*");
        static readonly Regex SkyNamed = Pattern(
            @"\A(?:поч[её]тны(?:м|ми) член(?:ом|ами) клуба «Рыцари неба»|(?:временным[и]? )?специальн\w+ (?:временн\w+ )?позывны\w*"
            + @"|членами клуба «Рыцари неба»,?\s+являющ\w+)\s+");
        static readonly Regex SkyEnglish = Pattern(
            @"Contacts for the award are counted starting from (?<from>[A-Za-z]+ \d{1,2}, \d{4})\.\s*The award has no expiration date\.\s*"
            + @"QSOs with radio amateurs from all countries on the " + Hf + @" bands(?<vhf> and VHF)? are valid\.\s*"
            + @"To earn the award, participants must collect (?<required>\d{1,4}) points\.\s*[•]?\s*"
            + @"QSOs with Honorary Members of the [“""]Knights of the Sky[”""] Club (?<calls>[^.]{8,600}?) are worth (?<special>\d{1,4}) points each\.\s*[•]?\s*"
            + @"QSOs with regular members of the [“""]Knights of the Sky[”""] Club are worth (?<member>\d{1,4}) points\.\s*"
            + @"A mode multiplier applies to the correspondents listed above:\s*[•]?\s*CW\s*[-–—]\s*×2\s*[•]?\s*"
            + @"SSB\s*[-–—]\s*×1[,.]5\s*[•]?\s*DIGI\s*[-–—]\s*×1\s*"
            + @"QSOs on 160 meters(?<vhfbonus> and VHF)? receive an additional multiplier of ×1[,.]5\.\s*"
            + @"Duplicate QSOs with the same club member on the same day are allowed on different bands,?\s*"
            + @"and on the same band using different modes \(all DIGI modes count as one\)\.");
        static readonly Regex Rcpw = Pattern(
            RcpwThreshold.Replace("count", "required") + Points.Replace("count", "alternative")
            + @"\s*Любая радиосвязь с членом RCPW в дни активности \((?:с\s*)?" + ClockStart
            + @"(?<start>" + RuleDate + "|" + ShortDate + @")\s*(?:г\.?\s*)?(?:по|[-–—])\s*" + ClockEnd
            + @"(?<last>" + RuleDate + @")\s*г?\.?\s*\)?\s*да[её]т" + Points.Replace("count", "special")
            + @"\s*По окончани[ию] дней активности\s*(?:\(\s*)?с\s*" + ClockStart + @"(?<after>" + RuleDate + @")\s*г?\.?\s*"
            + @"(?:до\s*" + ClockEnd + @"(?<end>" + RuleDate + @")\s*г?\.?\s*)?\)?\s*"
            + @"(?:(?:за радиосвязи с членами радиоклуба )?начисляется|[-–—])" + Points.Replace("count", "member")
            + @"\s*(?:" + RcpwRoster + @")?" + RcpwRepeat + @"\s*"
            + @"(?:(?:Дата начала действия|Время действия) Положения о дипломе\s*[-–—]\s*" + ClockStart
            + @"(?<officialStart>" + RuleDate + @")\s*г?\.?\s*)"
            + @"(?:по\s*" + ClockEnd + @"(?<officialEnd>" + RuleDate + @")\s*г?\.?|Время действия\s*[-–—]\s*бессрочный\.)");
        static readonly Regex RcpwFlat = Pattern(
            RcpwThreshold.Replace("count", "required") + Points.Replace("count", "alternative")
            + @"\s*Любая радиосвязь с членом RCPW да[её]т" + Points.Replace("count", "member")
            + @"\s*" + RcpwRoster + RcpwRepeat + @"\s*"
            + @"Дата начала действия Положения о дипломе\s*[-–—]\s*" + ClockStart
            + @"(?<start>" + RuleDate + @")\s*г?\.?\s*Время действия\s*[-–—]\s*бессрочный\.");
        static readonly Regex SpecialEvent = Pattern(
            @"Special Event Station:\s+(?<station>[A-Z0-9/]{3,16})\s+Award Requirements\b.{0,1800}?"
            + @"Rules\s+1\.\s*QSOs must be made between (?<start>[A-Za-z]+ \d{1,2}) and (?<end>[A-Za-z]+ \d{1,2}), (?<year>\d{4})\.\s*"
            + @"2\.\s*At least one QSO with (?<same>[A-Z0-9/]{3,16}) is required to qualify for the award\.\s*"
            + @"3\.\s*Award eligibility will be determined based on the QSOs uploaded to HAMLOG by the special event station\.\s*Award Issuance\b");

        static Regex Pattern(string expression)
        { return new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }

        internal static HamlogRulePlan Compile(string url, string text)
        {
            string note;
            return CompileWithReview(url, text, out note);
        }

        internal static HamlogRulePlan CompileWithReview(string url, string text, out string note)
        {
            note = null;
            if (String.IsNullOrWhiteSpace(text)) return null;
            string cleaned = text.Replace("\u00ad", "").Replace("\u0002", "").Replace('ĸ', 'к');
            cleaned = Regex.Replace(cleaned, @"(?<=[а-яё])-\r?\n[ \t]*(?=[а-яё])", "", RegexOptions.IgnoreCase);
            string normalized = Regex.Replace(cleaned, @"\s+", " ");
            HamlogReviewedEntry reviewed = HamlogReviewedPlans.Find(url, normalized);
            if (reviewed != null)
            {
                note = reviewed.Note;
                return reviewed.Plan;
            }
            HamlogRulePlan rule = null;
            if (url.StartsWith(HamlogLinks.Origin + "club/nebo/", StringComparison.Ordinal)
                || url.StartsWith(HamlogLinks.Origin + "club/nebo-agpa/", StringComparison.Ordinal)
                || url.StartsWith(HamlogLinks.Origin + "club/nebo-cosmodromes/", StringComparison.Ordinal)
                || url.StartsWith(HamlogLinks.Origin + "club/nebo-cwa/", StringComparison.Ordinal))
            {
                Match match = Sky.Match(normalized);
                string begin = match.Success ? Date(match.Groups["from"].Value, "d MMMM yyyy", CultureInfo.GetCultureInfo("ru-RU")) : null;
                if (begin != null && !match.NextMatch().Success)
                    rule = new HamlogRulePlan {
                        Kind = "sky-hunter", FromUtc = begin, Required = Number(match, "required"),
                        MemberPoints = Number(match, "member"), ExtraPoints = Number(match, "special"),
                        HfOnly = !match.Groups["vhf"].Success,
                        VhfBonus = match.Groups["vhfbonus"].Success,
                        Calls = Calls.Matches(match.Groups["calls"].Value).Cast<Match>().Select(item => item.Value).ToArray(),
                    };
                if (rule == null) rule = ScoreSky(normalized);
            }
            else if (url.StartsWith(HamlogLinks.Origin + "club/rcpw/", StringComparison.Ordinal))
                rule = ScoreRcpw(normalized) ?? ScoreFlatRcpw(normalized);
            if (rule == null)
            {
                Match match = SpecialEvent.Match(normalized);
                if (match.Success && !match.NextMatch().Success
                    && match.Groups["station"].Value == match.Groups["same"].Value)
                {
                    string year = " " + match.Groups["year"].Value;
                    string start = Date(match.Groups["start"].Value + year, "MMMM d yyyy", CultureInfo.InvariantCulture);
                    string end = Date(match.Groups["end"].Value + year, "MMMM d yyyy", CultureInfo.InvariantCulture);
                    if (start != null && end != null)
                        rule = new HamlogRulePlan { Kind = "single-station", FromUtc = start, UntilUtc = end, Required = 1,
                            Calls = new[] { match.Groups["station"].Value } };
                }
            }
            if (rule == null) return null;
            try { rule.Validate(); return rule; }
            catch (InvalidDataException) { return null; }
        }

        static HamlogRulePlan ScoreSky(string text)
        {
            Match match = SkyExtended.Match(text);
            if (match.Success && !match.NextMatch().Success)
            {
                Match bands = SkyBands.Match(text.Substring(0, match.Index + match.Length));
                if (!bands.Success) return null;
                bool vhf = bands.Groups["vhf"].Success;
                for (Match other = bands.NextMatch(); other.Success; other = other.NextMatch())
                    if (other.Groups["vhf"].Success != vhf) return null;
                return ScoreSkyRows(match, vhf);
            }
            match = SkyEnglish.Match(text);
            if (!match.Success || match.NextMatch().Success) return null;
            string from = Date(match.Groups["from"].Value, "MMMM d, yyyy", CultureInfo.InvariantCulture);
            string[] calls = ReadCalls(match.Groups["calls"].Value);
            return from == null ? null : new HamlogRulePlan {
                Kind = "sky-hunter", FromUtc = from, Required = Number(match, "required"), MemberPoints = Number(match, "member"),
                ExtraPoints = Number(match, "special"), Calls = calls, HfOnly = !match.Groups["vhf"].Success,
                VhfBonus = match.Groups["vhfbonus"].Success
            };
        }

        static HamlogRulePlan ScoreSkyRows(Match match, bool vhf)
        {
            string from = Date(match.Groups["from"].Value, "d MMMM yyyy", CultureInfo.GetCultureInfo("ru-RU"));
            if (from == null) return null;
            var groups = new List<HamlogBonusGroup>();
            int member = 0, length = 0;
            string rows = match.Groups["rows"].Value.Trim();
            foreach (Match row in SkyRow.Matches(rows))
            {
                length += row.Length;
                string target = row.Groups["target"].Value.Trim();
                int points = Number(row, "count");
                if (String.Equals(target, "членами клуба «Рыцари неба»", StringComparison.OrdinalIgnoreCase))
                {
                    if (member != 0 || points == 0) return null;
                    member = points;
                    continue;
                }
                string[] areas = HamlogAwardAreas.Parse(target);
                string[] targets = areas ?? ReadCalls(target);
                if (targets.Length == 0 || areas == null && !SkyNamed.IsMatch(target)) return null;
                groups.Add(new HamlogBonusGroup { Area = areas != null, Points = points, Targets = targets });
            }
            if (length != rows.Length || member == 0) return null;
            var named = groups.Where(group => !group.Area).GroupBy(group => group.Points)
                .OrderByDescending(group => group.Key).Select(group => new HamlogBonusGroup {
                    Points = group.Key, Targets = group.SelectMany(row => row.Targets).Distinct(StringComparer.Ordinal).ToArray()
                }).ToArray();
            if (named.Length == 0) return null;
            return new HamlogRulePlan { Kind = "sky-hunter", FromUtc = from, Required = Number(match, "required"),
                HfOnly = !vhf, VhfBonus = match.Groups["vhfbonus"].Success, MemberPoints = member,
                ExtraPoints = named[0].Points, Calls = named[0].Targets,
                BonusGroups = named.Skip(1).Concat(groups.Where(group => group.Area)).ToArray() };
        }

        static string[] ReadCalls(string text)
        { return Calls.Matches(text).Cast<Match>().Select(item => item.Value).Distinct(StringComparer.Ordinal).ToArray(); }

        static HamlogRulePlan ScoreRcpw(string text)
        {
            Match match = Rcpw.Match(text);
            if (!match.Success || match.NextMatch().Success) return null;
            string start = match.Groups["start"].Value, end = RcpwDate(match, "last"), after = RcpwDate(match, "after");
            if (Regex.IsMatch(start, @"\A" + ShortDate + @"\z") && end != null) start += end.Substring(0, 4);
            start = RcpwDate(start);
            if (start == null || end == null || after == null || start != RcpwDate(match, "officialStart")
                || String.CompareOrdinal(start, end) > 0 || AwardCatalog.ReadTime(end, true) != AwardCatalog.ReadTime(after)
                || match.Groups["officialEnd"].Success != match.Groups["end"].Success
                || RcpwDate(match, "officialEnd") != RcpwDate(match, "end")) return null;
            return RcpwPlan(match, start, after, RcpwDate(match, "end"));
        }

        static HamlogRulePlan ScoreFlatRcpw(string text)
        {
            Match match = RcpwFlat.Match(text);
            if (!match.Success || match.NextMatch().Success) return null;
            string start = RcpwDate(match, "start");
            return start == null ? null : RcpwPlan(match, start, start, null);
        }

        static HamlogRulePlan RcpwPlan(Match match, string start, string after, string end)
        {
            int required = Number(match, "required"), points = Number(match, "member");
            return new HamlogRulePlan {
                Kind = "rcpw-hunter", FromUtc = start, UntilUtc = end, BonusUntilUtc = after, Required = required,
                AlternativeRequired = match.Groups["alternative"].Success ? Number(match, "alternative") : required,
                MemberPoints = points, ExtraPoints = match.Groups["special"].Success ? Number(match, "special") : points
            };
        }

        static string RcpwDate(Match match, string name) { return RcpwDate(match.Groups[name].Value); }
        static string RcpwDate(string text)
        {
            return Date(text, "d.M.yyyy", CultureInfo.InvariantCulture) ?? Date(text, "d MMMM yyyy", CultureInfo.GetCultureInfo("ru-RU"));
        }

        static int Number(Match match, string name)
        { return Int32.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture); }

        static string Date(string text, string format, CultureInfo culture)
        {
            DateTime value;
            return DateTime.TryParseExact(text, format, culture, DateTimeStyles.None, out value)
                ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
        }
    }
}
