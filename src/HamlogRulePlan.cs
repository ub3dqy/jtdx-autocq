using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace JtdxAutoCq
{
    public sealed class HamlogRulePlan
    {
        public string Kind, FromUtc, UntilUtc;
        public int Required, MemberPoints, ExtraPoints;
        public bool HfOnly, VhfBonus;
        public string[] Calls = new string[0];
        internal bool Club { get { return Kind == "sky-hunter"; } }

        internal void Validate()
        {
            if (!Club && Kind != "single-station")
                throw new InvalidDataException("Неизвестный способ зачёта диплома.");
            if (Calls == null || Calls.Length < 1 || Calls.Length > 80)
                throw new InvalidDataException("Укажите позывные станций.");
            Calls = Calls.Select(Callsign.Parse).Distinct(StringComparer.Ordinal).ToArray();
            if (Calls.Any(call => call == null))
                throw new InvalidDataException("Проверьте позывные в списке.");
            if (Club ? Required < 1 || Required > 10000 || MemberPoints < 1 || MemberPoints > 1000
                || ExtraPoints < 1 || ExtraPoints > 1000 || HfOnly && VhfBonus : Required != 1 || Calls.Length != 1)
                throw new InvalidDataException("Проверьте число очков или позывных в условиях диплома.");
            FromUtc = (FromUtc ?? "").Trim();
            UntilUtc = (UntilUtc ?? "").Trim();
            DateTime? from = AwardCatalog.ReadTime(FromUtc), until = AwardCatalog.ReadTime(UntilUtc, true);
            if (from.HasValue && until.HasValue && from >= until)
                throw new InvalidDataException("Дата окончания должна быть позже даты начала.");
        }
    }

    internal static class HamlogPlanCompiler
    {
        internal const int Version = 1;
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
        static readonly Regex SpecialEvent = Pattern(
            @"Special Event Station:\s+(?<station>[A-Z0-9/]{3,16})\s+Award Requirements\b.{0,1800}?"
            + @"Rules\s+1\.\s*QSOs must be made between (?<start>[A-Za-z]+ \d{1,2}) and (?<end>[A-Za-z]+ \d{1,2}), (?<year>\d{4})\.\s*"
            + @"2\.\s*At least one QSO with (?<same>[A-Z0-9/]{3,16}) is required to qualify for the award\.\s*"
            + @"3\.\s*Award eligibility will be determined based on the QSOs uploaded to HAMLOG by the special event station\.\s*Award Issuance\b");

        static Regex Pattern(string expression)
        { return new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }

        internal static HamlogRulePlan Compile(string url, string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return null;
            string normalized = Regex.Replace(text.Replace("\u00ad", "").Replace("\u0002", ""), @"\s+", " ");
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
            }
            else
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
