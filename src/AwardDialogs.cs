using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
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
        readonly CheckBox archive = new CheckBox { Text = "Показать завершённые", AutoSize = true };
        readonly CheckBox answerAll = new CheckBox { Text = "Отвечать всем входящим", AutoSize = true };
        readonly ListView list = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false, CheckBoxes = true, HideSelection = false };
        readonly Label details;
        readonly Button open, edit, start;
        readonly Dictionary<AwardProgram, AwardProgress> progress = new Dictionary<AwardProgram, AwardProgress>();
        bool refreshing;

        internal AwardCatalog Catalog { get { return catalog; } }
        internal bool StartRequested { get; private set; }

        internal AwardsDialog(AwardCatalog catalog, LogbookHistory history, string own, Func<string, string> dxcc, bool canStart)
            : base("Дипломы HAMLOG", 820, 526)
        {
            this.catalog = catalog.Copy(); this.history = history; this.own = own; this.dxcc = dxcc; this.canStart = canStart;
            LabelAt("Поиск по названию", 20, 61, 175, 24);
            Place(filter, 197, 57, 341, 27);
            Place(archive, 558, 59, 235, 26);
            Place(list, 20, 97, 780, 280);
            list.Columns.Add("Диплом", 412);
            list.Columns.Add("По журналу", 125);
            list.Columns.Add("Состояние", 213);
            details = LabelAt("", 23, 389, 773, 45);

            ButtonAt("Импорт…", 20, 437, 112, Import);
            ButtonAt("Добавить…", 141, 437, 127, () => Edit(null));
            edit = ButtonAt("Условия…", 277, 437, 125, () => Edit(Selected));
            open = ButtonAt("Открыть на HAMLOG", 413, 437, 205, ShowPage);
            answerAll.Checked = catalog.AnswerOtherCallers;
            Place(answerAll, 20, 485, 276, 25);
            ButtonAt("Сохранить", 476, 481, 133, () => Accept(false));
            start = ButtonAt("Начать поиск", 621, 481, 179, () => Accept(true), true);

            filter.TextChanged += delegate { RefreshList(); };
            archive.CheckedChanged += delegate { RefreshList(); };
            list.SelectedIndexChanged += delegate { UpdateDetails(); };
            list.ItemCheck += delegate(object sender, ItemCheckEventArgs e) {
                if (refreshing) return;
                var program = (AwardProgram)list.Items[e.Index].Tag;
                if (!CanSelectProgram(program)) e.NewValue = CheckState.Unchecked;
                program.Selected = e.NewValue == CheckState.Checked;
                UpdateDetails();
            };
            list.DoubleClick += delegate { if (Selected != null) Edit(Selected); };
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
        {
            return item.ClosedByIssuance || item.Goals.Count > 0 && Progress(item).CompletedGoals == item.Goals.Count;
        }

        bool CanSelectProgram(AwardProgram item)
        { return item.Goals.Count > 0 && !Done(item) && (!item.Until.HasValue || DateTime.UtcNow < item.Until.Value); }

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
                    if (!archive.Checked && Done(program)) continue;
                    if (program.Title.IndexOf(filter.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                    AwardProgress status = Progress(program);
                    var row = new ListViewItem(program.Title) { Tag = program };
                    row.SubItems.Add(status.GoalsCount == 0 || program.ClosedByIssuance ? "—"
                        : status.CompletedGoals + " из " + status.GoalsCount);
                    row.SubItems.Add(program.ClosedByIssuance ? "Есть выданный диплом"
                        : status.GoalsCount == 0 ? "Добавьте условия"
                        : Done(program) ? "Готово по журналу"
                        : program.Until.HasValue && DateTime.UtcNow >= program.Until.Value ? "Срок истёк"
                        : status.CompletedGoals > 0 ? "Часть целей выполнена" : "Открыты все цели");
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
            edit.Enabled = program != null;
            open.Enabled = program != null && !String.IsNullOrEmpty(program.Url);
            start.Enabled = canStart && catalog.Programs.Any(item => item.Selected && CanSelectProgram(item));
            if (program == null)
            {
                details.Text = catalog.Programs.Count == 0 ? "Импортируйте каталог из расширения HAMLOG или добавьте диплом вручную."
                    : "Отметьте дипломы с заданными условиями. AutoCQ проверит цели по основному журналу.";
                return;
            }
            if (program.Goals.Count == 0) { details.Text = "Откройте положение на HAMLOG и добавьте известные цели."; return; }
            AwardProgress value = Progress(program);
            string[] remains = program.Goals.Select((goal, index) => new { Goal = goal, Left = goal.Required - value.LoggedFor(index) })
                .Where(item => item.Left > 0).Take(4).Select(item => item.Goal.Value + (item.Goal.Band.Length == 0 ? "" : " · " + item.Goal.Band)
                    + (item.Goal.Mode.Length == 0 ? "" : " · " + item.Goal.Mode) + (item.Left == 1 ? "" : " × " + item.Left)).ToArray();
            details.Text = program.ClosedByIssuance ? "HAMLOG уже выдал диплом по этой программе. Для другой степени откройте «Условия»."
                : remains.Length == 0 ? "По основному журналу все цели выполнены. Зачёт проверяет HAMLOG."
                : "Осталось по журналу — " + String.Join(", ", remains)
                    + (value.GoalsCount - value.CompletedGoals > remains.Length ? " и ещё " + (value.GoalsCount - value.CompletedGoals - remains.Length) : "") + ".";
        }

        void Import()
        {
            using (var file = new OpenFileDialog { Title = "Открыть каталог дипломов", Filter = "Каталог AutoCQ (*.json)|*.json", CheckFileExists = true })
            {
                if (file.ShowDialog(this) != DialogResult.OK) return;
                try { catalog.Import(file.FileName, own); progress.Clear(); RefreshList(); }
                catch (Exception error) { MessageBox.Show(this, error.Message, "Каталог дипломов", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        void Edit(AwardProgram program)
        {
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
            try
            {
                program.Validate();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(program.Url) { UseShellExecute = true });
            }
            catch (Exception error) { MessageBox.Show(this, error.Message, "Страница диплома", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        void Accept(bool run)
        {
            catalog.AnswerOtherCallers = answerAll.Checked;
            foreach (AwardProgram program in catalog.Programs)
                if (program.Selected && !CanSelectProgram(program)) program.Selected = false;
            if (run && !catalog.Programs.Any(program => program.Selected)) return;
            StartRequested = run;
            DialogResult = DialogResult.OK;
        }

        internal void SelectDemoProgram(AwardProgram program)
        { RefreshList(catalog.Programs.FirstOrDefault(item => item.Id == program.Id)); }
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
                updated.Validate();
                Program = updated;
                DialogResult = DialogResult.OK;
            }
            catch (Exception error) { MessageBox.Show(this, error.Message, "Условия диплома", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }
}
