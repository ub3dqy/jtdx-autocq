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
            { "AL", "8Y 9Y" }, { "AM", "0J" }, { "AR", "1O" }, { "LP", "2G 3G 5G" },
            { "MA", "2A 2B 2C 3A 3B 3C 5A 5B 5C" },
            { "MO", "2D 2H 3D 3F 3H 5D 5F 5H" },
        };
        static readonly Regex Country = new Regex(@"\ADXCC [A-Z0-9]{1,8}\z", RegexOptions.CultureInvariant);
        static readonly HashSet<string> RussianDxcc = new HashSet<string> { "UA", "UA2", "UA9", "R1FJ" };
        static readonly Regex RegionCall = new Regex(@"\A(?:R[A-Z]?|U[A-I])([0-9][A-Z])[A-Z]{1,3}\z", RegexOptions.CultureInvariant);

        internal static string[] Parse(string text)
        {
            string area;
            return PublishedNames.TryGetValue(text, out area) ? new[] { area } : null;
        }

        internal static bool Known(string area) { return area != null && (Country.IsMatch(area) || Series.ContainsKey(area)); }

        internal static bool Matches(string area, string call, string dxcc)
        {
            if (String.IsNullOrEmpty(dxcc)) return false;
            string series;
            return Series.TryGetValue(area, out series) ? MatchesRussianSeries(call, dxcc, series.Split(' '))
                : area == "DXCC " + dxcc;
        }

        internal static bool MatchesRussianSeries(string call, string dxcc, IEnumerable<string> series)
        {
            if (!RussianDxcc.Contains(dxcc)) return false;
            Match match = RegionCall.Match(call);
            return match.Success && series.Contains(match.Groups[1].Value);
        }
    }
}
