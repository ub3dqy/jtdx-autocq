using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Globalization;
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
        readonly DownloadProgressBar progress=new DownloadProgressBar();
        readonly Label transferDetails=new Label();
        readonly System.Windows.Forms.Timer progressTimer=new System.Windows.Forms.Timer();
        readonly Stopwatch downloadClock=new Stopwatch();
        UpdateDownloadProgress lastDownloadProgress;
        TimeSpan lastProgressAt;
        readonly CheckBox automatic=new CheckBox();
        AvailableUpdate release;
        PreparedUpdate prepared;
        public UpdateDialog(UpdateClient client,AppPreferences preferences,AppPreferencesStore store,string stateDirectory,string jtdxPath,bool demo,Action installing,AvailableUpdate cached=null)
        {
            this.client=client;this.preferences=preferences;this.store=store;this.stateDirectory=stateDirectory;this.jtdxPath=jtdxPath;this.demo=demo;this.installing=installing;
            Text="Обновления — "+AppBrand.DisplayName;Font=new Font("Segoe UI",10F);AutoScaleMode=AutoScaleMode.Dpi;ClientSize=new Size(690,600);
            BrandUi.ApplyIcon(this);
            FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterParent;BackColor=Color.FromArgb(238,243,248);
            var title=new Label{Text="Обновления "+AppBrand.DisplayName,Font=new Font("Segoe UI Semibold",18F)};title.SetBounds(22,17,640,38);Controls.Add(title);
            installed.Text="Установлена версия "+client.CurrentVersion.ToString(3);installed.SetBounds(24,68,630,26);Controls.Add(installed);
            notice.Text="Нажмите «Проверить обновления».";notice.SetBounds(24,101,636,52);Controls.Add(notice);
            check.Text="Проверить обновления";check.SetBounds(24,161,206,36);Controls.Add(check);check.Click+=delegate{CheckNow();};
            download.Text="Скачать";download.SetBounds(245,161,169,36);download.Enabled=false;Controls.Add(download);download.Click+=delegate{DownloadNow();};
            install.Text="Обновить и перезапустить";install.SetBounds(429,161,237,36);install.Enabled=false;install.BackColor=Color.FromArgb(0,100,91);install.ForeColor=Color.White;install.FlatStyle=FlatStyle.Flat;Controls.Add(install);install.Click+=delegate{InstallNow();};
            install.EnabledChanged+=delegate{install.BackColor=install.Enabled?Color.FromArgb(0,100,91):Color.FromArgb(226,235,235);};
            install.BackColor=Color.FromArgb(226,235,235);
            progress.SetBounds(24,209,642,18);Controls.Add(progress);
            transferDetails.Text="Во время скачивания здесь появятся объём, скорость и оставшееся время.";
            transferDetails.SetBounds(24,238,642,43);transferDetails.Font=new Font("Segoe UI",9F);transferDetails.ForeColor=Color.FromArgb(79,96,112);Controls.Add(transferDetails);
            notes.Multiline=true;notes.ReadOnly=true;notes.ScrollBars=ScrollBars.Vertical;notes.BackColor=Color.White;notes.SetBounds(24,292,642,190);Controls.Add(notes);
            automatic.Text="Проверять обновления при запуске и раз в сутки";automatic.Checked=preferences.AutomaticUpdates;automatic.SetBounds(24,495,642,26);Controls.Add(automatic);
            automatic.CheckedChanged+=delegate{preferences.AutomaticUpdates=automatic.Checked;TrySave();};
            var safety=new Label{Text="Скачивать можно во время работы. Перед установкой завершите связь\r\nи закройте JTDX. Настройки и очередь сохранятся; JTDX запускайте через LogHX."};safety.SetBounds(24,534,642,46);Controls.Add(safety);
            progressTimer.Interval=1000;progressTimer.Tick+=delegate{RefreshDownloadProgress();};
            Disposed+=delegate{progressTimer.Dispose();};
            if(cached!=null)ShowRelease(cached);
        }
        void TrySave(){try{store.Save(preferences);}catch(IOException){notice.Text="Не удалось сохранить настройку проверки обновлений.";}catch(UnauthorizedAccessException){notice.Text="Не удалось сохранить настройку проверки обновлений.";}}
        void Run(Func<Action<UpdateDownloadProgress>,object> action,Action<object> complete,bool reportProgress)
        {
            check.Enabled=download.Enabled=install.Enabled=false;progress.Value=0;
            progress.Style=ProgressBarStyle.Continuous;
            transferDetails.Text="";lastDownloadProgress=null;
            if(reportProgress){downloadClock.Restart();progressTimer.Start();}
            var worker=new BackgroundWorker{WorkerReportsProgress=reportProgress};
            worker.DoWork+=delegate(object sender,DoWorkEventArgs e){e.Result=action(value=>worker.ReportProgress(value.Percent??0,value));};
            worker.ProgressChanged+=delegate(object sender,ProgressChangedEventArgs e){if(!IsDisposed)ShowDownloadProgress((UpdateDownloadProgress)e.UserState);};
            worker.RunWorkerCompleted+=delegate(object sender,RunWorkerCompletedEventArgs e){
                if(IsDisposed){worker.Dispose();return;}progressTimer.Stop();downloadClock.Stop();progress.Style=ProgressBarStyle.Continuous;check.Enabled=true;
                if(e.Error!=null){notice.Text=e.Error is WebException?"Не удалось связаться с GitHub. Проверьте интернет и повторите проверку.":e.Error.Message;download.Enabled=release!=null && release.Version>client.CurrentVersion;}
                else complete(e.Result);worker.Dispose();
            };worker.RunWorkerAsync();
        }
        void CheckNow()
        {
            notice.Text="Проверяю последний выпуск на GitHub…";
            Run(report=>client.Check(),value=>{preferences.LastUpdateCheckUtc=DateTime.UtcNow;TrySave();ShowRelease((AvailableUpdate)value);},false);
        }
        internal void ShowRelease(AvailableUpdate value)
        {
            release=value;prepared=null;notes.Text=ReadableNotes(value.Notes);
            bool newer=value.Version>client.CurrentVersion;
            notice.Text=newer?"Доступна версия "+value.Version.ToString(3)+". Скачайте её, затем нажмите «Обновить и перезапустить».":"У вас установлена последняя версия AutoCQ.";
            transferDetails.Text="Во время скачивания здесь появятся объём, скорость и оставшееся время.";
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
            prepared=null;notice.Text="Подготавливаю загрузку…";
            Run(report=>client.Download(release,stateDirectory,report),value=>{prepared=(PreparedUpdate)value;progress.Value=100;notice.Text="Готово к установке: версия "+prepared.Version+". Контрольная сумма проверена. Закройте JTDX и нажмите «Обновить и перезапустить».";download.Enabled=true;install.Enabled=true;},true);
        }
        internal void ShowDownloadProgress(UpdateDownloadProgress value)
        {
            lastDownloadProgress=value;lastProgressAt=downloadClock.Elapsed;
            RefreshDownloadProgress();
        }
        static string SizeText(long bytes)
        {
            var culture=CultureInfo.GetCultureInfo("ru-RU");
            if(bytes<1024)return bytes.ToString(culture)+" Б";
            if(bytes<1024*1024)return (bytes/1024.0).ToString("0.#",culture)+" КБ";
            return (bytes/(1024.0*1024)).ToString("0.##",culture)+" МБ";
        }
        static string DurationText(TimeSpan time)
        { return time.TotalHours>=1?String.Format("{0}:{1:00}:{2:00}",(int)time.TotalHours,time.Minutes,time.Seconds):String.Format("{0:00}:{1:00}",(int)time.TotalMinutes,time.Seconds); }
        void RefreshDownloadProgress()
        {
            if(lastDownloadProgress==null)return;
            UpdateDownloadProgress value=lastDownloadProgress;
            TimeSpan idle=downloadClock.IsRunning?downloadClock.Elapsed-lastProgressAt:TimeSpan.Zero;
            if(value.Stage==UpdateDownloadStage.Downloading)
                value=new UpdateDownloadProgress(value.Stage,value.ReceivedBytes,value.TotalBytes,value.Elapsed+idle);
            progress.Style=ProgressBarStyle.Continuous;
            progress.Value=value.Stage>=UpdateDownloadStage.Verifying?100:value.Percent??0;
            string amount=SizeText(value.ReceivedBytes)+(value.TotalBytes.HasValue?" из "+SizeText(value.TotalBytes.Value):" · размер файла не указан сервером");
            string elapsed=DurationText(downloadClock.Elapsed>TimeSpan.Zero?downloadClock.Elapsed:value.Elapsed);
            if(value.Stage==UpdateDownloadStage.Preparing || value.Stage==UpdateDownloadStage.Connecting)
            {
                notice.Text=value.Stage==UpdateDownloadStage.Preparing?"Подготавливаю загрузку…":"Подключаюсь к серверу загрузки…";
                transferDetails.Text="Ожидаю ответ сервера · прошло "+elapsed;
            }
            else if(value.Stage==UpdateDownloadStage.Downloading)
            {
                notice.Text="Загружаю обновление"+(value.Percent.HasValue?": "+value.Percent+"%":"…");
                string speed=value.BytesPerSecond>0?SizeText((long)value.BytesPerSecond)+"/с":"рассчитываю скорость";
                string remaining=value.Remaining.HasValue?"осталось ≈ "+DurationText(value.Remaining.Value):"оставшееся время пока неизвестно";
                transferDetails.Text=amount+"\r\n"+speed+" · "+remaining+" · прошло "+elapsed;
                if(idle.TotalSeconds>=3)notice.Text+=" · ожидаю данные от сервера";
            }
            else
            {
                notice.Text=value.Stage==UpdateDownloadStage.Verifying?"Загрузка завершена. Проверяю архив…":value.Stage==UpdateDownloadStage.Extracting?"Архив проверен. Подготавливаю файлы обновления…":"Обновление скачано и проверено. Готово к установке.";
                transferDetails.Text="Скачано "+SizeText(value.ReceivedBytes)+" · 100%\r\nПрошло "+elapsed;
            }
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
