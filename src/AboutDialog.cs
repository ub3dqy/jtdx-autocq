using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace JtdxAutoCq
{
    static class BrandUi
    {
        public static void ApplyIcon(Form form)
        {
            using (Stream stream = typeof(AppBrand).Assembly.GetManifestResourceStream("AutoCQ.ico"))
            {
                if (stream == null) return;
                using (var original = new Icon(stream))
                {
                    var icon = (Icon)original.Clone();
                    form.Icon = icon;
                    form.Disposed += delegate { icon.Dispose(); };
                }
            }
        }

        public static LinkLabel Link(string text, string address)
        {
            var link = new LinkLabel { Text = text, LinkColor = Color.FromArgb(0, 100, 91),
                ActiveLinkColor = Color.FromArgb(0, 130, 112), VisitedLinkColor = Color.FromArgb(0, 100, 91),
                LinkBehavior = LinkBehavior.HoverUnderline, AutoSize = false };
            link.LinkClicked += delegate
            {
                try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); }
                catch (Exception error)
                {
                    if (!(error is System.ComponentModel.Win32Exception) && !(error is InvalidOperationException)) throw;
                    MessageBox.Show(link.FindForm(), "Не удалось открыть ссылку. Адрес: " + address, AppBrand.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };
            return link;
        }
    }

    public sealed class AboutDialog : Form
    {
        public AboutDialog(string version)
        {
            Text = "О программе — " + AppBrand.DisplayName;
            Font = new Font("Segoe UI", 10F); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(600, 430); FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(238, 243, 248); ForeColor = Color.FromArgb(21, 38, 58);
            BrandUi.ApplyIcon(this);
            var header = new Panel { BackColor = Color.FromArgb(17, 31, 53) };
            header.SetBounds(0, 0, 600, 120); Controls.Add(header);
            var logo = new PictureBox { Image = Icon.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom };
            logo.SetBounds(24, 28, 60, 60); header.Controls.Add(logo);
            Disposed += delegate { logo.Image.Dispose(); };
            var name = new Label { Text = AppBrand.Name, Font = new Font("Segoe UI Semibold", 27F), ForeColor = Color.White };
            name.SetBounds(103, 17, 465, 49); header.Controls.Add(name);
            var author = new Label { Text = "Разработано " + AppBrand.Author, Font = new Font("Segoe UI Semibold", 13F), ForeColor = Color.FromArgb(112, 239, 212) };
            author.SetBounds(106, 72, 455, 30); header.Controls.Add(author);
            var installed = new Label { Text = "Версия " + version, Font = new Font("Segoe UI Semibold", 12F) };
            installed.SetBounds(26, 142, 546, 28); Controls.Add(installed);
            var description = new Label { Text = "AutoCQ помогает JTDX продолжать работу после каждой связи.\r\nFT8 и FT4 · таймер · очередь вызовов · мониторинг целей." };
            description.SetBounds(26, 185, 548, 54); Controls.Add(description);
            var credit = new Label { Text = "Автор и разработчик: " + AppBrand.Author };
            credit.SetBounds(26, 250, 548, 26); Controls.Add(credit);
            var website = BrandUi.Link(AppBrand.WebsiteLabel, AppBrand.Website);
            website.Font = new Font("Segoe UI Semibold", 13F); website.SetBounds(26, 286, 548, 31); Controls.Add(website);
            var article = BrandUi.Link("О модуле и установке", AppBrand.ProjectPage);
            article.SetBounds(26, 332, 230, 26); Controls.Add(article);
            var source = BrandUi.Link("Исходники и выпуски на GitHub", UpdateClient.RepositoryUrl);
            source.SetBounds(280, 332, 294, 26); Controls.Add(source);
            var copyright = new Label { Text = AppBrand.Copyright, ForeColor = Color.FromArgb(79, 96, 112), Font = new Font("Segoe UI", 9F) };
            copyright.SetBounds(26, 386, 362, 25); Controls.Add(copyright);
            var close = new Button { Text = "Закрыть", DialogResult = DialogResult.OK, BackColor = Color.FromArgb(0, 100, 91), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            close.SetBounds(428, 379, 146, 34); Controls.Add(close); AcceptButton = CancelButton = close;
        }
    }
}
