using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace JtdxAutoCq
{
    public sealed class UpdateDialog : Form
    {
        readonly UpdateClient client;
        readonly AppPreferences preferences;
        readonly AppPreferencesStore store;
        readonly string stateDirectory,jtdxPath;
        readonly bool demo;
        readonly Action installing;
        readonly Label notice=new Label(),installed=new Label();
        readonly Button check=new Button(),download=new Button(),install=new Button();
        readonly TextBox notes=new TextBox();
        readonly ProgressBar progress=new ProgressBar();
        readonly CheckBox automatic=new CheckBox();
        AvailableUpdate release;
        PreparedUpdate prepared;
        public UpdateDialog(UpdateClient client,AppPreferences preferences,AppPreferencesStore store,string stateDirectory,string jtdxPath,bool demo,Action installing,AvailableUpdate cached=null)
        {
            this.client=client;this.preferences=preferences;this.store=store;this.stateDirectory=stateDirectory;this.jtdxPath=jtdxPath;this.demo=demo;this.installing=installing;
            Text="Обновления AutoCQ";Font=new Font("Segoe UI",10F);AutoScaleMode=AutoScaleMode.Dpi;ClientSize=new Size(690,540);
            FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterParent;BackColor=Color.FromArgb(238,243,248);
            var title=new Label{Text="Обновления AutoCQ",Font=new Font("Segoe UI Semibold",18F)};title.SetBounds(22,17,640,38);Controls.Add(title);
            installed.Text="Установлена версия "+client.CurrentVersion.ToString(3);installed.SetBounds(24,68,630,26);Controls.Add(installed);
            notice.Text="Нажмите «Проверить обновления».";notice.SetBounds(24,101,636,52);Controls.Add(notice);
            check.Text="Проверить обновления";check.SetBounds(24,161,206,36);Controls.Add(check);check.Click+=delegate{CheckNow();};
            download.Text="Скачать";download.SetBounds(245,161,169,36);download.Enabled=false;Controls.Add(download);download.Click+=delegate{DownloadNow();};
            install.Text="Обновить и перезапустить";install.SetBounds(429,161,237,36);install.Enabled=false;install.BackColor=Color.FromArgb(0,100,91);install.ForeColor=Color.White;install.FlatStyle=FlatStyle.Flat;Controls.Add(install);install.Click+=delegate{InstallNow();};
            install.EnabledChanged+=delegate{install.BackColor=install.Enabled?Color.FromArgb(0,100,91):Color.FromArgb(226,235,235);};
            install.BackColor=Color.FromArgb(226,235,235);
            progress.SetBounds(24,209,642,10);Controls.Add(progress);
            notes.Multiline=true;notes.ReadOnly=true;notes.ScrollBars=ScrollBars.Vertical;notes.BackColor=Color.White;notes.SetBounds(24,233,642,190);Controls.Add(notes);
            automatic.Text="Проверять обновления при запуске и раз в сутки";automatic.Checked=preferences.AutomaticUpdates;automatic.SetBounds(24,435,642,26);Controls.Add(automatic);
            automatic.CheckedChanged+=delegate{preferences.AutomaticUpdates=automatic.Checked;TrySave();};
            var safety=new Label{Text="Скачивать можно во время работы. Перед установкой завершите связь\r\nи закройте JTDX. Настройки и очередь сохранятся; JTDX запускайте через LogHX."};safety.SetBounds(24,472,642,46);Controls.Add(safety);
            if(cached!=null)ShowRelease(cached);
        }
        void TrySave(){try{store.Save(preferences);}catch(IOException){notice.Text="Не удалось сохранить настройку проверки обновлений.";}catch(UnauthorizedAccessException){notice.Text="Не удалось сохранить настройку проверки обновлений.";}}
        void Run(Func<object> action,Action<object> complete,bool reportProgress)
        {
            check.Enabled=download.Enabled=install.Enabled=false;progress.Style=ProgressBarStyle.Marquee;
            var worker=new BackgroundWorker{WorkerReportsProgress=reportProgress};
            worker.DoWork+=delegate(object sender,DoWorkEventArgs e){e.Result=action();};
            worker.RunWorkerCompleted+=delegate(object sender,RunWorkerCompletedEventArgs e){
                if(IsDisposed){worker.Dispose();return;}progress.Style=ProgressBarStyle.Blocks;progress.Value=0;check.Enabled=true;
                if(e.Error!=null){notice.Text=e.Error is WebException?"Не удалось связаться с GitHub. Проверьте интернет и повторите проверку.":e.Error.Message;download.Enabled=release!=null && release.Version>client.CurrentVersion;}
                else complete(e.Result);worker.Dispose();
            };worker.RunWorkerAsync();
        }
        void CheckNow()
        {
            notice.Text="Проверяю последний выпуск на GitHub…";
            Run(()=>client.Check(),value=>{preferences.LastUpdateCheckUtc=DateTime.UtcNow;TrySave();ShowRelease((AvailableUpdate)value);},false);
        }
        internal void ShowRelease(AvailableUpdate value)
        {
            release=value;prepared=null;notes.Text=ReadableNotes(value.Notes);
            bool newer=value.Version>client.CurrentVersion;
            notice.Text=newer?"Доступна версия "+value.Version.ToString(3)+". Скачайте её, затем нажмите «Обновить и перезапустить».":"У вас установлена последняя версия AutoCQ.";
            download.Enabled=newer;install.Enabled=false;
        }
        internal static string ReadableNotes(string markdown)
        {
            string text=(markdown??"").Replace("\r\n","\n");
            int english=text.IndexOf("\n## English",StringComparison.OrdinalIgnoreCase);
            if(english>=0)text=text.Substring(0,english);
            text=Regex.Replace(text,@"(?m)^#{1,6}\s*","");
            text=Regex.Replace(text,@"\[([^\]]+)\]\([^)]+\)","$1");
            return text.Replace("`","").Replace("**","").Trim().Replace("\n",Environment.NewLine);
        }
        void DownloadNow()
        {
            notice.Text="Скачиваю обновление и проверяю контрольную сумму…";
            Run(()=>client.Download(release,stateDirectory),value=>{prepared=(PreparedUpdate)value;notice.Text="Готово к установке: версия "+prepared.Version+". Контрольная сумма проверена. Закройте JTDX и нажмите «Обновить и перезапустить».";download.Enabled=true;install.Enabled=true;},false);
        }
        void InstallNow()
        {
            try
            {
                if(prepared==null)return;
                using(Process helper=UpdateClient.LaunchInstaller(prepared,Application.ExecutablePath,jtdxPath,demo)){}
                DialogResult=DialogResult.OK;Close();installing();
            }
            catch(Exception e){notice.Text=e is Win32Exception?"Разрешение Windows отменено. Обновление не установлено; можно повторить.":e.Message;}
        }
    }
}
