using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace JtdxAutoCq
{
    internal class OperatorDialog : Form
    {
        internal OperatorDialog(string title, int width, int height)
        {
            Text = title + " — " + AppBrand.DisplayName;
            ClientSize = new Size(width, height);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10);
            BackColor = Color.FromArgb(238, 243, 248);
            ForeColor = Color.FromArgb(21, 38, 58);
            MinimizeBox = MaximizeBox = false;
            BrandUi.ApplyIcon(this);
            LabelAt(title, 20, 14, width - 40, 37).Font = new Font("Segoe UI Semibold", 17);
        }

        protected Label LabelAt(string value, int x, int y, int width, int height)
        {
            var label = new Label { Text = value };
            Place(label, x, y, width, height);
            return label;
        }

        protected Button ButtonAt(string text, int x, int y, int width, Action action, bool primary = false)
        {
            var button = new Button { Text = text };
            if (primary)
            {
                button.FlatStyle = FlatStyle.Flat;
                button.BackColor = Color.FromArgb(0, 100, 91);
                button.ForeColor = Color.White;
            }
            button.Click += delegate { action(); };
            Place(button, x, y, width, 35);
            return button;
        }

        protected void Place(Control control, int x, int y, int width, int height)
        {
            control.SetBounds(x, y, width, height);
            Controls.Add(control);
        }
    }

    internal sealed class ProfilesDialog : OperatorDialog
    {
        readonly ListBox profiles = new ListBox();
        readonly TextBox name = new TextBox { MaxLength = 48 };
        readonly Label details;
        readonly Button save, remove, apply;
        readonly List<OperatorProfile> saved;
        readonly AutoCqOptions current;
        readonly bool canApply;
        internal AutoCqOptions Chosen { get; private set; }
        internal bool Changed { get; private set; }

        internal ProfilesDialog(List<OperatorProfile> saved, AutoCqOptions current, bool canApply)
            : base("Профили настроек", 648, 420)
        {
            this.saved = saved;
            this.current = current;
            this.canApply = canApply;
            LabelAt("Сохраните настройки AutoCQ под своим названием.", 23, 58, 603, 27);
            Place(profiles, 23, 93, 285, 192);
            details = LabelAt("", 325, 95, 292, 148);
            LabelAt("Название профиля", 23, 294, 283, 24);
            Place(name, 23, 320, 285, 27);
            save = ButtonAt("Сохранить текущие", 325, 315, 294, SaveProfile);
            remove = ButtonAt("Удалить профиль", 23, 366, 168, RemoveProfile);
            ButtonAt("Закрыть", 325, 366, 121, Close);
            apply = ButtonAt("Применить", 460, 366, 159, ApplyProfile, true);
            profiles.SelectedIndexChanged += delegate {
                if (Selected != null) name.Text = Selected.Name;
                RefreshStatus();
            };
            name.TextChanged += delegate { RefreshStatus(); };
            RefreshList(saved.FirstOrDefault());
        }

        OperatorProfile Selected
        { get { return profiles.SelectedIndex < 0 ? null : saved[profiles.SelectedIndex]; } }

        OperatorProfile Named
        { get { return saved.FirstOrDefault(item => String.Equals(item.Name, name.Text.Trim(), StringComparison.OrdinalIgnoreCase)); } }

        void RefreshList(OperatorProfile select)
        {
            profiles.Items.Clear();
            profiles.Items.AddRange(saved.Select(profile => (object)profile.Name).ToArray());
            profiles.SelectedIndex = select == null ? -1 : saved.IndexOf(select);
            RefreshStatus();
        }

        void RefreshStatus()
        {
            var profile = Selected;
            details.Text = profile == null ? "Выберите сохранённый профиль." :
                (profile.Options.MonitorEnabled ? "Мониторинг" : "Автоматический CQ") + " · " + profile.Options.RunMinutes
                + " мин" + Environment.NewLine + "Ожидание 73 · " + profile.Options.Wait73Periods
                + Environment.NewLine + "Вызовов без ответа · " + profile.Options.CallAttempts
                + Environment.NewLine + "Цели DXCC · " + profile.Options.MonitorDxcc.Length;
            if (!canApply) details.Text += Environment.NewLine + Environment.NewLine + "Применить профиль можно после остановки автомата.";
            save.Enabled = name.Text.Trim().Length > 0;
            save.Text = Named == null ? "Сохранить текущие" : "Обновить текущими";
            remove.Enabled = profile != null;
            apply.Enabled = canApply && profile != null;
        }

        void SaveProfile()
        {
            OperatorProfile profile = Named;
            if (profile == null)
            {
                profile = new OperatorProfile { Name = name.Text.Trim() };
                saved.Add(profile);
            }
            profile.Options = current.Copy();
            Changed = true;
            RefreshList(profile);
        }

        void RemoveProfile()
        {
            OperatorProfile item = Selected;
            if (item == null) return;
            profiles.SelectedIndex = -1;
            saved.Remove(item);
            name.Clear();
            Changed = true;
            RefreshList(null);
        }

        void ApplyProfile()
        {
            if (!canApply || Selected == null) return;
            Chosen = Selected.Options.Copy();
            DialogResult = DialogResult.OK;
        }
    }

    internal sealed class DxccTargetsDialog : OperatorDialog
    {
        readonly ListBox band = new ListBox { IntegralHeight = false, AccessibleName = "Диапазон" };
        readonly TextBox search = new TextBox();
        readonly CheckedListBox countries = new CheckedListBox { CheckOnClick = true };
        readonly HashSet<string> chosen = new HashSet<string>();
        readonly DxccCountries table;
        readonly LogbookHistory history;
        readonly AutoCqOptions options;
        readonly string own;
        readonly Label count;
        readonly Button add;
        bool filtering;
        internal string[] SelectedPrefixes { get { return chosen.OrderBy(value => value).ToArray(); } }

        internal DxccTargetsDialog(DxccCountries table, LogbookHistory history, AutoCqOptions options, string own,
            string initialBand, string source) : base("Подбор целей DXCC", 690, 538)
        {
            this.table = table;
            this.history = history;
            this.options = options;
            this.own = own;
            LabelAt("Журнал · " + source, 23, 56, 641, 26);
            LabelAt("Диапазон", 23, 90, 127, 22);
            LabelAt("Поиск по стране или префиксу", 166, 90, 498, 22);
            band.Items.AddRange(JtdxAdif.BandNames.Cast<object>().ToArray());
            band.SelectedItem = JtdxAdif.BandNames.Contains(initialBand) ? initialBand : "20m";
            Place(band, 23, 115, 126, 327);
            Place(search, 166, 115, 500, 28);
            Place(countries, 166, 157, 500, 285);
            count = LabelAt("", 23, 452, 641, 22);
            ButtonAt("Закрыть", 339, 482, 135, Close);
            add = ButtonAt("Добавить в цели", 487, 482, 179, () => DialogResult = DialogResult.OK, true);
            band.SelectedIndexChanged += delegate { chosen.Clear(); Rebuild(); };
            search.TextChanged += delegate { Rebuild(); };
            countries.ItemCheck += delegate(object sender, ItemCheckEventArgs e) {
                if (filtering) return;
                string prefix = ((CountryItem)countries.Items[e.Index]).Country.Prefix;
                if (e.NewValue == CheckState.Checked) chosen.Add(prefix);
                else chosen.Remove(prefix);
                add.Enabled = chosen.Count > 0;
            };
            Rebuild();
        }

        internal void SelectDemoCountry()
        {
            if (countries.Items.Count > 0) countries.SetItemChecked(0, true);
        }

        sealed class CountryItem
        {
            internal readonly DxccCountry Country;
            internal CountryItem(DxccCountry country) { Country = country; }
            public override string ToString() { return Country.Name + " · " + Country.Prefix; }
        }

        void Rebuild()
        {
            filtering = true;
            countries.BeginUpdate();
            try
            {
                HashSet<string> worked = history.WorkedDxcc(table, (string)band.SelectedItem, own, null);
                var available = table.Countries.Where(country => !worked.Contains(country.Prefix)
                    && !options.ExcludedDxcc.Contains(country.Prefix) && !options.MonitorDxcc.Contains(country.Prefix))
                    .GroupBy(country => country.Prefix).Select(group => group.First()).OrderBy(country => country.Name).ToArray();
                countries.Items.Clear();
                string term = search.Text.Trim();
                foreach (DxccCountry country in available.Where(country =>
                    country.Name.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0
                    || country.Prefix.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0))
                    countries.Items.Add(new CountryItem(country), chosen.Contains(country.Prefix));
                count.Text = "Доступно для добавления · " + available.Length + "   |   Связи уже есть · " + worked.Count;
                add.Enabled = chosen.Count > 0;
            }
            finally { countries.EndUpdate(); filtering = false; }
        }
    }

    internal sealed class LogbookSourceDialog : OperatorDialog
    {
        readonly Label source;
        readonly string automatic;
        internal string ChosenFile { get; private set; }

        internal LogbookSourceDialog(string configured, string automatic, string automaticName)
            : base("Основной журнал", 620, 274)
        {
            this.automatic = automaticName + " · " + Path.GetFileName(automatic);
            ChosenFile = configured ?? "";
            LabelAt("AutoCQ сверяет историю с этим журналом. Можно открыть LogHX (.lhx2) или экспорт ADIF.", 23, 59, 573, 50);
            source = LabelAt("", 23, 119, 573, 46);
            ButtonAt("Выбрать файл", 23, 177, 167, Browse);
            ButtonAt("Автоматический выбор", 200, 177, 228, () => { ChosenFile = ""; RefreshSource(); });
            ButtonAt("Сохранить", 441, 222, 155, () => DialogResult = DialogResult.OK, true);
            RefreshSource();
        }

        void RefreshSource()
        { source.Text = ChosenFile.Length == 0 ? "Сейчас по умолчанию · " + automatic : "Выбрано · " + ChosenFile; }

        void Browse()
        {
            using (var files = new OpenFileDialog {
                Title = "Выберите основной журнал",
                Filter = "Журналы LogHX и ADIF|*.lhx2;*.adi;*.adif|Базы LogHX|*.lhx2|Экспорт ADIF|*.adi;*.adif",
                CheckFileExists = true,
            })
            {
                if (files.ShowDialog(this) != DialogResult.OK) return;
                ChosenFile = files.FileName;
                RefreshSource();
            }
        }
    }

    internal sealed class StationDecisionDialog : OperatorDialog
    {
        internal StationDecisionDialog(string call, DxccCountry country, string reason, CqStation station)
            : base("Почему станция пропущена?", 610, 278)
        {
            LabelAt(call + (country == null ? "" : " · " + country.Name), 23, 57, 561, 33).Font = new Font("Segoe UI Semibold", 13);
            if (station != null && station.Decode != null)
                LabelAt(station.Decode.Message + "   ·   " + station.HeardUtc.ToLocalTime().ToString("HH:mm:ss"), 23, 96, 560, 31);
            LabelAt(reason, 23, 140, 560, 63);
            ButtonAt("Закрыть", 449, 224, 136, Close);
        }
    }

    internal sealed class SessionSummaryDialog : OperatorDialog
    {
        readonly OperatingSession session;
        readonly ListView contacts = new ListView { View = View.Details, FullRowSelect = true, GridLines = true };
        readonly Label check;
        readonly Button recover;
        readonly string nativeLogbook;
        readonly Func<string> currentLogbook;
        string recoverySource;
        LogbookQso[] recoveryDemo;

        internal SessionSummaryDialog(OperatingSession session, string nativeLogbook = null, Func<string> currentLogbook = null)
            : base("Итоги сеанса", 746, 509)
        {
            this.session = session;
            this.nativeLogbook = nativeLogbook;
            this.currentLogbook = currentLogbook;
            string elapsed = session.StartedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")
                + (session.StoppedUtc.HasValue ? " – " + session.StoppedUtc.Value.ToLocalTime().ToString("HH:mm:ss") : " · сеанс продолжается");
            LabelAt(elapsed + " · " + (session.Band ?? "—") + " · " + session.Mode + " · "
                + (session.Monitoring ? "Мониторинг" : "Автоматический CQ"), 23, 57, 700, 27);
            LabelAt("В журнале JTDX · " + session.Contacts.Count(row => row.LoggedUtc.HasValue)
                + "     Подтверждено · " + session.Contacts.Count(row => row.ConfirmedUtc.HasValue)
                + "     Без финала · " + session.Contacts.Count(row => row.MissingUtc.HasValue), 23, 89, 700, 25);
            LabelAt(session.StopReason ?? "Автомат продолжает работу.", 23, 119, 699, 43);
            check = LabelAt("Сверяю с основным журналом…", 23, 164, 700, 29);
            contacts.Columns.Add("Позывной", 133);
            contacts.Columns.Add("Время", 91);
            contacts.Columns.Add("Заключительный обмен", 175);
            contacts.Columns.Add("В JTDX", 91);
            contacts.Columns.Add("В основном журнале", 193);
            Place(contacts, 23, 205, 701, 237);
            recover = ButtonAt("Экспорт для LogHX…", 23, 457, 196, OpenRecovery);
            ButtonAt("Закрыть", 588, 457, 136, Close);
            Fill();
        }

        internal void Fill(LogbookHistory history = null, string source = null, Exception failure = null)
        {
            recoverySource = failure == null && JtdxLogbookRecovery.CanCompare(session, history) ? history.Path : null;
            recover.Enabled = recoverySource != null && !String.IsNullOrEmpty(nativeLogbook) && currentLogbook != null;
            var matched = history == null ? null : session.FindIn(history);
            if (matched != null)
                check.Text = "Журнал · " + source + "   |   Найдено · " + matched.Count + " из " + session.Contacts.Count;
            else if (failure != null)
                check.Text = "Сверка недоступна. " + failure.Message;
            contacts.BeginUpdate();
            try
            {
                contacts.Items.Clear();
                foreach (SessionContact row in session.Contacts)
                {
                    var item = new ListViewItem(row.Call);
                    item.SubItems.Add(row.TimeUtc.HasValue ? row.TimeUtc.Value.ToLocalTime().ToString("HH:mm:ss") : "—");
                    item.SubItems.Add(row.ConfirmedUtc.HasValue ? "Подтверждён" : row.MissingUtc.HasValue ? "Без финала" : "В ожидании");
                    item.SubItems.Add(row.LoggedUtc.HasValue ? "Есть" : "—");
                    item.SubItems.Add(matched == null ? "—" : matched.Contains(row) ? "Найдена" : "Нет в снимке");
                    contacts.Items.Add(item);
                }
            }
            finally { contacts.EndUpdate(); }
        }

        internal void FillRecoveryDemo(LogbookHistory history, string source, IEnumerable<LogbookQso> native)
        {
            Fill(history, source);
            recoveryDemo = JtdxLogbookRecovery.FindMissing(session, history, native);
            recover.Enabled = recoveryDemo.Length != 0;
        }

        internal Form RecoveryDemoForm()
        { return new RecoveryPreviewDialog(recoveryDemo ?? new LogbookQso[0], demo: true); }

        void OpenRecovery()
        {
            if (recoveryDemo != null)
            {
                using (var demo = RecoveryDemoForm()) demo.ShowDialog(this);
                return;
            }
            try
            {
                CheckRecoverySource();
                RunRecovery("Сверяю записи JTDX и LogHX…",
                    () => JtdxLogbookRecovery.ReadMissing(session, nativeLogbook, recoverySource), PreviewRecovery);
            }
            catch (Exception error) { ShowRecoveryError(error); }
        }

        void CheckRecoverySource()
        {
            if (recoverySource == null || currentLogbook == null
                || !String.Equals(recoverySource, currentLogbook(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Основной журнал изменился. Откройте итоги сеанса заново.");
        }

        void PreviewRecovery(LogbookQso[] missing)
        {
            CheckRecoverySource();
            if (missing.Length == 0)
            {
                check.Text = "Записей JTDX для переноса в LogHX не найдено.";
                return;
            }
            using (var preview = new RecoveryPreviewDialog(missing))
            {
                if (preview.ShowDialog(this) != DialogResult.OK) return;
                var selected = new HashSet<string>(preview.Selected.Select(row => row.RawAdif), StringComparer.Ordinal);
                using (var save = new SaveFileDialog {
                    Title = "Сохранить ADIF для LogHX", Filter = "ADIF (*.adi)|*.adi",
                    AddExtension = true, DefaultExt = "adi", OverwritePrompt = false,
                    FileName = "AutoCQ-LogHX-" + session.StartedUtc.ToString("yyyyMMdd-HHmmss") + ".adi",
                })
                {
                    if (save.ShowDialog(this) != DialogResult.OK) return;
                    CheckRecoverySource();
                    string destination = save.FileName;
                    RunRecovery("Проверяю оба журнала перед сохранением…",
                        () => JtdxLogbookRecovery.ReadMissing(session, nativeLogbook, recoverySource),
                        current => SaveRecovery(destination, selected, current));
                }
            }
        }

        void SaveRecovery(string destination, HashSet<string> selected, LogbookQso[] current)
        {
            CheckRecoverySource();
            var records = current.Where(row => selected.Contains(row.RawAdif)).ToArray();
            if (records.Length == 0)
            {
                check.Text = "После сверки не осталось записей для экспорта.";
                return;
            }
            RunRecovery("Сохраняю файл ADIF…", () => JtdxLogbookRecovery.Save(destination, records), saved => {
                check.Text = "Записей в файле ADIF · " + saved + " из " + selected.Count;
                MessageBox.Show(this, "В файл ADIF сохранено " + saved + " из " + selected.Count + " записей.\r\n"
                    + "Импортируйте в LogHX файл " + Path.GetFileName(destination) + ".",
                    "Экспорт для LogHX", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        }

        void RunRecovery<T>(string progress, Func<T> work, Action<T> complete)
        {
            string previous = check.Text;
            check.Text = progress;
            recover.Enabled = false;
            var worker = new BackgroundWorker();
            worker.DoWork += delegate(object sender, DoWorkEventArgs e) { e.Result = work(); };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs e) {
                worker.Dispose();
                if (IsDisposed || Disposing) return;
                check.Text = previous;
                recover.Enabled = true;
                if (e.Error != null) ShowRecoveryError(e.Error);
                else
                {
                    try { complete((T)e.Result); }
                    catch (Exception error) { ShowRecoveryError(error); }
                }
            };
            worker.RunWorkerAsync();
        }

        void ShowRecoveryError(Exception error)
        { MessageBox.Show(this, error.Message, "Экспорт для LogHX", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    internal sealed class RecoveryPreviewDialog : OperatorDialog
    {
        readonly ListView records = new ListView { View = View.Details, FullRowSelect = true, GridLines = true, CheckBoxes = true };
        internal LogbookQso[] Selected
        { get { return records.CheckedItems.Cast<ListViewItem>().Select(row => (LogbookQso)row.Tag).ToArray(); } }

        internal RecoveryPreviewDialog(IEnumerable<LogbookQso> missing, bool demo = false)
            : base("Связи для импорта в LogHX", 625, 416)
        {
            LabelAt("Отметьте записи JTDX, которые хотите сохранить в файл ADIF.", 23, 58, 578, 25);
            LabelAt("Перед сохранением AutoCQ ещё раз сверит оба журнала.", 23, 87, 578, 26);
            records.Columns.Add("Позывной", 143);
            records.Columns.Add("Начало QSO, UTC", 211);
            records.Columns.Add("Диапазон", 108);
            records.Columns.Add("Режим", 88);
            foreach (LogbookQso row in missing)
            {
                var item = new ListViewItem(row.Call) { Checked = true, Tag = row };
                item.SubItems.Add(row.StartedUtc.Value.ToString("dd.MM.yyyy HH:mm:ss"));
                item.SubItems.Add(row.Band);
                item.SubItems.Add(row.Mode);
                records.Items.Add(item);
            }
            Place(records, 23, 119, 579, 235);
            var back = ButtonAt("Назад", 331, 365, 96, () => DialogResult = DialogResult.Cancel);
            CancelButton = back;
            var save = ButtonAt("Сохранить ADIF…", 438, 365, 164, () => DialogResult = DialogResult.OK, true);
            save.Enabled = !demo && records.Items.Count > 0;
            records.ItemChecked += delegate { save.Enabled = !demo && records.CheckedItems.Count > 0; };
        }
    }
}
