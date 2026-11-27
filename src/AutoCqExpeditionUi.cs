using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Windows.Forms;

namespace JtdxAutoCq
{
    public sealed partial class MainForm
    {
        readonly ExpeditionCatalog expeditionCatalog = new ExpeditionCatalog();
        readonly Dictionary<ExpeditionSource, FeedStatus> expeditionFeeds = new Dictionary<ExpeditionSource, FeedStatus>();
        readonly Timer expeditionTimer = new Timer { Interval = 1000 };
        static readonly TimeSpan LiveSpotRetry = TimeSpan.FromMinutes(1);
        ExpeditionDialog expeditionDialog;

        sealed class FeedStatus
        {
            internal bool Loading;
            internal string Failure;
            internal ExpeditionSource? BackupSource;
            internal DateTime LastSuccess, NextAttempt, NextManualAttempt;
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
            string sources = demo ? "Демонстрационные данные" : ExpeditionSourceStatus(enabled, now);
            expeditionDialog.Render(expeditionCatalog, engine, canStart, sources, now);
        }

        void UpdateExpeditionFeed(ExpeditionSource source, DateTime now, bool refresh)
        {
            FeedStatus status;
            if (!expeditionFeeds.TryGetValue(source, out status))
            {
                status = new FeedStatus();
                expeditionFeeds.Add(source, status);
            }
            DateTime next = refresh ? status.NextManualAttempt : status.NextAttempt;
            if (status.Loading || now < next) return;
            status.Loading = true;
            var worker = new BackgroundWorker();
            bool local = source == ExpeditionSource.LogHx;
            worker.DoWork += delegate(object sender, DoWorkEventArgs args) {
                args.Result = local ? LogHxSpotFeed.Read(DateTime.UtcNow) : ExpeditionFeeds.Fetch(source);
            };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs args) {
                worker.Dispose();
                if (IsDisposed || Disposing) return;
                status.Loading = false;
                bool firstFailure = status.Failure == null;
                string failure = args.Error == null ? null : DescribeFeedFailure(args.Error);
                if (failure != status.Failure)
                {
                    string name = ExpeditionFeeds.Name(source);
                    DiagnosticEvent(failure == null ? "Источник " + name + " снова доступен."
                        : "Источник " + name + " недоступен (" + failure + ").");
                    status.Failure = failure;
                }
                DateTime received = DateTime.UtcNow;
                if (failure == null)
                {
                    var data = (ExpeditionFeedData)args.Result;
                    if (local) expeditionCatalog.MergeLogHx(data, received);
                    else expeditionCatalog.Replace(source, data);
                    if (data.BackupSource != status.BackupSource)
                    {
                        DiagnosticEvent(data.BackupSource.HasValue ? "Лента WWFF Spotline не ответила. Получаю споты через Spothole."
                            : "Лента WWFF Spotline снова отвечает.");
                        status.BackupSource = data.BackupSource;
                    }
                    status.LastSuccess = received;
                }
                TimeSpan interval = ExpeditionFeeds.Interval(source);
                bool retrySoon = CanRetrySpotFeedSoon(source, args.Error);
                status.NextAttempt = received + (failure == null ? interval
                    : SourceRetryDelay(source, args.Error, received, firstFailure));
                status.NextManualAttempt = retrySoon ? received + LiveSpotRetry
                    : failure == null && source != ExpeditionSource.Wwff && interval == ExpeditionFeeds.LiveSpotInterval
                        ? now + interval : status.NextAttempt;
            };
            worker.RunWorkerAsync();
        }

        internal static string DescribeFeedFailure(Exception error)
        {
            var network = error as WebException;
            if (network != null)
            {
                if (network.Status == WebExceptionStatus.Timeout) return "тайм-аут";
                var response = network.Response as HttpWebResponse;
                if (response != null) return "HTTP " + (int)response.StatusCode;
                if (network.Status == WebExceptionStatus.NameResolutionFailure) return "ошибка DNS";
                return "ошибка соединения";
            }
            if (error is DirectoryNotFoundException || error is FileNotFoundException) return "файл спотов не найден";
            if (error is UnauthorizedAccessException) return "нет доступа";
            if (error is InvalidDataException || error is SerializationException) return "ошибка формата данных";
            return "ошибка чтения";
        }

        static bool CanRetrySpotFeedSoon(ExpeditionSource source, Exception error)
        {
            return source != ExpeditionSource.Wwff && ExpeditionFeeds.Interval(source) == ExpeditionFeeds.LiveSpotInterval
                && ExpeditionFeeds.IsConnectionFailure(error);
        }

        internal static TimeSpan SourceRetryDelay(ExpeditionSource source, Exception error, DateTime now, bool firstFailure)
        {
            if (source == ExpeditionSource.LogHx) return TimeSpan.FromSeconds(10);
            if (firstFailure && CanRetrySpotFeedSoon(source, error)) return LiveSpotRetry;
            var network = error as WebException;
            var response = network == null ? null : network.Response;
            using (response)
                return RetryAfter(response == null ? null : response.Headers["Retry-After"], now);
        }

