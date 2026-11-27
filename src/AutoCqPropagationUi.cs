using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Windows.Forms;

namespace JtdxAutoCq
{
    internal sealed class PropagationDialog : OperatorDialog
    {
        readonly ListView rows = new ListView { View = View.Details, FullRowSelect = true };
        readonly ComboBox band = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly Label status;
        readonly Button load;
        readonly Func<string, string> country;
        ReceptionReport[] data = new ReceptionReport[0];
        DateTime nextExpiry = DateTime.MaxValue;
        internal string Call { get; private set; }

        internal PropagationDialog(string call, Func<string, string> country, Action refresh, Action help)
            : base("Где меня слышат", 828, 442)
        {
            Call = call;
            this.country = country;
            LabelAt(call + " · отчёты PSK Reporter за последний час · FT8 и FT4", 20, 53, 786, 24);
            status = LabelAt("", 20, 82, 786, 23);
            status.AutoEllipsis = true;
            rows.Columns.Add("Принято (UTC)", 128);
            rows.Columns.Add("Приёмник", 104);
            rows.Columns.Add("Территория DXCC", 190);
            rows.Columns.Add("Квадрат", 78);
            rows.Columns.Add("Диапазон", 94);
            rows.Columns.Add("Режим", 74);
            rows.Columns.Add("SNR, дБ", 77);
            Place(rows, 20, 112, 786, 267);
            band.Items.Add("Все диапазоны");
            band.Items.AddRange(JtdxAdif.BandNames.Cast<object>().ToArray());
            band.SelectedIndex = 0;
            band.SelectedIndexChanged += delegate { FillRows(); };
            Place(band, 20, 398, 156, 29);
            ButtonAt("Справка", 186, 393, 109, help);
            load = ButtonAt("Загрузить", 658, 393, 148, refresh, true);
        }

        internal void Render(ReceptionReport[] reports, string message, bool canLoad)
        {
            if (!ReferenceEquals(data, reports) || DateTime.UtcNow >= nextExpiry)
            {
                data = reports;
                FillRows();
            }
            status.Text = message;
            load.Enabled = canLoad;
        }

        void FillRows()
        {
            string selected = band.SelectedItem as string;
            DateTime cutoff = DateTime.UtcNow.AddHours(-1);
            nextExpiry = DateTime.MaxValue;
            rows.BeginUpdate();
            try
            {
                rows.Items.Clear();
                foreach (ReceptionReport report in data.Where(row => row.HeardUtc > cutoff &&
                    (band.SelectedIndex < 1 || row.Band == selected)))
                {
                    var item = new ListViewItem(report.HeardUtc.ToString("HH:mm:ss"));
                    item.SubItems.Add(report.Receiver);
                    item.SubItems.Add(country(report.Receiver) ?? "");
                    item.SubItems.Add(report.Grid ?? "");
                    item.SubItems.Add(report.Band);
                    item.SubItems.Add(report.Mode);
                    item.SubItems.Add(report.Snr.HasValue ? report.Snr.Value.ToString("+0;-0;0") : "");
                    rows.Items.Add(item);
                    DateTime expires = report.HeardUtc.AddHours(1);
                    if (expires < nextExpiry) nextExpiry = expires;
                }
            }
            finally { rows.EndUpdate(); }
        }
    }

    public sealed partial class MainForm
    {
        static readonly ReceptionReport[] EmptyReceptionReports = new ReceptionReport[0];
        PropagationDialog propagationDialog;
        string propagationCall, propagationError;
        ReceptionReport[] propagationReports = EmptyReceptionReports;
        DateTime propagationNext, propagationLoaded;
        bool propagationLoading;

