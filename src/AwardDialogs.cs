using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace JtdxAutoCq
{
    internal sealed class AwardsDialog : OperatorDialog
    {
        enum StateFilter { Active, Searchable, WithoutRules, Completed, Expired, Upcoming, All }
        const string AllSections = "Все разделы";
        readonly AwardCatalog catalog;
        readonly LogbookHistory history;
        readonly string own;
        readonly Func<string, string> dxcc;
        readonly bool demo;
        readonly HamlogRuleCache documents;
        readonly Action<string> log;
        readonly TextBox filter = new TextBox();
        readonly ComboBox section = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Раздел" };
        readonly ComboBox state = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Состояние" };
        readonly DataGridView list = new DataGridView {
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White,
        };
        readonly System.Windows.Forms.Timer validityTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly Label details, overview;
        readonly TextBox preserved = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Visible = false };
        readonly Button updateCatalog, open;
        readonly ContextMenuStrip actions = new ContextMenuStrip();
        readonly ToolStripItem rules, edit, editTargets, sync, journal;
        readonly Dictionary<AwardProgram, AwardProgress> progress = new Dictionary<AwardProgram, AwardProgress>();
        bool refreshing, synchronizing;
        DateTime nextValidityChange;
        CancellationTokenSource catalogDownload;

        internal AwardCatalog Catalog { get { return catalog; } }
        internal AwardsDialog(AwardCatalog catalog, LogbookHistory history, string own, Func<string, string> dxcc,
            string cache = null, Action<string> log = null, bool demo = false)
            : base("Дипломы HAMLOG", 920, 594)
        {
            this.catalog = catalog.Copy(); this.history = history; this.own = own; this.dxcc = dxcc;
            this.demo = demo;
            this.log = log;
            if (cache != null) documents = new HamlogRuleCache(cache);
            this.catalog.IncludeBuiltInPrograms();
            LabelAt("Название или клуб", 20, 54, 345, 23);
            LabelAt("Раздел", 382, 54, 275, 23);
            LabelAt("Состояние", 674, 54, 226, 23);
            filter.AccessibleName = "Название или клуб";
            Place(filter, 20, 78, 346, 27);
            Place(section, 382, 78, 276, 27);
            Place(state, 674, 78, 226, 27);
            state.Items.AddRange(new[] { "Действующие", "Доступны для поиска", "Без правил поиска",
                "Выполненные и выданные", "Срок истёк", "Начнутся позже", "Все дипломы" });
            state.SelectedIndex = (int)StateFilter.Active;
            overview = LabelAt("", 22, 114, 875, 23);
            Place(list, 20, 142, 880, 265);
            list.DefaultCellStyle.SelectionBackColor = Color.FromArgb(222, 235, 248);
            list.DefaultCellStyle.SelectionForeColor = ForeColor;
            list.Columns.Add(new DataGridViewCheckBoxColumn {
                HeaderText = "Поиск", AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 63
            });
            foreach (var column in new[] {
                new DataGridViewTextBoxColumn { HeaderText = "Диплом", FillWeight = 39 },
                new DataGridViewTextBoxColumn { HeaderText = "Зачёт или цели", FillWeight = 20 },
                new DataGridViewTextBoxColumn { HeaderText = "Вне зачёта", FillWeight = 11 },
                new DataGridViewTextBoxColumn { HeaderText = "Состояние", FillWeight = 30 }
            })
            {
                column.ReadOnly = true;
                column.SortMode = DataGridViewColumnSortMode.NotSortable;
                list.Columns.Add(column);
            }
            details = LabelAt("", 23, 417, 873, 70);
            Place(preserved, 22, 487, 876, 45);
            preserved.BackColor = Color.FromArgb(255, 247, 226);
            preserved.AccessibleName = "Ручные правки, сохранённые при обновлении";

            updateCatalog = ButtonAt("Обновить каталог", 20, 544, 190, DownloadCatalog, true);
            ButtonAt("Выполненные из кабинета…", 222, 544, 238, () => Import(true));
            open = ButtonAt("Страница на HAMLOG", 701, 544, 199, ShowPage);
            edit = actions.Items.Add("Правила поиска…", null, delegate { EditRules(); });
            rules = actions.Items.Add("Положение HAMLOG…", null, delegate { ShowRules(); });
            sync = actions.Items.Add("Сверить зачёт с HAMLOG", null, delegate { Synchronize(); });
            journal = actions.Items.Add("Связи из журнала…", null, delegate { ShowJournal(); });
            actions.Items.Add(new ToolStripSeparator());
            editTargets = actions.Items.Add("Задать свой список целей…", null, delegate { Edit(Selected); });
            actions.Items.Add("Создать отдельный список…", null, delegate { Edit(null); });
            actions.Items.Add("Импортировать список из файла…", null, delegate { Import(false); });
            list.ContextMenuStrip = actions;
            list.CellMouseDown += delegate(object sender, DataGridViewCellMouseEventArgs e) {
                if (e.Button == MouseButtons.Right && e.RowIndex >= 0) list.CurrentCell = list.Rows[e.RowIndex].Cells[1];
            };

            filter.TextChanged += delegate { if (!refreshing) RefreshList(); };
            section.SelectedIndexChanged += delegate { if (!refreshing) RefreshList(); };
            state.SelectedIndexChanged += delegate { if (!refreshing) RefreshList(); };
            validityTimer.Tick += delegate { if (DateTime.UtcNow >= nextValidityChange) RefreshList(); };
            Shown += delegate { validityTimer.Start(); };
            FormClosed += delegate {
                validityTimer.Stop();
                if (catalogDownload != null) catalogDownload.Cancel();
            };
            list.CurrentCellChanged += delegate { if (!refreshing) UpdateDetails(); };
            list.CellBeginEdit += delegate(object sender, DataGridViewCellCancelEventArgs e) {
                var program = (AwardProgram)list.Rows[e.RowIndex].Tag;
                e.Cancel = !Progress(program).CanSelectForSearch(DateTime.UtcNow);
            };
            list.CurrentCellDirtyStateChanged += delegate { if (list.IsCurrentCellDirty) list.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            list.CellValueChanged += delegate(object sender, DataGridViewCellEventArgs e) {
                if (refreshing || e.RowIndex < 0 || e.ColumnIndex != 0) return;
                DataGridViewRow row = list.Rows[e.RowIndex];
                var program = (AwardProgram)row.Tag;
                program.Selected = Object.Equals(row.Cells[0].Value, true) && Progress(program).CanSelectForSearch(DateTime.UtcNow);
                UpdateDetails();
            };
            list.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) {
                if (e.RowIndex < 0 || e.ColumnIndex <= 0) return;
                AwardProgram program = Selected;
                if (program == null) return;
                EditRules();
            };
            RefreshList();
        }

        AwardProgram Selected { get { return list.CurrentRow == null ? null : (AwardProgram)list.CurrentRow.Tag; } }

        protected override bool ShowWithoutActivation { get { return demo; } }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                validityTimer.Dispose();
                list.ContextMenuStrip = null;
                actions.Dispose();
            }
            base.Dispose(disposing);
        }

        AwardProgress Progress(AwardProgram item)
        {
            AwardProgress value;
            if (!progress.TryGetValue(item, out value))
                progress[item] = value = new AwardProgress(item, history, own, dxcc);
            return value;
        }

        static string SectionOf(AwardProgram program)
        {
            if (!String.IsNullOrEmpty(program.Section)) return program.Section;
            return program.PublicEntry || !String.IsNullOrEmpty(program.Url) ? "Другие дипломы HAMLOG" : "Мои списки";
        }

        static bool MatchesState(StateFilter filter, AwardProgress status, DateTime now)
        {
            switch (filter)
            {
                case StateFilter.All: return true;
                case StateFilter.Searchable: return status.CanSelectForSearch(now);
                case StateFilter.Completed: return status.Complete;
                case StateFilter.Expired: return status.Expired(now);
                case StateFilter.Upcoming: return !status.Complete && !status.Expired(now) && status.Upcoming(now);
                default: return !status.Complete && !status.Expired(now) && !status.Upcoming(now)
                    && (!status.Program.Withdrawn || status.Program.HasSearchRules)
                    && (filter == StateFilter.Active || !status.Program.HasSearchRules);
            }
        }

        static string Points(decimal points) { return points.ToString("0.##", CultureInfo.CurrentCulture); }

        string ProgressText(AwardProgress status)
        {
            if (status.Online != null)
                return status.Online.Standing == null ? "—" : Points(status.Online.Confirmed) + " из " + status.Online.Required;
            if (status.Points != null)
            {
                HamlogStanding standing = status.Points.Standing;
                if (standing == null) return "—";
                return Points(standing.Points) + " из " + status.Points.Required
                    + (standing.ProvisionalRequired ? " / " + status.Program.EffectiveRules.Required : "")
                    + String.Concat(status.Points.Additional.Select(condition => " · " + condition.Confirmed + " из " + condition.Required));
            }
            return status.GoalsCount == 0 || status.Program.ClosedByIssuance ? "—" : status.CompletedGoals + " из " + status.GoalsCount;
        }

        string StateText(AwardProgress status, DateTime now)
        {
            AwardProgram program = status.Program;
            if (program.ClosedByIssuance) return "Диплом выдан";
            if (program.Withdrawn && !program.HasSearchRules) return "Вне общего каталога";
            if (status.Expired(now)) return "Срок истёк";
            if (status.Upcoming(now)) return "С " + program.From.Value.ToString("dd.MM.yyyy") + " UTC";
            if (program.HasEditedRules && program.HasSearchRules && !status.Complete) return "Ручные правила";
            if (status.Online != null)
            {
                HamlogOnlineProgress online = status.Online;
                if (online.IsActivator) return "Условия для активаторов";
                if (online.Standing == null) return "Сверится перед поиском";
                if (online.Complete) return "Условия выполнены";
                if (online.NeedsCounties) return "Районы RDA · " + online.CountyCount + " из 25";
                string remaining = Points(Math.Max(0, online.Required - online.Confirmed));
                return program.EffectiveRules != null && program.EffectiveRules.Districts
                    ? "Не хватает районов RDA — " + remaining : "Осталось " + remaining + " " + online.Unit;
            }
            HamlogPointProgress points = status.Points;
            if (points == null)
            {
                if (!program.UsesLocalTargets)
                    return program.PublicEntry && String.IsNullOrEmpty(program.Url) ? "Ожидает публикации"
                        : String.IsNullOrEmpty(program.Url) ? "Добавьте цели"
                        : !String.IsNullOrEmpty(program.RuleNote) ? "Проверено · поиск недоступен"
                        : program.RuleDocumentStatus == "downloaded" ? "Условия пока не распознаны"
                        : program.RuleDocumentStatus == "unreadable" ? "Текст PDF не прочитан"
                        : program.RuleDocumentStatus == "missing" ? "Положение недоступно"
                        : program.RuleDocumentStatus == "retry" ? "Ошибка загрузки положения" : "Положение ещё не загружено";
                return status.Complete ? "Цели выполнены по журналу" : "Мои цели";
            }
            if (points.IsMember) return "Условия для активаторов";
            if (points.Standing == null) return program.DownloadedRules != null ? "Правила из положения" : "Сверится перед поиском";
            if (points.ConfirmedComplete) return "Условия выполнены";
            if (points.PendingEnough) return "Есть связи для зачёта";
            if (points.Standing.ProvisionalRequired) return "HAMLOG уточнит порог после зачёта";
            if (points.NeedOnline == 0) return "Остались обязательные условия";
            return "Не хватает " + Points(points.NeedOnline) + " очк.";
        }

        static bool MatchesQuery(AwardProgram program, string query)
        {
            return query.Length == 0 || new[] { program.Title, program.Sponsor, SectionOf(program) }.Any(value =>
                value != null && value.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0);
        }

        void RefreshSections()
        {
            string previous = section.SelectedItem as string ?? AllSections;
            string[] sections = new[] { AllSections }.Concat(catalog.Programs.Select(SectionOf).Distinct()
                .OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase)).ToArray();
            if (sections.SequenceEqual(section.Items.Cast<string>())) return;
            section.Items.Clear();
            section.Items.AddRange(sections);
            section.SelectedItem = sections.Contains(previous) ? previous : AllSections;
        }

        void RefreshList(AwardProgram select = null, bool reveal = false)
        {
            select = select ?? Selected;
            DateTime now = DateTime.UtcNow;
            refreshing = true;
            list.SuspendLayout();
            try
            {
                RefreshSections();
                if (reveal && select != null)
                {
                    AwardProgress selectedProgress = Progress(select);
                    if (!MatchesState((StateFilter)state.SelectedIndex, selectedProgress, now))
                        state.SelectedIndex = (int)(selectedProgress.Complete ? StateFilter.Completed : StateFilter.All);
                    if ((string)section.SelectedItem != SectionOf(select)) section.SelectedItem = AllSections;
                    if (!MatchesQuery(select, filter.Text.Trim())) filter.Clear();
                }
                string sectionName = (string)section.SelectedItem;
                string query = filter.Text.Trim();
                StateFilter show = (StateFilter)state.SelectedIndex;
                nextValidityChange = DateTime.MaxValue;
                var rows = new List<DataGridViewRow>();
                DataGridViewRow selectedRow = null;
                foreach (AwardProgram program in catalog.Programs.OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase))
                {
                    AwardProgress status = Progress(program);
                    DateTime boundary = status.NextValidityChange(now);
                    if (boundary < nextValidityChange) nextValidityChange = boundary;
                    bool selectable = status.CanSelectForSearch(now);
                    if (!selectable) program.Selected = false;
                    if (sectionName != AllSections && sectionName != SectionOf(program)
                        || !MatchesQuery(program, query) || !MatchesState(show, status, now)) continue;

                    string label = StateText(status, now);
                    string pending = status.Points != null && status.Points.Pending.Count > 0
                        ? "+" + Points(status.Points.PendingPoints) + " очк." : "—";
                    var row = new DataGridViewRow { Tag = program };
                    row.CreateCells(list, program.Selected, program.Title, ProgressText(status), pending, label);
                    if (!selectable)
                    {
                        row.Cells[0] = new DataGridViewTextBoxCell {
                            Value = "—", ToolTipText = label,
                            Style = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter }
                        };
                        row.Cells[0].ReadOnly = true;
                        row.DefaultCellStyle.ForeColor = row.DefaultCellStyle.SelectionForeColor = SystemColors.GrayText;
                    }
                    rows.Add(row);
                    if (select != null && program.Id == select.Id) selectedRow = row;
                }
                list.Rows.Clear();
                if (rows.Count > 0)
                {
                    list.Rows.AddRange(rows.ToArray());
                    list.CurrentCell = (selectedRow ?? rows[0]).Cells[1];
                }
            }
            finally { list.ResumeLayout(); refreshing = false; }
            UpdateDetails();
        }

        void UpdateDetails()
        {
            AwardProgram program = Selected;
            DateTime now = DateTime.UtcNow;
            edit.Enabled = !synchronizing && program != null && (!program.PublicEntry || !String.IsNullOrEmpty(program.Url));
            editTargets.Enabled = edit.Enabled && HamlogAward.Find(program.Url) == null;
            open.Enabled = rules.Enabled = program != null && !String.IsNullOrEmpty(program.Url);
            updateCatalog.Enabled = !synchronizing || catalogDownload != null;
            updateCatalog.Text = catalogDownload != null ? "Остановить загрузку"
                : documents != null && documents.CanRecompile(catalog) ? "Разобрать скачанное"
                : documents != null && HamlogRuleCache.CanResume(catalog, now) ? "Продолжить загрузку" : "Обновить каталог";
            sync.Enabled = !synchronizing && program != null && program.UsesTemplate && Callsign.Parse(own) != null;
            int selectedCount = catalog.Programs.Count(item => item.Selected && Progress(item).CanSelectForSearch(now));
            AwardProgress value = program == null ? null : Progress(program);
            journal.Enabled = value != null && value.Points != null && value.Points.Pending.Count > 0;
            if (catalogDownload == null)
                overview.Text = "Показано " + list.Rows.Count + " из " + catalog.Programs.Count + " · к поиску отмечено " + selectedCount
                    + (String.IsNullOrEmpty(catalog.Callsign) ? "" : " · отметки о выдаче для " + catalog.Callsign);
            if (program == null)
            {
                details.Text = "По этим фильтрам дипломов нет. Измените название, раздел или состояние.";
                return;
            }
            string context = String.IsNullOrEmpty(program.Sponsor) ? "" : program.Sponsor + "\r\n";
            if (program.UsesGeographicSearch)
            {
                details.Text = context + (program.Template.MonitoringRegion == null
                    ? "Поиск по европейской и азиатской частям России и Калининградской области."
                    : "Поиск среди станций области, указанной в положении.");
                details.Text += "\r\nОчки считает и диплом выдаёт HAMLOG.";
                return;
            }
            if (value.Online != null)
            {
                HamlogOnlineProgress online = value.Online;
                string checkedAt = online.Standing == null ? "Зачёт загрузится перед поиском. "
                    : "Сверено " + online.Standing.CheckedUtc.ToLocalTime().ToString("dd.MM в HH:mm") + ". ";
                details.Text = context + checkedAt + (program.Template.Rule == HamlogRule.Grid
                    ? "Для поиска нужен свежий QTH-локатор. AutoCQ ищет квадраты, которых ещё нет в зачёте."
                    : online.IsActivator ? "Для вашего района действуют условия активатора. Откройте положение."
                    : program.EffectiveRules != null && program.EffectiveRules.Districts
                    ? "Двойной щелчок открывает список зачётных районов."
                    : "AutoCQ проверяет зачёт и учитывает повторы по положению диплома.");
                return;
            }
            if (value.Points != null)
            {
                details.Text = context + (program.EffectiveRules == null ? "" : "Двойной щелчок открывает правила поиска.\r\n")
                    + PointDetails(value.Points);
                return;
            }
            if (!program.UsesLocalTargets)
            {
                details.Text = context + (program.RuleNote ?? (String.IsNullOrEmpty(program.Url)
                    ? program.PublicEntry ? "HAMLOG пока не опубликовал страницу этой программы."
                    : "Откройте правила поиска двойным щелчком, чтобы задать цели."
                    : "Двойной щелчок открывает правила поиска. На HAMLOG можно посмотреть положение организатора."));
                return;
            }
            string[] remains = program.Goals.Select((goal, index) => new { Goal = goal, Left = goal.Required - value.LoggedFor(index) })
                .Where(item => item.Left > 0).Take(4).Select(item => item.Goal.Value + (item.Goal.Band.Length == 0 ? "" : " · " + item.Goal.Band)
                    + (item.Goal.Mode.Length == 0 ? "" : " · " + item.Goal.Mode) + (item.Left == 1 ? "" : " × " + item.Left)).ToArray();
            details.Text = program.ClosedByIssuance ? "HAMLOG уже выдал диплом по этой программе. План для другой степени можно задать в «Моих целях»."
                : remains.Length == 0 ? "По основному журналу все цели выполнены. Зачёт проверяет HAMLOG."
                : context + "Осталось по вашему списку и журналу — " + String.Join(", ", remains)
                    + (value.GoalsCount - value.CompletedGoals > remains.Length ? " и ещё " + (value.GoalsCount - value.CompletedGoals - remains.Length) : "") + ".";
        }

        async void DownloadCatalog()
        {
            if (catalogDownload != null)
            {
                catalogDownload.Cancel();
                return;
            }
            if (synchronizing) return;
            bool recompile = documents != null && documents.CanRecompile(catalog);
            bool resume = recompile || documents != null && HamlogRuleCache.CanResume(catalog, DateTime.UtcNow);
            var abort = new CancellationTokenSource();
            catalogDownload = abort;
            synchronizing = true;
            preserved.Visible = false;
            UpdateDetails();
            overview.Text = recompile ? "Проверяю уже скачанные положения…" :
                resume ? "Продолжаю загрузку положений HAMLOG…" : "Загружаю общий каталог HAMLOG…";
            IProgress<string> progressText = new Progress<string>(message => {
                if (!IsDisposed && catalogDownload == abort) overview.Text = message;
            });
            try
            {
                if (!resume)
                {
                    HamlogCatalogSnapshot snapshot = await Task.Run(() => HamlogCatalogClient.Download(abort.Token,
                        (loaded, total) => progressText.Report("Загружено " + loaded + " из " + total + " клубных и региональных программ.")));
                    if (IsDisposed || abort.IsCancellationRequested) return;
                    catalog.ApplyPublicCatalog(snapshot, DateTime.UtcNow);
                    progress.Clear();
                    ShowPreservedEdits();
                    RefreshList();
                }
                if (documents != null) await DownloadRules(abort);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (log != null) log("HAMLOG. " + error.Message);
                if (!IsDisposed) MessageBox.Show(this, error.Message, "Каталог HAMLOG", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                catalogDownload = null;
                abort.Dispose();
                synchronizing = false;
                if (!IsDisposed)
                {
                    ShowPreservedEdits();
                    RefreshList();
                }
            }
        }

        void ShowPreservedEdits()
        {
            string[] edited = catalog.Programs.Where(item => item.HasEditedRules)
                .Select(item => item.Title).OrderBy(title => title, StringComparer.CurrentCultureIgnoreCase).ToArray();
            if (edited.Length == 0) return;
            preserved.Text = "Ручные правки сохранены при обновлении." + Environment.NewLine + String.Join(", ", edited);
            preserved.Visible = true;
        }

        async Task DownloadRules(CancellationTokenSource abort)
        {
            DateTime now = DateTime.UtcNow;
            AwardProgram[] pending = catalog.Programs.Where(program => HamlogRuleCache.NeedsRefresh(program, now))
                .OrderBy(program => Progress(program).Expired(now)).ThenBy(program => program.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
            var byId = pending.ToDictionary(program => program.Id, StringComparer.Ordinal);
            int visited = 0, created = 0;
            IProgress<HamlogRuleResult> completed = new Progress<HamlogRuleResult>(result => {
                if (IsDisposed || catalogDownload != abort) return;
                AwardProgram program = byId[result.Id];
                program.ApplyDocument(result);
                if (result.Error != null && log != null)
                    log("HAMLOG. " + program.Title + " (" + program.Url + "). " + result.Error);
                progress.Remove(program);
                if (result.Plan != null) created++;
                overview.Text = "Положения проверены " + ++visited + " из " + pending.Length + " · правил создано " + created;
                if (visited % 10 == 0 || visited == pending.Length) RefreshList();
            });
            await Task.Run(() => documents.Scan(pending, abort.Token, completed.Report));
        }

        string PointDetails(HamlogPointProgress points)
        {
            if (points.IsMember && !points.Closed) return "Для членов клуба действуют условия активатора. Откройте положение.";
            if (points.Standing == null) return Callsign.Parse(own) == null
                ? "Подключите JTDX с вашим позывным, чтобы получить зачёт HAMLOG."
                : "Перед поиском AutoCQ сверит зачёт на HAMLOG.";
            string snapshot = "Сверено " + points.Standing.CheckedUtc.ToLocalTime().ToString("dd.MM в HH:mm") + ". ";
            if (points.ConfirmedCountyMultiplier > 0)
                snapshot += "Множитель по районам RDA ×" + points.ConfirmedCountyMultiplier + ". ";
            if (points.Standing.ProvisionalRequired)
                snapshot += "HAMLOG пока показывает нулевой зачёт. До первой зачтённой связи для поиска используется меньший порог. ";
            if (points.Additional.Count != 0)
                snapshot += "Обязательный зачёт — " + String.Join(", ", points.Additional.Select(condition =>
                    condition.Label + " " + condition.Confirmed + " из " + condition.Required)) + ". ";
            if (points.Closed) return snapshot + "К зачёту подходят связи с 1 по 30 сентября 2021 года.";
            if (history.Path == null) return snapshot + "Выберите доступный основной журнал для сверки связей.";
            if (points.ConfirmedShortcut && points.NeedOnline > 0)
                return snapshot + "Условие на особую дату выполнено. Способ получения указан в положении.";
            if (points.ConfirmedComplete) return snapshot + "Диплом можно получить на HAMLOG.";
            if (points.Additional.Count != 0 && points.NeedOnline == 0 && !points.PendingEnough)
                return snapshot + "Автомат ищет связь для обязательного зачёта.";
            string summary = points.Pending.Count == 0 ? "Связи из журнала сверены."
                : "В журнале есть связи ещё на " + Points(points.PendingPoints) + " очк. "
                    + (points.PendingEnough ? "При зачёте их хватит на диплом." : "Откройте список, чтобы проверить их.");
            if (points.ConfirmedMissingFromLogbook > 0)
                summary += " В зачёте вне журнала — " + points.ConfirmedMissingFromLogbook + ".";
            return snapshot + summary;
        }

        void Synchronize()
        {
            AwardProgram program = Selected;
            if (!sync.Enabled || program == null) return;
            synchronizing = true;
            UpdateDetails();
            var worker = new BackgroundWorker();
            worker.DoWork += (sender, args) => args.Result = HamlogStandingClient.Read(program.Template, own);
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs args) {
                worker.Dispose();
                if (IsDisposed || Disposing) return;
                synchronizing = false;
                if (args.Error == null)
                {
                    program.Hamlog = (HamlogStanding)args.Result;
                    progress.Remove(program);
                    RefreshList(program, true);
                }
                else
                {
                    UpdateDetails();
                    MessageBox.Show(this, "Не удалось сверить зачёт. " + args.Error.Message,
                        "HAMLOG", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            worker.RunWorkerAsync();
        }

        void ShowJournal()
        {
            AwardProgram program = Selected;
            if (program == null) return;
            using (var dialog = new HamlogJournalDialog(program.Title, Progress(program).Points)) dialog.ShowDialog(this);
        }

        void Import(bool personal)
        {
            if (synchronizing) return;
            string station = own;
            if (personal)
                using (var guide = new HamlogAccountDialog(own, catalog.Callsign))
                {
                    if (guide.ShowDialog(this) != DialogResult.OK) return;
                    station = guide.Station;
                }
            using (var file = new OpenFileDialog {
                Title = personal ? "Выберите файл из кабинета HAMLOG" : "Открыть готовый список дипломов или целей",
                Filter = personal ? "Страница или каталог HAMLOG (*.html;*.htm;*.json)|*.html;*.htm;*.json"
                    : "Список дипломов или целей AutoCQ (*.json)|*.json", CheckFileExists = true, RestoreDirectory = true })
            {
                if (personal)
                {
                    string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    if (Directory.Exists(downloads)) file.InitialDirectory = downloads;
                }
                if (file.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    int unmatched = 0;
                    if (personal && !String.Equals(Path.GetExtension(file.FileName), ".json", StringComparison.OrdinalIgnoreCase))
                        unmatched = catalog.ImportIssuedPage(file.FileName, station);
                    else catalog.Import(file.FileName, station, personal);
                    progress.Clear();
                    RefreshList();
                    if (unmatched > 0)
                        MessageBox.Show(this, "Отмечены дипломы, найденные в списке. Строк без совпадения — " + unmatched + ".",
                            "HAMLOG", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception error) { MessageBox.Show(this, error.Message, "Каталог дипломов", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        void Edit(AwardProgram program)
        {
            if (synchronizing) return;
            if (program != null && HamlogAward.Find(program.Url) != null) return;
            using (var editor = new AwardGoalsDialog(program))
            {
                if (editor.ShowDialog(this) != DialogResult.OK) return;
                AwardProgram edited = editor.Program;
                AwardProgram duplicate = catalog.Programs.FirstOrDefault(item => item != program && item.Id == edited.Id);
                if (duplicate != null)
                {
                    MessageBox.Show(this, "Диплом уже есть в списке. Откройте его двойным щелчком.",
                        "Дипломы HAMLOG", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (program != null) { catalog.Programs.Remove(program); progress.Remove(program); }
                catalog.Programs.Add(edited);
                RefreshList(edited, true);
            }
        }

        void EditRules()
        {
            AwardProgram program = Selected;
            if (!edit.Enabled || program == null) return;
            if (program.EffectiveRules == null && HamlogAward.Find(program.Url) != null)
            {
                ShowRules();
                return;
            }
            if (program.EffectiveRules == null || program.ManualSearch && program.Goals.Count != 0)
            {
                Edit(program);
                return;
            }
            using (var dialog = new HamlogPlanDialog(program))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                program.EditedRules = dialog.Edited;
                if (program.Hamlog != null && !program.EffectiveRules.Requires(program.Hamlog.Required))
                    program.Hamlog = null;
                progress.Remove(program);
                RefreshList(program, true);
            }
        }

        void ShowPage()
        {
            AwardProgram program = Selected;
            if (program == null || String.IsNullOrEmpty(program.Url)) return;
            OpenInBrowser(this, HamlogLinks.Award(program.Url));
        }

        void ShowRules()
        {
            AwardProgram program = Selected;
            if (program == null || String.IsNullOrEmpty(program.Url)) return;
            using (var publication = new AwardRulesDialog(program, documents)) publication.ShowDialog(this);
        }

        internal static void OpenInBrowser(IWin32Window owner, string address)
        {
            try
            {
                if (address == null) throw new InvalidDataException("Ссылка на страницу недоступна.");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(address) { UseShellExecute = true });
            }
            catch (Exception error) { MessageBox.Show(owner, error.Message, "HAMLOG", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        internal void SelectDemoProgram(AwardProgram program)
        {
            state.SelectedIndex = (int)StateFilter.All;
            section.SelectedItem = SectionOf(program);
            RefreshList(catalog.Programs.FirstOrDefault(item => item.Id == program.Id));
            ShowPreservedEdits();
        }
    }

    internal sealed class HamlogAccountDialog : OperatorDialog
    {
        readonly TextBox station = new TextBox { MaxLength = 24 };
        internal string Station { get; private set; }

        internal HamlogAccountDialog(string radioCall, string catalogCall) : base("Дипломы из вашего кабинета", 690, 333)
        {
            LabelAt("1. Войдите в кабинет HAMLOG и откройте «Мои полученные дипломы».", 20, 59, 650, 35);
            LabelAt("2. Нажмите Ctrl+S и сохраните страницу как «Веб-страница, только HTML».", 20, 100, 650, 38);
            LabelAt("3. Выберите этот файл. AutoCQ отметит дипломы, которые уже выданы.", 20, 141, 650, 38);
            LabelAt("Позывной в HAMLOG", 20, 194, 170, 25);
            Station = Callsign.Parse(radioCall) ?? Callsign.Parse(catalogCall) ?? "";
            station.Text = Station; station.ReadOnly = Callsign.Parse(radioCall) != null;
            Place(station, 198, 190, 162, 29);
            LabelAt("Если у вас есть файл каталога AutoCQ в JSON, его тоже можно открыть здесь.", 20, 232, 650, 32);
            ButtonAt("Кабинет HAMLOG", 20, 279, 160, () => AwardsDialog.OpenInBrowser(this, HamlogLinks.Catalog));
            ButtonAt("Отмена", 392, 279, 102, () => DialogResult = DialogResult.Cancel);
            ButtonAt("Выбрать файл…", 505, 279, 165, Accept, true);
        }

        void Accept()
        {
            if (!String.IsNullOrWhiteSpace(station.Text) && Callsign.Parse(station.Text) == null)
            {
                MessageBox.Show(this, "Проверьте позывной для импорта.", "HAMLOG", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Station = Callsign.Parse(station.Text) ?? "";
            DialogResult = DialogResult.OK;
        }
    }

    internal sealed class HamlogPlanDialog : OperatorDialog
    {
        readonly AwardProgram program;
        readonly Label edition;
        readonly TextBox calls = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, MaxLength = 1500 };
        readonly TextBox from = new TextBox(), until = new TextBox();
        readonly TextBox searchRegion = new TextBox { MaxLength = 2 };
        readonly NumericUpDown required = new NumericUpDown { Minimum = 1, Maximum = 10000 };
        readonly NumericUpDown member = new NumericUpDown { Minimum = 1, Maximum = 1000 };
        readonly NumericUpDown special = new NumericUpDown { Minimum = 1, Maximum = 1000 };
        readonly NumericUpDown alternative = new NumericUpDown { Minimum = 1, Maximum = 10000 };
        readonly NumericUpDown minimumDistance = new NumericUpDown { Minimum = 1, Maximum = 20000, ThousandsSeparator = true };
        readonly NumericUpDown rcpwSpecial = new NumericUpDown { Minimum = 0, Maximum = 1000 };
        readonly CheckBox rcpwDoubleDistrictZero = new CheckBox { Text = "Двойные очки для 0-го района России", AutoSize = true };
        readonly CheckBox rcpwSpecialAfterActivity = new CheckBox { Text = "Оставить эти позывные в зачёте после дней активности", AutoSize = true };
        readonly TextBox bonusUntil = new TextBox { MaxLength = 32 };
        readonly TextBox groups = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, MaxLength = 4000 };
        readonly CheckBox hf = new CheckBox { Text = "Только КВ (160–10 м)", AutoSize = true };
        readonly CheckBox vhfBonus = new CheckBox { Text = "Множитель ×1,5 на УКВ", AutoSize = true };
        readonly CheckBox flat = new CheckBox { Text = "Очки без множителей", AutoSize = true };
        readonly CheckBox modeGroups = new CheckBox { Text = "Считать цифровые виды вместе", AutoSize = true };
        readonly CheckBox portableAliases = new CheckBox { Text = "Учитывать варианты позывных с дробью", AutoSize = true };
        readonly TextBox territories = new TextBox { MaxLength = 350 };
        readonly TextBox contactBands = new TextBox { MaxLength = 230 };
        readonly TextBox contactModes = new TextBox { MaxLength = 30 };
        readonly TextBox levels = new TextBox { MaxLength = 95 };
        readonly TextBox districtPrefix = new TextBox { MaxLength = 2 };
        readonly NumericUpDown lastCounty = new NumericUpDown { Minimum = 1, Maximum = 99 };
        readonly TextBox retiredCounties = new TextBox { MaxLength = 190 };
        readonly TextBox eligibleCounties = new TextBox { MaxLength = 396 };
        readonly ComboBox repeats = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly Button additionalButton;
        static readonly string[] RepeatKeys = { HamlogContactCriteria.BandMode, HamlogContactCriteria.DayBandMode,
            HamlogContactCriteria.EveryQso, HamlogContactCriteria.Call, HamlogContactCriteria.Band, HamlogContactCriteria.Day,
            HamlogContactCriteria.Locator };
        HamlogRulePlan loadedPlan;
        HamlogContactRequirement[] obligations;
        HamlogPointAdjustment[] adjustments;
        string shortcutDay;
        int shortcutContacts;
        internal HamlogRulePlan Edited { get; private set; }

        static bool HasContactGroups(AwardProgram program)
        {
            return program.EffectiveRules.ContactAward && (program.EffectiveRules.BonusGroups.Length != 0
                || program.DownloadedRules != null && program.DownloadedRules.BonusGroups.Length != 0);
        }

        internal HamlogPlanDialog(AwardProgram program) : base("Правила поиска диплома", 740,
            program.EffectiveRules.Club ? 574 : program.EffectiveRules.Districts ? 558
                : program.EffectiveRules.Rcpw ? 576
                : HasContactGroups(program) ? 703 : program.EffectiveRules.ContactAward ? 624 : 468)
        {
            this.program = program;
            HamlogRulePlan plan = program.EffectiveRules;
            int districtOffset = plan.Districts ? 38 : 0;
            bool distance = plan.ContactAward && plan.Contacts.MinimumDistanceKm > 0;
            LabelAt(program.Title, 20, 58, 699, 28);
            edition = LabelAt("", 20, 88, 699, 24);
            if (plan.Districts)
            {
                LabelAt("Код области RDA", 20, 126, 188, 24);
                Place(districtPrefix, 212, 124, 75, 27);
                LabelAt("Последний номер района", 312, 126, 237, 24);
                Place(lastCounty, 557, 124, 79, 27);
                LabelAt("Упразднённые районы", 20, 166, 188, 24);
                Place(retiredCounties, 212, 161, 506, 27);
                LabelAt("Зачётные районы", 20, 204, 188, 24);
                Place(eligibleCounties, 212, 199, 506, 27);
                LabelAt("В поиске участвуют станции области. Зачтённые районы определяет HAMLOG.",
                    20, 236, 698, 24);
            }
            else if (plan.Rcpw)
            {
                ButtonAt("Список членов RCPW", 20, 124, 184, () => AwardsDialog.OpenInBrowser(this, HamlogRcpwRoster.Url));
                LabelAt("Позывные дней активности", 20, 169, 189, 39);
                Place(calls, 212, 124, 506, 83);
            }
            else if (plan.RdaOnly)
            {
                LabelAt("Код области", 20, 128, 188, 26);
                Place(searchRegion, 212, 124, 78, 27);
                LabelAt("Пустое поле означает всю Россию, включая Калининград.", 308, 128, 410, 45);
            }
            else if (distance)
            {
                LabelAt("Минимальная дальность, км", 20, 130, 218, 27);
                Place(minimumDistance, 251, 126, 112, 27);
                LabelAt("AutoCQ сравнивает ваш QTH-локатор из JTDX с локатором корреспондента в свежем сообщении.",
                    20, 167, 690, 42);
            }
            else
            {
                LabelAt(plan.Club ? "Бонусные позывные" : plan.ContactAward ? "Позывные" : "Позывной станции", 20, 128, 188, 24);
                Place(calls, 212, 124, 506, 82);
            }
            LabelAt("Даты зачёта (UTC)", 20, 232 + districtOffset, 190, 26);
            Place(from, 212, 228 + districtOffset, 152, 27);
            Place(until, 378, 228 + districtOffset, 152, 27);
            LabelAt("Дата или точное UTC", 545, 233 + districtOffset, 173, 26);
            if (plan.Club)
            {
                LabelAt("Нужно очков", 20, 271, 119, 28);
                Place(required, 141, 267, 78, 27);
                LabelAt("За члена клуба", 248, 271, 128, 28);
                Place(member, 375, 267, 74, 27);
                LabelAt("За бонусный позывной", 471, 271, 173, 28);
                Place(special, 644, 267, 74, 27);
                Place(hf, 20, 296, 198, 24);
                Place(vhfBonus, 240, 296, 220, 24);
                Place(flat, 490, 296, 216, 24);
                hf.CheckedChanged += delegate { vhfBonus.Enabled = !hf.Checked && !flat.Checked; };
                flat.CheckedChanged += delegate { vhfBonus.Enabled = !hf.Checked && !flat.Checked; };
                LabelAt("Дополнительные группы", 20, 326, 228, 26);
                LabelAt("Очки | позывные, DXCC или код области", 249, 326, 450, 24);
                Place(groups, 20, 354, 698, 76);
                string factors = plan.ClubHamlogModeRates
                    ? "По зачёту HAMLOG CW даёт ×2, DIGI ×1,5 и SSB ×1."
                    : "При включённых множителях CW даёт ×2, SSB ×1,5, а 160 м ×1,5.";
                LabelAt("Повторы считаются по дате UTC, диапазону и виду работы. FT8 и FT4 относятся к DIGI. " + factors,
                    20, 444, 700, 48);
            }
            else if (plan.Rcpw)
            {
                LabelAt("Нужно очков", 20, 270, 166, 27);
                Place(required, 212, 267, 78, 27);
                if (plan.AlternativeRequired != plan.Required)
                {
                    LabelAt("Порог для 0-го района и за рубежом", 315, 270, 304, 27);
                    Place(alternative, 640, 267, 78, 27);
                }
                LabelAt("Очков в дни активности", 20, 307, 188, 27);
                Place(special, 212, 304, 78, 27);
                LabelAt("Очков после активности", 315, 307, 281, 27);
                Place(member, 640, 304, 78, 27);
                LabelAt("Обычные очки с этой даты (UTC)", 20, 344, 267, 27);
                Place(bonusUntil, 296, 341, 152, 27);
                LabelAt("Очки за указанные позывные", 20, 383, 256, 27);
                Place(rcpwSpecial, 296, 380, 78, 27);
                Place(rcpwDoubleDistrictZero, 394, 383, 324, 24);
                LabelAt("Повтор учитывается на другом диапазоне или другим видом. FT8 и FT4 считаются одним видом.",
                    20, 422, 700, 30);
                if (plan.RcpwOnlineAfterPoints > 0)
                    LabelAt("За поздние связи HAMLOG также начислял " + plan.RcpwOnlineAfterPoints + " очк. AutoCQ сверяет ставку по зачёту.",
                        20, 459, 698, 34);
                Place(rcpwSpecialAfterActivity, 20, 492, 675, 24);
                rcpwSpecialAfterActivity.Visible = plan.Calls.Length != 0;
            }
            else if (plan.Districts)
            {
                LabelAt("Высший порог", 20, 308, 188, 24);
                Place(required, 212, 305, 77, 27);
                LabelAt("Пороги степеней", 20, 347, 188, 24);
                Place(levels, 212, 343, 506, 27);
                LabelAt("Диапазоны (пусто — все)", 20, 387, 188, 24);
                Place(contactBands, 212, 383, 506, 27);
                LabelAt("Виды (пусто — все)", 20, 427, 188, 24);
                Place(contactModes, 212, 423, 506, 27);
            }
            else if (plan.ContactAward)
            {
                bool districtContacts = plan.Contacts.RestrictsCounties && plan.Contacts.Additional.Any(condition => condition.ByCounty);
                LabelAt(plan.MultiLevel ? "Высший порог" : districtContacts ? "Нужно связей" : "Нужно очков", 20, 270, 124, 24);
                Place(required, 148, 267, 77, 27);
                if (!distance)
                {
                    LabelAt("За позывной", 245, 270, 123, 24);
                    Place(special, 374, 267, 77, 27);
                    LabelAt("Страны DXCC", 20, 308, 184, 24);
                    Place(territories, 212, 304, 506, 27);
                }
                LabelAt(plan.AllStations || distance ? "За каждую связь" : districtContacts ? "За связь в районе" : String.IsNullOrEmpty(plan.Contacts.Roster) ? "За станцию DXCC"
                    : "За члена клуба", 466, 270, 157, 24);
                Place(member, 641, 267, 77, 27);
                LabelAt("Диапазоны (пусто — все)", 20, 346, 190, 24);
                Place(contactBands, 212, 342, 506, 27);
                if (!distance) LabelAt("Сколько очков на степень", 20, 384, 190, 24);
                if (plan.AllStations)
                {
                    LabelAt("Ближайшая степень берётся из зачёта HAMLOG.", 212, 384, 506, 24);
                    calls.ReadOnly = territories.ReadOnly = true;
                    special.Enabled = repeats.Enabled = false;
                }
                else if (!distance) Place(levels, 212, 380, 506, 27);
                LabelAt("Зачёт повторов", 20, 422, 190, 24);
                string day = plan.Contacts.RepeatDayOffsetMinutes == 180 ? "по московскому времени" : "по UTC";
                repeats.Items.AddRange(new[] { "На разных диапазонах или видах", "На разных диапазонах или видах за сутки " + day,
                    "Каждая отдельная связь", "Одна связь на позывной", "На разных диапазонах", "Одна связь на позывной за сутки " + day,
                    "Одна связь на квадрат, повторы по диапазону и виду" });
                Place(repeats, 212, 418, 506, 27);
                if (distance)
                {
                    repeats.Enabled = false;
                    LabelAt("Позывные с дробью учитываются отдельно. Для FT4 используется группа MFSK.", 20, 460, 690, 25);
                }
                else
                {
                    Place(modeGroups, 20, 456, 300, 23);
                    Place(portableAliases, 363, 456, 355, 23);
                }
                LabelAt(distance ? "Группы видов (по желанию)" : "Виды (пусто — все)", 20, 496, 190, 24);
                Place(contactModes, 212, 492, 506, 27);
                if (!String.IsNullOrEmpty(plan.Contacts.BonusFromUtc))
                    LabelAt("Специальные позывные дают повышенные очки только в срок мероприятия.", 20, 530, 697, 24);
                if (!String.IsNullOrEmpty(plan.Contacts.Roster))
                    LabelAt("Используется список " + HamlogAwardRosters.Label(plan.Contacts.Roster) + ".", 20, 555, 697, 25);
                if (HasContactGroups(program))
                {
                    LabelAt("Дополнительные группы", 20, 588, 188, 24);
                    Place(groups, 212, 582, 506, 56);
                }
            }
            else if (plan.RdaOnly)
            {
                LabelAt("Порог зачёта HAMLOG", 20, 270, 188, 28);
                Place(required, 212, 267, 78, 27);
                LabelAt("Программа выбирает станции по позывному. Зачёт определяет HAMLOG.", 20, 320, 698, 44);
            }
            else LabelAt("Для диплома нужна одна связь с этой станцией в указанный срок.", 20, 278, 699, 45);
            int buttons = plan.Club ? 516 : plan.Districts ? 502 : HasContactGroups(program) ? 645 : plan.ContactAward ? 582
                : plan.Rcpw ? 518 : 410;
            var restore = ButtonAt("Вернуть исходные", 20, buttons, 205, () => LoadValues(program.DownloadedRules, false));
            restore.Enabled = program.DownloadedRules != null;
            additionalButton = ButtonAt("Зачёт и надбавки", 235, buttons, 200, EditAdditional);
            ButtonAt("Отмена", 449, buttons, 112, () => DialogResult = DialogResult.Cancel);
            ButtonAt("Сохранить", 572, buttons, 146, Save, true);
            LoadValues(plan, program.EditedRules != null);
        }

        void LoadValues(HamlogRulePlan plan, bool edited)
        {
            loadedPlan = plan;
            edition.Text = edited ? "Действуют ваши правки" : "Правила созданы из положения HAMLOG";
            calls.Text = String.Join(", ", plan.Calls);
            from.Text = plan.FromUtc;
            until.Text = plan.UntilUtc;
            searchRegion.Text = plan.SearchRegion;
            required.Maximum = plan.MaxRequired;
            required.Value = plan.Required;
            hf.Checked = plan.HfOnly;
            vhfBonus.Checked = plan.VhfBonus;
            flat.Checked = plan.NoMultipliers;
            if (plan.Points)
            {
                member.Value = plan.MemberPoints;
                special.Value = plan.ExtraPoints;
            }
            if (plan.Rcpw)
            {
                alternative.Value = plan.AlternativeRequired;
                bonusUntil.Text = plan.BonusUntilUtc;
                rcpwSpecial.Value = plan.RcpwSpecialPoints;
                rcpwDoubleDistrictZero.Checked = plan.RcpwDoubleDistrictZero;
                rcpwSpecialAfterActivity.Checked = plan.RcpwSpecialAfterActivity;
            }
            if (plan.ContactAward)
            {
                obligations = plan.Contacts.Additional;
                adjustments = plan.Contacts.Adjustments;
                shortcutDay = plan.Contacts.ShortcutDay;
                shortcutContacts = plan.Contacts.ShortcutContacts;
                if (plan.Contacts.MinimumDistanceKm > 0) minimumDistance.Value = plan.Contacts.MinimumDistanceKm;
                additionalButton.Visible = obligations.Length != 0 || adjustments.Length != 0
                    || shortcutContacts != 0 || plan.Contacts.RestrictsCounties && !plan.Districts || plan.Contacts.ActivatorAreas.Length != 0;
                territories.Text = String.Join(", ", plan.Contacts.Countries);
                contactBands.Text = String.Join(", ", plan.Contacts.Bands);
                contactModes.Text = String.Join(", ", plan.Contacts.Modes);
                levels.Text = String.Join(", ", plan.Contacts.Levels);
                repeats.SelectedIndex = Array.IndexOf(RepeatKeys, plan.Contacts.Repeat);
                modeGroups.Checked = plan.Contacts.GroupModes;
                portableAliases.Checked = plan.Contacts.PortableAliases;
                if (plan.Districts)
                {
                    districtPrefix.Text = plan.Contacts.CountyPrefix;
                    lastCounty.Value = plan.Contacts.LastCounty;
                    retiredCounties.Text = String.Join(", ", plan.Contacts.RetiredCounties);
                    eligibleCounties.Text = String.Join(", ", plan.Contacts.EligibleCounties.Select(code => code.ToString("D2")));
                }
            }
            else additionalButton.Visible = false;
            groups.Lines = (plan.BonusGroups ?? new HamlogBonusGroup[0]).Select(group =>
                group.Points.ToString(CultureInfo.InvariantCulture) + " | " + String.Join(", ", group.Targets)).ToArray();
        }

        void Save()
        {
            HamlogRulePlan previous = loadedPlan, original = program.DownloadedRules;
            try
            {
                var plan = new HamlogRulePlan {
                    Kind = previous.Kind, FromUtc = from.Text, UntilUtc = until.Text, Required = (int)required.Value,
                    MemberPoints = previous.Points ? (int)member.Value : 0, ExtraPoints = previous.Points ? (int)special.Value : 0,
                    HfOnly = previous.Club && hf.Checked, VhfBonus = previous.Club && !hf.Checked && !flat.Checked && vhfBonus.Checked,
                    NoMultipliers = previous.Club && flat.Checked,
                    ClubMembersMayHunt = previous.ClubMembersMayHunt, ClubOnlyActivatorAreas = previous.ClubOnlyActivatorAreas,
                    ClubHamlogModeRates = previous.ClubHamlogModeRates, ClubHamlogNo160mBonus = previous.ClubHamlogNo160mBonus,
                    SearchRegion = previous.RdaOnly ? searchRegion.Text.Trim().ToUpperInvariant() : previous.SearchRegion,
                    AlternativeRequired = previous.Rcpw && previous.AlternativeRequired == previous.Required ? (int)required.Value
                        : previous.Rcpw ? (int)alternative.Value : previous.AlternativeRequired,
                    RcpwOnlineAfterPoints = member.Value == previous.MemberPoints && special.Value == previous.ExtraPoints
                        && bonusUntil.Text == previous.BonusUntilUtc ? previous.RcpwOnlineAfterPoints : 0,
                    RcpwSpecialPoints = previous.Rcpw ? (int)rcpwSpecial.Value : 0,
                    RcpwDoubleDistrictZero = previous.Rcpw && rcpwDoubleDistrictZero.Checked,
                    RcpwSpecialAfterActivity = previous.Rcpw && rcpwSpecialAfterActivity.Checked,
                    BonusUntilUtc = previous.Rcpw ? bonusUntil.Text : "",
                    Calls = Tokens(calls.Text),
                    BonusGroups = previous.Club || HasContactGroups(program) ? groups.Lines.Where(line => !String.IsNullOrWhiteSpace(line)).Select(ReadGroup).ToArray()
                        : new HamlogBonusGroup[0]
                };
                if (previous.ContactAward)
                {
                    string repeat = previous.Districts ? HamlogContactCriteria.District : RepeatKeys[repeats.SelectedIndex];
                    plan.Contacts = new HamlogContactCriteria {
                        Repeat = repeat,
                        RepeatDayOffsetMinutes = repeat == HamlogContactCriteria.Day || repeat == HamlogContactCriteria.DayBandMode
                            ? previous.Contacts.RepeatDayOffsetMinutes : 0,
                        ShortcutDay = shortcutDay, ShortcutContacts = shortcutContacts,
                        GroupModes = modeGroups.Checked,
                        AllStations = previous.Contacts.AllStations,
                        MinimumDistanceKm = previous.Contacts.MinimumDistanceKm > 0 ? (int)minimumDistance.Value : 0,
                        HamlogRosterCredit = previous.Contacts.HamlogRosterCredit,
                        PortableAliases = portableAliases.Checked,
                        PortableRegionalCalls = previous.Contacts.PortableRegionalCalls,
                        AcceptObserverCredit = previous.Contacts.AcceptObserverCredit,
                        Countries = Tokens(territories.Text.ToUpperInvariant()), Bands = Tokens(contactBands.Text.ToLowerInvariant()),
                        Modes = Tokens(contactModes.Text.ToUpperInvariant()),
                        Levels = Tokens(levels.Text).Select(value => Int32.Parse(value, CultureInfo.InvariantCulture)).ToArray(),
                        BonusFromUtc = previous.Contacts.BonusFromUtc, BonusUntilUtc = previous.Contacts.BonusUntilUtc,
                        CountyPrefix = previous.Districts ? districtPrefix.Text.Trim().ToUpperInvariant() : previous.Contacts.CountyPrefix,
                        LastCounty = previous.Districts ? (int)lastCounty.Value : previous.Contacts.LastCounty,
                        RetiredCounties = previous.Districts
                            ? Tokens(retiredCounties.Text).Select(code => Int32.Parse(code, CultureInfo.InvariantCulture)).ToArray()
                            : previous.Contacts.RetiredCounties,
                        EligibleCounties = previous.Districts
                            ? Tokens(eligibleCounties.Text).Select(code => Int32.Parse(code, CultureInfo.InvariantCulture)).ToArray()
                            : previous.Contacts.EligibleCounties,
                        MultiplyByCountyCount = previous.Contacts.MultiplyByCountyCount,
                        CreditBeforePublication = previous.Contacts.CreditBeforePublication,
                        Roster = previous.Contacts.Roster, ActivatorRoster = previous.Contacts.ActivatorRoster,
                        ActivatorAreas = previous.Contacts.ActivatorAreas, Additional = obligations, Adjustments = adjustments
                    };
                }
                plan.Validate();
                Edited = plan.SameAs(original) ? null : plan;
                DialogResult = DialogResult.OK;
            }
            catch (Exception error)
            { MessageBox.Show(this, error.Message, "Правила поиска", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }

        static string[] Tokens(string text)
        { return System.Text.RegularExpressions.Regex.Split(text.Trim(), @"[\s,;]+").Where(value => value.Length > 0).ToArray(); }

        void EditAdditional()
        {
            using (var dialog = new HamlogAdditionalRulesDialog(loadedPlan.Contacts, obligations, adjustments, shortcutDay, shortcutContacts))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                obligations = dialog.Obligations;
                adjustments = dialog.Adjustments;
                shortcutDay = dialog.ShortcutDay;
                shortcutContacts = dialog.ShortcutContacts;
            }
        }

        static HamlogBonusGroup ReadGroup(string line)
        {
            string[] parts = line.Split(new[] { '|' }, 2);
            int points;
            if (parts.Length != 2 || !Int32.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out points))
                throw new InvalidDataException("Укажите очки и группу через «|», например «5 | DXCC BY».");
            string[] targets = parts[1].ToUpperInvariant().Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()).ToArray();
            bool area = targets.All(HamlogAwardAreas.Known);
            return new HamlogBonusGroup { Points = points, Targets = targets, Area = area };
        }
    }

    internal sealed class HamlogAdditionalRulesDialog : OperatorDialog
    {
        internal HamlogContactRequirement[] Obligations { get; private set; }
        internal HamlogPointAdjustment[] Adjustments { get; private set; }
        internal string ShortcutDay { get; private set; }
        internal int ShortcutContacts { get; private set; }

        internal HamlogAdditionalRulesDialog(HamlogContactCriteria criteria, HamlogContactRequirement[] obligations,
            HamlogPointAdjustment[] adjustments, string shortcutDay = null, int shortcutContacts = 0)
            : base("Обязательный зачёт и надбавки", 780, 205 + obligations.Length * 73 + adjustments.Length * 58
                + (adjustments.Length == 0 ? 0 : 27) + (shortcutContacts == 0 ? 0 : 69))
        {
            int top = 58;
            string eligibility = criteria.RestrictsCounties && criteria.Repeat != HamlogContactCriteria.District
                ? "Зачётная территория — " + criteria.CountyPrefix + "-01 — " + criteria.CountyPrefix + "-" + criteria.LastCounty.ToString("D2") + ". "
                : "";
            if (criteria.ActivatorAreas.Length != 0)
                eligibility += "Отдельные условия активаторов действуют в " + String.Join(", ", criteria.ActivatorAreas) + ".";
            if (criteria.MultiplyByCountyCount)
                eligibility = "Сумма очков за связи умножается на число разных зачтённых районов RDA.";
            if (criteria.PortableRegionalCalls) eligibility += "Для позывных с /P учитывается код области по положению.";
            if (eligibility.Length == 0) eligibility = "Здесь указаны отдельные требования диплома и правила повышения очков.";
            LabelAt(eligibility, 20, top, 740, 42);
            top += 46;
            TextBox shortcutDate = null;
            NumericUpDown shortcutCount = null;
            if (shortcutContacts != 0)
            {
                shortcutDate = new TextBox { Text = shortcutDay, MaxLength = 5 };
                shortcutCount = new NumericUpDown { Minimum = 0, Maximum = 1000, Value = shortcutContacts };
                LabelAt("Можно выполнить диплом за один день", 20, top, 740, 24);
                LabelAt("Дата (ММ-ДД, UTC)", 20, top + 28, 150, 26);
                Place(shortcutDate, 172, top + 28, 89, 26);
                LabelAt("Связей в этот день", 285, top + 28, 177, 26);
                Place(shortcutCount, 468, top + 28, 70, 26);
                top += 69;
            }
            var readObligations = new List<Func<HamlogContactRequirement>>();
            foreach (HamlogContactRequirement condition in obligations)
            {
                LabelAt("Минимум — " + condition.Label + (condition.ReportedByHamlog ? " · сверяется с HAMLOG" : ""),
                    20, top, 685, 22);
                var count = new NumericUpDown { Minimum = 1, Maximum = 1000, Value = condition.Required };
                bool counties = condition.ByCounty;
                var values = new TextBox { MaxLength = 4800, Text = String.Join(", ", counties ? condition.CountyCodes : condition.Calls) };
                Place(count, 20, top + 25, 68, 26);
                Place(values, 104, top + 25, 656, 26);
                LabelAt(condition.Kind == HamlogContactRequirement.Counties ? "Пустое поле означает все районы зачётной территории."
                    : counties ? "Укажите коды районов RDA через запятую." : condition.UsesRoster ? "К списку клуба можно добавить позывные через запятую."
                    : "Позывные можно перечислить через запятую.",
                    106, top + 52, 650, 19);
                readObligations.Add(() => {
                    string[] names = values.Text.ToUpperInvariant().Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    var rule = new HamlogContactRequirement { Kind = condition.Kind, Required = (int)count.Value,
                        ReportedByHamlog = condition.ReportedByHamlog, DistinctStations = condition.DistinctStations,
                        PortableStations = condition.PortableStations, Roster = condition.Roster };
                    if (counties) rule.CountyCodes = names;
                    else rule.Calls = names;
                    rule.Validate(criteria);
                    return rule;
                });
                top += 73;
            }
            var readAdjustments = new List<Func<HamlogPointAdjustment>>();
            if (adjustments.Length != 0)
            {
                LabelAt("×", 20, top, 55, 25);
                LabelAt("+ очки", 88, top, 62, 25);
                LabelAt("С даты (UTC)", 163, top, 154, 25);
                LabelAt("По дату (UTC)", 332, top, 154, 25);
                LabelAt("Диапазоны", 503, top, 133, 25);
                LabelAt("Окончания", 657, top, 103, 25);
                top += 27;
            }
            foreach (HamlogPointAdjustment adjustment in adjustments)
            {
                var factor = new NumericUpDown { Minimum = 1, Maximum = 5, Value = adjustment.Factor };
                var extra = new NumericUpDown { Minimum = 0, Maximum = 1000, Value = adjustment.Add };
                var from = new TextBox { Text = adjustment.Annual ? adjustment.AnnualFrom : adjustment.FromUtc, MaxLength = 32 };
                var until = new TextBox { Text = adjustment.Annual ? adjustment.AnnualUntil : adjustment.UntilUtc, MaxLength = 32 };
                var bands = new TextBox { Text = String.Join(", ", adjustment.Bands), MaxLength = 230 };
                var suffixes = new TextBox { Text = String.Join(", ", adjustment.PortableSuffixes), MaxLength = 12 };
                Place(factor, 20, top, 55, 26);
                Place(extra, 88, top, 62, 26);
                Place(from, 163, top, 154, 26);
                Place(until, 332, top, 154, 26);
                Place(bands, 503, top, 143, 26);
                Place(suffixes, 657, top, 103, 26);
                readAdjustments.Add(() => {
                    string start = from.Text.Trim(), end = until.Text.Trim();
                    bool annual = start.Length == 5 || end.Length == 5;
                    var rule = new HamlogPointAdjustment { Factor = (int)factor.Value, Add = (int)extra.Value,
                        AnnualFrom = annual ? start : null, AnnualUntil = annual ? end : null,
                        FromUtc = annual ? null : start, UntilUtc = annual ? null : end,
                        Bands = bands.Text.ToLowerInvariant().Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries),
                        PortableSuffixes = suffixes.Text.ToUpperInvariant().Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries) };
                    rule.Validate();
                    return rule;
                });
                top += 58;
            }
            if (adjustments.Length != 0)
                LabelAt("Введите дату целиком или только месяц и день для ежегодных надбавок.", 20, top - 22, 740, 29);
            ButtonAt("Отмена", 477, top + 51, 126, () => DialogResult = DialogResult.Cancel);
            ButtonAt("Применить", 614, top + 51, 146, () => {
                try
                {
                    Obligations = readObligations.Select(read => read()).ToArray();
                    Adjustments = readAdjustments.Select(read => read()).ToArray();
                    ShortcutContacts = shortcutCount == null ? 0 : (int)shortcutCount.Value;
                    ShortcutDay = ShortcutContacts == 0 ? null : shortcutDate.Text.Trim();
                    if (ShortcutContacts != 0) HamlogCalendar.MonthDay(ShortcutDay);
                    DialogResult = DialogResult.OK;
                }
                catch (InvalidDataException error)
                { MessageBox.Show(this, error.Message, "Правила поиска", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            }, true);
        }
    }

    internal sealed class AwardRulesDialog : OperatorDialog
    {
        readonly string award;
        readonly HamlogRuleCache documents;
        readonly Label state;
        readonly TextBox text = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        readonly Button save;
        CancellationTokenSource download;
        HamlogRules publication;

        internal AwardRulesDialog(AwardProgram program, HamlogRuleCache documents = null) : base("Положение HAMLOG", 780, 474)
        {
            award = HamlogLinks.Award(program.Url);
            this.documents = documents;
            LabelAt(program.Title, 20, 63, 740, 40);
            state = LabelAt("Загружаю положение с HAMLOG…", 20, 108, 740, 27);
            Place(text, 20, 139, 740, 268);
            ButtonAt("Открыть оригинал", 20, 421, 174, () =>
                AwardsDialog.OpenInBrowser(this, publication == null ? award : publication.Source));
            save = ButtonAt("Сохранить…", 204, 421, 150, SaveDocument);
            save.Enabled = false;
            ButtonAt("Закрыть", 632, 421, 128, Close, true);
            Shown += delegate { Read(); };
            FormClosed += delegate { if (download != null) download.Cancel(); };
        }

        async void Read()
        {
            var abort = new CancellationTokenSource();
            download = abort;
            try
            {
                HamlogRules result = await Task.Run(() => (documents == null ? null : documents.Cached(award))
                    ?? HamlogRules.Read(award, abort.Token));
                if (IsDisposed || abort.IsCancellationRequested) return;
                publication = result;
                save.Enabled = result.Pdf != null || !String.IsNullOrWhiteSpace(result.Text);
                state.Text = result.Pdf != null ? "HAMLOG опубликовал положение в PDF."
                    : !String.IsNullOrEmpty(result.Text) ? "Текст со страницы HAMLOG"
                    : "Положение доступно на странице программы.";
                text.Text = result.Pdf != null ? "Нажмите «Открыть оригинал», чтобы прочитать документ, или сохраните PDF на компьютер."
                    : result.Text ?? "Откройте страницу диплома. Там могут быть дополнительные ссылки и таблицы.";
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!IsDisposed)
                {
                    state.Text = "Не удалось загрузить положение.";
                    text.Text = error.Message + Environment.NewLine + "Попробуйте открыть страницу в браузере.";
                }
            }
            finally { download = null; abort.Dispose(); }
        }

        void SaveDocument()
        {
            if (publication == null) return;
            bool pdf = publication.Pdf != null;
            if (!pdf && String.IsNullOrWhiteSpace(publication.Text)) return;
            string extension = pdf ? "pdf" : "txt";
            using (var file = new SaveFileDialog { Title = "Сохранить положение HAMLOG",
                Filter = pdf ? "Документ PDF (*.pdf)|*.pdf" : "Текст (*.txt)|*.txt",
                DefaultExt = extension, FileName = "Положение HAMLOG." + extension })
            {
                if (file.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    if (pdf) File.WriteAllBytes(file.FileName, publication.Pdf);
                    else File.WriteAllText(file.FileName, publication.Text);
                }
                catch (Exception error) { MessageBox.Show(this, error.Message, "Положение HAMLOG", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }
    }

    internal sealed class HamlogJournalDialog : OperatorDialog
    {
        internal HamlogJournalDialog(string title, HamlogPointProgress points) : base("Связи вне зачёта", 670, 416)
        {
            LabelAt(title + " · " + points.Pending.Count + " QSO на " + points.PendingPoints.ToString("0.##") + " очк.",
                20, 60, 630, 25);
            var rows = new ListView { View = View.Details, FullRowSelect = true, HideSelection = false };
            rows.Columns.Add("Позывной", 144);
            rows.Columns.Add("Дата UTC", 124);
            rows.Columns.Add("Диапазон", 110);
            rows.Columns.Add("Вид работы", 130);
            rows.Columns.Add("Очки", 100);
            foreach (HamlogCredit credit in points.Pending.OrderByDescending(row => row.DayUtc).ThenBy(row => row.Call))
                rows.Items.Add(new ListViewItem(new[] {
                    credit.Call, credit.DayUtc.ToString("dd.MM.yyyy"), credit.Band, credit.Mode, "+" + credit.Points.ToString("0.##")
                }));
            Place(rows, 20, 101, 630, 211);
            LabelAt("Эти связи есть в основном журнале. Для диплома их должен зачесть HAMLOG.", 20, 325, 630, 32);
            ButtonAt("Закрыть", 516, 369, 134, Close, true);
        }
    }

    internal sealed class AwardGoalsDialog : OperatorDialog
    {
        static readonly string[] Kinds = { "Позывной", "DXCC", "Маска" };
        static readonly string[] Keys = { "call", "dxcc", "mask" };
        readonly TextBox title = new TextBox { MaxLength = 300 }, url = new TextBox { MaxLength = 512 };
        readonly TextBox from = new TextBox { MaxLength = 30 }, until = new TextBox { MaxLength = 30 };
        readonly CheckBox otherLevel = new CheckBox { Text = "Искать другую степень, если диплом по программе уже выдавался", AutoSize = true };
        readonly DataGridView goals = new DataGridView {
            RowHeadersVisible = false, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White,
        };
        readonly AwardProgram original;
        internal AwardProgram Program { get; private set; }

        internal AwardGoalsDialog(AwardProgram program) : base("Условия диплома", 730, 553)
        {
            original = program;
            LabelAt("Название", 20, 61, 159, 24); Place(title, 188, 57, 522, 28);
            LabelAt("Страница HAMLOG", 20, 98, 168, 24); Place(url, 188, 94, 522, 28);
            LabelAt("Даты UTC", 20, 135, 150, 25);
            Place(from, 188, 131, 191, 28); Place(until, 390, 131, 191, 28);
            LabelAt("ГГГГ-ММ-ДД", 590, 136, 120, 24);
            LabelAt("Даты можно оставить пустыми. Введите начало и последний зачётный день.", 20, 169, 690, 25);
            LabelAt("Добавьте нужные позывные, префиксы DXCC или маски вроде JA*.", 20, 202, 690, 24);
            Place(goals, 20, 232, 690, 195);
            var kindColumn = new DataGridViewComboBoxColumn { HeaderText = "Тип цели", FillWeight = 100 };
            kindColumn.Items.AddRange(Kinds);
            goals.Columns.Add(kindColumn);
            goals.Columns.Add("value", "Позывной, DXCC или маска");
            var bandColumn = new DataGridViewComboBoxColumn { HeaderText = "Диапазон", FillWeight = 72 };
            bandColumn.Items.Add("");
            foreach (string band in JtdxAdif.BandNames) bandColumn.Items.Add(band);
            goals.Columns.Add(bandColumn);
            var modeColumn = new DataGridViewComboBoxColumn { HeaderText = "Вид", FillWeight = 66 };
            modeColumn.Items.AddRange("", "FT8", "FT4");
            goals.Columns.Add(modeColumn);
            goals.Columns.Add("count", "Связей");
            goals.Columns[4].FillWeight = 58;
            LabelAt("Пустые диапазон и вид означают любые. Пустое число связей означает одну.", 20, 434, 690, 24);
            Place(otherLevel, 20, 463, 690, 23);
            if (program != null) otherLevel.Checked = program.OtherLevel;
            ButtonAt("Отмена", 430, 504, 121, () => { DialogResult = DialogResult.Cancel; });
            ButtonAt("Сохранить", 562, 504, 148, Save, true);
            if (program == null) return;
            if (program.EffectiveRules != null)
                ButtonAt("Вернуть правила HAMLOG", 221, 504, 199, () => {
                    Program = original.Copy();
                    Program.Goals.Clear();
                    Program.ManualSearch = false;
                    Program.EditedRules = null;
                    DialogResult = DialogResult.OK;
                });
            title.Text = program.Title; url.Text = program.Url; from.Text = program.FromUtc; until.Text = program.UntilUtc;
            if (!String.IsNullOrEmpty(program.Url))
            {
                url.ReadOnly = program.PublicEntry;
                ButtonAt("Читать положение…", 20, 504, 191, () => {
                    using (var publication = new AwardRulesDialog(program)) publication.ShowDialog(this);
                });
            }
            foreach (AwardGoal goal in program.Goals)
                goals.Rows.Add(Kinds[Array.IndexOf(Keys, goal.Kind)], goal.Value, goal.Band, goal.Mode,
                    goal.Required.ToString(CultureInfo.InvariantCulture));
        }

        static string Value(DataGridViewRow row, int index) { return Convert.ToString(row.Cells[index].Value).Trim(); }

        void Save()
        {
            try
            {
                goals.EndEdit();
                var updated = original == null ? new AwardProgram() : original.Copy();
                updated.Title = title.Text; updated.Url = url.Text; updated.FromUtc = from.Text; updated.UntilUtc = until.Text;
                updated.OtherLevel = otherLevel.Checked;
                if (original != null && updated.Url.Trim().TrimEnd('/') != original.Url.TrimEnd('/'))
                {
                    updated.Id = null;
                    updated.HasIssued = null;
                    updated.Hamlog = null;
                    updated.SetPublication(new AwardProgram());
                }
                updated.Goals.Clear();
                foreach (DataGridViewRow row in goals.Rows)
                {
                    if (row.IsNewRow) continue;
                    string kind = Value(row, 0), value = Value(row, 1), band = Value(row, 2), mode = Value(row, 3), count = Value(row, 4);
                    if (kind.Length + value.Length + band.Length + mode.Length + count.Length == 0) continue;
                    int type = Array.IndexOf(Kinds, kind), number = 1;
                    if (type < 0 || count.Length != 0 && !Int32.TryParse(count, out number))
                        throw new InvalidDataException("Выберите тип цели и проверьте число связей.");
                    updated.Goals.Add(new AwardGoal { Kind = Keys[type], Value = value, Band = band, Mode = mode, Count = number });
                }
                updated.ManualSearch = !String.IsNullOrWhiteSpace(updated.Url) && updated.Goals.Count > 0;
                updated.Validate();
                Program = updated;
                DialogResult = DialogResult.OK;
            }
            catch (Exception error) { MessageBox.Show(this, error.Message, "Условия диплома", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }
}