        internal static TimeSpan RetryAfter(string header, DateTime now)
        {
            int seconds;
            DateTimeOffset date;
            double delay = 0;
            if (Int32.TryParse(header, NumberStyles.None, CultureInfo.InvariantCulture, out seconds)) delay = seconds;
            else if (DateTimeOffset.TryParse(header, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date))
                delay = (date.UtcDateTime - now).TotalSeconds;
            return TimeSpan.FromSeconds(Math.Max(300, delay));
        }

        string ExpeditionSourceStatus(ExpeditionSource[] selected, DateTime now)
        {
            if (selected.Length == 0) return "Выберите, что искать. Список обновится автоматически.";
            var unavailable = new List<string>();
            var loading = new List<string>();
            var empty = new List<string>();
            string backup = null;
            DateTime last = DateTime.MinValue;
            foreach (ExpeditionSource source in selected)
            {
                FeedStatus status = expeditionFeeds[source];
                string name = ExpeditionFeeds.Name(status.Failure == null ? status.BackupSource ?? source : source);
                if (status.LastSuccess > last) last = status.LastSuccess;
                if (status.Loading) loading.Add(name);
                else if (status.Failure != null) unavailable.Add(name + " (" + status.Failure + ")");
                else if ((source == ExpeditionSource.LogHx || source == ExpeditionSource.Pota || source == ExpeditionSource.Wwff)
                    && !expeditionCatalog.HasRecentSpots(source, now)) empty.Add(name);
                else if (status.BackupSource.HasValue) backup = name;
            }
            var parts = new List<string>();
            if (unavailable.Count > 0) parts.Add("Недоступно " + String.Join(", ", unavailable));
            if (backup != null) parts.Add(backup);
            if (empty.Count > 0) parts.Add("Без свежих спотов FT8/FT4 — " + String.Join(", ", empty));
            if (loading.Count > 0) parts.Add("Загружаю " + String.Join(", ", loading));
            if (last != DateTime.MinValue) parts.Add("Обновлено " + last.ToLocalTime().ToString("HH:mm:ss"));
            return String.Join(" · ", parts);
        }

        void SeedExpeditionDemo(DateTime now)
        {
            expeditionCatalog.Replace(ExpeditionSource.Ng3k, new ExpeditionFeedData { Activities = new[] {
                new ExpeditionActivity { Source = ExpeditionSource.Ng3k, Call = "V6AAA", Place = "Микронезия",
                    Mode = "FT8", StartUtc = now.Date.AddDays(-1), EndUtc = now.Date.AddDays(6) },
                new ExpeditionActivity { Source = ExpeditionSource.Ng3k, Call = "T88BBB", Place = "Палау",
                    Mode = "FT8 FT4", StartUtc = now.Date.AddDays(3), EndUtc = now.Date.AddDays(8) },
            } });
            expeditionCatalog.Replace(ExpeditionSource.DxSpotCalendar, new ExpeditionFeedData { Activities = new[] {
                new ExpeditionActivity { Source = ExpeditionSource.DxSpotCalendar, Call = "R4AAA/P", Place = "RFF-0123",
                    ReferenceProgram = "WWFF", Mode = "FT8", StartUtc = now.Date.AddDays(1), EndUtc = now.Date.AddDays(3) },
            } });
            expeditionCatalog.Replace(ExpeditionSource.DxRanks, new ExpeditionFeedData { Entities = new[] {
                new ExpeditionEntity { Dxcc = 173, Name = "Острова Гилберта", Prefix = "T30", Rank = 42 },
            } });
            expeditionCatalog.Replace(ExpeditionSource.DxSpot, new ExpeditionFeedData { Activities = new[] {
                DemoSpot(ExpeditionSource.DxSpot, "T30AAA", "", "FT8", 21074000, now.AddMinutes(-3), 173),
            } });
            expeditionCatalog.Replace(ExpeditionSource.Wwff, new ExpeditionFeedData { Activities = new[] {
                DemoSpot(ExpeditionSource.WwffSpothole, "DL1AAA/P", "DLFF-0123", "FT8", 10136000, now.AddMinutes(-4)),
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
            expeditionCatalog.Replace(ExpeditionSource.LogHx, new ExpeditionFeedData());
            var example = new[] { ExpeditionSource.Pota, ExpeditionSource.Wwff, ExpeditionSource.LogHx };
            expeditionFeeds[ExpeditionSource.Pota] = new FeedStatus { LastSuccess = now };
            expeditionFeeds[ExpeditionSource.Wwff] = new FeedStatus { BackupSource = ExpeditionSource.WwffSpothole, LastSuccess = now };
            expeditionFeeds[ExpeditionSource.LogHx] = new FeedStatus { LastSuccess = now };
            engine.Cqs.Observe(new DecodedMessage { Message = "CQ V6AAA QJ57", IsNew = true, Mode = "~",
                Milliseconds = (uint)now.TimeOfDay.TotalMilliseconds, Snr = -10, DeltaFrequency = 1128 },
                engine.Status, now);
            var form = new ExpeditionDialog(new ExpeditionOptions { LogHxSpots = true },
                (dialog, values) => values, options => { }, options => { }, () => { }, () => { });
            form.Render(expeditionCatalog, engine, true, "Демо · " + ExpeditionSourceStatus(example, now), now);
            return form;
        }
    }
}
