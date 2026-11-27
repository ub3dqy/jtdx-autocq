using System;
using System.Linq;
using System.Windows.Forms;

namespace JtdxAutoCq
{
    internal sealed class DecodeDiagnosticsDialog : OperatorDialog
    {
        readonly Func<DecodeSample[]> samples;
        readonly ComboBox mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly Label current, saved;
        readonly ListView list = new ListView { View = View.Details, FullRowSelect = true };
        DecodeSample[] reference = new DecodeSample[0];
        string lastDisplay;

        internal DecodeDiagnosticsDialog(Func<DecodeSample[]> samples, Action help)
            : base("Декодирование JTDX", 845, 491)
        {
            this.samples = samples;
            LabelAt("Последние 5 минут. Сравните два отрезка приёма после изменения настроек JTDX.", 20, 53, 807, 25);
            current = LabelAt("", 20, 84, 805, 27);
            saved = LabelAt("Зафиксируйте первый замер перед изменением настроек.", 20, 113, 805, 27);
            list.Columns.Add("Время (UTC)", 118);
            list.Columns.Add("Диапазон", 109);
            list.Columns.Add("Режим", 92);
            list.Columns.Add("Декодирование, мс", 182);
            list.Columns.Add("От границы TX, мс", 178);
            list.Columns.Add("Потери", 115);
            Place(list, 20, 148, 804, 276);
            mode.Items.AddRange(new object[] { "FT8", "FT4" });
            mode.SelectedIndex = 0;
            mode.SelectedIndexChanged += delegate { RefreshSnapshot(); };
            Place(mode, 20, 442, 107, 29);
            ButtonAt("Справка", 140, 438, 111, help);
            ButtonAt("Зафиксировать замер", 594, 438, 230, () => {
                reference = samples().Select(item => item.Copy()).ToArray();
                RefreshSnapshot();
            }, true);
            RefreshSnapshot();
        }

        internal void RefreshSnapshot()
        {
            string selected = (string)mode.SelectedItem;
            DecodeSample[] recent = samples().Where(item => item.Mode == selected).ToArray();
            current.Text = Summary("Сейчас", recent);
            if (reference.Length > 0) saved.Text = Summary("Первый замер", reference.Where(item => item.Mode == selected).ToArray());
            string signature = selected + String.Join("|", recent.Select(row => row.AtUtc.Ticks + "/" + row.FromBoundary));
            if (signature == lastDisplay) return;
            lastDisplay = signature;
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (DecodeSample item in recent.Reverse().Take(200))
                {
                    var row = new ListViewItem(item.AtUtc.ToString("HH:mm:ss.fff"));
                    row.SubItems.Add(item.Band ?? "");
                    row.SubItems.Add(item.Mode);
                    row.SubItems.Add(item.Duration.HasValue ? item.Duration.Value.ToString("0") : "");
                    row.SubItems.Add(item.FromBoundary.HasValue ? item.FromBoundary.Value.ToString("+0;-0;0") : "");
                    row.SubItems.Add(item.LostBlocks > 0 ? item.LostBlocks.ToString() : "");
                    list.Items.Add(row);
                }
            }
            finally { list.EndUpdate(); }
        }

        static string Summary(string prefix, DecodeSample[] rows)
        {
            if (rows.Length == 0) return prefix + " · ожидаю данные JTDX";
            int measured = rows.Count(item => item.FromBoundary.HasValue);
            var durations = rows.Where(item => item.Duration.HasValue).Select(item => item.Duration.Value).ToArray();
            return prefix + " · среднее время " + (durations.Length == 0 ? "—" : durations.Average().ToString("0") + " мс")
                + " · поздних " + rows.Count(item => item.FromBoundary > 0) + " из " + measured
                + " · потеряно блоков " + rows.Sum(item => (long)item.LostBlocks);
        }
    }

    public sealed partial class MainForm
    {
        readonly DecodeDiagnostics decodeDiagnostics = new DecodeDiagnostics();
        DecodeDiagnosticsDialog decodeDiagnosticsDialog;
        DateTime nextDecodeDisplay;

        void ShowDecodeDiagnostics()
        {
            if (decodeDiagnosticsDialog != null && !decodeDiagnosticsDialog.IsDisposed) { decodeDiagnosticsDialog.Activate(); return; }
            decodeDiagnosticsDialog = demo ? (DecodeDiagnosticsDialog)DecodeDiagnosticsDemoForm()
                : new DecodeDiagnosticsDialog(() => decodeDiagnostics.Samples(DateTime.UtcNow),
                    () => Guard(() => OpenGuide("decode-diagnostics", "декодирование-jtdx")));
            decodeDiagnosticsDialog.Show(this);
        }

        void UpdateDecodeDiagnostics(DateTime now)
        {
            if (decodeDiagnosticsDialog == null || decodeDiagnosticsDialog.IsDisposed || now < nextDecodeDisplay) return;
            nextDecodeDisplay = now.AddSeconds(1);
            decodeDiagnosticsDialog.RefreshSnapshot();
        }

        Form DecodeDiagnosticsDemoForm()
        {
            DateTime now = DateTime.UtcNow;
            return new DecodeDiagnosticsDialog(() => new[] {
                new DecodeSample { AtUtc = now.AddSeconds(-66), Band = "20m", Mode = "FT8", Duration = 685, FromBoundary = -203 },
                new DecodeSample { AtUtc = now.AddSeconds(-43), Band = "20m", Mode = "FT8", LostBlocks = 4 },
                new DecodeSample { AtUtc = now.AddSeconds(-36), Band = "20m", Mode = "FT8", Duration = 804, FromBoundary = 143 },
                new DecodeSample { AtUtc = now.AddSeconds(-6), Band = "20m", Mode = "FT8", Duration = 702, FromBoundary = -171 },
            }, () => { });
        }
    }
}
