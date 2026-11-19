using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Windows.Forms;

namespace JtdxAutoCq
{
    public sealed class BugReportDialog : Form
    {
        readonly TextBox subject = new TextBox(), description = new TextBox();
        readonly Label status = new Label(), window = new Label();
        readonly Button preview = new Button(), submit = new Button(), copy = new Button();
        readonly bool demo;
        BugReportData data;
        string number;
        bool sending;

        internal BugReportDialog(BugReportCapture capture) : this(capture.Data, false)
        {
            Shown += delegate {
                Run(capture.Read, report => {
                    data = (BugReportData)report;
                    status.Text = data.Limited
                        ? "Снимок готов. Часть записей недоступна или не поместилась."
                        : "Снимок готов. Его можно посмотреть перед отправкой.";
                    UpdateActions();
                }, error => { status.Text = "Не удалось собрать снимок диагностики."; });
            };
        }

        BugReportDialog(BugReportData frame, bool demo)
        {
            this.demo = demo;
            Text = "Сообщить об ошибке — " + AppBrand.DisplayName;
            BrandUi.ApplyIcon(this);
            Font = new Font("Segoe UI", 10F);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(238, 243, 248);
            ClientSize = new Size(680, 493);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            AddLabel("Сообщить об ошибке", 22, 14, 636, 42, new Font("Segoe UI Semibold", 18F));
            window.SetBounds(24, 61, 632, 24);
            DateTimeOffset until = DateTimeOffset.Parse(frame.ToUtc, CultureInfo.InvariantCulture).ToLocalTime();
            window.Text = until.AddMinutes(-20).ToString("HH:mm:ss") + " – " + until.ToString("HH:mm:ss")
                + " · 20 минут перед нажатием";
            Controls.Add(window);

            AddLabel("Короткий заголовок", 24, 100, 628, 23);
            subject.SetBounds(24, 126, 632, 28);
            subject.MaxLength = 100;
            Controls.Add(subject);
            AddLabel("Что произошло и как должно было сработать?", 24, 170, 628, 25);
            description.Multiline = true;
            description.ScrollBars = ScrollBars.Vertical;
            description.AcceptsReturn = true;
            description.MaxLength = 2000;
            description.SetBounds(24, 199, 632, 96);
            Controls.Add(description);

            var privacy = AddLabel("Диагностика отправляется в закрытый кабинет ub3dqy.com. В публичный черновик GitHub попадут ваш заголовок, описание, версия AutoCQ и номер отчёта.",
                24, 309, 632, 50, new Font("Segoe UI", 9F));
            privacy.ForeColor = Color.FromArgb(79, 96, 112);
            status.SetBounds(24, 364, 632, 48);
            status.Text = "Собираю снимок диагностики…";
            Controls.Add(status);

            preview.Text = "Посмотреть диагностику";
            preview.SetBounds(24, 433, 194, 36);
            preview.Click += delegate { Preview(); };
            Controls.Add(preview);
            copy.Text = "Скопировать текст";
            copy.SetBounds(226, 433, 168, 36);
            copy.Visible = false;
            copy.Click += delegate { CopyDraft(); };
            Controls.Add(copy);
            submit.Text = "Отправить и открыть GitHub";
            submit.SetBounds(405, 433, 251, 36);
            submit.FlatStyle = FlatStyle.Flat;
            submit.ForeColor = Color.White;
            submit.BackColor = Color.FromArgb(0, 100, 91);
            submit.Click += delegate { SendOrOpen(); };
            Controls.Add(submit);

            subject.TextChanged += delegate { UpdateActions(); };
            description.TextChanged += delegate { UpdateActions(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (sending && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
            };
            UpdateActions();
        }

        Label AddLabel(string value, int x, int y, int width, int height, Font font = null)
        {
            var label = new Label { Text = value, Font = font ?? Font };
            label.SetBounds(x, y, width, height);
            Controls.Add(label);
            return label;
        }

        void UpdateActions()
        {
            preview.Enabled = data != null;
            submit.Enabled = data != null && !sending && !String.IsNullOrWhiteSpace(subject.Text)
                && !String.IsNullOrWhiteSpace(description.Text);
            subject.Enabled = description.Enabled = !sending;
        }

        void Preview()
        {
            if (data == null) return;
            using (var dialog = new Form {
                Text = "Диагностика для ub3dqy.com", StartPosition = FormStartPosition.CenterParent,
                Size = new Size(800, 520), Font = Font, MinimizeBox = false
            })
            {
                BrandUi.ApplyIcon(dialog);
                var tabs = new TabControl { Dock = DockStyle.Fill };
                var details = new List<string> {
                    "AutoCQ " + data.AutoCqVersion, "JTDX " + data.JtdxVersion,
                    "UTC " + data.FromUtc + " – " + data.ToUtc
                };
                details.AddRange(data.State.Select(pair => pair.Key + " = " + pair.Value));
                AddPreview(tabs, "Состояние станции", String.Join(Environment.NewLine, details));
                AddPreview(tabs, "События AutoCQ", data.AutoCqLog);
                AddPreview(tabs, "События JTDX", data.JtdxLog);
                dialog.Controls.Add(tabs);
                dialog.ShowDialog(this);
            }
        }

        static void AddPreview(TabControl tabs, string label, string text)
        {
            var tab = new TabPage(label);
            tab.Controls.Add(new TextBox {
                Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, WordWrap = false,
                ScrollBars = ScrollBars.Both,
                Text = text.Length == 0 ? "За этот отрезок записей нет." : text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
            });
            tabs.TabPages.Add(tab);
        }

        void SendOrOpen()
        {
            if (demo)
            {
                status.Text = "Демонстрационный отчёт. Можно просмотреть учебные данные.";
                return;
            }
            if (number != null) { OpenDraft(); return; }
            if (data == null || sending) return;
            sending = true;
            UpdateActions();
            status.Text = "Отправляю диагностику на ub3dqy.com…";
            Run(() => BugReportClient.Send(data, BugReportClient.Endpoint), receipt => {
                sending = false;
                number = (string)receipt;
                copy.Visible = true;
                submit.Text = "Открыть черновик GitHub";
                UpdateActions();
                OpenDraft();
            }, SendFailed);
        }

        void SendFailed(Exception error)
        {
            sending = false;
            UpdateActions();
            var network = error as WebException;
            var response = network == null ? null : network.Response as HttpWebResponse;
            if (response != null)
            {
                using (response)
                    if ((int)response.StatusCode == 429)
                    {
                        status.Text = "Сайт временно ограничил отправку. Повторите позже из этого окна.";
                        return;
                    }
            }
            status.Text = "Не удалось отправить диагностику. Проверьте связь и повторите из этого окна.";
        }

        string DraftBody()
        {
            return BugReportClient.IssueBody(description.Text, data.AutoCqVersion, number);
        }

        void OpenDraft()
        {
            status.Text = number + Environment.NewLine + "Проверьте черновик и опубликуйте его на GitHub.";
            try
            {
                string url = BugReportClient.DraftUrl(subject.Text.Trim(), DraftBody());
                if (url == null)
                {
                    Clipboard.SetText(DraftBody());
                    url = BugReportClient.IssuePage + "?title=" + Uri.EscapeDataString(subject.Text.Trim());
                    status.Text = number + Environment.NewLine + "Описание скопировано. Вставьте его в черновик GitHub (Ctrl+V).";
                }
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Win32Exception)
            {
                status.Text = number + Environment.NewLine + "GitHub не открылся. Можно повторить или скопировать текст.";
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                status.Text = number + Environment.NewLine + "Буфер обмена занят. Попробуйте ещё раз.";
            }
        }

        void CopyDraft()
        {
            try
            {
                Clipboard.SetText(DraftBody());
                status.Text = number + Environment.NewLine + "Описание и номер скопированы.";
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                status.Text = number + Environment.NewLine + "Буфер обмена занят. Попробуйте ещё раз.";
            }
        }

        void Run(Func<object> work, Action<object> success, Action<Exception> fail)
        {
            var worker = new BackgroundWorker();
            worker.DoWork += delegate(object sender, DoWorkEventArgs e) { e.Result = work(); };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs e) {
                worker.Dispose();
                if (IsDisposed) return;
                if (e.Error == null) success(e.Result);
                else fail(e.Error);
            };
            worker.RunWorkerAsync();
        }

        public static BugReportDialog CreateDemo(string version)
        {
            var sample = new BugReportData {
                AutoCqVersion = version, JtdxVersion = "2.2.160-32A-rc10",
                FromUtc = "2026-01-15T12:20:00.0000000+00:00", ToUtc = "2026-01-15T12:40:00.0000000+00:00",
                AutoCqLog = "2026-01-15T12:38:00.000Z Мониторинг включён.",
                JtdxLog = "2026-01-15T12:39:45.000Z -10 0.1 1350 ~ CQ W2ABC FN31",
                State = new Dictionary<string, string> { { "mode", "FT8" }, { "monitoring", "True" } },
            };
            var dialog = new BugReportDialog(sample, true);
            dialog.data = sample;
            dialog.subject.Text = "Мониторинг пропустил одну станцию";
            dialog.description.Text = "После завершения связи следующая станция из очереди пропустилась.\r\nОжидал, что AutoCQ ответит ей в ближайшем периоде.";
            dialog.status.Text = "Снимок готов. Его можно посмотреть перед отправкой.";
            dialog.UpdateActions();
            return dialog;
        }
    }
}
