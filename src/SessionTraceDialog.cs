using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace JtdxAutoCq
{
    internal sealed class SessionTraceDialog : OperatorDialog
    {
        readonly Func<SessionTrace> snapshot;
        readonly Func<string, string> country;
        readonly OperatorProfile[] profiles;
        readonly Label details, count;
        readonly TextBox callsign = new TextBox { MaxLength = 32, CharacterCasing = CharacterCasing.Upper };
        readonly CheckBox decodes = new CheckBox { Text = "Показывать декоды" };
        readonly ListView events = new ListView {
            View = View.Details, FullRowSelect = true, GridLines = true, VirtualMode = true, ShowItemToolTips = true,
        };
        readonly ComboBox profile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly TextBox result = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        readonly Button refresh, open, save, replay;
        SessionTrace trace;
        TraceFrame[] visible = new TraceFrame[0];
        bool busy;

        internal SessionTraceDialog(Func<SessionTrace> snapshot, OperatorProfile[] profiles,
            Func<string, string> country, Action help) : base("Хронология сеанса", 1006, 682)
        {
            this.snapshot = snapshot;
            this.profiles = (profiles ?? new OperatorProfile[0]).Where(item => item != null && item.Options != null)
                .Select(item => new OperatorProfile { Name = item.Name, Options = item.Options.Copy() }).ToArray();
            this.country = country ?? (call => null);

            details = LabelAt("", 23, 57, 958, 28);
            refresh = ButtonAt("Текущий сеанс", 23, 90, 176, RefreshTrace);
            open = ButtonAt("Открыть XML…", 211, 90, 157, OpenTrace);
            save = ButtonAt("Сохранить XML…", 380, 90, 173, SaveTrace);
            LabelAt("Позывной", 23, 141, 83, 24);
            callsign.AccessibleName = "Точный позывной";
            Place(callsign, 110, 137, 172, 30);
            Place(decodes, 301, 136, 213, 30);
            count = LabelAt("", 743, 141, 238, 24);
            count.TextAlign = System.Drawing.ContentAlignment.TopRight;
            callsign.TextChanged += delegate { ApplyFilter(); };
            decodes.CheckedChanged += delegate { ApplyFilter(); };

            events.Columns.Add("Дата и время UTC", 174);
            events.Columns.Add("Событие", 163);
            events.Columns.Add("Текст", 597);
            events.RetrieveVirtualItem += delegate(object sender, RetrieveVirtualItemEventArgs e) {
                TraceFrame frame = e.ItemIndex >= 0 && e.ItemIndex < visible.Length ? visible[e.ItemIndex] : null;
                if (frame == null) { e.Item = new ListViewItem(); return; }
                string text = EventText(frame);
                e.Item = new ListViewItem(new[] {
                    frame.AtUtc.ToString("dd.MM HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    EventName(frame), text,
                }) { ToolTipText = text };
            };
            Place(events, 23, 174, 958, 309);

            LabelAt("Настройки для прогона", 23, 499, 181, 24);
            profile.AccessibleName = "Настройки для прогона";
            profile.Items.Add("Настройки из записи");
            foreach (OperatorProfile item in this.profiles) profile.Items.Add(item.Name ?? "");
            profile.SelectedIndex = 0;
            Place(profile, 211, 493, 326, 31);
            replay = ButtonAt("Прогнать запись", 549, 490, 180, ReplayTrace, primary: true);
            result.AccessibleName = "Результат прогона";
            Place(result, 23, 535, 958, 90);
            ButtonAt("Справка", 23, 636, 128, () => Guard(help));
            var close = ButtonAt("Закрыть", 845, 636, 136, Close);
            CancelButton = close;
            RefreshTrace();
        }

        internal void ShowReplayResult(string text)
        { result.Text = text; }

        void SetTrace(SessionTrace value)
        {
            trace = value;
            if (trace == null) details.Text = "Откройте XML или запустите сеанс, чтобы появилась запись.";
            else
            {
                details.Text = String.Join(" · ", new[] {
                    trace.Operation, trace.OwnCall, trace.FirstStatus.Mode, JtdxAdif.Band(trace.FirstStatus.Frequency),
                    trace.StartUtc.ToString("dd.MM.yyyy HH:mm:ss 'UTC'", CultureInfo.InvariantCulture),
                    trace.Shortened ? "Последний отрезок сеанса" : null,
                }.Where(text => !String.IsNullOrEmpty(text)));
            }
            result.Clear();
            if (trace != null && !trace.SupportsReplay)
                ShowReplayResult("Автономный прогон поддерживает CQ и обычный мониторинг. Хронология этого сеанса доступна для разбора.");
            else if (trace != null && trace.Shortened)
                ShowReplayResult("Начало сеанса вышло за пределы записи. Прогон начнётся с состояния JTDX в сохранённом отрезке.");
            ApplyFilter();
            UpdateButtons();
        }

        void UpdateButtons()
        {
            refresh.Enabled = !busy;
            open.Enabled = !busy;
            save.Enabled = !busy && trace != null;
            replay.Enabled = profile.Enabled = !busy && trace != null && trace.SupportsReplay;
        }

        void ApplyFilter()
        {
            string call = Callsign.Clean(callsign.Text);
            bool showDecodes = decodes.Checked;
            visible = trace == null ? new TraceFrame[0] : trace.Frames.Where(frame =>
                frame.Kind != TraceEvent.Timer && frame.Kind != TraceEvent.Status && frame.Kind != TraceEvent.Clear
                && (showDecodes || frame.Kind != TraceEvent.Decode)
                && (call.Length == 0 || MentionsCall(EventText(frame), call))).ToArray();
            events.VirtualListSize = visible.Length;
            events.Invalidate();
            count.Text = "Событий · " + visible.Length;
        }

        static bool MentionsCall(string text, string call)
        {
            int first = 0;
            while (first < text.Length)
            {
                int match = text.IndexOf(call, first, StringComparison.OrdinalIgnoreCase);
                if (match < 0) return false;
                int end = match + call.Length;
                if ((match == 0 || !CallCharacter(text[match - 1]))
                    && (end == text.Length || !CallCharacter(text[end]))) return true;
                first = match + 1;
            }
            return false;
        }

        static bool CallCharacter(char c)
        { return Char.IsLetterOrDigit(c) || c == '/'; }

        static string EventName(TraceFrame frame)
        {
            switch (frame.Kind)
            {
                case TraceEvent.Decode: return "Декод";
                case TraceEvent.Native:
                    string text = frame.Text ?? "";
                    return text.Contains("ransmitting ") && (text.Contains(" FT8:") || text.Contains(" FT4:"))
                        ? "Передача JTDX" : "Журнал JTDX";
                case TraceEvent.Command: return "Команда AutoCQ";
                case TraceEvent.Decision: return "Решение AutoCQ";
                case TraceEvent.Pause: return "Пауза";
                case TraceEvent.Resume: return "Продолжение";
                case TraceEvent.Stop: return "Остановка";
                default: return "Событие";
            }
        }

        static string EventText(TraceFrame frame)
        {
            if (frame.Kind == TraceEvent.Decode && frame.Decode != null) return frame.Decode.Message ?? "";
            if (!String.IsNullOrEmpty(frame.Text)) return frame.Text;
            switch (frame.Kind)
            {
                case TraceEvent.Pause: return "Запрошена пауза";
                case TraceEvent.Resume: return "Сеанс продолжен";
                default: return "";
            }
        }

        void RefreshTrace()
        { Guard(() => SetTrace(snapshot())); }

        void OpenTrace()
        {
            Guard(() => {
                using (var file = new OpenFileDialog {
                    Title = "Открыть запись сеанса", Filter = "Записи AutoCQ (*.xml)|*.xml", CheckFileExists = true,
                })
                {
                    if (file.ShowDialog(this) != DialogResult.OK) return;
                    string path = file.FileName;
                    Run("Читаю запись…", () => SessionTrace.Read(path), SetTrace);
                }
            });
        }

        void SaveTrace()
        {
            Guard(() => {
                if (trace == null) return;
                SessionTrace data = trace;
                using (var file = new SaveFileDialog {
                    Title = "Сохранить запись сеанса", Filter = "Записи AutoCQ (*.xml)|*.xml",
                    AddExtension = true, DefaultExt = "xml", FileName = "AutoCQ-" + data.StartUtc.ToString("yyyyMMdd-HHmmss") + ".xml",
                })
                {
                    if (file.ShowDialog(this) != DialogResult.OK) return;
                    string path = file.FileName;
                    Run("Сохраняю запись…", () => {
                        string temporary = Path.Combine(Path.GetDirectoryName(path), ".autocq-trace-" + Guid.NewGuid().ToString("N"));
                        try
                        {
                            data.Save(temporary);
                            if (File.Exists(path)) File.Replace(temporary, path, null);
                            else File.Move(temporary, path);
                        }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                        return Path.GetFileName(path);
                    }, name => ShowReplayResult("Запись сохранена в файле " + name + "."));
                }
            });
        }

        void ReplayTrace()
        {
            if (trace == null || busy) return;
            SessionTrace data = trace;
            int selected = profile.SelectedIndex;
            AutoCqOptions options = selected <= 0 ? data.Options.Copy() : profiles[selected - 1].Options.Copy();
            Run("Сравниваю команды…", () => data.Replay(options, country), ShowReplayResult);
        }

        void Run<T>(string progress, Func<T> execute, Action<T> complete)
        {
            if (busy) return;
            busy = true;
            UpdateButtons();
            ShowReplayResult(progress);
            var worker = new BackgroundWorker();
            worker.DoWork += delegate(object sender, DoWorkEventArgs e) { e.Result = execute(); };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs e) {
                worker.Dispose();
                if (IsDisposed || Disposing) return;
                busy = false;
                UpdateButtons();
                if (e.Error == null) Guard(() => complete((T)e.Result));
                else ShowReplayResult(e.Error.GetBaseException().Message);
            };
            worker.RunWorkerAsync();
        }

        void Guard(Action action)
        {
            try { if (action != null) action(); }
            catch (Exception error) { ShowReplayResult(error.GetBaseException().Message); }
        }
    }
}
