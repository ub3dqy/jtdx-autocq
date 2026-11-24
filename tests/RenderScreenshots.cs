using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using JtdxAutoCq;

// Render the real demo forms offscreen without opening a UDP connection or using a radio.
static class RenderScreenshots
{
    static object Field(object value,string name){return value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(value);}
    static object Invoke(object value,string name,params object[] arguments){return value.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(value,arguments);}
    static void Save(Form form,string path, bool showOffscreen = false)
    {
        form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-32000,-32000);form.ShowInTaskbar=false;
        typeof(Control).GetMethod("CreateControl",BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(bool)},null).Invoke(form,new object[]{true});
        form.PerformAutoScale();form.PerformLayout();
        if (showOffscreen)
        {
            // The native drop-downs paint their selected values only in a shown demo form.
            form.Show();
            form.Refresh();
            Application.DoEvents();
        }
        using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));bitmap.Save(path,ImageFormat.Png);}
        if (showOffscreen) form.Hide();
    }
    [STAThread] static void Main(string[] args)
    {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        string directory=Path.GetFullPath(args[0]);Directory.CreateDirectory(directory);
        string version=typeof(MainForm).Assembly.GetName().Version.ToString(3);
        using(var form=new MainForm(true))
        {
            ((Timer)Field(form,"timer")).Stop();
            var engine=Field(form,"engine");
            engine.GetType().GetMethod("AddQueuedCall").Invoke(engine,new object[]{"W2ABC",DateTime.UtcNow});
            engine.GetType().GetMethod("AddQueuedCall").Invoke(engine,new object[]{"JA1BBB",DateTime.UtcNow});
            Invoke(form,"UpdateUi");
            Save(form,Path.Combine(directory,"autocq-demo.png"));
        }
        using (var form = new MainForm(true))
        {
            ((Timer)Field(form, "timer")).Stop();
            var engine = (AutoCqEngine)Field(form, "engine");
            DateTime now = DateTime.UtcNow;
            engine.Start(now);
            var retuned = new RadioStatus {
                Frequency = 14074040, Mode = "FT8", DeCall = "K1ABC", TxFirst = engine.Status.TxFirst
            };
            engine.OnStatus(retuned, now.AddSeconds(1));
            engine.OnStatus(retuned, now.AddSeconds(2));
            Invoke(form, "UpdateUi");
            string reason = "Автомат остановлен. Частота изменилась с 14,074000 до 14,074040 МГц (+40 Гц).";
            if (((Label)Field(form, "status")).Text != reason || !((TextBox)Field(form, "journal")).Text.Contains(reason))
                throw new Exception("The frequency stop reason is missing from the status or journal.");
            Save(form, Path.Combine(directory, "frequency-stop-demo.png"));
        }
        using (var form = new MainForm(true))
        {
            ((Timer)Field(form, "timer")).Stop();
            var engine = (AutoCqEngine)Field(form, "engine");
            DateTime now = DateTime.UtcNow;
            DateTime tx = now.Date.AddSeconds(Math.Floor(now.TimeOfDay.TotalSeconds / 30) * 30 - 30);
            DateTime rx = tx.AddSeconds(15);
            DateTime decoded = tx.AddSeconds(29.5);
            Invoke(form, "SetDemoStatus", true, false, "DL1AAA", true);
            engine.Start(tx);
            engine.OnLine(tx.ToString("yyyyMMdd_HHmmss") + ".000(0) Transmitting 14.074 MHz + 1500Hz FT8: DL1AAA K1ABC RR73", tx);
            engine.OnLine(rx.ToString("yyyyMMdd_HHmmss") + " -15 0.1 1500 ~ K1ABC DL1AAA R+00", decoded);
            engine.OnLine(decoded.ToString("yyyyMMdd_HHmmss.fff") + "(0)  Decoding finished", decoded);
            engine.Tick(decoded, true);
            if (!engine.RepeatFinal73 || engine.SilentTerminalRepeats != 0)
                throw new Exception("The new report did not leave the quiet-retry count at zero.");
            Invoke(form, "UpdateUi");
            Save(form, Path.Combine(directory, "signoff-retry-demo.png"));
        }
        using(var form=new MainForm(true))
        {
            ((Timer)Field(form,"timer")).Stop();
            var engine=Field(form,"engine");
            var options=engine.GetType().GetField("Options").GetValue(engine);
            options.GetType().GetField("MonitorDxcc").SetValue(options,new[]{"JA","CE"});
            options.GetType().GetField("MonitorCalls").SetValue(options,new[]{"W1ABC"});
            options.GetType().GetField("MonitorMasks").SetValue(options,new[]{"VK*"});
            ((CheckBox)Field(form,"monitor")).Checked=true;
            ((AutoCqEngine)engine).Start(DateTime.UtcNow);
            Invoke(form,"UpdateUi");
            Invoke(form,"Log","ДЕМО: цели JA, CE, W1ABC, VK*. Входящие вызовы принимаются вне фильтра.");
            Save(form,Path.Combine(directory,"monitor-demo.png"));
            var monitor=(AutoCqEngine)engine;DateTime now=DateTime.UtcNow;
            foreach(string call in new[]{"JA1BBB","CE1AAA"})
                monitor.Cqs.Observe(new DecodedMessage{IsNew=false,Milliseconds=(uint)now.TimeOfDay.TotalMilliseconds,Snr=-12,DeltaTime=0.1,DeltaFrequency=1800,Mode="~",Message="CQ "+call+" FF46"},monitor.Status,now);
            monitor.Cqs.Failed("JA1BBB",now.AddSeconds(-72),true);monitor.Cqs.Failed("CE1AAA",now.AddSeconds(-185),true);
            ((TabControl)Field(form,"lists")).SelectedIndex=2;Invoke(form,"UpdateUi");
            Save(form,Path.Combine(directory,"monitor-timeouts-demo.png"));
            var outside=new DecodedMessage{IsNew=false,Milliseconds=(uint)now.TimeOfDay.TotalMilliseconds,Snr=-9,DeltaTime=0.1,DeltaFrequency=1700,Mode="~",Message="CQ DL1AAA JO62"};
            monitor.Cqs.Observe(outside,monitor.Status,now);
            ((TabControl)Field(form,"lists")).SelectedIndex=1;Invoke(form,"UpdateUi");
            var view=(ListView)Field(form,"cqView");
            foreach(ListViewItem item in view.Items)if((string)item.Tag=="DL1AAA"){item.Selected=true;item.Focused=true;}
            if(!((Button)Field(form,"callSelectedCq")).Enabled)throw new Exception("Manual call button is disabled in monitoring.");
            Invoke(form,"CallSelectedCq");monitor.Tick(DateTime.UtcNow,true);Invoke(form,"UpdateUi");
            if(!monitor.Monitoring || monitor.ManualMonitorCall!="DL1AAA")throw new Exception("Manual call did not preserve monitoring.");
            foreach(ListViewItem item in view.Items)if((string)item.Tag=="DL1AAA")item.EnsureVisible();
            Save(form,Path.Combine(directory,"monitor-manual-demo.png"));
        }
        using(var form=new MainForm(true))
        {
            ((Timer)Field(form,"timer")).Stop();
            var engine=(AutoCqEngine)Field(form,"engine");
            engine.Options.MonitorDxcc=new[]{"CE"};
            engine.Options.ExcludedDxcc=new[]{"JA"};
            ((CheckBox)Field(form,"monitor")).Checked=true;
            Invoke(form,"CountryTable");
            DateTime now=DateTime.UtcNow;
            engine.OnLine("QSO logged: DL1AAA",now.AddMinutes(-1));
            engine.Start(now);
            DateTime slot=now.Date.AddSeconds(Math.Floor(now.TimeOfDay.TotalSeconds/15)*15);
            foreach(string call in new[]{"JA2AAA","DL1AAA"})
                engine.OnDecode(new DecodedMessage{IsNew=true,Milliseconds=(uint)slot.TimeOfDay.TotalMilliseconds,Snr=-11,DeltaTime=0.2,DeltaFrequency=1700,Mode="~",Message="K1ABC "+call+" JO62"},now);
            Invoke(form,"Log","DL1AAA вызывает вас повторно. JA2AAA входит в исключения DXCC.");
            engine.Tick(now,true);
            if(!engine.Monitoring || engine.IncomingMonitorCall!="DL1AAA")throw new Exception("The repeat caller was skipped or an excluded call was selected.");
            ((TabControl)Field(form,"lists")).SelectedIndex=0;Invoke(form,"UpdateUi");
            Save(form,Path.Combine(directory,"monitor-callers-demo.png"));
        }
        using(var form=new SetupDialog(@"C:\Radio\JTDX\bin\jtdx.exe"))Save(form,Path.Combine(directory,"setup-demo.png"));
        using(var form=new AboutDialog(version))Save(form,Path.Combine(directory,"about-demo.png"));
        using(var form=new UpdateDialog(new UpdateClient(version),new AppPreferences(),new AppPreferencesStore(null),Path.GetTempPath(),@"C:\Radio\JTDX\bin\jtdx.exe",true,()=>{},
            new AvailableUpdate{Version=Version.Parse(version),Notes="AutoCQ "+version+" · UB3DQY\r\n\r\n• Процент и объём загрузки.\r\n• Скорость и примерное оставшееся время.\r\n• Отдельные этапы проверки и подготовки файлов.\r\n\r\nДЕМО: пример описания установленного выпуска."}))
            Save(form,Path.Combine(directory,"updates-demo.png"));
        using(var form=new UpdateDialog(new UpdateClient("0.9.9"),new AppPreferences(),new AppPreferencesStore(null),Path.GetTempPath(),@"C:\Radio\JTDX\bin\jtdx.exe",true,()=>{},
            new AvailableUpdate{Version=Version.Parse(version),Notes="AutoCQ "+version+" · UB3DQY\r\n\r\nДЕМО: пример нового индикатора загрузки.\r\nЧисла приведены для наглядности; файлы не скачиваются."}))
        {
            typeof(Control).GetMethod("CreateControl",BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(bool)},null).Invoke(form,new object[]{true});
            Invoke(form,"ShowDownloadProgress",new UpdateDownloadProgress(UpdateDownloadStage.Downloading,424*1024,640*1024,TimeSpan.FromSeconds(3)));
            foreach(string button in new[]{"check","download","install"})((Button)Field(form,button)).Enabled=false;
            Save(form,Path.Combine(directory,"updates-download-demo.png"));
        }
        using(var form=BugReportDialog.CreateDemo(version))Save(form,Path.Combine(directory,"bug-report-demo.png"));
        using (var form = new MainForm(true))
        {
            ((Timer)Field(form,"timer")).Stop();
            string awardCache = Path.Combine(Path.GetTempPath(), "AutoCQ-demo-" + Guid.NewGuid().ToString("N"));
            string[] names = { "logbook-source-demo.png", "profiles-demo.png", "dxcc-targets-demo.png",
                "station-reason-demo.png", "session-summary-demo.png", "awards-demo.png", "award-goals-demo.png",
                "award-points-journal-demo.png", "hamlog-account-demo.png", "hamlog-plan-demo.png" };
            try
            {
                var screens = (Form[])Invoke(form,"OperatorToolsDemoForms", awardCache);
                try
                {
                    for (int index = 0; index < screens.Length; index++)
                        Save(screens[index], Path.Combine(directory, names[index]),
                            showOffscreen: names[index] == "awards-demo.png" || names[index] == "hamlog-plan-demo.png");
                }
                finally { foreach (Form screen in screens) screen.Dispose(); }
            }
            finally
            {
                if (Directory.Exists(awardCache))
                {
                    foreach (string file in Directory.GetFiles(awardCache)) File.Delete(file);
                    Directory.Delete(awardCache);
                }
            }
        }
        Console.WriteLine("Rendered offline demo screenshots.");
    }
}
