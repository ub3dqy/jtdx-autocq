using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace JtdxAutoCq
{
    public sealed class ListeningOptions
    {
        public bool UseMonitorTargets = true;
        public bool UseExpeditions, DesktopNotifications, Sound;
        public string[] LocatorPrefixes = new string[0];

        internal ListeningOptions Copy()
        {
            var copy = (ListeningOptions)MemberwiseClone();
            copy.LocatorPrefixes = (LocatorPrefixes ?? new string[0]).ToArray();
            return copy;
        }

        internal static string[] NormalizeLocators(IEnumerable<string> values)
        {
            return (values ?? Enumerable.Empty<string>()).Select(value =>
            {
                string grid = (value ?? "").Trim().ToUpperInvariant();
                if (AwardLocator.Full(grid) == null && !Regex.IsMatch(grid, @"\A[A-R]{2}\z", RegexOptions.CultureInvariant))
                    throw new InvalidOperationException("Укажите локатор из 2, 4 или 6 знаков, например KO, KO85 или KO85AA.");
                return grid;
            }).Distinct(StringComparer.Ordinal).ToArray();
        }
    }

    internal sealed class ListeningHit
    {
        internal DateTime TimeUtc;
        internal string Call, Band, Mode, Source, Details;
        internal bool External;
    }

    internal sealed class ListeningWatch
    {
        static readonly TimeSpan RepeatDelay = TimeSpan.FromMinutes(5);
        readonly HeardCqList heard = new HeardCqList();
        readonly MonitorTargets targets;
        readonly ExpeditionCatalog catalog;
        readonly ExpeditionSearch expeditions;
        readonly HashSet<ExpeditionSource> sources;
        readonly Predicate<string> allowed;
        readonly Dictionary<string, Seen> seen = new Dictionary<string, Seen>();
        DateTime nextPrune;

        sealed class Seen
        {
            internal DateTime ObservedUtc, AlertedUtc;
        }

        internal ListeningOptions Options { get; private set; }
        internal IEnumerable<ExpeditionSource> Sources { get { return sources; } }

        internal ListeningWatch(ListeningOptions options, AutoCqOptions monitorOptions, ExpeditionOptions expeditionOptions,
            ExpeditionCatalog catalog, Func<string, string> dxcc, Predicate<string> allowed)
        {
            Options = options.Copy();
            Options.LocatorPrefixes = ListeningOptions.NormalizeLocators(Options.LocatorPrefixes);
            this.catalog = catalog;
            this.allowed = allowed;
            if (Options.UseMonitorTargets)
            {
                AutoCqOptions goals = monitorOptions.Copy();
                goals.MonitorOnePerBand = false;
                goals.Validate();
                if (goals.MonitorDxcc.Length + goals.MonitorCalls.Length + goals.MonitorMasks.Length > 0)
                    targets = new MonitorTargets(goals, dxcc, null);
            }
            var configured = expeditionOptions.Copy();
            if (Options.UseExpeditions && configured.HasTargets)
                expeditions = new ExpeditionSearch(configured, catalog, dxcc);
            sources = new HashSet<ExpeditionSource>(expeditions == null
                ? Enumerable.Empty<ExpeditionSource>() : ExpeditionFeeds.Enabled(configured));
            if (targets == null && expeditions == null && Options.LocatorPrefixes.Length == 0)
                throw new InvalidOperationException("Выберите цели мониторинга, укажите локаторы или включите поиск по целям экспедиций.");
        }

        internal ListeningHit Observe(DecodedMessage decode, RadioStatus radio, DateTime now)
        {
            if (decode == null || radio == null || !decode.IsNew || decode.OffAir || decode.LowConfidence
                || decode.Milliseconds >= 86400000 || String.IsNullOrEmpty(decode.Message)) return null;
            if (radio.Mode != "FT8" && radio.Mode != "FT4" || radio.TxEnabled || radio.Transmitting) return null;
            if (radio.Mode == "FT8" ? decode.Mode != "~" : decode.Mode != ":" && decode.Mode != "+") return null;
            if (decode.Message.TrimStart().StartsWith("QRZ ", StringComparison.OrdinalIgnoreCase)) return null;
            string band = JtdxAdif.Band(radio.Frequency);
            if (band == null) return null;
            DateTime utc = decode.UtcTime(now);
            if (utc > now.AddSeconds(2) || (now - utc).TotalSeconds > (radio.Mode == "FT4" ? 15.5 : 30.5)) return null;

            heard.SetContext(radio);
            heard.Observe(decode, radio, now);
            CqStation station = heard.Rows.FirstOrDefault(row => row.HeardUtc == utc && heard.Fresh(row, now)
                && row.Decode.Message == decode.Message && row.Decode.DeltaFrequency == decode.DeltaFrequency);
            if (station == null || !allowed(station.Call)) return null;

            string locator = heard.LocatorFor(station.Call, now);
            var reasons = new List<string>();
            if (targets != null && targets.CanTry(station.Call)) reasons.Add("Цель мониторинга");
            if (locator != null && Options.LocatorPrefixes.Any(prefix => locator.StartsWith(prefix, StringComparison.Ordinal)))
                reasons.Add("Локатор " + locator);
            ExpeditionMatch expedition = expeditions == null ? null : expeditions.Match(station.Call, band, radio.Mode, now);
            if (expedition != null) reasons.Add(expedition.Reason);
            if (reasons.Count == 0) return null;
            var hit = new ListeningHit {
                Call = station.Call, Band = band, Mode = radio.Mode, Source = "Декод JTDX", TimeUtc = utc,
                Details = decode.Message.Trim() + " · " + String.Join(" · ", reasons)
            };
            return Record("rx", hit, now) ? hit : null;
        }

        internal IEnumerable<ListeningHit> ObserveSpots(string ownCall, DateTime now)
        {
            if (expeditions == null) yield break;
            string own = Callsign.BaseCall(Callsign.Clean(ownCall));
            foreach (ExpeditionActivity spot in catalog.Activities)
            {
                if (spot.Announced || !spot.Current(now) || spot.Mode != "FT8" && spot.Mode != "FT4"
                    || spot.Band == null || String.IsNullOrEmpty(spot.Call) || Callsign.BaseCall(spot.Call) == own || !allowed(spot.Call)) continue;
                if (!sources.Contains(spot.Source) && (spot.Source != ExpeditionSource.WwffSpothole || !sources.Contains(ExpeditionSource.Wwff)))
                    continue;
                ExpeditionMatch match = expeditions.Match(spot, now);
                if (match == null) continue;
                var hit = new ListeningHit {
                    Call = spot.Call, Band = spot.Band, Mode = spot.Mode, TimeUtc = spot.SpottedUtc, External = true,
                    Source = "Спот · " + ExpeditionFeeds.Name(spot.Source), Details = match.Reason
                };
                if (Record("spot/" + spot.Place, hit, now)) yield return hit;
            }
        }

        internal void ResetDecodes()
        {
            heard.Invalidate();
        }

        bool Record(string origin, ListeningHit hit, DateTime now)
        {
            if (now >= nextPrune)
            {
                DateTime cutoff = now.AddMinutes(-20);
                foreach (string stale in seen.Where(pair => pair.Value.ObservedUtc < cutoff).Select(pair => pair.Key).ToArray())
                    seen.Remove(stale);
                nextPrune = now.AddMinutes(1);
            }
            string key = origin + "/" + hit.Call + "/" + hit.Band + "/" + hit.Mode;
            Seen previous;
            if (!seen.TryGetValue(key, out previous))
            {
                previous = new Seen();
                seen.Add(key, previous);
            }
            if (hit.TimeUtc <= previous.ObservedUtc) return false;
            previous.ObservedUtc = hit.TimeUtc;
            if (now - previous.AlertedUtc < RepeatDelay) return false;
            previous.AlertedUtc = now;
            return true;
        }
    }
}
