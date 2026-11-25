using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace JtdxAutoCq
{
    public sealed partial class MainForm
    {
        readonly Button whyCq = new Button();
        LogbookHistory startupHistory = LogbookHistory.Empty;
        bool startingWithHistory, applyingProfile, loadingTargets;
        int historyStartRequest;

        void InitializeOperatorTools()
        {
            whyCq.Text = "Почему пропущена?";
            whyCq.SetBounds(469, 571, 208, 30);
            whyCq.Enabled = false;
            whyCq.Click += delegate { Guard(ShowStationReason); };
            cqView.SelectedIndexChanged += delegate { whyCq.Enabled = cqView.SelectedItems.Count == 1; };
            Controls.Add(whyCq);

            var tools = new Button { Text = "Журнал и дипломы ▾" };
            tools.SetBounds(744, 571, 190, 30);
            tools.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            StyleButton(tools, Color.White, Ink, Color.FromArgb(201, 213, 224));
            Controls.Add(tools);
            var menu = new ContextMenuStrip();
            var source = menu.Items.Add("Основной журнал", null, delegate { Guard(ChooseLogbook); });
            var profiles = menu.Items.Add("Профили настроек", null, delegate { Guard(ShowProfiles); });
            var targets = menu.Items.Add("Подбор целей DXCC", null, delegate { Guard(ChooseDxccFromLogbook); });
            var awards = menu.Items.Add("Дипломы HAMLOG", null, delegate { Guard(ShowAwards); });
            var awardMonitor = menu.Items.Add("Мониторинг дипломов", null, delegate { Guard(() => StartRadioSession(preferences.Awards)); });
            menu.Items.Add("Итоги сеанса", null, delegate { Guard(ShowSessionSummary); });
            menu.Opening += delegate {
                source.Enabled = !engine.Armed && !startingWithHistory;
                targets.Enabled = source.Enabled && !loadingTargets;
                awards.Enabled = targets.Enabled;
                awardMonitor.Enabled = awards.Enabled && engine.Status != null && !engine.Status.TxEnabled && !engine.Status.Transmitting
                    && preferences.Awards.Programs.Any(program => program.Selected && program.CanSelectForSearch(DateTime.UtcNow));
                profiles.Enabled = !startingWithHistory;
            };
            tools.Click += delegate { menu.Show(tools, new Point(0, tools.Height)); };
            Disposed += delegate { menu.Dispose(); };
        }

        string NativeLogbook
        { get { return demo ? null : Path.Combine(settings.Profile, "wsjtx_log.adi"); } }

        string AutomaticLogbook()
        {
            if (demo) return @"C:\Radio\Log\Example.lhx2";
            Process[] loggers = Process.GetProcessesByName("LogHX3");
            if (loggers.Length == 0) return NativeLogbook;
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (Process log in loggers)
                {
                    try
                    {
                        string installed = RunningExecutable(log.Id);
                        if (!String.IsNullOrEmpty(installed)) folders.Add(Path.Combine(Path.GetDirectoryName(installed), "Log"));
                    }
                    catch (Win32Exception) { }
                    catch (InvalidOperationException) { }
                }
            }
            finally { foreach (Process log in loggers) log.Dispose(); }
            if (folders.Count == 0)
                folders.Add(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory), "LogHX", "Log"));
            var available = new List<string>();
            foreach (string folder in folders.Where(Directory.Exists))
            {
                try { available.AddRange(Directory.GetFiles(folder, "*.lhx2")); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (available.Count == 1) return available[0];
            string own = engine.Status == null ? "" : Callsign.Clean(engine.Status.DeCall);
            string[] matches = available.Where(file => String.Equals(Path.GetFileNameWithoutExtension(file), own,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            return matches.Length == 1 ? matches[0] : NativeLogbook;
        }

        string PrimaryLogbook()
        { return String.IsNullOrWhiteSpace(preferences.LogbookPath) ? AutomaticLogbook() : preferences.LogbookPath; }

        string LogbookName(string file)
        {
            if (String.Equals(file, NativeLogbook, StringComparison.OrdinalIgnoreCase)) return "JTDX";
            return String.Equals(Path.GetExtension(file), ".lhx2", StringComparison.OrdinalIgnoreCase) ? "LogHX" : "экспорт ADIF";
        }

        void ReadLogbook(string file, Action<LogbookHistory, Exception> completed)
        {
            if (demo) { completed(DemoLogbook(), null); return; }
            string local = NativeLogbook;
            var worker = new BackgroundWorker();
            worker.DoWork += delegate(object sender, DoWorkEventArgs eventArgs) {
                eventArgs.Result = String.Equals(file, local, StringComparison.OrdinalIgnoreCase)
                    ? new LogbookHistory(local, JtdxAdif.Contacts(local)) : LogbookHistory.Read(file);
            };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs eventArgs) {
                worker.Dispose();
                if (IsDisposed || Disposing) return;
                completed(eventArgs.Error == null ? (LogbookHistory)eventArgs.Result : null, eventArgs.Error);
            };
            worker.RunWorkerAsync();
        }

        void StartAutomation()
        {
            if (startingWithHistory || engine.Armed) return;
            string file = PrimaryLogbook();
            int request = ++historyStartRequest;
            startingWithHistory = true;
            UpdateUi();
            ReadLogbook(file, (history, failure) => {
                if (request != historyStartRequest) return;
                if (failure != null && !String.Equals(file, NativeLogbook, StringComparison.OrdinalIgnoreCase))
                {
                    Log("Основной журнал недоступен. Проверяю локальный журнал JTDX.");
                    ReadLogbook(NativeLogbook, (local, error) => FinishStart(request, local, error));
                    return;
                }
                FinishStart(request, history, failure);
            });
        }

        void FinishStart(int request, LogbookHistory history, Exception failure)
        {
            if (request != historyStartRequest) return;
            startupHistory = history ?? LogbookHistory.Empty;
            if (failure != null) Log("История связей недоступна. Автомат запустится без данных прошлых сеансов.");
            else if (!demo) Log("История связей загружена из журнала " + LogbookName(history.Path) + ".");
            startingWithHistory = false;
            UpdateUi();
            Guard(() => StartRadioSession(null));
        }

        void ShowAwards()
        {
            if (engine.Armed || startingWithHistory || loadingTargets) return;
            string own = engine.Status == null ? "" : Callsign.Clean(engine.Status.DeCall);
            var catalog = preferences.Awards ?? new AwardCatalog();
            using (var dialog = new AwardsDialog(catalog, own, demo ? null : Path.Combine(settings.StateDirectory, "hamlog-rules"), Log))
            {
                dialog.ShowDialog(this);
                preferences.Awards = dialog.Catalog;
                try { preferenceStore.Save(preferences); }
                catch { preferences.Awards = catalog; throw; }
            }
        }

        void HaltAutomation()
        {
            ++historyStartRequest;
            startingWithHistory = false;
            engine.Stop("Остановлено оператором.", true);
            UpdateUi();
        }

        void ChooseLogbook()
        {
            string automatic = AutomaticLogbook();
            using (var dialog = new LogbookSourceDialog(preferences.LogbookPath, automatic, LogbookName(automatic)))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string old = preferences.LogbookPath;
                preferences.LogbookPath = dialog.ChosenFile;
                try { preferenceStore.Save(preferences); }
                catch { preferences.LogbookPath = old; throw; }
            }
        }

        void ShowProfiles()
        {
            var profiles = (preferences.Profiles ?? new List<OperatorProfile>())
                .Where(profile => profile != null && !String.IsNullOrWhiteSpace(profile.Name) && profile.Options != null)
                .Select(profile => new OperatorProfile { Name = profile.Name, Options = profile.Options.Copy() }).ToList();
            foreach (var profile in profiles) profile.Options.Validate();
            using (var dialog = new ProfilesDialog(profiles, engine.Options.Copy(), !engine.Armed))
            {
                dialog.ShowDialog(this);
                if (dialog.Changed)
                {
                    var old = preferences.Profiles;
                    preferences.Profiles = profiles;
                    try { preferenceStore.Save(preferences); }
                    catch { preferences.Profiles = old; throw; }
                }
                if (dialog.Chosen != null) ApplyOptions(dialog.Chosen);
            }
        }

        void ApplyOptions(AutoCqOptions options)
        {
            if (engine.Armed) return;
            options.Validate();
            bool prepare = jtdxPresent && (options.Wait73Periods != engine.Options.Wait73Periods
                || options.CallAttempts != engine.Options.CallAttempts);
            applyingProfile = true;
            try
            {
                engine.Options = options;
                waitPeriods.Value = options.Wait73Periods;
                callAttempts.Value = options.CallAttempts;
                runMinutes.Value = options.RunMinutes;
                cqAttempts.Value = options.CqAttemptsBeforeSearch;
                switchMinutes.Value = options.SwitchPeriodMinutes;
                queueMinutes.Value = options.QueueMinutes;
                searchEnabled.Checked = options.SearchEnabled;
                priorityAfter73.Checked = options.PriorityAfter73;
                monitor.Checked = options.MonitorEnabled;
            }
            finally { applyingProfile = false; }
            queueDirty = queueUiDirty = cqUiDirty = true;
            SaveOperatorState();
            UpdateUi();
            Log(prepare ? "Профиль применён. Закройте JTDX, нажмите «1. Подготовить» и запустите JTDX со счётчиками профиля."
                : "Профиль настроек применён.");
        }

        void ChooseDxccFromLogbook()
        {
            if (loadingTargets) return;
            DxccCountries table = CountryTable();
            string file = PrimaryLogbook();
            string own = engine.Status == null ? "" : Callsign.Clean(engine.Status.DeCall);
            string band = engine.Status == null ? null : JtdxAdif.Band(engine.Status.Frequency);
            loadingTargets = true;
            Log("Читаю историю для подбора целей DXCC…");
            ReadLogbook(file, (history, error) => Guard(() => {
                loadingTargets = false;
                if (engine.Armed) return;
                if (error != null) throw new IOException("Не удалось прочитать основной журнал. " + error.Message);
                using (var dialog = new DxccTargetsDialog(table, history, engine.Options, own, band, LogbookName(file)))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    engine.Options.MonitorDxcc = engine.Options.MonitorDxcc.Concat(dialog.SelectedPrefixes).Distinct().ToArray();
                    queueDirty = cqUiDirty = true;
                    SaveOperatorState();
                    Log("Отмеченные страны добавлены в цели мониторинга.");
                }
            }));
        }

        void ShowStationReason()
        {
            string call = SelectedCqCall();
            DateTime now = DateTime.UtcNow;
            DxccCountry country = dxcc == null ? null : dxcc.Find(call);
            using (var dialog = new StationDecisionDialog(call, country, engine.ExplainCq(call, now), engine.Cqs.Find(call)))
                dialog.ShowDialog(this);
        }

        void SaveSessionSummary(OperatingSession session)
        {
            preferences.LastSession = session.Snapshot();
            if (demo) return;
            try { preferenceStore.Save(preferences); }
            catch (IOException) { DiagnosticEvent("Не удалось сохранить итоги сеанса."); }
            catch (UnauthorizedAccessException) { DiagnosticEvent("Не удалось сохранить итоги сеанса."); }
        }

        void ShowSessionSummary()
        {
            OperatingSession session = engine.Session ?? preferences.LastSession;
            if (session == null) throw new InvalidOperationException("Запустите автомат или мониторинг, чтобы появились итоги сеанса.");
            string file = PrimaryLogbook();
            using (var dialog = new SessionSummaryDialog(session.Snapshot()))
            {
                // Start the read after the dialog has a message loop.
                dialog.Shown += delegate { ReadLogbook(file, (history, error) => {
                    if (!dialog.IsDisposed) dialog.Fill(history, LogbookName(file), error);
                }); };
                dialog.ShowDialog(this);
            }
        }

        static DxccCountries DemoCountryTable()
        {
            return DxccCountries.Parse(new[] {
                "США: 05: 08: NA: 37: 91: 5: K:", "    K,W,N,AA;",
                "Япония: 25: 45: AS: 36: -138: -9: JA:", "    JA,JE,JF;",
                "Германия: 14: 28: EU: 52: -10: -1: DL:", "    DA,DL;",
                "Бразилия: 11: 15: SA: -10: 48: 3: PY:", "    PP,PY;",
                "Чили: 12: 14: SA: -34: 71: 4: CE:", "    CA,CE;",
                "Новая Зеландия: 32: 60: OC: -41: -174: -12: ZL:", "    ZL;",
            });
        }

        static readonly DateTime DemoStart = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        static LogbookHistory DemoLogbook()
        {
            return new LogbookHistory(@"C:\Radio\Log\Example.lhx2", new List<LogbookQso> {
                new LogbookQso { Call = "W2ABC", Band = "20m", Mode = "FT8", EndedUtc = DemoStart.AddMinutes(2) },
                new LogbookQso { Call = "DL1AAA", Band = "20m", Mode = "SSB" },
            });
        }

        // The screenshot renderer opens these same forms using fictional data.
        Form[] OperatorToolsDemoForms(string awardCache)
        {
            var table = DemoCountryTable();
            var history = DemoLogbook();
            var session = new OperatingSession {
                StartedUtc = DemoStart, StoppedUtc = DemoStart.AddMinutes(20), Band = "20m", Mode = "FT8", Monitoring = true,
                StopReason = "Время вышло. Мониторинг завершён, JTDX на приёме.",
                Contacts = new List<SessionContact> {
                    new SessionContact { Call = "W2ABC", LoggedUtc = DemoStart.AddMinutes(2), ConfirmedUtc = DemoStart.AddMinutes(2.5) },
                    new SessionContact { Call = "PY2XYZ", LoggedUtc = DemoStart.AddMinutes(10), MissingUtc = DemoStart.AddMinutes(11) },
                },
            };
            var summary = new SessionSummaryDialog(session);
            summary.Fill(history, "LogHX");
            var profiles = new List<OperatorProfile> {
                new OperatorProfile { Name = "40 м вечером", Options = new AutoCqOptions { RunMinutes = 40 } },
                new OperatorProfile { Name = "Поиск для диплома", Options = new AutoCqOptions { MonitorEnabled = true, MonitorDxcc = new[] { "JA", "PY" } } },
            };
            var targets = new DxccTargetsDialog(table, history, profiles[1].Options, "", "20m", "LogHX");
            targets.SelectDemoCountry();
            var diploma = new AwardProgram { Title = "Страны на 20 м", Goals = new List<AwardGoal> {
                new AwardGoal { Kind = "dxcc", Value = "K", Band = "20m", Mode = "FT8" },
                new AwardGoal { Kind = "dxcc", Value = "JA", Band = "20m", Mode = "FT8" },
            } };
            var arck = HamlogAward.Arck35.Create();
            arck.HasIssued = true;
            arck.Sponsor = "Радиоклуб «Кристалл»";
            var romania = HamlogAward.Romania.Create();
            romania.Selected = true;
            romania.Sponsor = "Румынские дипломы";
            var raem = new AwardProgram { Title = "RAEM", Url = HamlogLinks.Origin + "raem/", Sponsor = "СРР",
                RuleDocumentStatus = "downloaded", RuleNote = "По положению подходят связи телеграфом. AutoCQ использует FT8 и FT4."
            };
            var oldTula = HamlogAward.Tula.Create();
            oldTula.Sponsor = "Тульское отделение СРР";
            var lake = new AwardProgram { Title = "Соль России · Баскунчак", Url = HamlogLinks.Origin + "club/demo/106/",
                Sponsor = "Астраханская область", Selected = true, RuleDocumentStatus = "downloaded",
                DownloadedRules = new HamlogRulePlan { Kind = "rda-search", SearchRegion = "AO", Required = 106, FromUtc = "2022-12-25" }
            };
            var distance = new AwardProgram { Title = "Дальние цифровые связи", Url = HamlogLinks.Origin + "club/demo/1100/",
                Sponsor = "Российский цифровой радиолюбительский клуб", Selected = true, RuleDocumentStatus = "downloaded",
                DownloadedRules = new HamlogRulePlan { Kind = "contact-points", FromUtc = "2014-03-21", Required = 100,
                    MemberPoints = 1, ExtraPoints = 1,
                    Contacts = new HamlogContactCriteria { Repeat = HamlogContactCriteria.BandMode, MinimumDistanceKm = 10000 }
                }
            };
            var local = new AwardProgram { Title = "Личный список позывных", Url = HamlogLinks.Origin + "club/demo/19/",
                ManualSearch = true, Goals = new List<AwardGoal> { new AwardGoal { Kind = "call", Value = "W2ABC" } } };
            var example = new AwardCatalog { Callsign = "K1ABC", PublicCatalogUtc = DateTime.UtcNow.AddMinutes(-20),
                Programs = new List<AwardProgram> { arck, romania, raem, oldTula, lake, distance, local }
            };
            foreach (AwardProgram program in example.Programs)
            {
                program.PublicEntry = true;
                program.Section = "Клубные и региональные";
                program.RuleCompilerVersion = HamlogPlanCompiler.Version;
                program.RulesCheckedUtc = DateTime.UtcNow;
                program.Validate();
            }
            var awards = new AwardsDialog(example, "K1ABC", cache: awardCache, demo: true);
            awards.SelectDemoProgram(distance);
            return new Form[] {
                new LogbookSourceDialog("", history.Path, "LogHX"),
                new ProfilesDialog(profiles, profiles[0].Options, true),
                targets,
                new StationDecisionDialog("JA1AAA", table.Find("JA1AAA"),
                    "Пауза после вызова. После её окончания AutoCQ ждёт свежее сообщение.",
                    new CqStation { Decode = new DecodedMessage { Message = "CQ JA1AAA PM95" }, HeardUtc = DemoStart }),
                summary,
                awards,
                new AwardGoalsDialog(diploma),
                new HamlogAccountDialog("K1ABC", ""),
                new HamlogPlanDialog(distance),
            };
        }
    }
}
