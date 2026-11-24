using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace JtdxAutoCq
{
    public sealed class HamlogCredit
    {
        public string Call, Band, Mode, County, Locator;
        public DateTime DayUtc;
        public decimal Points;
        public bool Observation;
    }

    public sealed class HamlogStanding
    {
        public string Callsign;
        public DateTime CheckedUtc;
        public decimal Points;
        public int Required;
        public bool ProvisionalRequired;
        public int RcpwAfterPoints;
        public List<HamlogCredit> Credits = new List<HamlogCredit>();
        public string[] ConfirmedSquares = new string[0];
        public string[] RcpwMembers;
        // The serializable numbers use the same order as RcpwMembers.
        public string[] RcpwMemberNumbers;
    }

    internal enum HamlogRule { Legacy, Grid, Stations, Bands, Tula, Tatarstan, Bryansk, Vysotsky, Marathon, Arck35, SkyHunter, EventStation, RcpwHunter, ContactPoints, Districts, RdaSearch }

    internal sealed class HamlogAward
    {
        internal static readonly HamlogAward Murom = new HamlogAward("Муромский сокол",
            "club/nebo/dc4b742d6f1c2c631f21703d0565cacf/", "2026-09-11", "", 85);
        internal static readonly HamlogAward Tula = new HamlogAward("Тула — 875 лет",
            "global/russia/r3p/a8052398c5047a367fe59da599b1818f/", "2021-09-01", "2021-09-30", 50);
        internal static readonly HamlogAward Russia = new HamlogAward("Моя Россия", "rrnars/", "2026-01-01", "", 750, HamlogRule.Grid);
        internal static readonly HamlogAward Gunsmiths = new HamlogAward("Легендарные оружейники Тулы",
            "global/russia/r3p/eb739f05ee1eb35b118a447552a8d0c2/", "2022-09-19", "", 100, HamlogRule.Tula);
        internal static readonly HamlogAward Tatarstan = new HamlogAward("Татарстан",
            "club/kazan/a7df3f8685baaaf26d8abf691e0d4cd7/", "2024-01-01", "", 50, HamlogRule.Tatarstan);
        internal static readonly HamlogAward Bryansk = new HamlogAward("Работал с R3Y",
            "club/r3ydosaaf/db8799094e17ba762616ac19f8252502/", "2002-06-12", "", 27, HamlogRule.Bryansk);
        internal static readonly HamlogAward Vysotsky = new HamlogAward("В. С. Высоцкому — 88 лет",
            "club/arck/f4001e43804b7424b605414bbeaddef8/", "2026-01-15", "2026-12-31", 88, HamlogRule.Vysotsky);
        internal static readonly HamlogAward Arck35 = new HamlogAward("ARCK — 35 лет",
            "club/arck/b57d5bd347637005b3ddfca5497cc969/", "2026-01-15", "2026-12-31", 35, HamlogRule.Arck35);
        internal static readonly HamlogAward Marathon = new HamlogAward("36 лет Miller-DX-Club",
            "club/mdxc/ba9d1e767d8d1afea1cd10c5f4ef9a1d/", "2026-11-16", "2026-11-30T21:00:00Z", 1990, HamlogRule.Marathon);
        internal static readonly HamlogAward Estonia4 = new HamlogAward("Северное побережье Эстонии (ES4)",
            "club/swfriends/a8b08bdaadd7c0021aa6c8e4ab9d5191/", "2021-12-09", "", 30, HamlogRule.Stations, "ES4");
        internal static readonly HamlogAward Estonia5 = new HamlogAward("5-й регион Эстонии (ES5)",
            "club/swfriends/b44b72619808961043250cb8d6267a7b/", "2019-05-26", "", 50, HamlogRule.Stations, "ES5");
        internal static readonly HamlogAward Romania = new HamlogAward("YP40CSM — 40 лет клуба CSM Bistrița",
            "club/romania/b61c03dc2a9c2f25db6dc450931e8908/", "2026-11-01", "2026-11-30", 1, HamlogRule.Stations, "YP40CSM");
        internal static readonly HamlogAward Foundation = Vk5Esm("Foundation", "e4a58b8314cbcd79cfccb8287f0ddd00", "80m 40m 15m 10m");
        internal static readonly HamlogAward Standard = Vk5Esm("Standard", "eb7f951b84bb0bba23fb8b1a97682801", "80m 40m 20m 15m 10m");
        internal static readonly HamlogAward Advanced = Vk5Esm("Advanced", "fb25c0f16b919c5dea0a4ba0d9a99f9e", "80m 40m 30m 20m 17m 15m 12m 10m");
        internal static readonly HamlogAward Warc = Vk5Esm("WARC", "c87e288d35a637c97959b983680df96b", "30m 17m 12m");
        internal static readonly HamlogAward[] Templates = {
            Russia, Gunsmiths, Tatarstan, Bryansk, Vysotsky, Arck35, Marathon, Estonia4, Estonia5, Romania,
            Foundation, Standard, Advanced, Warc, Murom, Tula
        };
        internal readonly string Title, Url, From, Until;
        internal readonly int Required;
        internal readonly HamlogRule Rule;
        internal readonly string Station;
        internal readonly string[] Bands;
        internal readonly HamlogRulePlan DocumentPlan;
        internal bool HasPointRules
        { get { return this == Murom || DocumentPlan != null && DocumentPlan.Points && !DocumentPlan.Districts && !DocumentPlan.Locators; } }
        internal bool HasOnlineRules { get { return Rule != HamlogRule.Legacy && !HasPointRules; } }
        internal string MonitoringRegion
        {
            get
            {
                if (Rule == HamlogRule.Bryansk) return "BR";
                if (Rule == HamlogRule.Tatarstan) return "TA";
                if (Rule == HamlogRule.Tula) return "TL";
                return DocumentPlan == null ? null : DocumentPlan.MonitoringRegion;
            }
        }
        internal bool GeographicSearch { get { return MonitoringRegion != null || DocumentPlan != null && DocumentPlan.GeographicSearch; } }

        internal bool Requires(int value)
        { return DocumentPlan != null ? DocumentPlan.Requires(value) : value == Required || Rule == HamlogRule.Vysotsky && value == 15; }

        HamlogAward(string title, string path, string from, string until, int required, HamlogRule rule = HamlogRule.Legacy,
            string station = "", string[] bands = null, HamlogRulePlan plan = null)
        {
            Title = title;
            Url = "https://hamlog.online/" + path;
            From = from;
            Until = until;
            Required = required;
            Rule = rule;
            Station = station;
            Bands = bands ?? new string[0];
            DocumentPlan = plan;
        }

        static HamlogAward Vk5Esm(string level, string id, string bands)
        {
            string[] list = bands.Split(' ');
            return new HamlogAward("VK5ESM — Worked All Bands (" + level + ")", "global/australia/vk5/" + id + "/",
                "2026-11-01", "", list.Length, HamlogRule.Bands, "VK5ESM", list);
        }

        internal static HamlogAward Find(string url)
        {
            if (String.IsNullOrEmpty(url)) return null;
            return Templates.FirstOrDefault(award => award.Url == url || award.HasOnlineRules && award.Url + "rules/" == url);
        }

        internal static HamlogAward FromDocument(AwardProgram program)
        {
            HamlogRulePlan plan = program.EffectiveRules;
            if (plan == null || String.IsNullOrEmpty(program.Url)) return null;
            HamlogRule rule = plan.Club ? HamlogRule.SkyHunter : plan.Rcpw ? HamlogRule.RcpwHunter
                : plan.RdaOnly ? HamlogRule.RdaSearch : plan.Districts ? HamlogRule.Districts : plan.Locators ? HamlogRule.Grid
                : plan.ContactAward ? HamlogRule.ContactPoints : HamlogRule.EventStation;
            return new HamlogAward(program.Title, new Uri(program.Url).AbsolutePath.TrimStart('/'), plan.FromUtc, plan.UntilUtc, plan.Required,
                rule, rule == HamlogRule.EventStation ? plan.Calls[0] : "", plan: plan);
        }

        internal AwardProgram Create()
        {
            var program = new AwardProgram { Title = Title, Url = Url };
            program.Validate();
            return program;
        }
    }

    internal static class HamlogStandingClient
    {
        static readonly Regex ResultsTable = new Regex(@"<table\b[^>]*class\s*=\s*[""'][^""']*\baward-results-table\b[^""']*[""'][^>]*>(.*?)</table>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        static readonly Regex Total = new Regex(@"<span\b[^>]*class\s*=\s*[""'][^""']*\baward-dashboard-value\b[^""']*[""'][^>]*>\s*(\d+(?:[.,]\d+)?)\s*/\s*(\d+)\s*</span>",
            RegexOptions.IgnoreCase);
        static readonly Regex AdditionalTotal = new Regex(@"<li\b[^>]*>\s*(?<kind>Required counties|Required station contacts)\s*:\s*(?<value>"
            + Total + ")", RegexOptions.IgnoreCase);
        static readonly Regex Body = new Regex(@"<tbody\b[^>]*>(.*?)</tbody>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        static readonly Regex Row = new Regex(@"<tr\b[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        static readonly Regex Cell = new Regex(@"<td\b[^>]*>(.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        static readonly Regex Tag = new Regex(@"<[^>]*>", RegexOptions.Singleline);
        static readonly Regex FtSubmode = new Regex(@"\A(?:MFSK|FT[48])\s*\(\s*(FT[48])\s*\)\z", RegexOptions.IgnoreCase);
        static readonly Regex Observer = new Regex(
            @"\A[A-Z0-9]{2,8}-(?:(?:[A-Z0-9]{1,8}-)?[0-9]{1,8}|[0-9]{1,8}-[A-Z]{2,3})\z", RegexOptions.CultureInvariant);
        static readonly string[] DateFormats = { "d MMM yyyy", "dd MMM yyyy", "d MMMM yyyy", "dd.MM.yyyy", "yyyy-MM-dd" };
        static readonly Regex Hunter = new Regex(@"<section\s+class=""rrnars-award-group""[^>]*>\s*<h5>\s*(?:Hunter|Охотник)\s*</h5>(.*?)</section>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        static readonly Regex Tier = new Regex(@"<div\s+class=""small mb-2""[^>]*>\s*(\d+)\s*/\s*(\d+)\s*</div>", RegexOptions.IgnoreCase);
        static readonly Regex SquareButton = new Regex(@"<button\b[^>]*\bdata-locator=""([A-R]{2}\d{2})""[^>]*\bdata-status=""(cfm|both)""",
            RegexOptions.IgnoreCase);
        const int MaximumPageBytes = 2 * 1024 * 1024;
        const int MaximumQsoSeriesPageBytes = 96 * 1024 * 1024;

        internal static HamlogStanding Read(HamlogAward award, string own)
        {
            own = Callsign.Require(own);
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var request = (HttpWebRequest)WebRequest.Create(award.Url);
            byte[] query = Encoding.ASCII.GetBytes("call=" + Uri.EscapeDataString(own));
            request.Method = "POST"; // Public callsign search, as on the award page.
            request.ContentType = "application/x-www-form-urlencoded";
            request.UserAgent = "JTDX-AutoCQ";
            request.Accept = "text/html";
            request.Headers[HttpRequestHeader.AcceptLanguage] = "en-US,en;q=0.9";
            request.Headers[HttpRequestHeader.CacheControl] = "no-cache";
            request.AllowAutoRedirect = false;
            request.Timeout = request.ReadWriteTimeout = 15000;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.ContentLength = query.Length;
            using (Stream form = request.GetRequestStream()) form.Write(query, 0, query.Length);
            HamlogStanding standing;
            int maximum = award.DocumentPlan != null && award.DocumentPlan.AllStations ? MaximumQsoSeriesPageBytes : MaximumPageBytes;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (Stream input = response.GetResponseStream())
            using (var output = new MemoryStream())
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new IOException("HAMLOG не вернул результат проверки.");
                var buffer = new byte[8192];
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (output.Length + count > maximum)
                        throw new InvalidDataException("Ответ HAMLOG слишком большой.");
                    output.Write(buffer, 0, count);
                }
                standing = Parse(Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length), award, own, DateTime.UtcNow);
            }
            if (award.DocumentPlan != null && award.DocumentPlan.UsesRcpwRoster)
            {
                try
                {
                    var members = HamlogRcpwRoster.Read().ToArray();
                    standing.RcpwMembers = members.Select(member => member.Key).ToArray();
                    standing.RcpwMemberNumbers = members.Select(member => member.Value).ToArray();
                }
                catch (WebException error) { throw new IOException("Не удалось загрузить список членов RCPW. " + error.Status + ".", error); }
            }
            return standing;
        }

        static decimal Number(string text)
        {
            decimal value;
            if (!Decimal.TryParse(text.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out value) || value < 0 || value > 1000000)
                throw new InvalidDataException("Не удалось прочитать очки на странице HAMLOG.");
            return value;
        }

        static string Text(string markup)
        { return Regex.Replace(WebUtility.HtmlDecode(Tag.Replace(markup, " ")), @"\s+", " ").Trim(); }

        static DateTime ContactDay(string text)
        {
            DateTime day;
            const DateTimeStyles style = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
            if (DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, style, out day)
                || DateTime.TryParseExact(text, DateFormats, CultureInfo.GetCultureInfo("ru-RU"), style, out day))
                return day.Date;
            throw new InvalidDataException("Не удалось прочитать дату связи на странице HAMLOG.");
        }

        internal static HamlogStanding Parse(string html, HamlogAward award, string own, DateTime checkedUtc)
        {
            own = Callsign.Require(own);
            if (!Regex.IsMatch(WebUtility.HtmlDecode(html), @"(?:Results of|Результаты(?: для)?)\s+" + Regex.Escape(own) + @"\s*</p>",
                    RegexOptions.IgnoreCase))
                throw new InvalidDataException("HAMLOG не вернул результат для " + own + ".");
            if (award.Rule == HamlogRule.Grid && award.DocumentPlan == null) return ParseSquares(html, award, own, checkedUtc);
            Match table = ResultsTable.Match(html);
            Group content = table.Groups[1];
            Match body = Body.Match(html, content.Index, content.Length);
            if (!body.Success) throw new InvalidDataException("На странице HAMLOG отсутствует таблица зачтённых связей.");
            var credits = new List<HamlogCredit>();
            HamlogRulePlan plan = award.DocumentPlan;
            bool series = plan != null && plan.AllStations;
            Group rows = body.Groups[1];
            for (Match row = Row.Match(html, rows.Index, rows.Length); row.Success; row = row.NextMatch())
            {
                Match[] cells = Cell.Matches(row.Groups[1].Value).Cast<Match>().ToArray();
                if (cells.Length != 8) throw new InvalidDataException("Изменился формат строк HAMLOG.");
                string accepted = cells[7].Groups[1].Value;
                if (Regex.IsMatch(accepted, @"\bfa-times(?:-circle)?\b")) continue;
                if (!Regex.IsMatch(accepted, @"\bfa-check(?:-circle)?\b"))
                    throw new InvalidDataException("Не удалось определить, зачтена ли связь в HAMLOG.");
                string[] values = cells.Select(cell => Text(cell.Groups[1].Value)).ToArray();
                if (values[6] == "-" && plan != null && (plan.RdaOnly || plan.Club && plan.NoMultipliers))
                    continue;
                // Some older logs use a backslash in a portable callsign or a district suffix in an SWL number.
                string rawCall = series ? values[0].Replace('\\', '/') : values[0];
                string call = Callsign.Parse(rawCall);
                string observer = series && call == null ? Regex.Replace(rawCall, @"/[0-9]{1,2}\z", "") : rawCall;
                bool observation = call == null && plan != null && Observer.IsMatch(observer)
                    && (plan.Club || plan.RdaOnly || plan.ContactAward && plan.Contacts.AcceptObserverCredit);
                string band = values[2].ToLowerInvariant();
                if (call == null && !observation || !JtdxAdif.BandNames.Contains(band) || String.IsNullOrEmpty(values[3]))
                    throw new InvalidDataException("Некорректная строка зачёта HAMLOG.");
                Match submode = FtSubmode.Match(values[3]);
                credits.Add(new HamlogCredit {
                    Call = call ?? values[0], DayUtc = ContactDay(values[1]), Band = band, Observation = observation,
                    Mode = (submode.Success ? submode.Groups[1].Value : values[3]).ToUpperInvariant(),
                    County = values[4].ToUpperInvariant(), Locator = AwardLocator.Parse(values[5]),
                    Points = (award.Rule == HamlogRule.Bryansk || award.Rule == HamlogRule.Districts) && values[6] == "-"
                        ? 1 : Number(values[6])
                });
            }
            var settings = award.DocumentPlan == null ? null : award.DocumentPlan.Contacts;
            Match[] additional = settings != null && settings.Additional.Length > 0
                ? AdditionalTotal.Matches(html).Cast<Match>().ToArray() : new Match[0];
            var totals = Total.Matches(html).Cast<Match>()
                .Where(match => !additional.Any(item => item.Groups["value"].Index == match.Index)).ToArray();
            Match total = totals.FirstOrDefault();
            bool provisional = total == null && award.DocumentPlan != null
                && award.DocumentPlan.AlternativeRequired > 0 && award.DocumentPlan.AlternativeRequired != award.Required;
            int required = total != null ? (int)Number(total.Groups[2].Value)
                : provisional ? award.DocumentPlan.AlternativeRequired : award.Required;
            decimal points = total != null ? Number(total.Groups[1].Value) : 0;
            if (plan != null && plan.MultiLevel && total == null && credits.Count == 0)
                required = series ? plan.AlternativeRequired : plan.Contacts.Levels.First();
            else if (plan != null && plan.MultiLevel)
            {
                int[] levels = totals.Select(match => (int)Number(match.Groups[2].Value)).OrderBy(level => level).ToArray();
                bool validLevels = series
                    ? levels.Length > 0 && levels.Length <= 128 && levels.Distinct().Count() == levels.Length && levels.All(award.Requires)
                    : levels.SequenceEqual(award.DocumentPlan.Contacts.Levels);
                if (!validLevels || totals.Any(match => Number(match.Groups[1].Value) != points))
                    throw new InvalidDataException("Степени или итоги зачёта HAMLOG отличаются от положения.");
                required = levels.FirstOrDefault(level => level > points);
                if (required == 0) required = levels.Last();
            }
            else if (totals.Length > 1
                || total == null && (award.HasOnlineRules || credits.Count != 0)
                || !award.Requires(required))
                throw new InvalidDataException("Схема зачёта HAMLOG отличается от положения этого диплома.");
            if (settings != null && settings.Additional.Length > 0 && (total != null || additional.Length != 0))
                VerifyAdditional(settings.Additional, additional, credits.ToArray());
            decimal pointsFromRows = credits.Sum(credit => credit.Points);
            if (settings != null && settings.MultiplyByCountyCount)
                pointsFromRows *= credits.Select(credit => credit.County).Where(county => settings.InCountyScope(county, true)).Distinct().Count();
            bool clubSummaryOmitsFraction = award.DocumentPlan != null && award.DocumentPlan.Club
                && points == Math.Floor(pointsFromRows);
            if (pointsFromRows != points && !clubSummaryOmitsFraction)
                throw new InvalidDataException("Очки в таблице HAMLOG не совпадают с итогом.");
            if (clubSummaryOmitsFraction) points = pointsFromRows;
            int rcpwAfterPoints = 0;
            if (award.Rule == HamlogRule.RcpwHunter)
            {
                DateTime? bonusUntil = AwardCatalog.ReadTime(plan.BonusUntilUtc);
                int factor = plan.RcpwFactor(own);
                var special = new HashSet<string>(plan.Calls, StringComparer.Ordinal);
                if (credits.Any(credit => !RcpwRateFits(credit, plan, bonusUntil, factor, special.Contains(credit.Call))))
                    throw new InvalidDataException("Очки за связи в HAMLOG отличаются от ставок в положении RCPW.");
                HamlogCredit recent = credits.Where(credit => (!bonusUntil.HasValue || credit.DayUtc >= bonusUntil.Value)
                    && !special.Contains(credit.Call))
                    .OrderByDescending(credit => credit.DayUtc).FirstOrDefault();
                rcpwAfterPoints = recent == null ? plan.MemberPoints : (int)recent.Points / factor;
            }
            if (award.Rule == HamlogRule.ContactPoints && !ContactPointsAgree(award.DocumentPlan, credits))
                throw new InvalidDataException("Очки за связи в HAMLOG отличаются от условий диплома.");
            if (award.Rule == HamlogRule.Districts)
            {
                var rule = award.DocumentPlan.Contacts;
                var counties = new HashSet<string>(StringComparer.Ordinal);
                if (credits.Any(credit => credit.Points != 1 || rule.DistrictCode(credit.Call, credit.County, allowRetired: true) == null
                    || !rule.AllowsBandMode(credit.Band, HamlogPointProgress.ModeGroup(credit.Mode))
                    || !counties.Add(credit.County)))
                    throw new InvalidDataException("Подтверждённые районы HAMLOG отличаются от условий диплома.");
            }
            string[] grids = new string[0];
            if (award.Rule == HamlogRule.Grid)
            {
                grids = credits.Select(credit => credit.Locator).Distinct().ToArray();
                if (grids.Length != credits.Count || grids.Any(grid => grid == null)
                    || credits.Any(credit => credit.Points != 1 || !settings.AllowsBandMode(credit.Band, HamlogPointProgress.ModeGroup(credit.Mode)))
                    || credits.GroupBy(credit => settings.ContactKey(credit.Call, credit.Band, credit.Mode)).Any(group => group.Count() > 1))
                    throw new InvalidDataException("Подтверждённые квадраты HAMLOG отличаются от условий диплома.");
            }
            return new HamlogStanding { Callsign = own, CheckedUtc = checkedUtc, Required = required,
                ProvisionalRequired = provisional, Points = points, Credits = credits, ConfirmedSquares = grids,
                RcpwAfterPoints = rcpwAfterPoints };
        }

        static bool RcpwRateFits(HamlogCredit credit, HamlogRulePlan plan, DateTime? until, int factor, bool special)
        {
            decimal rate = credit.Points / factor;
            // HAMLOG reports the day of the contact. Both rates may be valid on the boundary day.
            bool during = !until.HasValue || credit.DayUtc < until.Value;
            bool after = !until.HasValue || credit.DayUtc.AddDays(1) > until.Value;
            return during && rate == (special ? plan.RcpwSpecialPoints : plan.ExtraPoints)
                || after && (!special || plan.RcpwSpecialAfterActivity)
                    && (rate == plan.MemberPoints || plan.RcpwOnlineAfterPoints > 0 && rate == plan.RcpwOnlineAfterPoints);
        }

        static void VerifyAdditional(HamlogContactRequirement[] requirements, Match[] reported, HamlogCredit[] credits)
        {
            var required = requirements.Where(condition => condition.ReportedByHamlog).ToArray();
            if (required.Length != reported.Length)
                throw new InvalidDataException("В зачёте HAMLOG отсутствуют обязательные условия диплома.");
            foreach (HamlogContactRequirement condition in required)
            {
                string name = condition.Kind == HamlogContactRequirement.Counties ? "Required counties" : "Required station contacts";
                Match[] found = reported.Where(match => String.Equals(match.Groups["kind"].Value, name, StringComparison.OrdinalIgnoreCase)).ToArray();
                Match count = found.Length == 1 ? Total.Match(found[0].Groups["value"].Value) : null;
                if (count == null || Number(count.Groups[2].Value) != condition.Required)
                    throw new InvalidDataException("Число зачтённых обязательных связей или районов отличается от таблицы HAMLOG.");
                decimal reportedCount = Number(count.Groups[1].Value);
                if (reportedCount != condition.Count(credits) && !(condition.DistinctStations && reportedCount == credits
                    .Where(condition.Matches).Select(credit => condition.Station(credit.Call)).Distinct().Count()))
                    throw new InvalidDataException("Число зачтённых обязательных связей или районов отличается от таблицы HAMLOG.");
            }
        }

        static bool ContactPointsAgree(HamlogRulePlan plan, List<HamlogCredit> credits)
        {
            var criteria = plan.Contacts;
            DateTime? from = AwardCatalog.ReadTime(criteria.BonusFromUtc), until = AwardCatalog.ReadTime(criteria.BonusUntilUtc);
            return credits.All(credit => {
                if (!criteria.AllowsBandMode(credit.Band, HamlogPointProgress.ModeGroup(credit.Mode))) return false;
                if (criteria.AllStations) return credit.Points == plan.MemberPoints;
                if (criteria.MultiplyByCountyCount)
                    return credit.Points == plan.MemberPoints && (String.IsNullOrEmpty(credit.County)
                        || criteria.DistrictCode(credit.Call, credit.County, allowRetired: true) != null);
                if (criteria.RestrictsCounties && criteria.DistrictCode(credit.Call, credit.County, allowRetired: true) == null) return false;
                string call = criteria.NamedCall(credit.Call);
                bool named = plan.Calls.Contains(call);
                bool couldBeBonus = named && (!from.HasValue || credit.DayUtc.AddDays(1) > from && credit.DayUtc < until);
                bool onlyBonus = named && (!from.HasValue || credit.DayUtc >= from && credit.DayUtc.AddDays(1) <= until);
                HamlogBonusGroup namedGroup = plan.BonusGroups.FirstOrDefault(group => !group.Area && group.Targets.Contains(call));
                return (couldBeBonus && criteria.MatchesPoints(plan.ExtraPoints, credit))
                    || (!onlyBonus && namedGroup != null && criteria.MatchesPoints(namedGroup.Points, credit))
                    || (!onlyBonus && namedGroup == null && (criteria.Countries.Length != 0 || !String.IsNullOrEmpty(criteria.Roster)
                        || criteria.RestrictsCounties) && criteria.MatchesPoints(plan.MemberPoints, credit))
                    || (!onlyBonus && namedGroup == null && plan.BonusGroups.Any(group => group.Area
                        && (!group.Targets.All(HamlogCountyIndex.IsCounty) || group.Targets.Contains(credit.County))
                        && criteria.MatchesPoints(group.Points, credit)));
            });
        }

        static HamlogStanding ParseSquares(string html, HamlogAward award, string own, DateTime checkedUtc)
        {
            Match hunter = Hunter.Match(html);
            var values = Tier.Matches(hunter.Groups[1].Value).Cast<Match>().Select(match => new {
                Points = (int)Number(match.Groups[1].Value), Required = (int)Number(match.Groups[2].Value)
            }).ToArray();
            string[] squares = SquareButton.Matches(html).Cast<Match>()
                .Select(match => match.Groups[1].Value.ToUpperInvariant()).Distinct().ToArray();
            if (values.Length == 0 || values.Max(value => value.Required) != award.Required
                || values.Any(value => value.Points != squares.Length))
                throw new InvalidDataException("Список подтверждённых квадратов HAMLOG не совпадает с итогом.");
            int required = values.Where(value => value.Required > squares.Length).Select(value => value.Required)
                .DefaultIfEmpty(award.Required).Min();
            return new HamlogStanding {
                Callsign = own, CheckedUtc = checkedUtc, Required = required, Points = squares.Length, ConfirmedSquares = squares
            };
        }
    }

    internal sealed class HamlogPointProgress
    {
        // Public roster of Knights of the Sky, checked on 2026-11-23.
        // Calls separated by spaces in the same entry have one membership number.
        // https://www.skyham.ru/spisok-kluba.htm
        const string Members =
            "EW1D|R4CQ|EV6C|RA6F|EW8LL|UT5RL|UZ9RR USR55|R5GG|RY1A|EW3FH|EW3WF|EU1XD|RW4CCW|R2AKN|" +
            "F5PBL|RD4CAF|EW2CQ|EW8ZM|EV6W|RA3QH|OK1MBR|RZ6AIJ|UX8IX|UX8IXX|LW2EIY|U4MIR|YU1SU|R4CGP|RC4P|" +
            "EU7DD|RA3TD|UR1MI|YU1TY|PY1AX|UB3YCJ|RK4CL|EW3ADK|EU1ZZ|R4DL|UA9CES|" +
            "LZ1ZF AC1ZF LZ9Z LZ77ZZ LZ44WFF LZ25MIG LZ78ZZ LZ21MIG|US2MT|LZ1KSZ|US1EA|RA3RPW|LZ1KGR|LZ3DC|" +
            "RZ6AKK|RY6AAC|BD7NSK|UA4PIE|RU3GC|R0QAF|R4CGQ|UB9UZA|YL2AG|OE6VKO|UB6HQQ|R3AP|RA5AD|" +
            "R8FF R8FF/1 R8FF/3 R8FF/6 R8FF/7 R8FF/8 R8FF/M R8FF/P|RA3SO|UA4NV|R5AV|RV1A|LZ1ON|RW9CC|" +
            "UB4CIA|RA9ODW|RA4DAB|SV1QVA|UD6AXJ|UB3MBD";
        static readonly Dictionary<string, string> Membership = Members.Split('|')
            .SelectMany(group => group.Split(' ').Select(call => new { Call = call, Member = group.Split(' ')[0] }))
            .ToDictionary(entry => entry.Call, entry => entry.Member, StringComparer.Ordinal);
        internal static IEnumerable<string> SkyCallsigns { get { return Membership.Keys; } }
        static readonly HashSet<string> Bonus = new HashSet<string> {
            "U4MIR", "UA9CES", "RW9CC", "R2AKN", "R4CQ", "RA3TD", "RA6F", "LZ1ZF", "AC1ZF", "LZ07RN"
        };
        static readonly Regex Digital = new Regex(@"^(?:DIGI(?:TAL)?|FT[48]|JT\w+|Q65|FST4\w*|MSK144|JS8|RTTY\w*|PSK\w*|MFSK\w*|OLIVIA|CONTESTI|VARA\w*|HELL\w*|SSTV|THOR\w*|DOMINO\w*|ROS|PACKET|PKT|AMTOR\w*|MT63|FSK\w*|WSPR)$",
            RegexOptions.CultureInvariant);
        static readonly HashSet<string> IneligibleBands = new HashSet<string> { "2190m", "630m", "560m" };
        static readonly HashSet<string> SkyHf = new HashSet<string>("160m 80m 60m 40m 30m 20m 17m 15m 12m 10m".Split(' '));
        static readonly HashSet<string> SkyVhf = new HashSet<string> { "6m", "4m", "2m" };
        readonly DateTime? from, until, creditFrom, bonusFrom, bonusUntil;
        readonly HashSet<string> recorded = new HashSet<string>(StringComparer.Ordinal);
        readonly List<HamlogCredit> pending = new List<HamlogCredit>();
        readonly string own;
        readonly bool logbookAvailable;
        readonly HamlogRulePlan plan;
        readonly HashSet<string> bonus;
        readonly HashSet<string> rcpwMembers;
        readonly Dictionary<string, string> rcpwMemberNumbers;
        readonly Dictionary<string, int> namedBonuses = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Dictionary<string, int> areaBonuses = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Func<string, string> dxcc;
        readonly bool rcpw;
        readonly HamlogContactCriteria contacts;
        readonly HamlogCountyIndex counties;
        readonly HamlogRequirementProgress[] requirements;
        readonly HashSet<string> multiplierCounties;
        readonly decimal confirmedContactPoints;
        readonly Dictionary<DateTime, int> shortcutDays;
        readonly int shortcutDay;
        readonly int rcpwFactor;
        bool projectedShortcut;
        decimal pendingContactPoints;
        static readonly HashSet<string> RussianEntities = new HashSet<string> { "UA", "UA2", "UA9", "R1FJ" };
        bool EveryQso { get { return contacts != null && contacts.Repeat == HamlogContactCriteria.EveryQso; } }

        internal readonly HamlogStanding Standing;
        internal readonly int Required;
        internal readonly bool Closed;
        internal readonly bool IsMember;
        internal int ConfirmedMissingFromLogbook { get; private set; }
        internal IReadOnlyList<HamlogCredit> Pending { get { return pending; } }
        internal decimal PendingPoints { get; private set; }
        internal int ConfirmedCountyMultiplier { get; private set; }
        internal decimal NeedOnline { get { return Math.Max(0, Required - (Standing == null ? 0 : Standing.Points)); } }
        internal readonly bool ConfirmedShortcut;
        internal bool ConfirmedComplete
        { get { return Standing != null && (ConfirmedShortcut || NeedOnline == 0 && requirements.All(requirement => requirement.Confirmed >= requirement.Required)); } }
        internal bool PendingEnough
        { get { return Standing != null && !ConfirmedComplete && (projectedShortcut || PendingPoints >= NeedOnline && requirements.All(requirement => requirement.Enough)); } }
        internal bool CanSearch { get { return Standing != null && logbookAvailable && !IsMember && !Closed && !ConfirmedComplete && !PendingEnough; } }
        internal IReadOnlyList<HamlogRequirementProgress> Additional { get { return requirements; } }

        internal HamlogPointProgress(AwardProgram program, LogbookHistory history, string own, Func<string, string> dxcc = null)
        {
            this.own = Callsign.Clean(own);
            this.dxcc = dxcc;
            logbookAvailable = history.Path != null;
            var template = program.Template;
            plan = template.DocumentPlan;
            contacts = plan != null && plan.ContactAward ? plan.Contacts : null;
            bonus = plan == null ? Bonus : new HashSet<string>(plan.Calls, StringComparer.Ordinal);
            rcpw = plan != null && plan.Rcpw;
            rcpwFactor = rcpw ? plan.RcpwFactor(this.own) : 1;
            if (contacts != null && contacts.ShortcutContacts > 0)
            {
                shortcutDay = HamlogCalendar.MonthDay(contacts.ShortcutDay);
                shortcutDays = new Dictionary<DateTime, int>();
            }
            bonusFrom = contacts == null ? null : AwardCatalog.ReadTime(contacts.BonusFromUtc);
            bonusUntil = contacts != null ? AwardCatalog.ReadTime(contacts.BonusUntilUtc)
                : rcpw ? AwardCatalog.ReadTime(plan.BonusUntilUtc) : null;
            from = program.From;
            creditFrom = contacts != null && contacts.CreditBeforePublication ? AwardCatalog.ReadTime(plan.FromUtc) : from;
            until = program.Until;
            Closed = !template.HasPointRules;
            Standing = program.Hamlog;
            bool needsRcpw = plan != null && plan.UsesRcpwRoster;
            if (Standing != null && (Standing.Callsign != this.own || !template.Requires(Standing.Required) || Standing.Credits == null
                || needsRcpw && (Standing.RcpwMembers == null || Standing.RcpwMembers.Length == 0)))
                Standing = null;
            Required = Standing == null ? template.Required : Standing.Required;
            requirements = contacts != null && Standing != null
                ? contacts.Additional.Select(rule => new HamlogRequirementProgress(rule, Standing.Credits)).ToArray()
                : new HamlogRequirementProgress[0];
            if (contacts != null && Standing != null && (contacts.RestrictsCounties || contacts.Additional.Any(condition => condition.ByCounty)
                || plan.BonusGroups.Any(group => group.Area && group.Targets.Any(HamlogCountyIndex.IsCounty))))
                counties = new HamlogCountyIndex();
            if (contacts != null && contacts.MultiplyByCountyCount && Standing != null)
            {
                multiplierCounties = new HashSet<string>(Standing.Credits.Select(credit => credit.County)
                    .Where(county => contacts.InCountyScope(county, true)), StringComparer.Ordinal);
                ConfirmedCountyMultiplier = multiplierCounties.Count;
                confirmedContactPoints = Standing.Credits.Sum(credit => credit.Points);
            }
            if (needsRcpw && Standing != null) rcpwMembers = new HashSet<string>(Standing.RcpwMembers, StringComparer.Ordinal);
            if (contacts == null)
                IsMember = !rcpw && !(plan != null && plan.ClubMembersMayHunt) && Membership.ContainsKey(this.own)
                    || plan != null && plan.Club && dxcc != null && plan.ClubOnlyActivatorAreas.Any(area =>
                        HamlogAwardAreas.Matches(area, this.own, dxcc(this.own)));
            else
            {
                IsMember = contacts.ActivatorRoster && ContactMember(this.own, dxcc == null ? null : dxcc(this.own))
                    || contacts.IsActivatorArea(this.own, dxcc);
            }
            if (contacts != null && contacts.Roster == "rcpw" && Standing != null && Standing.RcpwMemberNumbers != null
                && Standing.RcpwMemberNumbers.Length == Standing.RcpwMembers.Length)
                rcpwMemberNumbers = Standing.RcpwMembers.Zip(Standing.RcpwMemberNumbers, (call, number) => new { Call = call, Number = number })
                    .GroupBy(member => member.Call, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First().Number, StringComparer.Ordinal);
            if (plan != null)
                foreach (HamlogBonusGroup group in plan.BonusGroups ?? new HamlogBonusGroup[0])
                    foreach (string target in group.Targets)
                        (group.Area ? areaBonuses : namedBonuses)[target] = group.Points;
            var logbookKeys = new HashSet<string>(StringComparer.Ordinal);
            var creditedByDay = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (LogbookQso row in history.Records)
            {
                DateTime? date = row.StartedUtc ?? row.EndedUtc;
                if (counties != null && row.BelongsTo(this.own)) counties.Add(row.Call, row.Locator, row.County);
                if (date.HasValue && WithinCredit(date.Value) && row.BelongsTo(this.own))
                    logbookKeys.Add(ContactKey(row.Call, row.Band, row.Mode, date.Value));
            }
            if (Standing != null)
            {
                foreach (HamlogCredit credit in Standing.Credits)
                {
                    if (counties != null) counties.Add(credit.Call, credit.Locator, credit.County);
                    if (credit.Observation) continue;
                    RecordShortcut(credit.DayUtc);
                    if (EveryQso)
                    {
                        string key = CountKey(credit.Call, credit.Band, credit.Mode, credit.DayUtc);
                        int count;
                        creditedByDay.TryGetValue(key, out count);
                        creditedByDay[key] = count + 1;
                        continue;
                    }
                    recorded.Add(Key(credit.Call, credit.Band, credit.Mode, credit.DayUtc));
                    if (contacts != null && contacts.RepeatDayOffsetMinutes != 0)
                    {
                        // A date-only row may belong to either local day when the repeat ends outside UTC midnight.
                        int offset = contacts.RepeatDayOffsetMinutes;
                        recorded.Add(Key(credit.Call, credit.Band, credit.Mode,
                            offset > 0 ? credit.DayUtc.AddDays(1).AddTicks(-1) : credit.DayUtc.AddMinutes(-offset)));
                    }
                    if (!logbookKeys.Contains(ContactKey(credit.Call, credit.Band, credit.Mode, credit.DayUtc)))
                        ConfirmedMissingFromLogbook++;
                }
            }
            ConfirmedShortcut = projectedShortcut;
            if (Closed || IsMember || Standing == null) return;
            foreach (LogbookQso row in history.Records.OrderBy(row => row.StartedUtc ?? row.EndedUtc))
            {
                DateTime? date = row.StartedUtc ?? row.EndedUtc;
                if (!date.HasValue || !row.BelongsTo(this.own)) continue;
                string county = CountyFor(row.Call, row.Locator, row.County);
                if (EveryQso && WithinCredit(date.Value) && Credit(row.Call, row.Band, row.Mode, date.Value, county) > 0)
                {
                    string key = CountKey(row.Call, row.Band, row.Mode, date.Value);
                    int count;
                    if (creditedByDay.TryGetValue(key, out count) && count > 0)
                    {
                        if (recorded.Add(Key(row.Call, row.Band, row.Mode, date.Value))) creditedByDay[key] = count - 1;
                        continue;
                    }
                }
                Record(row.Call, row.Band, row.Mode, date.Value, row.County, row.Locator);
            }
            if (EveryQso) ConfirmedMissingFromLogbook = creditedByDay.Values.Sum();
        }

        internal bool Within(DateTime time)
        { return (!from.HasValue || time >= from.Value) && (!until.HasValue || time < until.Value); }

        bool WithinCredit(DateTime time)
        { return (!creditFrom.HasValue || time >= creditFrom.Value) && (!until.HasValue || time < until.Value); }

        internal static string ModeGroup(string mode)
        {
            mode = (mode ?? "").ToUpperInvariant();
            int submode = mode.IndexOf(" (", StringComparison.Ordinal);
            if (submode > 0) mode = mode.Substring(0, submode);
            if (Digital.IsMatch(mode)) return "DIGI";
            if (mode == "USB" || mode == "LSB" || mode == "PHONE" || mode == "PH" || mode == "AM" || mode == "FM") return "SSB";
            return mode;
        }

        string Key(string call, string band, string mode, DateTime time)
        {
            if (contacts != null)
            {
                call = contacts.NamedCall(call);
                const string divider = "|";
                switch (contacts.Repeat)
                {
                    case HamlogContactCriteria.Call:
                        string memberNumber;
                        return rcpwMemberNumbers != null && rcpwMemberNumbers.TryGetValue(call, out memberNumber)
                            && !String.IsNullOrEmpty(memberNumber) ? "#" + memberNumber : call;
                    case HamlogContactCriteria.Band: return call + divider + band;
                    case HamlogContactCriteria.BandMode: return contacts.ContactKey(call, band, mode);
                    case HamlogContactCriteria.Day: return call + divider + time.AddMinutes(contacts.RepeatDayOffsetMinutes).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                    case HamlogContactCriteria.DayBandMode: return CountKey(call, band, mode, time.AddMinutes(contacts.RepeatDayOffsetMinutes));
                    case HamlogContactCriteria.EveryQso: return CountKey(call, band, mode, time) + divider + time.Ticks;
                }
            }
            if (rcpw) return call + "|" + band + "|" + ModeGroup(mode);
            string member;
            if (!Membership.TryGetValue(call, out member)) member = call;
            return ContactKey(member, band, mode, time);
        }

        string RepeatMode(string mode) { return contacts.GroupModes ? ModeGroup(mode) : (mode ?? "").ToUpperInvariant(); }
        string CountKey(string call, string band, string mode, DateTime time)
        { return call + "|" + time.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "|" + band + "|" + RepeatMode(mode); }

        static string ContactKey(string call, string band, string mode, DateTime time)
        { return call + "|" + time.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "|" + band + "|" + ModeGroup(mode); }

        bool OnBand(string band, string group)
        {
            if (contacts != null)
                return contacts.AllowsBandMode(band, group);
            return !String.IsNullOrEmpty(band) && !IneligibleBands.Contains(band) && JtdxAdif.BandNames.Contains(band)
                && (group == "DIGI" || group == "SSB" || group == "CW")
                && (plan == null || rcpw || SkyHf.Contains(band) || !plan.HfOnly && SkyVhf.Contains(band));
        }

        string CountyFor(string call, string grid, string county = null)
        {
            if (counties == null) return null;
            if (String.IsNullOrEmpty(county)) county = counties.Find(call, grid);
            return contacts.RestrictsCounties && !contacts.MultiplyByCountyCount ? contacts.DistrictCode(call, county)
                : HamlogCountyIndex.IsCounty(county) && !call.EndsWith("/AM", StringComparison.Ordinal) ? county : null;
        }

        decimal Credit(string call, string band, string mode, DateTime time, string county = null)
        {
            string group = ModeGroup(mode);
            if (!OnBand(band, group)) return 0;
            if (contacts != null)
            {
                if (contacts.RestrictsCounties && !contacts.MultiplyByCountyCount && county == null) return 0;
                return contacts.Adjust(ContactCredit(call, time, county), band, time, call);
            }
            if (rcpw)
            {
                bool special = bonus.Contains(call);
                bool activity = bonusUntil.HasValue && time < bonusUntil.Value;
                if (special ? !activity && !plan.RcpwSpecialAfterActivity : !rcpwMembers.Contains(call)) return 0;
                int rate = activity ? (special ? plan.RcpwSpecialPoints : plan.ExtraPoints)
                    : Standing.RcpwAfterPoints > 0 ? Standing.RcpwAfterPoints : plan.MemberPoints;
                return rate * rcpwFactor;
            }
            int points;
            if (bonus.Contains(call)) points = plan == null ? 10 : plan.ExtraPoints;
            else if (!namedBonuses.TryGetValue(call, out points))
            {
                if (Membership.ContainsKey(call)) points = plan == null ? 6 : plan.MemberPoints;
                if (areaBonuses.Count != 0 && dxcc != null)
                {
                    string country = dxcc(call);
                    foreach (KeyValuePair<string, int> area in areaBonuses)
                        if (HamlogAwardAreas.Matches(area.Key, call, country)) points = Math.Max(points, area.Value);
                }
            }
            if (plan != null && plan.NoMultipliers) return points;
            bool bandBonus = band == "160m" && !(plan != null && plan.ClubHamlogNo160mBonus)
                || plan != null && plan.VhfBonus && SkyVhf.Contains(band);
            bool onlineModeRates = plan != null && plan.ClubHamlogModeRates;
            decimal modeFactor = group == "CW" ? 2m : (onlineModeRates ? group == "DIGI" : group == "SSB") ? 1.5m : 1m;
            return points * modeFactor * (bandBonus ? 1.5m : 1m);
        }

        int ContactCredit(string call, DateTime time, string county)
        {
            if (contacts.AllStations) return plan.MemberPoints;
            if (contacts.MultiplyByCountyCount && county != null)
                return contacts.InCountyScope(county, false) ? plan.MemberPoints : 0;
            string baseCall = contacts.NamedCall(call);
            if (bonus.Contains(baseCall) && (!bonusFrom.HasValue || time >= bonusFrom.Value && time < bonusUntil.Value))
                return plan.ExtraPoints;
            int points;
            if (namedBonuses.TryGetValue(baseCall, out points)) return points;
            string country = dxcc == null ? null : dxcc(call);
            points = contacts.RestrictsCounties && !contacts.MultiplyByCountyCount || contacts.Countries.Contains(country)
                || ContactMember(call, country) ? plan.MemberPoints : 0;
            foreach (KeyValuePair<string, int> area in areaBonuses)
                if (HamlogAwardAreas.Matches(area.Key, call, country, county, contacts.PortableRegionalCalls))
                    points = Math.Max(points, area.Value);
            return points;
        }

        bool ContactMember(string call, string country)
        {
            HashSet<string> roster = ContactRoster();
            return (roster.Contains(call) || contacts.PortableAliases && roster.Contains(contacts.NamedCall(call)))
                && (contacts.Roster != "arck-dx" || country != null && !RussianEntities.Contains(country));
        }

        HashSet<string> ContactRoster()
        {
            return (contacts.Roster == "rcpw" ? rcpwMembers : HamlogAwardRosters.For(contacts.Roster)) ?? EmptyRoster;
        }
        static readonly HashSet<string> EmptyRoster = new HashSet<string>(StringComparer.Ordinal);

        internal decimal CandidatePoints(string call, string band, string mode, DateTime time, string grid = null)
        {
            if (!CanSearch || !Within(time) || call == own || recorded.Contains(Key(call, band, mode, time))) return 0;
            string county = CountyFor(call, grid);
            if (PendingPoints >= NeedOnline && !requirements.Any(requirement => requirement.Needs(call, county))) return 0;
            decimal points = Credit(call, band, mode, time, county);
            if (multiplierCounties == null || points == 0) return points;
            int addedCounty = county != null && !multiplierCounties.Contains(county) ? 1 : 0;
            return (confirmedContactPoints + pendingContactPoints + points) * (multiplierCounties.Count + addedCounty)
                - Standing.Points - PendingPoints;
        }

        internal bool Available(string band, string mode, DateTime time)
        {
            if (!CanSearch || !Within(time) || !OnBand(band, ModeGroup(mode))) return false;
            if (contacts != null)
            {
                if (contacts.AllStations || contacts.RestrictsCounties || contacts.Countries.Length != 0 || areaBonuses.Count != 0) return true;
                return bonus.Concat(namedBonuses.Keys).Concat(ContactRoster()).Any(call => CandidatePoints(call, band, mode, time) > 0);
            }
            if (areaBonuses.Count != 0) return true;
            IEnumerable<string> candidates = rcpw ? rcpwMembers.Concat(bonus) : Membership.Keys.Concat(bonus).Concat(namedBonuses.Keys);
            return candidates.Any(call => CandidatePoints(call, band, mode, time) > 0);
        }

        internal void Record(string call, string band, string mode, DateTime time, string county = null, string locator = null)
        {
            if (Closed || IsMember || Standing == null || !WithinCredit(time) || call == own) return;
            if (counties != null) counties.Add(call, locator, county);
            county = CountyFor(call, locator, county);
            decimal points = Credit(call, band, mode, time, county);
            if (points == 0 || !recorded.Add(Key(call, band, mode, time))) return;
            RecordShortcut(time);
            var credit = new HamlogCredit { Call = call, DayUtc = time.Date, Band = band, Mode = mode, Points = points,
                County = county, Locator = AwardLocator.Parse(locator) };
            pending.Add(credit);
            foreach (HamlogRequirementProgress requirement in requirements) requirement.Record(credit);
            if (multiplierCounties == null) PendingPoints += points;
            else
            {
                if (county != null) multiplierCounties.Add(county);
                pendingContactPoints += points;
                PendingPoints = (confirmedContactPoints + pendingContactPoints) * multiplierCounties.Count - Standing.Points;
            }
        }

        void RecordShortcut(DateTime at)
        {
            if (shortcutDays == null || !WithinCredit(at) || HamlogCalendar.MonthDay(at) != shortcutDay) return;
            int count;
            shortcutDays.TryGetValue(at.Date, out count);
            shortcutDays[at.Date] = ++count;
            if (count >= contacts.ShortcutContacts) projectedShortcut = true;
        }
    }
}
