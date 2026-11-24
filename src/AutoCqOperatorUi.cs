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
            var awardMonitor = menu.Items.Add("Мониторинг дипломов", null, delegate { Guard(() => StartAutomation(true)); });
            menu.Items.Add("Итоги сеанса", null, delegate { Guard(ShowSessionSummary); });
            menu.Opening += delegate {
                source.Enabled = !engine.Armed && !startingWithHistory;
                targets.Enabled = source.Enabled && !loadingTargets;
                awards.Enabled = targets.Enabled;
                awardMonitor.Enabled = awards.Enabled && engine.Status != null && !engine.Status.TxEnabled && !engine.Status.Transmitting
                    && preferences.Awards.Programs.Any(program => program.Selected && program.HasSearchRules && !program.ClosedByIssuance);
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
        { StartAutomation(false); }

        void StartAutomation(bool awards)
        {
            if (startingWithHistory || engine.Armed) return;
            string file = PrimaryLogbook();
            bool needsPrimary = awards && preferences.Awards.Programs.Any(program => program.Selected && program.UsesPointRules);
            int request = ++historyStartRequest;
            startingWithHistory = true;
            UpdateUi();
            ReadLogbook(file, (history, failure) => {
                if (request != historyStartRequest) return;
                if (failure != null && needsPrimary)
                {
                    startingWithHistory = false;
                    UpdateUi();
                    Guard(() => { throw new InvalidOperationException("Не удалось открыть основной журнал для расчёта очков. Проверьте его в меню «Основной журнал»."); });
                    return;
                }
                if (failure != null && !String.Equals(file, NativeLogbook, StringComparison.OrdinalIgnoreCase))
                {
                    Log("Основной журнал недоступен. Проверяю локальный журнал JTDX.");
                    ReadLogbook(NativeLogbook, (local, error) => FinishStart(request, local, error, awards));
                    return;
                }
                FinishStart(request, history, failure, awards);
            });
        }

        void FinishStart(int request, LogbookHistory history, Exception failure, bool awards)
        {
            if (request != historyStartRequest) return;
            startupHistory = history ?? LogbookHistory.Empty;
            if (failure != null) Log("История связей недоступна. Автомат запустится без данных прошлых сеансов.");
            else if (!demo) Log("История связей загружена из журнала " + LogbookName(history.Path) + ".");
            if (awards && !demo)
            {
                RefreshAwardCredit(request);
                return;
            }
            startingWithHistory = false;
            UpdateUi();
            Guard(() => StartRadioSession(awards ? preferences.Awards : null));
        }

        void RefreshAwardCredit(int request)
        {
            string own = engine.Status == null ? null : Callsign.Parse(engine.Status.DeCall);
            if (own == null)
            {
                startingWithHistory = false;
                UpdateUi();
                Guard(() => { throw new InvalidOperationException("Подключите JTDX с вашим позывным перед сверкой с HAMLOG."); });
                return;
            }
            AwardCatalog selected = preferences.Awards.Copy();
            DateTime now = DateTime.UtcNow;
            var programs = selected.Programs.Where(program => program.Selected && program.HasSearchRules && !program.ClosedByIssuance
                && (program.UsesOnlineRules || program.UsesPointRules) && (!program.Until.HasValue || now < program.Until.Value)).ToArray();
            if (programs.Length == 0)
            {
                startingWithHistory = false;
                UpdateUi();
                Guard(() => StartRadioSession(selected));
                return;
            }
            Log("Сверяю зачёт выбранных дипломов по позывному " + own + ".");
            var failures = new string[programs.Length];
            var worker = new BackgroundWorker();
            worker.DoWork += delegate {
                System.Threading.Tasks.Parallel.For(0, programs.Length,
                    new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 3 }, index => {
                        AwardProgram program = programs[index];
                        try { program.Hamlog = HamlogStandingClient.Read(program.Template, own); }
                        catch (Exception error)
                        {
                            failures[index] = program.Title + ". " + error.Message;
                            if (!program.UsesGeographicSearch) program.Selected = false;
                        }
                    });
            };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs args) {
                worker.Dispose();
                if (IsDisposed || Disposing || request != historyStartRequest) return;
                startingWithHistory = false;
                UpdateUi();
                Guard(() => {
                    if (args.Error != null) throw new InvalidOperationException("Не удалось сверить зачёт с HAMLOG.", args.Error);
                    if (engine.Status == null || Callsign.Clean(engine.Status.DeCall) != own
                        || engine.Status.TxEnabled || engine.Status.Transmitting)
                        throw new InvalidOperationException("Состояние JTDX изменилось во время сверки. Запустите поиск на приёме.");
                    DateTime checkedAt = DateTime.UtcNow;
                    for (int index = 0; index < programs.Length; index++)
                    {
                        AwardProgram snapshot = programs[index];
                        if (failures[index] != null)
                        {
                            Log((snapshot.UsesGeographicSearch ? "Зачёт пока не сверён. Поиск по территории диплома доступен. " : "Диплом пропущен. ")
                                + failures[index]);
                            continue;
                        }
                        snapshot.Selected = new AwardProgress(snapshot, startupHistory, own, engine.DxccForCall)
                            .CanSelectForSearch(checkedAt);
                        AwardProgram saved = preferences.Awards.Programs.FirstOrDefault(program => program.Id == snapshot.Id);
                        if (saved != null)
                        {
                            saved.Hamlog = snapshot.Hamlog;
                            saved.Selected = snapshot.Selected;
                        }
                    }
                    if (failures.All(failure => failure != null)
                        && !selected.Programs.Any(program => program.Selected && (program.UsesLocalTargets || program.UsesGeographicSearch)))
                        throw new InvalidOperationException("HAMLOG не подтвердил зачёт выбранных дипломов. Подробности в журнале AutoCQ.");
                    preferenceStore.Save(preferences);
                    Log("Зачёт обновлён для " + failures.Count(failure => failure == null) + " дипломов.");
                    StartRadioSession(selected);
                });
            };
            worker.RunWorkerAsync();
        }

        void ShowAwards()
        {
            if (engine.Armed || startingWithHistory || loadingTargets) return;
            loadingTargets = true;
            string file = PrimaryLogbook();
            ReadLogbook(file, (history, failure) => {
                loadingTargets = false;
                if (engine.Armed || startingWithHistory) return;
                if (failure != null) Log("Не удалось прочитать основной журнал для дипломов. Проверьте его в меню «Основной журнал».");
                Guard(() => EditAwardCatalog(history ?? LogbookHistory.Empty));
            });
        }

        void EditAwardCatalog(LogbookHistory history)
        {
            string own = engine.Status == null ? "" : Callsign.Clean(engine.Status.DeCall);
            var catalog = preferences.Awards ?? new AwardCatalog();
            using (var dialog = new AwardsDialog(catalog, history, own, call => {
                DxccCountry country = CountryTable().Find(call);
                return country == null ? null : country.Prefix;
            }, demo ? null : Path.Combine(settings.StateDirectory, "hamlog-rules"), Log))
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
            var stations = new AwardProgram { Title = "Две японские станции", Goals = new List<AwardGoal> {
                new AwardGoal { Kind = "mask", Value = "JA*", Band = "20m", Count = 2 },
            } };
            var falcon = HamlogAward.Murom.Create();
            var tula = HamlogAward.Tula.Create();
            var russia = HamlogAward.Russia.Create();
            var gunsmiths = HamlogAward.Gunsmiths.Create();
            var arck = HamlogAward.Arck35.Create();
            arck.HasIssued = true;
            var counties = new AwardProgram { Title = "New England County Award (NECA)",
                Url = HamlogLinks.Origin + "neca/", Sponsor = "HAMLOG", HasIssued = false };
            var angels = new AwardProgram { Title = "Небесные ангелы", Url = HamlogLinks.Origin + "club/nebo/1572/",
                Sponsor = "Рыцари неба", Selected = true, RuleDocumentStatus = "downloaded",
                DownloadedRules = new HamlogRulePlan { Kind = "sky-hunter", FromUtc = "2025-07-30",
                    Required = 95, MemberPoints = 10, ExtraPoints = 20,
                    Calls = new[] { "U4MIR", "UA9CES", "R2AKN", "R4CQ", "RA6F", "LZ1ZF", "LZ05RN", "LZ55ZF" } }
            };
            var border = new AwardProgram { Title = "Ни шагу назад", Url = HamlogLinks.Origin + "club/rcpw/1800/",
                Sponsor = "Радиоклуб пограничников RCPW", Selected = true, RuleDocumentStatus = "downloaded",
                DownloadedRules = new HamlogRulePlan { Kind = "rcpw-hunter", FromUtc = "2026-06-22", BonusUntilUtc = "2026-07-01",
                    Required = 100, AlternativeRequired = 50, ExtraPoints = 10, MemberPoints = 5 }
            };
            var romania = new AwardProgram { Title = "День объединения Румынии", Url = HamlogLinks.Origin + "club/demo/2026/",
                Sponsor = "AJR DX Radio Alba", Selected = true, RuleDocumentStatus = "downloaded",
                DownloadedRules = new HamlogRulePlan { Kind = "contact-points", FromUtc = "2026-11-20", UntilUtc = "2026-12-01",
                    Required = 2, MemberPoints = 1, ExtraPoints = 1, Calls = new[] { "YQ0U" },
                    Contacts = new HamlogContactCriteria { Repeat = HamlogContactCriteria.BandMode }
                }
            };
            var raem = new AwardProgram { Title = "RAEM", Url = HamlogLinks.Origin + "raem/", Sponsor = "СРР", RuleDocumentStatus = "downloaded",
                RuleNote = "Охотнику нужны 68 очков за связи телеграфом на КВ. Для автоматического поиска в AutoCQ доступны FT8 и FT4."
            };
            var saratov = new AwardProgram { Title = "Земля Саратовская — левый берег", Url = HamlogLinks.Origin + "club/demo/1802/",
                Sponsor = "Саратовское отделение СРР", Selected = true, RuleDocumentStatus = "downloaded",
                DownloadedRules = new HamlogRulePlan { Kind = "contact-points", FromUtc = "2024-01-07",
                    Required = 8, MemberPoints = 1, ExtraPoints = 1,
                    Contacts = new HamlogContactCriteria { Repeat = HamlogContactCriteria.District, CountyPrefix = "SA", LastCounty = 58,
                        EligibleCounties = new[] { 19, 23, 28, 29, 31, 32, 35, 36, 38, 40, 41, 42, 44, 45, 46, 51, 54, 56 },
                        Bands = new[] { "160m", "80m", "60m", "40m", "30m", "20m", "17m", "15m", "12m", "10m" } }
                }
            };
            var tulaDistricts = new AwardProgram { Title = "Тула", Url = HamlogLinks.Origin + "club/demo/1146/",
                Sponsor = "Тульское отделение СРР", Selected = true, RuleDocumentStatus = "downloaded",
                DownloadedRules = new HamlogRulePlan { Kind = "contact-points", FromUtc = "2022-03-01",
                    Required = 1146, MemberPoints = 2, ExtraPoints = 1,
                    Contacts = new HamlogContactCriteria { Repeat = HamlogContactCriteria.BandMode, CountyPrefix = "TL", LastCounty = 38,
                        MultiplyByCountyCount = true, ActivatorAreas = new[] { "TL" } },
                    BonusGroups = new[] { new HamlogBonusGroup { Area = true, Points = 2, Targets = new[] { "TL" } } }
                }
            };
            var kazan = new AwardProgram { Title = "Казань", Url = HamlogLinks.Origin + "club/demo/2505/",
                Sponsor = "Казанский радиоклуб", Selected = true, RuleDocumentStatus = "downloaded",
                DownloadedRules = new HamlogRulePlan { Kind = "contact-points", FromUtc = "2024-01-01",
                    Required = 25, MemberPoints = 1, ExtraPoints = 1,
                    Contacts = new HamlogContactCriteria { Repeat = HamlogContactCriteria.BandMode,
                        GroupModes = true, CountyPrefix = "TA", LastCounty = 7,
                        Additional = new[] { new HamlogContactRequirement {
                            Kind = HamlogContactRequirement.Counties, Required = 5, ReportedByHamlog = true
                        } }
                    }
                }
            };
            var yesenin = new AwardProgram { Title = "Родина русского поэта С. А. Есенина", Url = HamlogLinks.Origin + "club/demo/1030/",
                Sponsor = "Рязанская область", Selected = true, RuleDocumentStatus = "downloaded",
                DownloadedRules = new HamlogRulePlan { Kind = "contact-points", FromUtc = "2025-10-01",
                    Required = 30, MemberPoints = 1, ExtraPoints = 1,
                    BonusGroups = new[] { new HamlogBonusGroup { Area = true, Points = 1, Targets = new[] { "RA" } } },
                    Contacts = new HamlogContactCriteria { Repeat = HamlogContactCriteria.EveryQso, ShortcutDay = "10-03",
                        ShortcutContacts = 3, Adjustments = new[] { new HamlogPointAdjustment { Factor = 3,
                            AnnualFrom = "10-01", AnnualUntil = "10-07" } } }
                }
            };
            var lake = new AwardProgram { Title = "Соль России · Баскунчак", Url = HamlogLinks.Origin + "club/demo/106/",
                Sponsor = "Астраханская область", Selected = true, RuleDocumentStatus = "downloaded",
                DownloadedRules = new HamlogRulePlan { Kind = "rda-search", SearchRegion = "AO", Required = 106, FromUtc = "2022-12-25" }
            };
            new HamlogRuleCache(awardCache, (url, cancel) => new HamlogRules(url, "Демонстрационный документ", null),
                (delay, cancel) => { }).Scan(new[] { border }, default(System.Threading.CancellationToken), result => { });
            var custom = new AwardProgram { Title = "Учебный диплом", Url = HamlogLinks.Origin + "club/demo/19/",
                ManualSearch = true, Goals = new List<AwardGoal> { new AwardGoal { Kind = "call", Value = "W2ABC" } } };
            foreach (AwardProgram program in new[] { russia, gunsmiths, falcon, tula, arck, counties, angels, border, romania, raem,
                saratov, tulaDistricts, kazan, yesenin, lake, custom })
            {
                program.PublicEntry = true;
                program.Section = "Клубные и региональные";
            }
            russia.Section = "Международные и национальные";
            counties.Section = "Территориальные";
            russia.Sponsor = "Моя Россия · HAMLOG";
            DateTime october = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
            falcon.Hamlog = new HamlogStanding {
                Callsign = "K1ABC", Points = 78, Required = 85, CheckedUtc = october.AddDays(14).AddHours(11),
                Credits = Enumerable.Range(0, 13).Select(day => new HamlogCredit {
                    Call = "RA3RPW", Band = "20m", Mode = "FT8", DayUtc = october.AddDays(day), Points = 6
                }).ToList()
            };
            tula.Hamlog = new HamlogStanding { Callsign = "K1ABC", Required = 50, CheckedUtc = falcon.Hamlog.CheckedUtc };
            russia.Hamlog = new HamlogStanding {
                Callsign = "K1ABC", Points = 87, Required = 100, CheckedUtc = falcon.Hamlog.CheckedUtc,
                ConfirmedSquares = Enumerable.Range(0, 87).Select(square => "KO" + square.ToString("00")).ToArray()
            };
            gunsmiths.Hamlog = new HamlogStanding { Callsign = "K1ABC", Points = 62, Required = 100, CheckedUtc = falcon.Hamlog.CheckedUtc };
            saratov.Hamlog = new HamlogStanding {
                Callsign = "K1ABC", Points = 5, Required = 8, CheckedUtc = falcon.Hamlog.CheckedUtc,
                Credits = saratov.DownloadedRules.Contacts.EligibleCounties.Take(5).Select((district, index) =>
                    new HamlogCredit { Call = "R4CA" + (char)('A' + index), County = "SA-" + district.ToString("D2"),
                        Locator = "LO43", Band = "40m", Mode = "FT8", DayUtc = october, Points = 1 }).ToList()
            };
            tulaDistricts.Hamlog = new HamlogStanding {
                Callsign = "K1ABC", Points = 144, Required = 1146, CheckedUtc = falcon.Hamlog.CheckedUtc,
                Credits = Enumerable.Range(0, 18).Select(index => new HamlogCredit {
                    Call = "R3PA" + (char)('A' + index), County = "TL-0" + (1 + index % 4), Locator = "KO92",
                    Band = "40m", Mode = "FT8", DayUtc = october, Points = 2
                }).ToList()
            };
            kazan.Hamlog = new HamlogStanding {
                Callsign = "K1ABC", Points = 25, Required = 25, CheckedUtc = falcon.Hamlog.CheckedUtc,
                Credits = Enumerable.Range(0, 25).Select(index => new HamlogCredit {
                    Call = "R4PA" + (char)('A' + index), County = "TA-0" + (1 + index % 4), Locator = "LO44",
                    Band = "40m", Mode = "FT8", DayUtc = october, Points = 1
                }).ToList()
            };
            yesenin.Hamlog = new HamlogStanding {
                Callsign = "K1ABC", Points = 6, Required = 30, CheckedUtc = falcon.Hamlog.CheckedUtc,
                Credits = new List<HamlogCredit> {
                    new HamlogCredit { Call = "R3SAA", Band = "20m", Mode = "FT8", DayUtc = october.AddDays(1), Points = 3 },
                    new HamlogCredit { Call = "R3SAB", Band = "40m", Mode = "FT8", DayUtc = october.AddDays(2), Points = 3 }
                }
            };
            lake.Hamlog = new HamlogStanding {
                Callsign = "K1ABC", Points = 20, Required = 106, CheckedUtc = falcon.Hamlog.CheckedUtc,
                Credits = new List<HamlogCredit> {
                    new HamlogCredit { Call = "R7UAA", Band = "20m", Mode = "FT8", DayUtc = october, Points = 20 }
                }
            };
            var awardHistory = new LogbookHistory(history.Path, history.Records.Concat(falcon.Hamlog.Credits.Take(12).Select(credit =>
                new LogbookQso { Call = credit.Call, Band = credit.Band, Mode = credit.Mode, StartedUtc = credit.DayUtc }))
                .Concat(new[] {
                    new LogbookQso { Call = "R5AV", Band = "40m", Mode = "FT8", StartedUtc = october.AddDays(14) },
                    new LogbookQso { Call = "R4CQ", Band = "160m", Mode = "FT8", StartedUtc = october.AddDays(13) },
                }).ToList());
            var example = new AwardCatalog {
                Callsign = "K1ABC", PublicCatalogUtc = DateTime.UtcNow.AddMinutes(-20),
                Programs = new List<AwardProgram> { russia, gunsmiths, arck, counties, diploma, stations, falcon, tula, angels, border,
                    romania, raem, saratov, tulaDistricts, kazan, yesenin, lake, custom }
            };
            foreach (AwardProgram program in example.Programs) program.Validate();
            var awards = new AwardsDialog(example, awardHistory, "K1ABC", call => {
                DxccCountry country = table.Find(call);
                return country == null ? null : country.Prefix;
            }, cache: awardCache, demo: true);
            awards.SelectDemoProgram(lake);
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
                new HamlogJournalDialog(falcon.Title, new HamlogPointProgress(falcon, awardHistory, "K1ABC")),
                new HamlogAccountDialog("K1ABC", ""),
                new HamlogPlanDialog(lake),
                new HamlogAdditionalRulesDialog(yesenin.EffectiveRules.Contacts, yesenin.EffectiveRules.Contacts.Additional,
                    yesenin.EffectiveRules.Contacts.Adjustments, yesenin.EffectiveRules.Contacts.ShortcutDay,
                    yesenin.EffectiveRules.Contacts.ShortcutContacts),
            };
        }
    }
}
