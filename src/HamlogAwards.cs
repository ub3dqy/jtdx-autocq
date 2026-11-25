using System;
using System.Linq;

namespace JtdxAutoCq
{
    internal enum HamlogRule { Legacy, Grid, Stations, Bands, Tula, Tatarstan, Bryansk, Vysotsky, Marathon, Arck35, SkyHunter, EventStation, RcpwHunter, ContactPoints, Districts, RdaSearch }

    internal sealed class HamlogAward
    {
        internal static readonly HamlogAward Murom = new HamlogAward("Муромский сокол",
            "club/nebo/dc4b742d6f1c2c631f21703d0565cacf/", "2026-09-11", "");
        internal static readonly HamlogAward Tula = new HamlogAward("Тула — 875 лет",
            "global/russia/r3p/a8052398c5047a367fe59da599b1818f/", "2021-09-01", "2021-09-30");
        internal static readonly HamlogAward Russia = new HamlogAward("Моя Россия", "rrnars/", "2026-01-01", "", HamlogRule.Grid);
        internal static readonly HamlogAward Gunsmiths = new HamlogAward("Легендарные оружейники Тулы",
            "global/russia/r3p/eb739f05ee1eb35b118a447552a8d0c2/", "2022-09-19", "", HamlogRule.Tula);
        internal static readonly HamlogAward Tatarstan = new HamlogAward("Татарстан",
            "club/kazan/a7df3f8685baaaf26d8abf691e0d4cd7/", "2024-01-01", "", HamlogRule.Tatarstan);
        internal static readonly HamlogAward Bryansk = new HamlogAward("Работал с R3Y",
            "club/r3ydosaaf/db8799094e17ba762616ac19f8252502/", "2002-06-12", "", HamlogRule.Bryansk);
        internal static readonly HamlogAward Vysotsky = new HamlogAward("В. С. Высоцкому — 88 лет",
            "club/arck/f4001e43804b7424b605414bbeaddef8/", "2026-01-15", "2026-12-31", HamlogRule.Vysotsky);
        internal static readonly HamlogAward Arck35 = new HamlogAward("ARCK — 35 лет",
            "club/arck/b57d5bd347637005b3ddfca5497cc969/", "2026-01-15", "2026-12-31", HamlogRule.Arck35);
        internal static readonly HamlogAward Marathon = new HamlogAward("36 лет Miller-DX-Club",
            "club/mdxc/ba9d1e767d8d1afea1cd10c5f4ef9a1d/", "2026-11-16", "2026-11-30T21:00:00Z", HamlogRule.Marathon);
        internal static readonly HamlogAward Estonia4 = new HamlogAward("Северное побережье Эстонии (ES4)",
            "club/swfriends/a8b08bdaadd7c0021aa6c8e4ab9d5191/", "2021-12-09", "", HamlogRule.Stations, "ES4");
        internal static readonly HamlogAward Estonia5 = new HamlogAward("5-й регион Эстонии (ES5)",
            "club/swfriends/b44b72619808961043250cb8d6267a7b/", "2019-05-26", "", HamlogRule.Stations, "ES5");
        internal static readonly HamlogAward Romania = new HamlogAward("YP40CSM — 40 лет клуба CSM Bistrița",
            "club/romania/b61c03dc2a9c2f25db6dc450931e8908/", "2026-11-01", "2026-11-30", HamlogRule.Stations, "YP40CSM");
        internal static readonly HamlogAward Foundation = Vk5Esm("Foundation", "e4a58b8314cbcd79cfccb8287f0ddd00", "80m 40m 15m 10m");
        internal static readonly HamlogAward Standard = Vk5Esm("Standard", "eb7f951b84bb0bba23fb8b1a97682801", "80m 40m 20m 15m 10m");
        internal static readonly HamlogAward Advanced = Vk5Esm("Advanced", "fb25c0f16b919c5dea0a4ba0d9a99f9e", "80m 40m 30m 20m 17m 15m 12m 10m");
        internal static readonly HamlogAward Warc = Vk5Esm("WARC", "c87e288d35a637c97959b983680df96b", "30m 17m 12m");
        internal static readonly HamlogAward[] Templates = {
            Russia, Gunsmiths, Tatarstan, Bryansk, Vysotsky, Arck35, Marathon, Estonia4, Estonia5, Romania,
            Foundation, Standard, Advanced, Warc, Murom, Tula
        };
        internal readonly string Title, Url, From, Until;
        internal readonly HamlogRule Rule;
        internal readonly string Station;
        internal readonly string[] Bands;
        internal readonly HamlogRulePlan DocumentPlan;
        internal bool HasSearchRules { get { return this == Murom || Rule != HamlogRule.Legacy; } }
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
        HamlogAward(string title, string path, string from, string until, HamlogRule rule = HamlogRule.Legacy,
            string station = "", string[] bands = null, HamlogRulePlan plan = null)
        {
            Title = title;
            Url = "https://hamlog.online/" + path;
            From = from;
            Until = until;
            Rule = rule;
            Station = station;
            Bands = bands ?? new string[0];
            DocumentPlan = plan;
        }

        static HamlogAward Vk5Esm(string level, string id, string bands)
        {
            string[] list = bands.Split(' ');
            return new HamlogAward("VK5ESM — Worked All Bands (" + level + ")", "global/australia/vk5/" + id + "/",
                "2026-11-01", "", HamlogRule.Bands, "VK5ESM", list);
        }

        internal static HamlogAward Find(string url)
        {
            if (String.IsNullOrEmpty(url)) return null;
            return Templates.FirstOrDefault(award => award.Url == url || award.Url + "rules/" == url);
        }

        internal static HamlogAward FromDocument(AwardProgram program)
        {
            HamlogRulePlan plan = program.EffectiveRules;
            if (plan == null || String.IsNullOrEmpty(program.Url)) return null;
            HamlogRule rule = plan.Club ? HamlogRule.SkyHunter : plan.Rcpw ? HamlogRule.RcpwHunter
                : plan.RdaOnly ? HamlogRule.RdaSearch : plan.Districts ? HamlogRule.Districts : plan.Locators ? HamlogRule.Grid
                : plan.ContactAward ? HamlogRule.ContactPoints : HamlogRule.EventStation;
            return new HamlogAward(program.Title, new Uri(program.Url).AbsolutePath.TrimStart('/'), plan.FromUtc, plan.UntilUtc,
                rule, rule == HamlogRule.EventStation ? plan.Calls[0] : "", plan: plan);
        }

        internal AwardProgram Create()
        {
            var program = new AwardProgram { Title = Title, Url = Url };
            program.Validate();
            return program;
        }
    }
}
