using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace JtdxAutoCq
{
    public static class NativeInstaller
    {
        public static string FindJtdx(string preferred)
        {
            foreach(string candidate in new[]{preferred,MainForm.DefaultJtdxPath,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),@"JTDX\bin\jtdx.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),@"JTDX64\bin\jtdx.exe")})
                if(!String.IsNullOrEmpty(candidate) && File.Exists(candidate))return Path.GetFullPath(candidate);
            foreach(RegistryHive hive in new[]{RegistryHive.CurrentUser,RegistryHive.LocalMachine})
            foreach(RegistryView view in new[]{RegistryView.Registry64,RegistryView.Registry32})
            {
                try
                {
                    using(RegistryKey root=RegistryKey.OpenBaseKey(hive,view))
                    using(RegistryKey uninstall=root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if(uninstall==null)continue;
                        foreach(string name in uninstall.GetSubKeyNames())
                        using(RegistryKey entry=uninstall.OpenSubKey(name))
                        {
                            if(entry==null)continue;
                            string title=Convert.ToString(entry.GetValue("DisplayName")),folder=Convert.ToString(entry.GetValue("InstallLocation"));
                            if(title.IndexOf("JTDX",StringComparison.OrdinalIgnoreCase)<0 || folder.Length==0)continue;
                            foreach(string relative in new[]{@"bin\jtdx.exe","jtdx.exe"})
                            {string file=Path.Combine(folder,relative);if(File.Exists(file))return file;}
                        }
                    }
                }
                catch(UnauthorizedAccessException){}catch(System.Security.SecurityException){}
            }
            return MainForm.DefaultJtdxPath;
        }
        public static void Prepare(string file)
        {
            try { JtdxPatcher.Apply(file); }
            catch(UnauthorizedAccessException)
            {
                string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"JtdxAutoCQ","tools",Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);string tool=Path.Combine(folder,"JTDX-AutoCQ-Patch.exe");
                try
                {
                    File.WriteAllBytes(tool,JtdxPatcher.Resource("JTDX-AutoCQ-Patch.exe"));
                    var start=new ProcessStartInfo(tool,"--jtdx "+UpdateClient.Quote(file)){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};
                    using(Process helper=Process.Start(start)){helper.WaitForExit();if(helper.ExitCode!=0)throw new IOException("Windows не разрешил подготовку JTDX. Проверьте путь и повторите.");}
                    if(JtdxPatcher.HashFile(file)!=JtdxPatcher.PatchedHash)throw new IOException("Подготовка JTDX не завершилась.");
                }
                finally
                {
                    try { if(File.Exists(tool))File.Delete(tool);Directory.Delete(folder); }
                    catch(IOException){}catch(UnauthorizedAccessException){}
                }
            }
        }
    }
    public sealed class SetupDialog : Form
    {
        readonly TextBox path=new TextBox();
        readonly Label notice=new Label();
        readonly Button prepare=new Button(),done=new Button(),choose=new Button();
        readonly ProgressBar progress=new ProgressBar();
        bool busy;
        public string SelectedPath { get { return Path.GetFullPath(path.Text.Trim()); } }
        public SetupDialog(string initialPath)
        {
            Text="Подготовка JTDX — "+AppBrand.DisplayName;Font=new Font("Segoe UI",10F);AutoScaleMode=AutoScaleMode.Dpi;
            BrandUi.ApplyIcon(this);
            ClientSize=new Size(650,355);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterParent;
            BackColor=Color.FromArgb(238,243,248);
            var title=new Label{Text="Подготовим JTDX к работе с AutoCQ",Font=new Font("Segoe UI Semibold",16F),AutoSize=true};title.SetBounds(22,20,610,30);Controls.Add(title);
            var help=new Label{Text="Закройте JTDX. Проверьте, что ниже указан его файл jtdx.exe.\r\nНажмите «Подготовить JTDX» — всё необходимое установится автоматически."};
            help.SetBounds(22,68,608,54);Controls.Add(help);
            path.Text=initialPath;path.SetBounds(22,137,480,29);Controls.Add(path);
            choose.Text="Выбрать файл…";choose.SetBounds(513,135,115,32);Controls.Add(choose);
            choose.Click+=delegate{using(var dialog=new OpenFileDialog{Title="Выберите установленный jtdx.exe",Filter="Программа JTDX (jtdx.exe)|jtdx.exe",CheckFileExists=true}){if(dialog.ShowDialog(this)==DialogResult.OK)path.Text=dialog.FileName;}};
            notice.Text="Нужна JTDX 2.2.160-32A-rc10, 64 бита. Ваши настройки и журнал сохранятся.";
            notice.SetBounds(22,185,606,56);Controls.Add(notice);
            var getJtdx=BrandUi.Link("Где скачать подходящий JTDX?",AppBrand.ProjectPage);getJtdx.SetBounds(22,228,440,23);Controls.Add(getJtdx);
            var author=BrandUi.Link(AppBrand.Author+" · "+AppBrand.WebsiteLabel,AppBrand.Website);
            author.Font=new Font("Segoe UI",9F);author.TextAlign=ContentAlignment.MiddleCenter;author.SetBounds(250,282,221,39);Controls.Add(author);
            progress.SetBounds(22,250,606,8);progress.Visible=false;Controls.Add(progress);
            prepare.Text="Подготовить JTDX";prepare.SetBounds(22,282,218,39);prepare.BackColor=Color.FromArgb(0,100,91);prepare.ForeColor=Color.White;prepare.FlatStyle=FlatStyle.Flat;Controls.Add(prepare);
            done.Text="Отмена";done.SetBounds(482,282,146,39);Controls.Add(done);done.Click+=delegate{DialogResult=done.Tag==null?DialogResult.Cancel:DialogResult.OK;};
            prepare.Click+=delegate{RunPreparation();};
            FormClosing+=delegate(object sender,FormClosingEventArgs e){if(busy)e.Cancel=true;};
        }
        void RunPreparation()
        {
            string file;
            try {file=SelectedPath;if(!File.Exists(file) || !Path.GetFileName(file).Equals("jtdx.exe",StringComparison.OrdinalIgnoreCase))throw new IOException("Нажмите «Выбрать файл» и укажите установленный jtdx.exe.");}
            catch(Exception e){notice.Text=e.Message;return;}
            busy=true;prepare.Enabled=choose.Enabled=path.Enabled=done.Enabled=false;progress.Style=ProgressBarStyle.Marquee;progress.Visible=true;
            notice.Text="Проверяю сборку и готовлю JTDX. Если Windows запросит разрешение, подтвердите его.";
            var worker=new BackgroundWorker();
            worker.DoWork+=delegate{NativeInstaller.Prepare(file);};
            worker.RunWorkerCompleted+=delegate(object sender,RunWorkerCompletedEventArgs e){
                busy=false;if(IsDisposed){worker.Dispose();return;}progress.Visible=false;done.Enabled=true;
                if(e.Error!=null){notice.Text=e.Error is Win32Exception?"Разрешение Windows отменено. Можно повторить подготовку.":e.Error.Message;prepare.Enabled=choose.Enabled=path.Enabled=true;}
                else{notice.ForeColor=Color.FromArgb(0,100,91);notice.Text="Готово! Вернитесь в AutoCQ и продолжите подготовку подключения.";done.Text="Продолжить";done.Tag=true;}
                worker.Dispose();
            };worker.RunWorkerAsync();
        }
    }
}
