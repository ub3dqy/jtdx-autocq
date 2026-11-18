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
            Invoke(form,"Log","ДЕМО: цели JA, CE, W1ABC, VK*. Ожидание CQ, 73, RRR или RR73.");
            Save(form,Path.Combine(directory,"monitor-demo.png"));
            var monitor=(AutoCqEngine)engine;DateTime now=DateTime.UtcNow;
            foreach(string call in new[]{"JA1BBB","CE1AAA"})
                monitor.Cqs.Observe(new DecodedMessage{IsNew=false,Milliseconds=(uint)now.TimeOfDay.TotalMilliseconds,Snr=-12,DeltaTime=0.1,DeltaFrequency=1800,Mode="~",Message="CQ "+call+" FF46"},monitor.Status,now);
            monitor.Cqs.Failed("JA1BBB",now.AddSeconds(-72),true);monitor.Cqs.Failed("CE1AAA",now.AddSeconds(-185),true);
            ((TabControl)Field(form,"lists")).SelectedIndex=2;Invoke(form,"UpdateUi");
            Save(form,Path.Combine(directory,"monitor-timeouts-demo.png"));
        }
        using(var form=new SetupDialog(@"C:\Radio\JTDX\bin\jtdx.exe"))Save(form,Path.Combine(directory,"setup-demo.png"));
        using(var form=new UpdateDialog(new UpdateClient(version),new AppPreferences(),new AppPreferencesStore(null),Path.GetTempPath(),@"C:\Radio\JTDX\bin\jtdx.exe",true,()=>{},
            new AvailableUpdate{Version=Version.Parse(version),Notes="AutoCQ "+version+"\r\n\r\n• Мониторинг по CQ, 73, RRR и RR73.\r\n• Подготовка JTDX из окна программы.\r\n• Проверка и установка обновлений.\r\n\r\nДЕМО: пример описания установленного выпуска."}))
            Save(form,Path.Combine(directory,"updates-demo.png"));
        Console.WriteLine("Rendered five offline demo screenshots.");
    }
}
