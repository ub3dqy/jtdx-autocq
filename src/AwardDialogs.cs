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
        readonly AwardCatalog catalog;
        readonly LogbookHistory history;
        readonly string own;
        readonly Func<string, string> dxcc;
        readonly bool canStart;
        readonly TextBox filter = new TextBox();
        readonly CheckBox ready = new CheckBox { Text = "Готовые к поиску", AutoSize = true };
        readonly CheckBox archive = new CheckBox { Text = "Показать завершённые и истёкшие", AutoSize = true };
        readonly ListView list = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false, CheckBoxes = true, HideSelection = false };
        readonly Label details, overview;
        readonly Button updateCatalog, open, rules, edit, sync, journal, save, start;
        readonly Dictionary<AwardProgram, AwardProgress> progress = new Dictionary<AwardProgram, AwardProgress>();
        bool refreshing, synchronizing;
        CancellationTokenSource catalogDownload;

        internal AwardCatalog Catalog { get { return catalog; } }
        internal bool StartRequested { get; private set; }

        internal AwardsDialog(AwardCatalog catalog, LogbookHistory history, string own, Func<string, string> dxcc, bool canStart)
            : base("Дипломы HAMLOG", 920, 568)
        {
            this.catalog = catalog.Copy(); this.history = history; this.own = own; this.dxcc = dxcc; this.canStart = canStart;
            LabelAt("Поиск", 20, 61, 65, 24);
            Place(filter, 86, 57, 323, 27);
            Place(ready, 433, 59, 168, 26);
            Place(archive, 620, 59, 280, 26);
            overview = LabelAt("", 22, 93, 875, 23);
            Place(list, 20, 122, 880, 245);
            list.Columns.Add("Диплом", 335);
            list.Columns.Add("Зачёт или цели", 148);
            list.Columns.Add("Вне зачёта", 128);
            list.Columns.Add("Состояние", 240);
            details = LabelAt("", 23, 379, 873, 66);

            updateCatalog = ButtonAt("Загрузить каталог", 20, 451, 174, DownloadCatalog);
            ButtonAt("Из кабинета…", 204, 451, 144, () => Import(true));
            ButtonAt("Импорт JSON…", 358, 451, 134, () => Import(false));
            Button templates = null;
            templates = ButtonAt("Готовые дипломы", 502, 451, 148, () => ShowTemplates(templates));
            rules = ButtonAt("Положение…", 660, 451, 119, ShowRules);
            open = ButtonAt("На HAMLOG", 789, 451, 111, ShowPage);
            ButtonAt("Добавить…", 20, 517, 100, () => Edit(null));
            edit = ButtonAt("Мои цели…", 130, 517, 107, () => Edit(Selected));
            sync = ButtonAt("Сверить с HAMLOG", 247, 517, 164, Synchronize);
            journal = ButtonAt("Связи из журнала…", 421, 517, 155, ShowJournal);
            ButtonAt("Справка", 583, 517, 75, () => {
                try { MainForm.OpenGuide(true); }
                catch (Exception error) { MessageBox.Show(this, error.Message, "Руководство", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            });
            save = ButtonAt("Сохранить", 664, 517, 101, () => Accept(false));
            start = ButtonAt("Начать поиск", 775, 517, 125, () => Accept(true), true);

            filter.TextChanged += delegate { RefreshList(); };
            ready.CheckedChanged += delegate { RefreshList(); };
            archive.CheckedChanged += delegate { RefreshList(); };
            FormClosed += delegate { if (catalogDownload != null) catalogDownload.Cancel(); };
            list.SelectedIndexChanged += delegate { UpdateDetails(); };
            list.ItemCheck += delegate(object sender, ItemCheckEventArgs e) {
                if (refreshing) return;
                var program = (AwardProgram)list.Items[e.Index].Tag;
                if (!CanSelectProgram(program)) e.NewValue = CheckState.Unchecked;
                program.Selected = e.NewValue == CheckState.Checked;
                UpdateDetails();
            };
            list.DoubleClick += delegate {
                AwardProgram program = Selected;
                if (program == null) return;
                if (!String.IsNullOrEmpty(program.Url)) ShowRules();
                else if (!program.PublicEntry) Edit(program);
            };
            RefreshList();
        }

        AwardProgram Selected { get { return list.SelectedItems.Count == 1 ? (AwardProgram)list.SelectedItems[0].Tag : null; } }

        AwardProgress Progress(AwardProgram item)
        {
            AwardProgress value;
            if (!progress.TryGetValue(item, out value))
                progress[item] = value = new AwardProgress(item, history, own, dxcc);
            return value;
        }

        bool Done(AwardProgram item)
        { return Progress(item).Complete; }

        static bool Expired(AwardProgram item)
        { return item.Until.HasValue && DateTime.UtcNow >= item.Until.Value; }

        bool CanSelectProgram(AwardProgram item)
        {
            AwardProgress status = Progress(item);
            return item.HasSearchRules && !status.Complete && !Expired(item) && !Upcoming(item);
        }

        static bool Upcoming(AwardProgram item)
        { return item.From.HasValue && DateTime.UtcNow < item.From.Value; }

        static string Points(decimal points) { return points.ToString("0.##", CultureInfo.CurrentCulture); }

        string ProgressText(AwardProgress status)
        {
            if (status.Online != null)
                return status.Online.Standing == null ? "—" : Points(status.Online.Confirmed) + " из " + status.Online.Required;
            if (status.Points != null)
                return status.Points.Standing == null ? "—" : Points(status.Points.Standing.Points) + " из " + status.Points.Required;
            return status.GoalsCount == 0 || status.Program.ClosedByIssuance ? "—" : status.CompletedGoals + " из " + status.GoalsCount;
        }

        string StateText(AwardProgress status)
        {
            AwardProgram program = status.Program;
            if (program.ClosedByIssuance) return "Диплом выдан";
            if (program.Withdrawn && !program.HasSearchRules) return "Вне общего каталога";
            if (Expired(program)) return "Срок истёк";
            if (Upcoming(program)) return "С " + program.From.Value.ToString("dd.MM.yyyy") + " UTC";
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
                        : program.HasIssued == false ? "Без выдачи · нужен план" : "Положение HAMLOG";
                return status.Complete ? "Цели выполнены по журналу" : "Мои цели";
            }
            if (points.IsMember) return "Условия для активаторов";
            if (points.Standing == null) return "Сверьте с HAMLOG";
            if (points.ConfirmedComplete) return "Условия выполнены";
            if (points.PendingEnough) return "Есть связи для зачёта";
            return "Не хватает " + Points(points.NeedOnline) + " очк.";
        }

        void RefreshList(AwardProgram select = null)
        {
            select = select ?? Selected;
            refreshing = true;
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (AwardProgram program in catalog.Programs.OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase))
                {
                    if (!archive.Checked && (program.Withdrawn && !program.HasSearchRules || Done(program) || Expired(program))) continue;
                    if (ready.Checked && !CanSelectProgram(program)) continue;
                    string query = filter.Text.Trim();
                    if (query.Length != 0 && !new[] { program.Title, program.Sponsor, program.Section }.Any(value =>
                        value != null && value.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)) continue;
                    AwardProgress status = Progress(program);
                    var row = new ListViewItem(program.Title) { Tag = program };
                    row.SubItems.Add(ProgressText(status));
                    row.SubItems.Add(status.Points != null && status.Points.Pending.Count > 0
                        ? "+" + Points(status.Points.PendingPoints) + " очк." : "—");
                    row.SubItems.Add(StateText(status));
                    list.Items.Add(row);
                    row.Checked = program.Selected && CanSelectProgram(program);
                    if (program == select) { row.Selected = true; row.Focused = true; }
                }
            }
            finally { list.EndUpdate(); refreshing = false; }
            UpdateDetails();
        }

        void UpdateDetails()
        {
            AwardProgram program = Selected;
            edit.Enabled = !synchronizing && program != null && program.Template == null
                && (!program.PublicEntry || !String.IsNullOrEmpty(program.Url));
            open.Enabled = rules.Enabled = program != null && !String.IsNullOrEmpty(program.Url);
            updateCatalog.Enabled = !synchronizing;
            updateCatalog.Text = catalog.PublicCatalogUtc == DateTime.MinValue ? "Загрузить каталог" : "Обновить каталог";
            sync.Enabled = !synchronizing && program != null && program.UsesTemplate && Callsign.Parse(own) != null;
            save.Enabled = !synchronizing;
            start.Enabled = !synchronizing && canStart && catalog.Programs.Any(item => item.Selected && CanSelectProgram(item));
            AwardProgress value = program == null ? null : Progress(program);
            journal.Enabled = value != null && value.Points != null && value.Points.Pending.Count > 0;
            if (catalogDownload == null)
                overview.Text = catalog.PublicCatalogUtc == DateTime.MinValue
                    ? String.IsNullOrEmpty(catalog.Callsign) ? "Загрузите каталог HAMLOG или откройте готовый диплом."
                        : "Дипломы из кабинета " + catalog.Callsign + " · показано " + list.Items.Count
                    : "В каталоге HAMLOG " + catalog.Programs.Count(item => item.PublicEntry && !item.Withdrawn)
                        + " · показано " + list.Items.Count
                        + (String.IsNullOrEmpty(catalog.Callsign) ? "" : " · отметки о выдаче для " + catalog.Callsign);
            if (program == null)
            {
                details.Text = "Через «Из кабинета» можно скрыть выданные дипломы для вашего позывного. "
                    + "Галочка «Готовые к поиску» покажет карточки, по которым AutoCQ уже умеет искать станции.";
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
            if (value.Points != null) { details.Text = context + PointDetails(value.Points); return; }
            if (!program.UsesLocalTargets)
            {
                details.Text = context + (String.IsNullOrEmpty(program.Url)
                    ? program.PublicEntry ? "HAMLOG пока не опубликовал страницу этой программы."
                    : "Добавьте позывные, страны или маски для своего поиска."
                    : "Откройте положение. Если вам известны нужные станции, добавьте их через «Мои цели».");
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
            if (synchronizing) return;
            var abort = new CancellationTokenSource();
            catalogDownload = abort;
            synchronizing = true;
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

        string PointDetails(HamlogPointProgress points)
        {
            if (points.IsMember && !points.Closed) return "Для членов клуба действуют условия активатора. Откройте положение.";
            if (points.Standing == null) return Callsign.Parse(own) == null
                ? "Подключите JTDX с вашим позывным, чтобы получить зачёт HAMLOG."
                : "Нажмите «Сверить с HAMLOG», чтобы получить зачтённые связи для вашего позывного.";
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

        void ShowTemplates(Control anchor)
        {
            if (synchronizing) return;
            var menu = new ContextMenuStrip();
            foreach (HamlogAward model in HamlogAward.Templates)
            {
                HamlogAward template = model;
                menu.Items.Add(template.Title, null, delegate {
                    AwardProgram program = catalog.Programs.FirstOrDefault(item => item.Url == template.Url);
                    if (program == null)
                    {
                        program = template.Create();
                        catalog.Programs.Add(program);
                    }
                    if (Expired(program) || Done(program)) archive.Checked = true;
                    filter.Clear();
                    RefreshList(program);
                });
            }
            menu.Closed += delegate { menu.Dispose(); };
            menu.Show(anchor, new Point(0, anchor.Height));
        }

        void Synchronize()
        {
            AwardProgram program = Selected;
            if (!sync.Enabled || program == null) return;
            synchronizing = true;
            sync.Text = "Проверяю HAMLOG…";
            UpdateDetails();
            var worker = new BackgroundWorker();
            worker.DoWork += (sender, args) => args.Result = HamlogStandingClient.Read(program.Template, own);
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs args) {
                worker.Dispose();
                if (IsDisposed || Disposing) return;
                synchronizing = false;
                sync.Text = "Сверить с HAMLOG";
                if (args.Error == null)
                {
                    program.Hamlog = (HamlogStanding)args.Result;
                    progress.Remove(program);
                    if (!CanSelectProgram(program)) program.Selected = false;
                    if (Done(program)) archive.Checked = true;
                    RefreshList(program);
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
                Title = personal ? "Выберите файл из кабинета HAMLOG" : "Открыть каталог или список целей",
                Filter = personal ? "Страница или каталог HAMLOG (*.html;*.htm;*.json)|*.html;*.htm;*.json"
                    : "Каталог AutoCQ (*.json)|*.json", CheckFileExists = true, RestoreDirectory = true })
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
            if (program != null && program.Template != null) return;
            using (var editor = new AwardGoalsDialog(program))
            {
                if (editor.ShowDialog(this) != DialogResult.OK) return;
                AwardProgram edited = editor.Program;
                AwardProgram duplicate = catalog.Programs.FirstOrDefault(item => item != program && item.Id == edited.Id);
                if (duplicate != null)
                {
                    MessageBox.Show(this, "Диплом уже есть в списке. Выберите его и откройте «Условия».",
                        "Дипломы HAMLOG", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (program != null) { catalog.Programs.Remove(program); progress.Remove(program); }
                catalog.Programs.Add(edited);
                filter.Clear();
                RefreshList(edited);
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
            using (var publication = new AwardRulesDialog(program)) publication.ShowDialog(this);
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

        void Accept(bool run)
        {
            if (synchronizing) return;
            foreach (AwardProgram program in catalog.Programs)
                if (program.Selected && !CanSelectProgram(program)) program.Selected = false;
            if (run && !catalog.Programs.Any(program => program.Selected)) return;
            StartRequested = run;
            DialogResult = DialogResult.OK;
        }

        internal void SelectDemoProgram(AwardProgram program)
        {
            archive.Checked = true;
            RefreshList(catalog.Programs.FirstOrDefault(item => item.Id == program.Id));
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

    internal sealed class AwardRulesDialog : OperatorDialog
    {
        readonly string award;
        readonly Label state;
        readonly TextBox text = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        readonly Button save;
        CancellationTokenSource download;
        HamlogRules publication;

        internal AwardRulesDialog(AwardProgram program) : base("Положение HAMLOG", 780, 474)
        {
            award = HamlogLinks.Award(program.Url);
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
                HamlogRules result = await Task.Run(() => HamlogRules.Read(award, abort.Token));
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
