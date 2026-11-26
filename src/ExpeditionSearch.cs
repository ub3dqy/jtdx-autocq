using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JtdxAutoCq
{
    public sealed class ExpeditionOptions
    {
        public bool DxTrips = true, Parks = true, Nature = true, PortableRda = true, WantedRda = true, RareDx = true;
        public int MaxDxRank = 50;
        public string[] WantedDxcc = new string[0];
        public string WantedBand = "";

        internal ExpeditionOptions Copy()
        {
            var copy = (ExpeditionOptions)MemberwiseClone();
            copy.MaxDxRank = Math.Max(1, Math.Min(340, MaxDxRank));
            copy.WantedDxcc = (WantedDxcc ?? new string[0]).Select(Callsign.Clean).Where(value => value.Length > 0).Distinct().ToArray();
            copy.WantedBand = JtdxAdif.BandNames.Contains(WantedBand) ? WantedBand : "";
            return copy;
        }

        internal bool HasTargets
        {
            get { return DxTrips || Parks || Nature || PortableRda || WantedRda || RareDx || WantedDxcc.Length > 0; }
        }
    }

    internal enum ExpeditionSource { Ng3k, Pota, Wwff, Rda, WantedRda, DxRanks, DxSpots }

    internal sealed class ExpeditionActivity
    {
        internal ExpeditionSource Source;
        internal string Call, Place, Mode, Bands;
        internal double Frequency;
        internal int Dxcc;
        internal DateTime StartUtc, EndUtc, SpottedUtc;

        internal bool Announced { get { return Source == ExpeditionSource.Ng3k; } }
        internal string Band { get { return JtdxAdif.Band(Frequency); } }

        internal bool Current(DateTime now)
        {
            return Announced ? now >= StartUtc && now < EndUtc
                : SpottedUtc <= now.AddMinutes(1) && now - SpottedUtc <= TimeSpan.FromMinutes(15);
        }

        internal bool Fits(string band, string mode)
        {
            return Announced ? (String.IsNullOrEmpty(Bands) || Bands.Split(' ').Contains(band))
                    && (String.IsNullOrEmpty(Mode) || Mode.Split(' ').Contains(mode))
                : Band == band && Mode == mode;
        }
    }

    internal sealed class ExpeditionEntity
    {
        internal int Dxcc, Rank;
        internal string Prefix, Name;
    }

    internal sealed class ExpeditionFeedData
    {
        internal ExpeditionActivity[] Activities = new ExpeditionActivity[0];
        internal ExpeditionEntity[] Entities = new ExpeditionEntity[0];
        internal Dictionary<string, int> RdaRanks = new Dictionary<string, int>();
    }

    internal sealed class ExpeditionCatalog
    {
        readonly Dictionary<ExpeditionSource, ExpeditionFeedData> items = new Dictionary<ExpeditionSource, ExpeditionFeedData>();
        ILookup<string, ExpeditionActivity> calls = new ExpeditionActivity[0].ToLookup(item => item.Call);
        ILookup<string, ExpeditionEntity> prefixes = new ExpeditionEntity[0].ToLookup(item => item.Prefix);
        Dictionary<int, ExpeditionEntity> dxcc = new Dictionary<int, ExpeditionEntity>();
        Dictionary<string, int> rdaRanks = new Dictionary<string, int>();

        internal IEnumerable<ExpeditionActivity> Activities { get { return items.Values.SelectMany(item => item.Activities); } }
        internal IEnumerable<ExpeditionActivity> ForCall(string call) { return calls[call]; }

        internal void Replace(ExpeditionSource source, ExpeditionFeedData data)
        {
            items[source] = data;
            if (source == ExpeditionSource.DxRanks)
            {
                dxcc = data.Entities.GroupBy(entity => entity.Dxcc).ToDictionary(group => group.Key, group => group.First());
                prefixes = dxcc.Values.ToLookup(entity => entity.Prefix);
            }
            else if (source == ExpeditionSource.WantedRda) rdaRanks = data.RdaRanks;
            else calls = Activities.ToLookup(item => item.Call);
        }

        internal int RdaRank(string key)
        {
            int rank;
            return rdaRanks.TryGetValue(key, out rank) ? rank : 0;
        }

        internal ExpeditionEntity Entity(int id, string prefix)
        {
            ExpeditionEntity entity;
            if (id > 0 && dxcc.TryGetValue(id, out entity)) return entity;
            // A shared main prefix is insufficient to assign a DXCC rank.
            var mapped = prefixes[prefix ?? ""];
            return mapped.Count() == 1 ? mapped.First() : null;
        }
    }

    internal sealed class ExpeditionMatch
    {
        internal string Reason, Source;
        internal int Priority;
    }

    internal sealed class ExpeditionSearch
    {
        readonly ExpeditionOptions options;
        readonly ExpeditionCatalog catalog;
        readonly Func<string, string> dxccForCall;

        internal ExpeditionSearch(ExpeditionOptions options, ExpeditionCatalog catalog, Func<string, string> dxccForCall)
        {
            this.options = options.Copy();
            this.catalog = catalog;
            this.dxccForCall = dxccForCall;
        }

        internal bool HasTargets { get { return options.HasTargets; } }

        internal ExpeditionMatch Match(string call, string band, string mode, DateTime now)
        {
            if (band == null || (mode != "FT8" && mode != "FT4")) return null;
            var activities = catalog.ForCall(call).Where(item => item.Fits(band, mode) && Current(item, now));
            return BestMatch(activities, call, band);
        }

        internal ExpeditionMatch Match(ExpeditionActivity spot, DateTime now)
        {
            if (spot.Announced || !Current(spot, now)) return null;
            // Match a live spot with a calendar entry, or use its own field reference.
            var announcements = catalog.ForCall(spot.Call).Where(item => item.Announced
                && item.Current(spot.SpottedUtc) && item.Current(now) && item.Fits(spot.Band, spot.Mode));
            return BestMatch(announcements.Concat(new[] { spot }), spot.Call, spot.Band);
        }

        bool Current(ExpeditionActivity activity, DateTime now)
        {
            if (!activity.Current(now)) return false;
            if (activity.Announced || activity.Source == ExpeditionSource.DxSpots) return true;
            return !catalog.ForCall(activity.Call).Any(item => item.Source == activity.Source
                && item.Place != activity.Place && item.SpottedUtc > activity.SpottedUtc && item.Current(now));
        }

        ExpeditionMatch BestMatch(IEnumerable<ExpeditionActivity> activities, string call, string band)
        {
            ExpeditionActivity[] evidence = activities.OrderByDescending(item => item.SpottedUtc).ToArray();
            string prefix = options.RareDx || options.WantedDxcc.Length > 0 ? dxccForCall(call) : null;
            var match = evidence.Select(MatchActivity).Where(item => item != null).OrderByDescending(item => item.Priority).FirstOrDefault();
            if (options.WantedDxcc.Contains(prefix) && (options.WantedBand.Length == 0 || options.WantedBand == band))
                return Description("Ваша цель DXCC " + prefix, "Личные цели", 5000, match);
            if (options.RareDx)
            {
                int id = evidence.Where(item => item.Dxcc > 0).Select(item => item.Dxcc).FirstOrDefault();
                ExpeditionEntity entity = catalog.Entity(id, prefix);
                if (entity != null && entity.Rank > 0 && entity.Rank <= options.MaxDxRank)
                    return Description("Редкая DXCC #" + entity.Rank + " · " + entity.Name, "Club Log / CQDX", 4000 - entity.Rank, match);
            }
            return match;
        }

        internal ExpeditionMatch MatchActivity(ExpeditionActivity item)
        {
            switch (item.Source)
            {
                case ExpeditionSource.Ng3k:
                    return options.DxTrips ? Description("DX-выезд · " + item.Place, "NG3K", 2000) : null;
                case ExpeditionSource.Pota:
                    return options.Parks ? Description("Парк " + item.Place, "POTA", 1000) : null;
                case ExpeditionSource.Wwff:
                    return options.Nature ? Description("Природная территория " + item.Place, "WWFF", 1000) : null;
                case ExpeditionSource.Rda:
                    int rank = catalog.RdaRank(item.Place);
                    if (options.WantedRda && rank > 0)
                        return Description("Редкий район " + item.Place, "Wanted RDA", 3000 - rank);
                    if (options.PortableRda && (item.Call.EndsWith("/P", StringComparison.Ordinal)
                        || item.Call.EndsWith("/M", StringComparison.Ordinal)))
                        return Description("Полевой выезд · RDA " + item.Place, "Кластер RDA", 1000);
                    break;
            }
            return null;
        }

        static ExpeditionMatch Description(string reason, string source, int priority, ExpeditionMatch also = null)
        {
            return new ExpeditionMatch {
                Reason = also == null ? reason : reason + " · " + also.Reason,
                Source = also == null ? source : source + " + " + also.Source,
                Priority = priority
            };
        }
    }
}
