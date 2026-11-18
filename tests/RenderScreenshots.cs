using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using JtdxAutoCq;

// Render the real demo forms without opening windows, a UDP connection or a radio.
static class RenderScreenshots
{
    static object Field(object value,string name){return value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(value);}
    static void Invoke(object value,string name,params object[] arguments){value.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(value,arguments);}
    static void Save(Form form,string path)
    {
        form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-32000,-32000);form.ShowInTaskbar=false;
        typeof(Control).GetMethod("CreateControl",BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(bool)},null).Invoke(form,new object[]{true});
        form.PerformAutoScale();form.PerformLayout();
        using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));bitmap.Save(path,ImageFormat.Png);}
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
            engine.Options.MonitorDxcc=new[]{"JA","CE"};
            ((CheckBox)Field(form,"monitor")).Checked=true;
            DateTime now=DateTime.UtcNow;engine.Start(now);
            DateTime slot=now.Date.AddSeconds(Math.Floor(now.TimeOfDay.TotalSeconds/15)*15);
            foreach(string call in new[]{"DL1AAA","W2ABC"})
                engine.OnDecode(new DecodedMessage{IsNew=true,Milliseconds=(uint)slot.TimeOfDay.TotalMilliseconds,Snr=-11,DeltaTime=0.2,DeltaFrequency=1700,Mode="~",Message="K1ABC "+call+" JO62"},now);
            Invoke(form,"Log","ДЕМО: фильтр JA и CE; DL1AAA и W2ABC сами вызывают K1ABC.");
            engine.Tick(now,true);
            if(!engine.Monitoring || engine.IncomingMonitorCall!="DL1AAA")throw new Exception("Incoming monitoring reply was skipped.");
            ((TabControl)Field(form,"lists")).SelectedIndex=0;Invoke(form,"UpdateUi");
            Save(form,Path.Combine(directory,"monitor-callers-demo.png"));
        }
        using(var form=new SetupDialog(@"C:\Radio\JTDX\bin\jtdx.exe"))Save(form,Path.Combine(directory,"setup-demo.png"));
        using(var form=new AboutDialog(version))Save(form,Path.Combine(directory,"about-demo.png"));
        using(var form=new UpdateDialog(new UpdateClient(version),new AppPreferences(),new AppPreferencesStore(null),Path.GetTempPath(),@"C:\Radio\JTDX\bin\jtdx.exe",true,()=>{},
            new AvailableUpdate{Version=Version.Parse(version),Notes="AutoCQ "+version+" · UB3DQY\r\n\r\n• Позывной автора в главном окне.\r\n• Ссылка на ub3dqy.com и новое окно «О программе».\r\n• Автор и название в свойствах файла программы.\r\n\r\nДЕМО: пример описания установленного выпуска."}))
            Save(form,Path.Combine(directory,"updates-demo.png"));
        Console.WriteLine("Rendered eight offline demo screenshots.");
    }
}