        void ShowPropagation()
        {
            if (propagationDialog != null && !propagationDialog.IsDisposed) { propagationDialog.Activate(); return; }
            if (demo) { propagationDialog = (PropagationDialog)PropagationDemoForm(); propagationDialog.Show(this); return; }
            string call = engine.Status == null ? null : Callsign.Parse(engine.Status.DeCall);
            if (call == null) throw new InvalidOperationException("Подключите JTDX с вашим позывным.");
            try { CountryTable(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            propagationDialog = new PropagationDialog(call, ReceptionCountry, () => LoadPropagation(call),
                () => Guard(() => OpenGuide("propagation", "где-меня-слышат")));
            UpdatePropagation(DateTime.UtcNow);
            propagationDialog.Show(this);
        }

        string ReceptionCountry(string call)
        {
            var match = dxcc == null ? null : dxcc.Find(call);
            return match == null ? "" : match.Name;
        }

        void LoadPropagation(string call)
        {
            DateTime now = DateTime.UtcNow;
            if (demo || propagationLoading || now < propagationNext) return;
            propagationLoading = true;
            propagationNext = now + PskReporter.Interval;
            if (propagationCall != call)
            {
                propagationCall = call;
                propagationLoaded = DateTime.MinValue;
                propagationReports = EmptyReceptionReports;
            }
            propagationError = null;
            UpdatePropagation(now);
            var worker = new BackgroundWorker();
            worker.DoWork += delegate(object sender, DoWorkEventArgs e) { e.Result = PskReporter.Fetch(call); };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs e) {
                worker.Dispose();
                if (IsDisposed || Disposing) return;
                propagationLoading = false;
                DateTime received = DateTime.UtcNow;
                if (e.Error == null)
                {
                    propagationReports = (ReceptionReport[])e.Result;
                    propagationLoaded = received;
                }
                else
                {
                    propagationError = "PSK Reporter недоступен (" + DescribeFeedFailure(e.Error) + ").";
                    var network = e.Error as WebException;
                    if (network != null && network.Response != null)
                        using (WebResponse response = network.Response)
                            propagationNext = received + RetryAfter(response.Headers["Retry-After"], received);
                }
                UpdatePropagation(received);
            };
            worker.RunWorkerAsync();
        }

        void UpdatePropagation(DateTime now)
        {
            if (propagationDialog == null || propagationDialog.IsDisposed || demo) return;
            bool sameCall = propagationDialog.Call == propagationCall;
            bool ready = !propagationLoading && now >= propagationNext;
            string status = "Нажмите «Загрузить», чтобы запросить отчёты.";
            if (propagationLoading) status = "Загружаю отчёты PSK Reporter…";
            else if (sameCall && propagationError != null) status = propagationError;
            else if (sameCall && propagationLoaded != DateTime.MinValue)
            {
                int total = propagationReports.Count(row => row.HeardUtc >= now.AddHours(-1));
                status = (total == 0 ? "За последний час нет отчётов FT8 или FT4." : "Получено отчётов за последний час — " + total + ".")
                    + "  Обновлено " + propagationLoaded.ToLocalTime().ToString("HH:mm:ss") + ".";
            }
            if (!ready && !propagationLoading) status += "  Новый запрос в " + propagationNext.ToLocalTime().ToString("HH:mm:ss") + ".";
            propagationDialog.Render(sameCall ? propagationReports : EmptyReceptionReports, status, ready);
        }

        Form PropagationDemoForm()
        {
            CountryTable();
            DateTime now = DateTime.UtcNow;
            var form = new PropagationDialog("K1ABC", ReceptionCountry, () => { }, () => { });
            form.Render(new[] {
                new ReceptionReport { Receiver = "JA1AAA", Band = "20m", Mode = "FT8", Grid = "PM95", HeardUtc = now.AddMinutes(-2), Snr = -8 },
                new ReceptionReport { Receiver = "ZL1AAA", Band = "20m", Mode = "FT8", Grid = "RF73", HeardUtc = now.AddMinutes(-3), Snr = -16 },
                new ReceptionReport { Receiver = "PY2AAA", Band = "15m", Mode = "FT4", Grid = "GG66", HeardUtc = now.AddMinutes(-4), Snr = -9 },
                new ReceptionReport { Receiver = "DL1AAA", Band = "40m", Mode = "FT8", Grid = "JO62", HeardUtc = now.AddMinutes(-6), Snr = 4 },
            }, "Демонстрационные отчёты приёма", false);
            return form;
        }
    }
}
