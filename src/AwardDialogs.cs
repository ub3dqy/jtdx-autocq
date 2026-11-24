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
        internal AwardsDialog(AwardCatalog catalog, LogbookHistory history, string own, Func<string, string> dxcc, string cache = null, bool demo = false)
            : base("Дипломы HAMLOG", 920, 594)
        {
            this.catalog = catalog.Copy(); this.history = history; this.own = own; this.dxcc = dxcc;
            this.demo = demo;
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
                new DataGridViewTextBoxColumn { HeaderText = "Диплом", FillWeight = 43 },
                new DataGridViewTextBoxColumn { HeaderText = "Зачёт или цели", FillWeight = 14 },
                new DataGridViewTextBoxColumn { HeaderText = "Вне зачёта", FillWeight = 13 },
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
                return status.Points.Standing == null ? "—" : Points(status.Points.Standing.Points) + " из " + status.Points.Required;
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
                if (online.Standing == null) return "Сверится перед поиском";
                if (online.Complete) return "Условия выполнены";
                if (online.NeedsCounties) return "Районы RDA · " + online.CountyCount + " из 25";
                return "Осталось " + Points(Math.Max(0, online.Required - online.Confirmed)) + " " + online.Unit;
            }
            HamlogPointProgress points = status.Points;
            if (points == null)
            {
                if (!program.UsesLocalTargets)
                    return program.PublicEntry && String.IsNullOrEmpty(program.Url) ? "Ожидает публикации"
                        : String.IsNullOrEmpty(program.Url) ? "Добавьте цели"
                        : program.RuleDocumentStatus == "downloaded" ? "Положение есть · нужен разбор"
                        : program.RuleDocumentStatus == "missing" ? "Положение недоступно"
                        : program.RuleDocumentStatus == "retry" ? "Ошибка загрузки положения" : "Положение ещё не загружено";
                return status.Complete ? "Цели выполнены по журналу" : "Мои цели";
            }
            if (points.IsMember) return "Условия для активаторов";
            if (points.Standing == null) return program.DownloadedRules != null ? "Правила из положения" : "Сверится перед поиском";
            if (points.ConfirmedComplete) return "Условия выполнены";
            if (points.PendingEnough) return "Есть связи для зачёта";
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
            updateCatalog.Text = catalogDownload == null ? "Обновить каталог" : "Остановить загрузку";
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
            if (value.Online != null)
            {
                HamlogOnlineProgress online = value.Online;
                string checkedAt = online.Standing == null ? "Зачёт загрузится перед поиском. "
                    : "Сверено " + online.Standing.CheckedUtc.ToLocalTime().ToString("dd.MM в HH:mm") + ". ";
                details.Text = context + checkedAt + (program.Template.Rule == HamlogRule.Grid
                    ? "Для поиска нужен свежий QTH-локатор. AutoCQ ищет квадраты, которых ещё нет в зачёте."
                    : program.Template.Rule == HamlogRule.Bryansk || program.Template.Rule == HamlogRule.Tatarstan
                    ? "Для зачёта нового района нужен известный RDA корреспондента."
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
                details.Text = context + (String.IsNullOrEmpty(program.Url)
                    ? program.PublicEntry ? "HAMLOG пока не опубликовал страницу этой программы."
                    : "Откройте правила поиска двойным щелчком, чтобы задать цели."
                    : "Двойной щелчок открывает правила поиска. На HAMLOG можно посмотреть положение организатора.");
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
            var abort = new CancellationTokenSource();
            catalogDownload = abort;
            synchronizing = true;
            preserved.Visible = false;
            UpdateDetails();
            overview.Text = "Загружаю общий каталог HAMLOG…";
            IProgress<string> progressText = new Progress<string>(message => {
                if (!IsDisposed && catalogDownload == abort) overview.Text = message;
            });
            try
            {
                HamlogCatalogSnapshot snapshot = await Task.Run(() => HamlogCatalogClient.Download(abort.Token,
                    (loaded, total) => progressText.Report("Загружено " + loaded + " из " + total + " клубных и региональных программ.")));
                if (!IsDisposed && !abort.IsCancellationRequested)
                {
                    catalog.ApplyPublicCatalog(snapshot, DateTime.UtcNow);
                    progress.Clear();
                    ShowPreservedEdits();
                    RefreshList();
                    if (documents != null) await DownloadRules(abort);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!IsDisposed) MessageBox.Show(this, error.Message, "Каталог HAMLOG", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                catalogDownload = null;
                abort.Dispose();
                synchronizing = false;
                if (!IsDisposed) RefreshList();
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
            overview.Text = "Загружаю положения HAMLOG…";
            IProgress<HamlogRuleResult> completed = new Progress<HamlogRuleResult>(result => {
                if (IsDisposed || catalogDownload != abort) return;
                AwardProgram program = byId[result.Id];
                program.ApplyDocument(result);
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
            if (points.Closed) return snapshot + "К зачёту подходят связи с 1 по 30 сентября 2021 года.";
            if (history.Path == null) return snapshot + "Выберите доступный основной журнал для сверки связей.";
            if (points.ConfirmedComplete) return snapshot + "Диплом можно получить на HAMLOG.";
            string summary = points.Pending.Count == 0 ? "Все найденные в журнале связи уже в зачёте."
                : "В журнале есть связи ещё на " + Points(points.PendingPoints) + " очк. "
                    + (points.PendingEnough ? "При зачёте их хватит на диплом." : "Откройте список, чтобы проверить их.");
            if (points.ConfirmedMissingFromLogbook > 0)
                summary += " Зачтённых QSO вне основного журнала — " + points.ConfirmedMissingFromLogbook + ".";
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
                if (program.Hamlog != null && program.Hamlog.Required != program.EffectiveRules.Required)
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
        readonly NumericUpDown required = new NumericUpDown { Minimum = 1, Maximum = 10000 };
        readonly NumericUpDown member = new NumericUpDown { Minimum = 1, Maximum = 1000 };
        readonly NumericUpDown special = new NumericUpDown { Minimum = 1, Maximum = 1000 };
        readonly CheckBox hf = new CheckBox { Text = "Только КВ (160–10 м)", AutoSize = true };
        readonly CheckBox vhfBonus = new CheckBox { Text = "Множитель ×1,5 на УКВ", AutoSize = true };
        internal HamlogRulePlan Edited { get; private set; }

        internal HamlogPlanDialog(AwardProgram program) : base("Правила поиска диплома", 740, 468)
        {
            this.program = program;
            HamlogRulePlan plan = program.EffectiveRules;
            LabelAt(program.Title, 20, 58, 699, 28);
            edition = LabelAt("", 20, 88, 699, 24);
            LabelAt(plan.Club ? "Бонусные позывные" : "Позывной станции", 20, 128, 188, 24);
            Place(calls, 212, 124, 506, 82);
            LabelAt("Даты зачёта (UTC)", 20, 232, 190, 26);
            Place(from, 212, 228, 152, 27);
            Place(until, 378, 228, 152, 27);
            LabelAt("ГГГГ-ММ-ДД", 545, 233, 173, 26);
            if (plan.Club)
            {
                LabelAt("Нужно очков", 20, 271, 119, 28);
                Place(required, 141, 267, 78, 27);
                LabelAt("За члена клуба", 248, 271, 128, 28);
                Place(member, 375, 267, 74, 27);
                LabelAt("За бонусный позывной", 471, 271, 173, 28);
                Place(special, 644, 267, 74, 27);
                Place(hf, 20, 296, 278, 24);
                Place(vhfBonus, 310, 296, 245, 24);
                hf.CheckedChanged += delegate { vhfBonus.Enabled = !hf.Checked; };
                LabelAt("Клуб «Рыцари неба». Повтор засчитывается по дате UTC, диапазону и виду работы. "
                    + "FT8 и FT4 относятся к DIGI. Множители для CW ×2, для SSB ×1,5, для 160 м ×1,5.", 20, 326, 700, 66);
            }
            else LabelAt("Для диплома нужна одна связь с этой станцией в указанный срок.", 20, 278, 699, 45);
            var restore = ButtonAt("Вернуть исходные", 20, 410, 205, () => LoadValues(program.DownloadedRules, false));
            restore.Enabled = program.DownloadedRules != null;
            ButtonAt("Отмена", 449, 410, 112, () => DialogResult = DialogResult.Cancel);
            ButtonAt("Сохранить", 572, 410, 146, Save, true);
            LoadValues(plan, program.EditedRules != null);
        }

        void LoadValues(HamlogRulePlan plan, bool edited)
        {
            edition.Text = edited ? "Действуют ваши правки" : "Правила созданы из положения HAMLOG";
            calls.Text = String.Join(", ", plan.Calls);
            from.Text = plan.FromUtc;
            until.Text = plan.UntilUtc;
            required.Value = plan.Required;
            hf.Checked = plan.HfOnly;
            vhfBonus.Checked = plan.VhfBonus;
            if (plan.Club)
            {
                member.Value = plan.MemberPoints;
                special.Value = plan.ExtraPoints;
            }
        }

        void Save()
        {
            HamlogRulePlan previous = program.EffectiveRules, original = program.DownloadedRules;
            var plan = new HamlogRulePlan {
                Kind = previous.Kind, FromUtc = from.Text, UntilUtc = until.Text,
                Required = (int)required.Value, MemberPoints = previous.Club ? (int)member.Value : 0,
                HfOnly = previous.Club && hf.Checked,
                VhfBonus = previous.Club && !hf.Checked && vhfBonus.Checked,
                ExtraPoints = previous.Club ? (int)special.Value : 0,
                Calls = System.Text.RegularExpressions.Regex.Split(calls.Text.Trim(), @"[\s,;]+").Where(value => value.Length > 0).ToArray()
            };
            try
            {
                plan.Validate();
                Edited = original != null && plan.Required == original.Required && plan.MemberPoints == original.MemberPoints
                    && plan.ExtraPoints == original.ExtraPoints && plan.HfOnly == original.HfOnly
                    && plan.VhfBonus == original.VhfBonus && plan.FromUtc == (original.FromUtc ?? "")
                    && plan.UntilUtc == (original.UntilUtc ?? "") && plan.Calls.SequenceEqual(original.Calls) ? null : plan;
                DialogResult = DialogResult.OK;
            }
            catch (Exception error)
            { MessageBox.Show(this, error.Message, "Правила поиска", MessageBoxButtons.OK, MessageBoxIcon.Information); }
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
