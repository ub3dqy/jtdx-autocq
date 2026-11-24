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
        public List<HamlogCredit> Credits = new List<HamlogCredit>();
        public string[] ConfirmedSquares = new string[0];
        public string[] RcpwMembers;
    }

    internal enum HamlogRule { Legacy, Grid, Stations, Bands, Tula, Tatarstan, Bryansk, Vysotsky, Marathon, Arck35, SkyHunter, EventStation, RcpwHunter, ContactPoints }

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
        internal bool HasPointRules { get { return this == Murom || DocumentPlan != null && DocumentPlan.Points; } }
        internal bool HasOnlineRules { get { return Rule != HamlogRule.Legacy && !HasPointRules; } }

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
                : plan.ContactAward ? HamlogRule.ContactPoints : HamlogRule.EventStation;
            return new HamlogAward(program.Title, new Uri(program.Url).AbsolutePath.TrimStart('/'), plan.FromUtc, plan.UntilUtc, plan.Required,
                rule, plan.Points ? "" : plan.Calls[0], plan: plan);
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
        static readonly Regex Body = new Regex(@"<tbody\b[^>]*>(.*?)</tbody>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        static readonly Regex Row = new Regex(@"<tr\b[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        static readonly Regex Cell = new Regex(@"<td\b[^>]*>(.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        static readonly Regex Tag = new Regex(@"<[^>]*>", RegexOptions.Singleline);
        static readonly Regex FtSubmode = new Regex(@"\A(?:MFSK|FT[48])\s*\(\s*(FT[48])\s*\)\z", RegexOptions.IgnoreCase);
        static readonly Regex Observer = new Regex(@"\A[A-Z0-9]{2,8}(?:-[A-Z0-9]{1,8})?-[0-9]{1,8}\z", RegexOptions.CultureInvariant);
        static readonly string[] DateFormats = { "d MMM yyyy", "dd MMM yyyy", "d MMMM yyyy", "dd.MM.yyyy", "yyyy-MM-dd" };
        static readonly Regex Hunter = new Regex(@"<section\s+class=""rrnars-award-group""[^>]*>\s*<h5>\s*(?:Hunter|Охотник)\s*</h5>(.*?)</section>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        static readonly Regex Tier = new Regex(@"<div\s+class=""small mb-2""[^>]*>\s*(\d+)\s*/\s*(\d+)\s*</div>", RegexOptions.IgnoreCase);
        static readonly Regex SquareButton = new Regex(@"<button\b[^>]*\bdata-locator=""([A-R]{2}\d{2})""[^>]*\bdata-status=""(cfm|both)""",
            RegexOptions.IgnoreCase);
        const int MaximumPageBytes = 2 * 1024 * 1024;

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
                    if (output.Length + count > MaximumPageBytes)
                        throw new InvalidDataException("Ответ HAMLOG слишком большой.");
                    output.Write(buffer, 0, count);
                }
                standing = Parse(Encoding.UTF8.GetString(output.ToArray()), award, own, DateTime.UtcNow);
            }
            if (award.DocumentPlan != null && award.DocumentPlan.UsesRcpwRoster)
            {
                try { standing.RcpwMembers = HamlogRcpwRoster.Read(); }
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
            if (award.Rule == HamlogRule.Grid) return ParseSquares(html, award, own, checkedUtc);
            Match table = ResultsTable.Match(html);
            Match body = Body.Match(table.Groups[1].Value);
            if (!body.Success) throw new InvalidDataException("На странице HAMLOG отсутствует таблица зачтённых связей.");
            var credits = new List<HamlogCredit>();
            foreach (Match row in Row.Matches(body.Groups[1].Value))
            {
                Match[] cells = Cell.Matches(row.Groups[1].Value).Cast<Match>().ToArray();
                if (cells.Length != 8) throw new InvalidDataException("Изменился формат строк HAMLOG.");
                string accepted = cells[7].Groups[1].Value;
                if (Regex.IsMatch(accepted, @"\bfa-times(?:-circle)?\b")) continue;
                if (!Regex.IsMatch(accepted, @"\bfa-check(?:-circle)?\b"))
                    throw new InvalidDataException("Не удалось определить, зачтена ли связь в HAMLOG.");
                string[] values = cells.Select(cell => Text(cell.Groups[1].Value)).ToArray();
                string call = Callsign.Parse(values[0]);
                HamlogRulePlan plan = award.DocumentPlan;
                bool observation = call == null && plan != null && Observer.IsMatch(values[0])
                    && (plan.Club || plan.ContactAward && plan.Contacts.AcceptObserverCredit);
                string band = values[2].ToLowerInvariant();
                if (call == null && !observation || !JtdxAdif.BandNames.Contains(band) || String.IsNullOrEmpty(values[3]))
                    throw new InvalidDataException("Некорректная строка зачёта HAMLOG.");
                if (values[6] == "-" && award.DocumentPlan != null && award.DocumentPlan.Club && award.DocumentPlan.NoMultipliers)
                    continue;
                Match submode = FtSubmode.Match(values[3]);
                credits.Add(new HamlogCredit {
                    Call = call ?? values[0], DayUtc = ContactDay(values[1]), Band = band, Observation = observation,
                    Mode = (submode.Success ? submode.Groups[1].Value : values[3]).ToUpperInvariant(),
                    County = values[4].ToUpperInvariant(), Locator = AwardLocator.Parse(values[5]),
                    Points = award.Rule == HamlogRule.Bryansk && values[6] == "-" ? 1 : Number(values[6])
                });
            }
            Match total = Total.Match(html);
            bool provisional = !total.Success && award.DocumentPlan != null
                && award.DocumentPlan.AlternativeRequired > 0 && award.DocumentPlan.AlternativeRequired != award.Required;
            int required = total.Success ? (int)Number(total.Groups[2].Value)
                : provisional ? award.DocumentPlan.AlternativeRequired : award.Required;
            decimal points = total.Success ? Number(total.Groups[1].Value) : 0;
            if (award.DocumentPlan != null && award.DocumentPlan.MultiLevel)
            {
                Match[] totals = Total.Matches(html).Cast<Match>().ToArray();
                int[] levels = totals.Select(match => (int)Number(match.Groups[2].Value)).OrderBy(level => level).ToArray();
                if (!levels.SequenceEqual(award.DocumentPlan.Contacts.Levels) || totals.Any(match => Number(match.Groups[1].Value) != points))
                    throw new InvalidDataException("Степени или итоги зачёта HAMLOG отличаются от положения.");
                required = levels.Where(level => level > points).DefaultIfEmpty(award.Required).Min();
            }
            else if (total.Success && total.NextMatch().Success
                || !total.Success && (award.HasOnlineRules || credits.Count != 0)
                || !award.Requires(required))
                throw new InvalidDataException("Схема зачёта HAMLOG отличается от положения этого диплома.");
            if (credits.Sum(credit => credit.Points) != points)
                throw new InvalidDataException("Очки в таблице HAMLOG не совпадают с итогом.");
            if (award.Rule == HamlogRule.RcpwHunter && credits.Count != 0)
            {
                DateTime? bonusUntil = AwardCatalog.ReadTime(award.DocumentPlan.BonusUntilUtc);
                if (credits.Any(credit => credit.Points != (credit.DayUtc < bonusUntil
                    ? award.DocumentPlan.ExtraPoints : award.DocumentPlan.MemberPoints)))
                    throw new InvalidDataException("Очки за связи в HAMLOG отличаются от ставок в положении RCPW.");
            }
            if (award.Rule == HamlogRule.ContactPoints && !ContactPointsAgree(award.DocumentPlan, credits))
                throw new InvalidDataException("Очки за связи в HAMLOG отличаются от условий диплома.");
            return new HamlogStanding { Callsign = own, CheckedUtc = checkedUtc, Required = required,
                ProvisionalRequired = provisional, Points = points, Credits = credits };
        }

        static bool ContactPointsAgree(HamlogRulePlan plan, List<HamlogCredit> credits)
        {
            var criteria = plan.Contacts;
            DateTime? from = AwardCatalog.ReadTime(criteria.BonusFromUtc), until = AwardCatalog.ReadTime(criteria.BonusUntilUtc);
            return credits.All(credit => {
                string mode = HamlogPointProgress.ModeGroup(credit.Mode);
                if (criteria.Modes.Length != 0 && !criteria.Modes.Contains(mode)
                    || criteria.Bands.Length != 0 && !criteria.Bands.Contains(credit.Band)) return false;
                bool named = plan.Calls.Contains(criteria.NamedCall(credit.Call));
                bool couldBeBonus = named && (!from.HasValue || credit.DayUtc.AddDays(1) > from && credit.DayUtc < until);
                bool onlyBonus = named && (!from.HasValue || credit.DayUtc >= from && credit.DayUtc.AddDays(1) <= until);
                return (couldBeBonus && credit.Points == plan.ExtraPoints)
                    || (!onlyBonus && (criteria.Countries.Length != 0 || !String.IsNullOrEmpty(criteria.Roster))
                        && credit.Points == plan.MemberPoints)
                    || (!onlyBonus && plan.BonusGroups.Any(group => group.Points == credit.Points));
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
        static readonly HashSet<string> Bonus = new HashSet<string> {
            "U4MIR", "UA9CES", "RW9CC", "R2AKN", "R4CQ", "RA3TD", "RA6F", "LZ1ZF", "AC1ZF", "LZ07RN"
        };
        static readonly Regex Digital = new Regex(@"^(?:DIGI(?:TAL)?|FT[48]|JT\w+|Q65|FST4\w*|MSK144|JS8|RTTY\w*|PSK\w*|MFSK\w*|OLIVIA|CONTESTI|VARA\w*|HELL\w*|SSTV|THOR\w*|DOMINO\w*|ROS|PACKET|PKT|AMTOR\w*|MT63|FSK\w*|WSPR)$",
            RegexOptions.CultureInvariant);
        static readonly HashSet<string> IneligibleBands = new HashSet<string> { "2190m", "630m", "560m" };
        static readonly HashSet<string> SkyHf = new HashSet<string>("160m 80m 60m 40m 30m 20m 17m 15m 12m 10m".Split(' '));
        static readonly HashSet<string> SkyVhf = new HashSet<string> { "6m", "4m", "2m" };
        readonly DateTime? from, until, bonusFrom, bonusUntil;
        readonly HashSet<string> recorded = new HashSet<string>(StringComparer.Ordinal);
        readonly List<HamlogCredit> pending = new List<HamlogCredit>();
        readonly string own;
        readonly bool logbookAvailable;
        readonly HamlogRulePlan plan;
        readonly HashSet<string> bonus;
        readonly HashSet<string> rcpwMembers;
        readonly Dictionary<string, int> namedBonuses = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Dictionary<string, int> areaBonuses = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Func<string, string> dxcc;
        readonly bool rcpw;
        readonly HamlogContactCriteria contacts;
        static readonly HashSet<string> RussianEntities = new HashSet<string> { "UA", "UA2", "UA9", "R1FJ" };
        bool EveryQso { get { return contacts != null && contacts.Repeat == HamlogContactCriteria.EveryQso; } }

        internal readonly HamlogStanding Standing;
        internal readonly int Required;
        internal readonly bool Closed;
        internal bool IsMember { get { return contacts == null && !rcpw && Membership.ContainsKey(own); } }
        internal int ConfirmedMissingFromLogbook { get; private set; }
        internal IReadOnlyList<HamlogCredit> Pending { get { return pending; } }
        internal decimal PendingPoints { get; private set; }
        internal decimal NeedOnline { get { return Math.Max(0, Required - (Standing == null ? 0 : Standing.Points)); } }
        internal bool ConfirmedComplete { get { return Standing != null && NeedOnline == 0; } }
        internal bool PendingEnough { get { return Standing != null && PendingPoints >= NeedOnline && !ConfirmedComplete; } }
        internal bool CanSearch { get { return Standing != null && logbookAvailable && !IsMember && !Closed && !ConfirmedComplete && !PendingEnough; } }

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
            bonusFrom = contacts == null ? null : AwardCatalog.ReadTime(contacts.BonusFromUtc);
            bonusUntil = contacts != null ? AwardCatalog.ReadTime(contacts.BonusUntilUtc)
                : rcpw ? AwardCatalog.ReadTime(plan.BonusUntilUtc) : null;
            from = program.From;
            until = program.Until;
            Closed = !template.HasPointRules;
            Standing = program.Hamlog;
            bool needsRcpw = plan != null && plan.UsesRcpwRoster;
            if (Standing != null && (Standing.Callsign != this.own || !template.Requires(Standing.Required) || Standing.Credits == null
                || needsRcpw && (Standing.RcpwMembers == null || Standing.RcpwMembers.Length == 0)))
                Standing = null;
            Required = Standing == null ? template.Required : Standing.Required;
            if (needsRcpw && Standing != null) rcpwMembers = new HashSet<string>(Standing.RcpwMembers, StringComparer.Ordinal);
            if (plan != null)
                foreach (HamlogBonusGroup group in plan.BonusGroups ?? new HamlogBonusGroup[0])
                    foreach (string target in group.Targets)
                        (group.Area ? areaBonuses : namedBonuses)[target] = group.Points;
            var logbookKeys = new HashSet<string>(StringComparer.Ordinal);
            var creditedByDay = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (LogbookQso row in history.Records)
            {
                DateTime? date = row.StartedUtc ?? row.EndedUtc;
                if (date.HasValue && Within(date.Value) && row.BelongsTo(this.own))
                    logbookKeys.Add(ContactKey(row.Call, row.Band, row.Mode, date.Value));
            }
            if (Standing != null)
            {
                foreach (HamlogCredit credit in Standing.Credits)
                {
                    if (credit.Observation) continue;
                    if (EveryQso)
                    {
                        string key = CountKey(credit.Call, credit.Band, credit.Mode, credit.DayUtc);
                        int count;
                        creditedByDay.TryGetValue(key, out count);
                        creditedByDay[key] = count + 1;
                        continue;
                    }
                    recorded.Add(Key(credit.Call, credit.Band, credit.Mode, credit.DayUtc));
                    if (!logbookKeys.Contains(ContactKey(credit.Call, credit.Band, credit.Mode, credit.DayUtc)))
                        ConfirmedMissingFromLogbook++;
                }
            }
            if (Closed || IsMember || Standing == null) return;
            foreach (LogbookQso row in history.Records.OrderBy(row => row.StartedUtc ?? row.EndedUtc))
            {
                DateTime? date = row.StartedUtc ?? row.EndedUtc;
                if (!date.HasValue || !row.BelongsTo(this.own)) continue;
                if (EveryQso && Within(date.Value) && Credit(row.Call, row.Band, row.Mode, date.Value) > 0)
                {
                    string key = CountKey(row.Call, row.Band, row.Mode, date.Value);
                    int count;
                    if (creditedByDay.TryGetValue(key, out count) && count > 0)
                    {
                        if (recorded.Add(Key(row.Call, row.Band, row.Mode, date.Value))) creditedByDay[key] = count - 1;
                        continue;
                    }
                }
                Record(row.Call, row.Band, row.Mode, date.Value);
            }
            if (EveryQso) ConfirmedMissingFromLogbook = creditedByDay.Values.Sum();
        }

        internal bool Within(DateTime time)
        { return (!from.HasValue || time >= from.Value) && (!until.HasValue || time < until.Value); }

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
                    case HamlogContactCriteria.Call: return call;
                    case HamlogContactCriteria.Band: return call + divider + band;
                    case HamlogContactCriteria.BandMode: return call + divider + band + divider + RepeatMode(mode);
                    case HamlogContactCriteria.Day: return call + divider + time.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                    case HamlogContactCriteria.DayBandMode: return CountKey(call, band, mode, time);
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
                return !String.IsNullOrEmpty(group) && JtdxAdif.BandNames.Contains(band)
                    && (contacts.Bands.Length == 0 || contacts.Bands.Contains(band))
                    && (contacts.Modes.Length == 0 || contacts.Modes.Contains(group));
            return !String.IsNullOrEmpty(band) && !IneligibleBands.Contains(band) && JtdxAdif.BandNames.Contains(band)
                && (group == "DIGI" || group == "SSB" || group == "CW")
                && (plan == null || rcpw || SkyHf.Contains(band) || !plan.HfOnly && SkyVhf.Contains(band));
        }

        decimal Credit(string call, string band, string mode, DateTime time)
        {
            string group = ModeGroup(mode);
            if (!OnBand(band, group)) return 0;
            if (contacts != null) return ContactCredit(call, time);
            if (rcpw) return rcpwMembers.Contains(call) ? time < bonusUntil.Value ? plan.ExtraPoints : plan.MemberPoints : 0;
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
            bool bandBonus = band == "160m" || plan != null && plan.VhfBonus && SkyVhf.Contains(band);
            return points * (group == "CW" ? 2m : group == "SSB" ? 1.5m : 1m) * (bandBonus ? 1.5m : 1m);
        }

        int ContactCredit(string call, DateTime time)
        {
            if (bonus.Contains(contacts.NamedCall(call)) && (!bonusFrom.HasValue || time >= bonusFrom.Value && time < bonusUntil.Value))
                return plan.ExtraPoints;
            string country = dxcc == null ? null : dxcc(call);
            int points = contacts.Countries.Contains(country) || ContactMember(call, country) ? plan.MemberPoints : 0;
            foreach (KeyValuePair<string, int> area in areaBonuses)
                if (HamlogAwardAreas.Matches(area.Key, call, country)) points = Math.Max(points, area.Value);
            return points;
        }

        bool ContactMember(string call, string country)
        {
            if (contacts.Roster == "rcpw") return rcpwMembers != null && rcpwMembers.Contains(call);
            return !String.IsNullOrEmpty(contacts.Roster) && HamlogAwardRosters.Arck.Contains(call)
                && (contacts.Roster == "arck" || country != null && !RussianEntities.Contains(country));
        }

        internal decimal CandidatePoints(string call, string band, string mode, DateTime time)
        {
            if (!CanSearch || !Within(time) || call == own || recorded.Contains(Key(call, band, mode, time))) return 0;
            return Credit(call, band, mode, time);
        }

        internal bool Available(string band, string mode, DateTime time)
        {
            if (!CanSearch || !Within(time) || !OnBand(band, ModeGroup(mode))) return false;
            if (contacts != null)
            {
                if (contacts.Countries.Length != 0 || areaBonuses.Count != 0) return true;
                IEnumerable<string> members = String.IsNullOrEmpty(contacts.Roster) ? Enumerable.Empty<string>()
                    : contacts.Roster == "rcpw" ? rcpwMembers : HamlogAwardRosters.Arck;
                return bonus.Concat(members).Any(call => CandidatePoints(call, band, mode, time) > 0);
            }
            if (areaBonuses.Count != 0) return true;
            IEnumerable<string> candidates = rcpw ? rcpwMembers : Membership.Keys.Concat(bonus).Concat(namedBonuses.Keys);
            return candidates.Any(call => CandidatePoints(call, band, mode, time) > 0);
        }

        internal void Record(string call, string band, string mode, DateTime time)
        {
            if (Closed || IsMember || Standing == null || !Within(time) || call == own) return;
            decimal points = Credit(call, band, mode, time);
            if (points == 0 || !recorded.Add(Key(call, band, mode, time))) return;
            pending.Add(new HamlogCredit { Call = call, DayUtc = time.Date, Band = band, Mode = mode, Points = points });
            PendingPoints += points;
        }
    }
}
