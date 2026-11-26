using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Windows.Forms;

namespace JtdxAutoCq
{
    public sealed partial class MainForm
    {
        readonly ExpeditionCatalog expeditionCatalog = new ExpeditionCatalog();
        readonly Dictionary<ExpeditionSource, FeedStatus> expeditionFeeds = new Dictionary<ExpeditionSource, FeedStatus>();
        readonly Timer expeditionTimer = new Timer { Interval = 1000 };
        ExpeditionDialog expeditionDialog;

        sealed class FeedStatus
        {
            internal bool Loading, Failed;
            internal DateTime LastAttempt, LastSuccess, NextAttempt;
        }

        void InitializeExpeditions()
        {
            expeditionTimer.Tick += delegate { UpdateExpeditions(); };
            Disposed += delegate { expeditionTimer.Dispose(); };
        }

        void ShowExpeditions()
        {
            if (expeditionDialog != null && !expeditionDialog.IsDisposed)
            { expeditionDialog.Activate(); return; }
            try { CountryTable(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (demo) SeedExpeditionDemo(DateTime.UtcNow);
            expeditionDialog = new ExpeditionDialog(preferences.ExpeditionSearch,
                (window, prefixes) => {
                    string[] result = null;
                    Guard(() => result = ChooseCountries("Личные цели DXCC", prefixes, window));
                    return result;
                },
                options => Guard(() => {
                    if (engine.Expeditions == null) SaveExpeditionOptions(options);
                    UpdateExpeditions(true);
                }),
                options => Guard(() => StartExpeditions(options)),
                HaltAutomation,
                () => Guard(() => OpenGuide("expeditions", "экспедиции-и-редкие-станции")));
            expeditionDialog.Show(this);
            expeditionTimer.Start();
            UpdateExpeditions();
        }

        void SaveExpeditionOptions(ExpeditionOptions options)
        {
            ExpeditionOptions previous = preferences.ExpeditionSearch;
            preferences.ExpeditionSearch = options;
            try { preferenceStore.Save(preferences); }
            catch { preferences.ExpeditionSearch = previous; throw; }
        }

        void StartExpeditions(ExpeditionOptions options)
        {
            if (engine.Armed || startingWithHistory) return;
            if (!options.HasTargets) throw new InvalidOperationException("Выберите, какие экспедиции и станции искать.");
            SaveExpeditionOptions(options);
            StartRadioSession(null, new ExpeditionSearch(options, expeditionCatalog, engine.DxccForCall));
            UpdateExpeditions();
        }

        void UpdateExpeditions(bool refresh = false)
        {
            if (IsDisposed || Disposing) return;
            bool visible = expeditionDialog != null && !expeditionDialog.IsDisposed;
            if (!visible && engine.Expeditions == null)
            { expeditionTimer.Stop(); return; }
            ExpeditionOptions options = engine.Expeditions != null ? preferences.ExpeditionSearch : expeditionDialog.Options;
            DateTime now = DateTime.UtcNow;
            ExpeditionSource[] enabled = ExpeditionFeeds.Enabled(options).ToArray();
            if (!demo)
                foreach (ExpeditionSource source in enabled) UpdateExpeditionFeed(source, now, refresh);
            if (!visible) return;
            bool connected = demo || client != null && clientId == "JTDX" && clientVersion == "2.2.160-32A-rc10"
                && (now - lastSeen).TotalSeconds < 35;
            bool canStart = connected && !engine.Armed && !startingWithHistory && engine.Status != null
                && !engine.Status.TxEnabled && !engine.Status.Transmitting;
            expeditionDialog.Render(expeditionCatalog, engine, canStart, ExpeditionSourceStatus(enabled), now);
        }

        void UpdateExpeditionFeed(ExpeditionSource source, DateTime now, bool refresh)
        {
            FeedStatus status;
            if (!expeditionFeeds.TryGetValue(source, out status))
            {
                status = new FeedStatus();
                expeditionFeeds.Add(source, status);
            }
            bool mayRefreshNow = refresh && !status.Failed && ExpeditionFeeds.Interval(source) == TimeSpan.FromMinutes(2)
                && now - status.LastAttempt >= TimeSpan.FromMinutes(2);
            if (status.Loading || now < status.NextAttempt && !mayRefreshNow) return;
            status.Loading = true;
            status.LastAttempt = now;
            var worker = new BackgroundWorker();
            bool local = source == ExpeditionSource.LogHx;
            worker.DoWork += delegate(object sender, DoWorkEventArgs args) {
                args.Result = local ? LogHxSpotFeed.Read(DateTime.UtcNow) : ExpeditionFeeds.Fetch(source);
            };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs args) {
                worker.Dispose();
                if (IsDisposed || Disposing) return;
                status.Loading = false;
                status.Failed = args.Error != null;
                DateTime received = DateTime.UtcNow;
                if (!status.Failed)
                {
                    if (local) expeditionCatalog.MergeLogHx((ExpeditionFeedData)args.Result, received);
                    else expeditionCatalog.Replace(source, (ExpeditionFeedData)args.Result);
                    status.LastSuccess = received;
                }
                status.NextAttempt = received + (status.Failed
                    ? local ? TimeSpan.FromSeconds(10) : SourceRetryDelay(args.Error)
                    : ExpeditionFeeds.Interval(source));
            };
            worker.RunWorkerAsync();
        }

        static TimeSpan SourceRetryDelay(Exception error)
        {
            var network = error as WebException;
            var response = network == null ? null : network.Response;
            using (response)
            {
                int delay;
                if (response != null && Int32.TryParse(response.Headers["Retry-After"], out delay))
                    return TimeSpan.FromSeconds(Math.Max(300, Math.Min(86400, delay)));
            }
            return TimeSpan.FromMinutes(5);
        }

        string ExpeditionSourceStatus(ExpeditionSource[] selected)
        {
            if (demo) return "Демонстрационные данные";
            if (selected.Length == 0) return "Выберите, что искать. Список обновится автоматически.";
            var unavailable = selected.Where(source => expeditionFeeds[source].Failed && !expeditionFeeds[source].Loading)
                .Select(ExpeditionFeeds.Name).ToArray();
            var loading = selected.Where(source => expeditionFeeds[source].Loading).Select(ExpeditionFeeds.Name).ToArray();
            DateTime last = selected.Max(source => expeditionFeeds[source].LastSuccess);
            return (last == DateTime.MinValue ? "" : "Обновлено " + last.ToLocalTime().ToString("HH:mm:ss") + "   ")
                + (loading.Length == 0 ? "" : "Загружаю " + String.Join(", ", loading) + "   ")
                + (unavailable.Length == 0 ? "" : "Недоступно " + String.Join(", ", unavailable));
        }

        void SeedExpeditionDemo(DateTime now)
        {
            expeditionCatalog.Replace(ExpeditionSource.Ng3k, new ExpeditionFeedData { Activities = new[] {
                new ExpeditionActivity { Source = ExpeditionSource.Ng3k, Call = "V6AAA", Place = "Микронезия",
                    Mode = "FT8", StartUtc = now.Date.AddDays(-1), EndUtc = now.Date.AddDays(6) },
                new ExpeditionActivity { Source = ExpeditionSource.Ng3k, Call = "T88BBB", Place = "Палау",
                    Mode = "FT8 FT4", StartUtc = now.Date.AddDays(3), EndUtc = now.Date.AddDays(8) },
            } });
            expeditionCatalog.Replace(ExpeditionSource.DxNews, new ExpeditionFeedData { Activities = new[] {
                new ExpeditionActivity { Source = ExpeditionSource.DxNews, Call = "A35AAA", Place = "Тонга",
                    StartUtc = now.Date.AddDays(4), EndUtc = now.Date.AddDays(11) },
            } });
            expeditionCatalog.Replace(ExpeditionSource.DxRanks, new ExpeditionFeedData { Entities = new[] {
                new ExpeditionEntity { Dxcc = 173, Name = "Острова Гилберта", Prefix = "T30", Rank = 42 },
            } });
            expeditionCatalog.Replace(ExpeditionSource.DxSpots, new ExpeditionFeedData { Activities = new[] {
                DemoSpot(ExpeditionSource.DxSpots, "T30AAA", "", "FT8", 21074000, now.AddMinutes(-3), 173),
            } });
            expeditionCatalog.Replace(ExpeditionSource.WantedRda, new ExpeditionFeedData {
                RdaRanks = new Dictionary<string, int> { { "CK-04", 1 } },
            });
            expeditionCatalog.Replace(ExpeditionSource.Rda, new ExpeditionFeedData { Activities = new[] {
                DemoSpot(ExpeditionSource.Rda, "R0AAA/P", "CK-04", "FT8", 7074000, now.AddMinutes(-2)),
            } });
            expeditionCatalog.Replace(ExpeditionSource.Pota, new ExpeditionFeedData { Activities = new[] {
                DemoSpot(ExpeditionSource.Pota, "W2ABC", "US-1234", "FT4", 14080000, now.AddMinutes(-6)),
            } });
            expeditionCatalog.Replace(ExpeditionSource.Wwff, new ExpeditionFeedData { Activities = new[] {
                DemoSpot(ExpeditionSource.Wwff, "DL1AAA/P", "DLFF-0123", "FT8", 10136000, now.AddMinutes(-4)),
            } });
            expeditionCatalog.Replace(ExpeditionSource.Sota, new ExpeditionFeedData { Activities = new[] {
                DemoSpot(ExpeditionSource.Sota, "HB0AAA/P", "HB0/LI-004", "FT8", 7074000, now.AddMinutes(-5)),
            } });
            expeditionCatalog.Replace(ExpeditionSource.Wwbota, new ExpeditionFeedData { Activities = new[] {
                DemoSpot(ExpeditionSource.Wwbota, "ON1AAA/P", "B/ON-0051", "FT4", 7047500, now.AddMinutes(-6)),
            } });
            ExpeditionActivity island = DemoSpot(ExpeditionSource.DxSummit, "V6BBB", "OC-011", "FT8", 18095000, now.AddMinutes(-2));
            island.ReferenceProgram = "IOTA";
            expeditionCatalog.Replace(ExpeditionSource.DxSummit, new ExpeditionFeedData { Activities = new[] { island } });
            ExpeditionActivity district = DemoSpot(ExpeditionSource.LogHx, "R2AAA/P", "TB-02", "FT8", 7074000, now.AddMinutes(-3));
            district.ReferenceProgram = "RDA";
            expeditionCatalog.Replace(ExpeditionSource.LogHx, new ExpeditionFeedData { Activities = new[] { district } });
        }

        static ExpeditionActivity DemoSpot(ExpeditionSource source, string call, string place, string mode, double frequency, DateTime seen, int dxcc = 0)
        { return new ExpeditionActivity { Source = source, Call = call, Place = place, Mode = mode, Frequency = frequency, SpottedUtc = seen, Dxcc = dxcc }; }

        Form ExpeditionDemoForm()
        {
            DateTime now = DateTime.UtcNow;
            SeedExpeditionDemo(now);
            engine.Cqs.Observe(new DecodedMessage { Message = "CQ V6AAA QJ57", IsNew = true, Mode = "~",
                Milliseconds = (uint)now.TimeOfDay.TotalMilliseconds, Snr = -10, DeltaFrequency = 1128 },
                engine.Status, now);
            var form = new ExpeditionDialog(new ExpeditionOptions { LogHxSpots = true },
                (dialog, values) => values, options => { }, options => { }, () => { }, () => { });
            form.Render(expeditionCatalog, engine, true, ExpeditionSourceStatus(new ExpeditionSource[0]), now);
            return form;
        }
    }
}
