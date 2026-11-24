using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace JtdxAutoCq
{
    internal static class HamlogAwardAreas
    {
        static readonly Dictionary<string, string> PublishedNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "радиолюбителями Австралии", "DXCC VK" }, { "радиолюбителями Бразилии", "DXCC PY" },
            { "радиолюбителями Индии", "DXCC VU" }, { "радиолюбителями Казахстана", "DXCC UN" },
            { "радиолюбителями КНР", "DXCC BY" },
            { "радиолюбителями представляющими республику Беларусь", "DXCC EU" },
            { "радиолюбителями Алтайского края", "AL" }, { "радиолюбителями Амурской области", "AM" },
            { "радиолюбителями Архангельской области", "AR" },
            { "радиолюбителями Липецкой области", "LP" }, { "радиолюбителями г. Липецка и Липецкой области", "LP" },
            { "радиолюбителями Португалии", "DXCC CT" }, { "представителями Украины", "DXCC UR" },
            { "представителями Польши", "DXCC SP" }, { "представителями Сербской республики", "DXCC YU" }
        };
        // Regular home calls in the official SRR subject table. A suffix or a special
        // event call must not inherit a subject from these series.
        static readonly Dictionary<string, string> Series = new Dictionary<string, string> {
            { "AL", "8Y 9Y" }, { "AM", "0J" }, { "AO", "6U 6V 7U 7V" }, { "AR", "1O" }, { "BR", "2Y 3Y 5Y" },
            { "BU", "0O" }, { "CK", "0K" }, { "CU", "4Y 4Z" }, { "IR", "0S 0T" },
            { "KA", "2F 2K" }, { "KE", "8U 8V 9U 9V" }, { "KG", "2X 3X 5X" },
            { "KL", "1K 1N" }, { "KU", "2W 3W 5W" }, { "LO", "1C 1D 1E" },
            { "LP", "2G 3G 5G" }, { "KS", "2N 3N 5N" },
            { "MA", "2A 2B 2C 3A 3B 3C 5A 5B 5C" },
            { "MO", "2D 2H 3D 3F 3H 5D 5F 5H" },
            { "MR", "4S 4T" }, { "MU", "1Y 1Z" }, { "OR", "2E 3E 5E" }, { "PS", "1W 1X" },
            { "RO", "6L 6M 6N 7L 7M 7N" }, { "RA", "2S 3S 5S" },
            { "SA", "4C 4D" }, { "SM", "2L 3L 5L" }, { "SP", "1A 1B 1F 1L 1M" }, { "TL", "2P 3P 5P" },
            { "TA", "4P 4Q 4R" }, { "TB", "2R 3R 5R" }, { "TV", "2I 3I 5I" },
            { "VG", "4A 4B" }, { "VR", "2O 2Q 3K 3O 3Q 5K 5O 5Q" }, { "YA", "0Q" },
        };
        static readonly Regex Country = new Regex(@"\ADXCC [A-Z0-9]{1,8}\z", RegexOptions.CultureInvariant);
        static readonly Regex KazakhstanSeries = new Regex(@"\AU[N-Q][0-9]([A-Z])[A-Z]{0,3}\z", RegexOptions.CultureInvariant);
        static readonly HashSet<string> KazakhstanAreas = new HashSet<string> { "UN GQ", "UN NT" };
        static readonly HashSet<string> RussianDxcc = new HashSet<string> { "UA", "UA2", "UA9", "R1FJ" };
        static readonly Regex RegionCall = new Regex(@"\A(?:R[A-Z]?|U[A-I])([0-9][A-Z])[A-Z]{1,3}\z", RegexOptions.CultureInvariant);

        internal static bool Russian(string dxcc) { return dxcc == "UA" || dxcc == "UA9" || dxcc == "UA2"; }

        internal static string Region(string area)
        {
            if (HamlogCountyIndex.IsCounty(area)) area = area.Substring(0, 2);
            return area != null && Series.ContainsKey(area) ? area : null;
        }

        internal static bool MatchesMonitoringRegion(string region, string call, string dxcc)
        {
            if (region == null) return true;
            string[] parts = call.Split('/');
            return (parts.Length == 1 || parts.Length == 2 && (parts[1] == "P" || parts[1] == "M" || parts[1] == "QRP"))
                && Matches(region, parts[0], dxcc);
        }

        internal static bool IsZeroDistrict(string call)
        {
            Match regular = RegionCall.Match(call ?? "");
            return regular.Success && regular.Groups[1].Value[0] == '0';
        }

        internal static string[] Parse(string text)
        {
            string area;
            return PublishedNames.TryGetValue(text, out area) ? new[] { area } : null;
        }

        internal static bool Known(string area)
        { return area != null && (Country.IsMatch(area) || Series.ContainsKey(area) || KazakhstanAreas.Contains(area) || HamlogCountyIndex.IsCounty(area)); }

        internal static bool Matches(string area, string call, string dxcc, string county = null, bool portable = false)
        {
            if (HamlogCountyIndex.IsCounty(area)) return area == county;
            if (String.IsNullOrEmpty(dxcc)) return false;
            if (KazakhstanAreas.Contains(area))
            {
                Match suffix = KazakhstanSeries.Match(call ?? "");
                return dxcc == "UN" && suffix.Success && area.IndexOf(suffix.Groups[1].Value[0], 3) >= 0;
            }
            string series;
            if (!Series.TryGetValue(area, out series)) return area == "DXCC " + dxcc;
            return HamlogCountyIndex.IsCounty(county) ? county.StartsWith(area + "-", StringComparison.Ordinal)
                : MatchesRussianSeries(portable && call.EndsWith("/P", StringComparison.Ordinal)
                    ? call.Substring(0, call.Length - 2) : call, dxcc, series.Split(' '));
        }

        internal static bool MatchesRussianSeries(string call, string dxcc, IEnumerable<string> series)
        {
            if (!RussianDxcc.Contains(dxcc)) return false;
            Match match = RegionCall.Match(call);
            return match.Success && series.Contains(match.Groups[1].Value);
        }
    }
}
