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
        AwardCatalog catalog;
        readonly string own;
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
        readonly ToolStripItem rules, edit, editTargets;
        bool refreshing, synchronizing;
        DateTime nextValidityChange;
        CancellationTokenSource catalogDownload;

        internal AwardCatalog Catalog { get { return catalog; } }
        internal AwardsDialog(AwardCatalog catalog, string own,
            string cache = null, Action<string> log = null, bool demo = false)
            : base("Дипломы HAMLOG", 920, 594)
        {
            this.catalog = catalog.Copy(); this.own = own;
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
                "Полученные", "Срок истёк", "Начнутся позже", "Все дипломы" });
            state.SelectedIndex = (int)StateFilter.Active;
            overview = LabelAt("", 22, 114, 875, 23);
            Place(list, 20, 142, 880, 265);
            list.DefaultCellStyle.SelectionBackColor = Color.FromArgb(222, 235, 248);
            list.DefaultCellStyle.SelectionForeColor = ForeColor;
            list.Columns.Add(new DataGridViewCheckBoxColumn {
                HeaderText = "Поиск", AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 63
            });
            foreach (var column in new[] {
                new DataGridViewTextBoxColumn { HeaderText = "Диплом", FillWeight = 51 },
                new DataGridViewTextBoxColumn { HeaderText = "Клуб или организатор", FillWeight = 24 },
                new DataGridViewTextBoxColumn { HeaderText = "Состояние", FillWeight = 25 }
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
            ButtonAt("Полученные из кабинета…", 222, 544, 238, () => Import(true));
            open = ButtonAt("Страница на HAMLOG", 701, 544, 199, ShowPage);
            edit = actions.Items.Add("Правила поиска…", null, delegate { EditRules(); });
            rules = actions.Items.Add("Положение HAMLOG…", null, delegate { ShowRules(); });
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
                e.Cancel = !program.CanSelectForSearch(DateTime.UtcNow);
            };
            list.CurrentCellDirtyStateChanged += delegate { if (list.IsCurrentCellDirty) list.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            list.CellValueChanged += delegate(object sender, DataGridViewCellEventArgs e) {
                if (refreshing || e.RowIndex < 0 || e.ColumnIndex != 0) return;
                DataGridViewRow row = list.Rows[e.RowIndex];
                var program = (AwardProgram)row.Tag;
                program.Selected = Object.Equals(row.Cells[0].Value, true) && program.CanSelectForSearch(DateTime.UtcNow);
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

        static string SectionOf(AwardProgram program)
        {
            if (!String.IsNullOrEmpty(program.Section)) return program.Section;
            return program.PublicEntry || !String.IsNullOrEmpty(program.Url) ? "Другие дипломы HAMLOG" : "Мои списки";
        }

        static bool MatchesState(StateFilter filter, AwardProgram program, DateTime now)
        {
            switch (filter)
            {
                case StateFilter.All: return true;
                case StateFilter.Searchable: return program.CanSelectForSearch(now);
                case StateFilter.Completed: return program.ClosedByIssuance;
                case StateFilter.Expired: return program.Expired(now);
                case StateFilter.Upcoming: return !program.ClosedByIssuance && !program.Expired(now) && program.Upcoming(now);
                default: return !program.ClosedByIssuance && !program.Expired(now) && !program.Upcoming(now)
                    && (!program.Withdrawn || program.HasSearchRules)
                    && (filter == StateFilter.Active || !program.HasSearchRules);
            }
        }

        static string StateText(AwardProgram program, DateTime now)
        {
            if (program.ClosedByIssuance) return "Диплом получен";
            if (program.Expired(now)) return "Срок истёк";
            if (program.Upcoming(now)) return "С " + program.From.Value.ToString("dd.MM.yyyy") + " UTC";
            if (program.HasSearchRules) return program.HasEditedRules ? "Ручные правила" : "Готов к поиску";
            if (program.Withdrawn) return "Вне общего каталога";
            if (String.IsNullOrEmpty(program.Url)) return program.PublicEntry ? "Ожидает публикации" : "Добавьте цели";
            if (!String.IsNullOrEmpty(program.RuleNote)) return "Поиск недоступен";
            switch (program.RuleDocumentStatus)
            {
                case "downloaded": return "Условия пока не распознаны";
                case "unreadable": return "Текст PDF не прочитан";
                case "missing": return "Положение недоступно";
                case "retry": return "Ошибка загрузки положения";
                default: return "Положение ещё не загружено";
            }
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
                    if (!MatchesState((StateFilter)state.SelectedIndex, select, now))
                        state.SelectedIndex = (int)(select.ClosedByIssuance ? StateFilter.Completed : StateFilter.All);
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
                    DateTime boundary = program.NextValidityChange(now);
                    if (boundary < nextValidityChange) nextValidityChange = boundary;
                    bool selectable = program.CanSelectForSearch(now);
                    if (!selectable) program.Selected = false;
                    if (sectionName != AllSections && sectionName != SectionOf(program)
                        || !MatchesQuery(program, query) || !MatchesState(show, program, now)) continue;

                    string label = StateText(program, now);
                    var row = new DataGridViewRow { Tag = program };
                    row.CreateCells(list, program.Selected, program.Title, program.Sponsor ?? "", label);
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
            int selectedCount = catalog.Programs.Count(item => item.Selected && item.CanSelectForSearch(now));
            if (catalogDownload == null)
                overview.Text = "Показано " + list.Rows.Count + " из " + catalog.Programs.Count + " · выбрано для мониторинга " + selectedCount
                    + (String.IsNullOrEmpty(catalog.Callsign) ? "" : " · полученные для " + catalog.Callsign);
            if (program == null)
            {
                details.Text = "По этим фильтрам дипломов нет. Измените название, раздел или состояние.";
                return;
            }
            if (program.ClosedByIssuance)
            {
                details.Text = "Диплом отмечен как полученный при импорте из кабинета HAMLOG.";
                return;
            }
            if (program.HasSearchRules)
            {
                HamlogAward template = program.Template;
                string region = template == null ? null : template.MonitoringRegion;
                details.Text = region != null ? "Поиск среди станций области, указанной в положении."
                    : program.EffectiveRules != null && program.EffectiveRules.RdaOnly
                        ? "Поиск по европейской и азиатской частям России и Калининградской области."
                        : "Мониторинг подбирает станции по условиям диплома на текущем диапазоне.";
                details.Text += "\r\nДвойной щелчок открывает правила поиска.";
                return;
            }
            details.Text = program.RuleNote ?? (String.IsNullOrEmpty(program.Url) && program.PublicEntry
                ? "HAMLOG пока не опубликовал страницу этой программы."
                : "Откройте положение на HAMLOG или задайте правила поиска двойным щелчком.");
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
            overview.Text = recompile ? "Разбираю уже скачанные положения…" :
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
                .OrderBy(program => program.Expired(now)).ThenBy(program => program.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
            var byId = pending.ToDictionary(program => program.Id, StringComparer.Ordinal);
            int visited = 0, created = 0;
            IProgress<HamlogRuleResult> completed = new Progress<HamlogRuleResult>(result => {
                if (IsDisposed || catalogDownload != abort) return;
                AwardProgram program = byId[result.Id];
                program.ApplyDocument(result);
                if (result.Error != null && log != null)
                    log("HAMLOG. " + program.Title + " (" + program.Url + "). " + result.Error);
                if (result.Plan != null) created++;
                overview.Text = "Положения проверены " + ++visited + " из " + pending.Length + " · правил создано " + created;
                if (visited % 10 == 0 || visited == pending.Length) RefreshList();
            });
            await Task.Run(() => documents.Scan(pending, abort.Token, completed.Report));
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
                Title = personal ? "Выберите сохранённые страницы из кабинета HAMLOG" : "Открыть готовый список дипломов или целей",
                Filter = personal ? "Страница или каталог HAMLOG (*.html;*.htm;*.json)|*.html;*.htm;*.json"
                    : "Список дипломов или целей AutoCQ (*.json)|*.json", CheckFileExists = true, RestoreDirectory = true, Multiselect = personal })
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
                    AwardCatalog updated = catalog.Copy();
                    foreach (string path in file.FileNames)
                        if (personal && !String.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
                            unmatched += updated.ImportIssuedPage(path, station);
                        else updated.Import(path, station, personal);
                    catalog = updated;
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
                if (program != null) { catalog.Programs.Remove(program); }
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
            LabelAt("2. Сохраните страницы через Ctrl+S в формате «Веб-страница, только HTML».", 20, 100, 650, 38);
            LabelAt("3. Выберите сохранённые файлы. AutoCQ отметит полученные дипломы.", 20, 141, 650, 38);
            LabelAt("Позывной в HAMLOG", 20, 194, 170, 25);
            Station = Callsign.Parse(radioCall) ?? Callsign.Parse(catalogCall) ?? "";
            station.Text = Station; station.ReadOnly = Callsign.Parse(radioCall) != null;
            Place(station, 198, 190, 162, 29);
            LabelAt("Если у вас есть файл каталога AutoCQ в JSON, его тоже можно открыть здесь.", 20, 232, 650, 32);
            ButtonAt("Кабинет HAMLOG", 20, 279, 160, () => AwardsDialog.OpenInBrowser(this, HamlogLinks.Catalog));
            ButtonAt("Отмена", 392, 279, 102, () => DialogResult = DialogResult.Cancel);
            ButtonAt("Выбрать файлы…", 505, 279, 165, Accept, true);
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
        readonly TextBox from = new TextBox { MaxLength = 32 }, until = new TextBox { MaxLength = 32 };
        readonly TextBox calls = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, MaxLength = 1500 };
        readonly TextBox groups = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, MaxLength = 4000 };
        readonly TextBox region = new TextBox { MaxLength = 2 };
        readonly TextBox countries = new TextBox { MaxLength = 350 };
        readonly TextBox bands = new TextBox { MaxLength = 230 }, modes = new TextBox { MaxLength = 80 };
        readonly NumericUpDown distance = new NumericUpDown { Minimum = 1, Maximum = 20000, ThousandsSeparator = true };
        readonly CheckBox hfOnly = new CheckBox { Text = "Только КВ (160–10 м)", AutoSize = true };
        readonly bool showRegion, showCalls, showGroups, showDistance;
        HamlogRulePlan loaded;
        internal HamlogRulePlan Edited { get; private set; }

        internal HamlogPlanDialog(AwardProgram program) : base("Правила мониторинга", 740, 488)
        {
            this.program = program;
            HamlogRulePlan plan = program.EffectiveRules;
            LabelAt(program.Title, 20, 59, 698, 27);
            edition = LabelAt("", 20, 89, 698, 25);
            LabelAt("Срок действия (UTC)", 20, 130, 188, 25);
            Place(from, 212, 126, 179, 27);
            Place(until, 405, 126, 179, 27);
            LabelAt("ГГГГ-ММ-ДД", 600, 131, 118, 24);

            showRegion = plan.RdaOnly || plan.ContactAward && plan.Contacts.RestrictsCounties;
            showDistance = plan.ContactAward && plan.Contacts.MinimumDistanceKm > 0;
            showCalls = !plan.RdaOnly && !showDistance && !plan.AllStations && !plan.Districts;
            showGroups = (plan.Club || plan.ContactAward) && showCalls;
            int top = 168;
            if (showRegion)
            {
                LabelAt("Код области RDA", 20, top + 4, 188, 25);
                Place(region, 212, top, 79, 27);
                LabelAt(plan.RdaOnly ? "Пустое поле означает всю Россию, включая Калининград." : "AutoCQ ищет станции из указанной области.",
                    305, top + 4, 413, 28);
                top += 41;
            }
            if (showCalls)
            {
                LabelAt("Позывные по положению", 20, top + 4, 188, 25);
                Place(calls, 212, top, 506, 65);
                top += 74;
            }
            if (showGroups)
            {
                LabelAt("Дополнительные станции", 20, top + 4, 188, 25);
                Place(groups, 212, top, 506, 65);
                LabelAt("Позывные или коды территорий через запятую. Для страны укажите, например, DXCC JA.",
                    212, top + 71, 506, 34);
                top += 112;
            }
            if (showDistance)
            {
                LabelAt("Дальность, больше км", 20, top + 4, 188, 25);
                Place(distance, 212, top, 112, 27);
                LabelAt("Нужны ваш локатор в JTDX и локатор станции из свежего сообщения.", 340, top + 2, 378, 46);
                top += 55;
            }
            if (plan.ContactAward)
            {
                if (showCalls)
                {
                    LabelAt("Страны DXCC", 20, top + 4, 188, 25);
                    Place(countries, 212, top, 506, 27);
                    top += 37;
                }
                LabelAt("Диапазоны (пусто — все)", 20, top + 4, 188, 25);
                Place(bands, 212, top, 506, 27);
                LabelAt("Виды (пусто — все)", 20, top + 41, 188, 25);
                Place(modes, 212, top + 37, 506, 27);
                top += 76;
            }
            if (plan.Club)
            {
                Place(hfOnly, 20, top, 215, 25);
                top += 34;
            }
            string roster = plan.Rcpw ? "rcpw" : plan.ContactAward ? plan.Contacts.Roster : null;
            if (!String.IsNullOrEmpty(roster) || plan.Club || plan.AllStations)
            {
                LabelAt(plan.AllStations ? "Подходят станции всех стран." : plan.Club
                    ? "Используется список клуба «Рыцари неба»."
                    : "Используется список " + HamlogAwardRosters.Label(roster) + ".", 20, top, 698, 26);
                top += 33;
            }

            var restore = ButtonAt("Вернуть исходные", 20, top, 195, () => LoadValues(program.DownloadedRules, false));
            restore.Enabled = program.DownloadedRules != null;
            ButtonAt("Отмена", 456, top, 112, () => DialogResult = DialogResult.Cancel);
            ButtonAt("Сохранить", 572, top, 146, Save, true);
            ClientSize = new Size(740, top + 53);
            LoadValues(plan, program.EditedRules != null);
        }

        void LoadValues(HamlogRulePlan plan, bool edited)
        {
            loaded = plan;
            edition.Text = edited ? "Действуют ваши правки" : "По положению HAMLOG";
            from.Text = plan.FromUtc;
            until.Text = plan.UntilUtc;
            if (showRegion) region.Text = RegionFor(plan);
            calls.Text = String.Join(", ", plan.Calls);
            groups.Lines = plan.BonusGroups.Select(group => String.Join(", ", group.Targets)).ToArray();
            hfOnly.Checked = plan.HfOnly;
            if (plan.ContactAward)
            {
                bands.Text = String.Join(", ", plan.Contacts.Bands);
                modes.Text = String.Join(", ", plan.Contacts.Modes);
                countries.Text = String.Join(", ", plan.Contacts.Countries);
                if (showDistance) distance.Value = plan.Contacts.MinimumDistanceKm;
            }
        }

        void Save()
        {
            try
            {
                HamlogRulePlan changed = loaded.Copy();
                changed.FromUtc = from.Text;
                changed.UntilUtc = until.Text;
                if (showRegion)
                {
                    string entered = region.Text.Trim().ToUpperInvariant();
                    if (!loaded.RdaOnly && entered.Length == 0) throw new InvalidDataException("Укажите код области RDA.");
                    changed.SearchRegion = entered == RegionFor(loaded) ? loaded.SearchRegion : entered;
                }
                if (showCalls) changed.Calls = Words(calls.Text);
                if (showGroups)
                    changed.BonusGroups = groups.Lines.Where(line => !String.IsNullOrWhiteSpace(line))
                        .Select((line, i) => ReadGroup(line, i)).ToArray();
                if (changed.ContactAward)
                {
                    changed.Contacts.Bands = Words(bands.Text).Select(band => band.ToLowerInvariant()).ToArray();
                    changed.Contacts.Modes = Words(modes.Text).Select(mode => mode.ToUpperInvariant()).ToArray();
                    if (showCalls) changed.Contacts.Countries = Words(countries.Text).Select(country => country.ToUpperInvariant()).ToArray();
                    if (showDistance) changed.Contacts.MinimumDistanceKm = (int)distance.Value;
                }
                if (changed.Club)
                {
                    changed.HfOnly = hfOnly.Checked;
                    if (changed.HfOnly) changed.VhfBonus = false;
                }
                changed.Validate();
                Edited = changed.SameAs(program.DownloadedRules) ? null : changed;
                DialogResult = DialogResult.OK;
            }
            catch (Exception error)
            { MessageBox.Show(this, error.Message, "Правила мониторинга", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }

        HamlogBonusGroup ReadGroup(string line, int index)
        {
            string[] values = line.ToUpperInvariant().Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()).Where(value => value.Length != 0).ToArray();
            if (values.Length == 0) throw new InvalidDataException("Укажите позывные или территорию.");
            return new HamlogBonusGroup { Targets = values, Area = values.All(HamlogAwardAreas.Known),
                Points = index < loaded.BonusGroups.Length ? loaded.BonusGroups[index].Points : 1 };
        }

        static string[] Words(string text)
        { return text.Split(new[] { ' ', ',', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries); }

        static string RegionFor(HamlogRulePlan plan)
        { return plan.RdaOnly ? plan.SearchRegion ?? "" : plan.MonitoringRegion ?? plan.Contacts.CountyPrefix; }
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

    internal sealed class AwardGoalsDialog : OperatorDialog
    {
        static readonly string[] Kinds = { "Позывной", "DXCC", "Маска" };
        static readonly string[] Keys = { "call", "dxcc", "mask" };
        readonly TextBox title = new TextBox { MaxLength = 300 }, url = new TextBox { MaxLength = 512 };
        readonly TextBox from = new TextBox { MaxLength = 30 }, until = new TextBox { MaxLength = 30 };
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
            LabelAt("Даты можно оставить пустыми. Укажите первый и последний день действия.", 20, 169, 690, 25);
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
            LabelAt("Пустое поле означает любой диапазон или вид работы.", 20, 434, 690, 24);
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
                goals.Rows.Add(Kinds[Array.IndexOf(Keys, goal.Kind)], goal.Value, goal.Band, goal.Mode);
        }

        static string Value(DataGridViewRow row, int index) { return Convert.ToString(row.Cells[index].Value).Trim(); }

        void Save()
        {
            try
            {
                goals.EndEdit();
                var updated = original == null ? new AwardProgram() : original.Copy();
                updated.Title = title.Text; updated.Url = url.Text; updated.FromUtc = from.Text; updated.UntilUtc = until.Text;
                if (original != null && updated.Url.Trim().TrimEnd('/') != original.Url.TrimEnd('/'))
                {
                    updated.Id = null;
                    updated.HasIssued = null;
                    updated.SetPublication(new AwardProgram());
                }
                updated.Goals.Clear();
                foreach (DataGridViewRow row in goals.Rows)
                {
                    if (row.IsNewRow) continue;
                    string kind = Value(row, 0), value = Value(row, 1), band = Value(row, 2), mode = Value(row, 3);
                    if (kind.Length + value.Length + band.Length + mode.Length == 0) continue;
                    int type = Array.IndexOf(Kinds, kind);
                    if (type < 0)
                        throw new InvalidDataException("Выберите тип цели.");
                    updated.Goals.Add(new AwardGoal { Kind = Keys[type], Value = value, Band = band, Mode = mode });
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
