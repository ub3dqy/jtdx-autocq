using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Serialization;
using System.Xml;
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyVersion("0.9.40.0")]
[assembly: AssemblyFileVersion("0.9.40.0")]
[assembly: AssemblyTitle(JtdxAutoCq.AppBrand.DisplayName)]
[assembly: AssemblyProduct(JtdxAutoCq.AppBrand.DisplayName)]
[assembly: AssemblyCompany(JtdxAutoCq.AppBrand.Author)]
[assembly: AssemblyDescription(JtdxAutoCq.AppBrand.DisplayName + " — JTDX FT8/FT4. " + JtdxAutoCq.AppBrand.Website)]
[assembly: AssemblyCopyright(JtdxAutoCq.AppBrand.Copyright)]

namespace JtdxAutoCq
{
    public sealed class SavedValue
    {
        public string Section, Key, Original, Applied;
        public bool Existed;
    }
    public sealed class ConnectionRecord
    {
        public string Host;
        public int Port;
        public List<SavedValue> Values = new List<SavedValue>();
    }
    public sealed class IniFile
    {
        readonly List<string> lines;
        public IniFile(string path) { lines = new List<string>(File.ReadAllLines(path)); }
        int Find(string section, string key)
        {
            string current = "";
            for (int n = 0; n < lines.Count; n++)
            {
                string s = lines[n].Trim();
                if (s.StartsWith("[") && s.EndsWith("]")) current = s.Substring(1, s.Length - 2);
                if (current == section && s.StartsWith(key + "=", StringComparison.Ordinal)) return n;
            }
            return -1;
        }
        public string Get(string section, string key, string fallback)
        { int n = Find(section, key); return n < 0 ? fallback : lines[n].Substring(lines[n].IndexOf('=') + 1); }
        public void Put(string section, string key, string value)
        {
            int n = Find(section, key);
            if (n >= 0) { if (value == null) lines.RemoveAt(n); else lines[n] = key + "=" + value; return; }
            if (value == null) return;
            int header = lines.FindIndex(s => s.Trim() == "[" + section + "]");
            if (header < 0) { lines.Add(""); lines.Add("[" + section + "]"); lines.Add(key + "=" + value); }
            else lines.Insert(header + 1, key + "=" + value);
        }
        public void Save(string path)
        {
            string temp = path + ".autocq.tmp";
            File.WriteAllLines(temp, lines, new UTF8Encoding(false));
            File.Replace(temp, path, null);
        }
    }

    public sealed class SettingsManager
    {
        public const int ListenPort = 2247;
        public readonly string Profile;
        public readonly string StateDirectory;
        public string IniPath { get { return Path.Combine(Profile, "JTDX.ini"); } }
        string RecordPath { get { return Path.Combine(StateDirectory, "connection.xml"); } }
        public ConnectionRecord Record { get; private set; }
        public SettingsManager() : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JTDX"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JtdxAutoCQ")) { }
        public SettingsManager(string profile, string stateDirectory)
        {
            Profile = profile; StateDirectory = stateDirectory;
            if (File.Exists(RecordPath))
                using (FileStream f = File.OpenRead(RecordPath)) Record = (ConnectionRecord)new XmlSerializer(typeof(ConnectionRecord)).Deserialize(f);
        }
        void SaveRecord()
        {
            Directory.CreateDirectory(StateDirectory);
            string temp = RecordPath + ".tmp";
            using (FileStream f = File.Create(temp)) new XmlSerializer(typeof(ConnectionRecord)).Serialize(f, Record);
            if (File.Exists(RecordPath)) File.Replace(temp, RecordPath, null); else File.Move(temp, RecordPath);
        }
        public void Prepare()
        { Prepare(new AutoCqOptions()); }
        public void Prepare(AutoCqOptions options)
        {
            options.Validate();
            if (!File.Exists(IniPath)) throw new IOException("Не найден профиль " + IniPath);
            IniFile ini = new IniFile(IniPath);
            if (Record != null)
            {
                if (ini.Get("Configuration", "UDPServer", "") != "127.0.0.1" || ini.Get("Configuration", "UDPServerPort", "") != ListenPort.ToString())
                    throw new InvalidOperationException("Настройки UDP были изменены. Сначала восстановите прежнее подключение кнопкой ниже.");
                // An older prepared session may still override preferences
                // that this version leaves under the operator's control.
                if (!Record.Values.Any(v => v.Key == "AutoErase" || v.Key == "BypassAllTextFilters"
                    || v.Key == "CallMode" || v.Key == "CallPriorityCQ" || v.Key == "SingleShotQSO"
                    || v.Key == "PromptToLog"))
                {
                    if (ini.Get("Common", "SingleShotQSO", "") != "false")
                        ini.Put("Common", "SingleShotQSO", "false");
                    UpdatePreparedValue(ini, "SeqAnswerCQCounterValue", options.CallAttempts);
                    UpdatePreparedValue(ini, "SeqAnswerInCallCounterValue", options.CallAttempts);
                    UpdatePreparedValue(ini, "SeqSentRReportCounterValue", options.CallAttempts);
                    UpdatePreparedValue(ini, "SeqSentRR7373CounterValue", Math.Min(5, options.Wait73Periods));
                    SaveRecord();
                    ini.Save(IniPath);
                    return;
                }
                Restore();
                ini = new IniFile(IniPath);
            }
            ConnectionRecord record = new ConnectionRecord();
            record.Host = ini.Get("Configuration", "UDPServer", "127.0.0.1");
            record.Port = Int32.Parse(ini.Get("Configuration", "UDPServerPort", "2237"));
            if (record.Port == ListenPort) throw new InvalidOperationException("Порт 2247 уже указан в JTDX. Выберите прежний порт журнала перед подключением.");
            string[,] changes = {
                {"Configuration", "UDPServer", "127.0.0.1"},
                {"Configuration", "UDPServerPort", ListenPort.ToString()},
                {"Configuration", "AcceptUDPRequests", "true"},
                {"Configuration", "WriteDecodedDebugALLTXT", "true"},
                {"Configuration", "AutoQSOLogging", "true"},
                {"Configuration", "udpWindowToFront", "false"},
                {"Configuration", "udpWindowRestore", "false"},
                {"Common", "AutoSequence", "true"},
                {"Common", "QuickCall", "true"},
                {"Common", "AcceptUDPReplyMessages", "3"},
                // Enforce the SCALL limit for all priorities, including new DXCC.
                {"Configuration", "StrictDirectionalCQ", "true"},
                {"Configuration", "SeqAnswerCQCount", "true"},
                {"Configuration", "SeqAnswerCQCounterValue", options.CallAttempts.ToString()},
                {"Configuration", "SeqAnswerInCallCount", "true"},
                {"Configuration", "SeqAnswerInCallCounterValue", options.CallAttempts.ToString()},
                {"Configuration", "SeqSentRReportCount", "true"},
                {"Configuration", "SeqSentRReportCounterValue", options.CallAttempts.ToString()},
                {"Configuration", "SeqSentRR7373Count", "true"},
                {"Configuration", "SeqSentRR7373CounterValue", Math.Min(5,options.Wait73Periods).ToString()}
            };
            for (int n = 0; n < changes.GetLength(0); n++)
            {
                string original = ini.Get(changes[n,0], changes[n,1], null);
                record.Values.Add(new SavedValue { Section = changes[n,0], Key = changes[n,1], Original = original, Existed = original != null, Applied = changes[n,2] });
                ini.Put(changes[n,0], changes[n,1], changes[n,2]);
            }
            // The operator wants this preference off in ordinary JTDX too.
            // Do not add it to the temporary connection restore record.
            ini.Put("Common", "SingleShotQSO", "false");
            Directory.CreateDirectory(StateDirectory);
            File.Copy(IniPath, Path.Combine(StateDirectory, "JTDX-before-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".ini"));
            Record = record; SaveRecord();
            try { ini.Save(IniPath); }
            catch { Record = null; File.Delete(RecordPath); throw; }
        }
        void UpdatePreparedValue(IniFile ini, string key, int value)
        {
            const string section = "Configuration";
            SavedValue saved = Record.Values.FirstOrDefault(v => v.Section == section && v.Key == key);
            if (saved == null)
            {
                string original = ini.Get(section, key, null);
                saved = new SavedValue { Section = section, Key = key, Original = original, Existed = original != null };
                Record.Values.Add(saved);
            }
            saved.Applied = value.ToString();
            ini.Put(section, key, saved.Applied);
        }
        public void Restore()
        {
            if (Record == null) return;
            IniFile ini = new IniFile(IniPath);
            bool ours = ini.Get("Configuration", "UDPServer", "") == "127.0.0.1" && ini.Get("Configuration", "UDPServerPort", "") == ListenPort.ToString();
            // Respect values edited by the operator after connection.
            foreach (SavedValue v in Record.Values)
            {
                bool isNetwork = v.Key == "UDPServer" || v.Key == "UDPServerPort";
                if ((!isNetwork || ours) && ini.Get(v.Section, v.Key, null) == v.Applied)
                    ini.Put(v.Section, v.Key, v.Existed ? v.Original : null);
            }
            ini.Save(IniPath); File.Delete(RecordPath); Record = null;
        }
        public string Direction()
        {
            string d = new IniFile(IniPath).Get("Configuration", "CQdirection", "").Trim().ToUpperInvariant();
            return d.Length == 2 && d.All(c => c >= 'A' && c <= 'Z') ? d : "";
        }
    }

    public sealed partial class MainForm : Form
    {
        const string ModuleVersion = "0.9.40";
        public const string DefaultJtdxPath = @"C:\JTDX64\160-32A-rc10\bin\jtdx.exe";
        const string NativeQueueHash = JtdxPatcher.PatchedHash;
        static readonly Color Navy = Color.FromArgb(17, 31, 53);
        static readonly Color Ink = Color.FromArgb(21, 38, 58);
        static readonly Color Slate = Color.FromArgb(79, 96, 112);
        static readonly Color Teal = Color.FromArgb(0, 100, 91);
        static readonly Color Amber = Color.FromArgb(250, 206, 129);
        static readonly Color Mint = Color.FromArgb(112, 239, 212);
        readonly AutoCqEngine engine = new AutoCqEngine();
        readonly bool demo;
        string jtdxPath;
        readonly AppPreferences preferences;
        readonly AppPreferencesStore preferenceStore;
        readonly UpdateClient updateClient = new UpdateClient(ModuleVersion);
        readonly Button updatesButton = new Button(), helpButton = new Button(), reportButton = new Button();
        readonly ToolTip updatesTip = new ToolTip();
        BackgroundWorker automaticUpdateWorker;
        BugReportDialog reportDialog;
        AvailableUpdate availableUpdate;
        DateTime nextAutomaticUpdate = DateTime.UtcNow.AddSeconds(8);
        SettingsManager settings;
        LogTail tail;
        UdpBridge bridge;
        IPEndPoint client, upstream;
        string clientId, clientVersion, direction = "";
        uint schema;
        DateTime lastSeen, demoNext;
        DateTime nextSelectionPacket, nextReplayRequest;
        bool selectionHeld, managedSession, replayWanted;
        bool listenAfter73, repeatFinal73;
        int repeatTerminalMessage;
        string repeatTerminalCall;
        DateTime pendingReplayUntil;
        int demoStep;
        bool closeWhenDone, hadProcess;
        string lastNotice = "";
        DateTime nextDiagnosticWrite;
        OperatorStateStore operatorStore;
        DxccCountries dxcc;
        bool queueDirty, queueUiDirty = true, refreshingQueue, jtdxPresent, demoSeeded;
        DateTime nextOperatorSave;
        string demoPeer = "W1AW";
        bool replayRequested, nativeStatusSeen;
        bool cqUiDirty = true;
        DateTime nextCqRefresh;
        DateTime nextTimeoutRefresh;
        readonly Label heading = new Label(), status = new Label(), radio = new Label(), counts = new Label();
        readonly Panel header = new Panel(), optionsPanel = new Panel();
        readonly Button open = new Button(), start = new Button(), stop = new Button(), restore = new Button();
        readonly TextBox journal = new TextBox();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly NumericUpDown waitPeriods = new NumericUpDown(), callAttempts = new NumericUpDown();
        readonly ListView waitingView = new ListView();
        readonly TextBox callEntry = new TextBox();
        readonly NumericUpDown runMinutes = new NumericUpDown(), cqAttempts = new NumericUpDown();
        readonly NumericUpDown switchMinutes = new NumericUpDown(), queueMinutes = new NumericUpDown();
        readonly CheckBox searchEnabled = new CheckBox(), priorityAfter73 = new CheckBox(), monitor = new CheckBox();
        readonly Button exclusions = new Button(), priorities = new Button(), monitorSetup = new Button();
        readonly Label sessionInfo = new Label(), listHint = new Label(), optionsHint = new Label();
        readonly TabControl lists = new TabControl();
        readonly ListView cqView = new ListView();
        readonly ListView timeoutView = new ListView();
        readonly TabPage timeoutTab = new TabPage("Пауза мониторинга");
        readonly Button callSelectedCq = new Button(), retrySelectedCq = new Button();
        readonly List<Control> waitEditors = new List<Control>();

        public MainForm(bool demo, string jtdxPath = null)
        {
            this.demo = demo;
            if (!demo)
            {
                settings = new SettingsManager(); operatorStore = new OperatorStateStore(settings.StateDirectory);
                OperatorState saved = operatorStore.Load(); engine.Options = saved.Options; engine.Waiting.Load(saved.Stations);
                jtdxPresent = JtdxPatcher.IsRunning();
            }
            preferenceStore = new AppPreferencesStore(demo ? null : settings.StateDirectory);
            preferences = preferenceStore.Load();
            this.jtdxPath = Path.GetFullPath(jtdxPath ?? (demo ? DefaultJtdxPath : NativeInstaller.FindJtdx(preferences.JtdxPath)));
            if (!String.Equals(Path.GetFileName(this.jtdxPath), "jtdx.exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Укажите путь к файлу jtdx.exe.");
            engine.Waiting.Changed = delegate { queueDirty = true; queueUiDirty = true; };
            engine.Cqs.Changed = delegate { cqUiDirty = true; nextTimeoutRefresh = DateTime.MinValue; };
            Text = AppBrand.DisplayName + " " + ModuleVersion + " — JTDX" + (demo ? " [ДЕМО]" : "");
            BrandUi.ApplyIcon(this);
            AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Segoe UI", 10F);
            ClientSize = new Size(960, 739); MinimumSize = Size; StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(238, 243, 248); ForeColor = Ink;

            header.SetBounds(0, 0, 960, 134);
            header.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            header.BackColor = Navy; Controls.Add(header);
            if (Icon != null)
            {
                var logo = new PictureBox { Image = Icon.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom };
                logo.SetBounds(22, 14, 48, 48); header.Controls.Add(logo);
                Disposed += delegate { logo.Image.Dispose(); };
            }
            heading.Text = "AutoCQ"; heading.Font = new Font("Segoe UI Semibold", 22F); heading.ForeColor = Color.White;
            heading.SetBounds(76, 17, 155, 41); header.Controls.Add(heading);
            var authorBadge = new LinkLabel { Text = AppBrand.Author, Font = new Font("Consolas", 14F, FontStyle.Bold),
                LinkColor = Mint, ActiveLinkColor = Color.White, VisitedLinkColor = Mint, LinkBehavior = LinkBehavior.HoverUnderline,
                BackColor = Color.FromArgb(35, 54, 78), BorderStyle = BorderStyle.FixedSingle, TextAlign = ContentAlignment.MiddleCenter,
                AccessibleName = "О программе AutoCQ и авторе UB3DQY" };
            authorBadge.SetBounds(238, 24, 153, 33); header.Controls.Add(authorBadge);
            authorBadge.LinkClicked += delegate { Guard(ShowAbout); };
            updatesTip.SetToolTip(authorBadge, "О программе и авторе " + AppBrand.Author);
            updatesButton.Text = "Обновления"; updatesButton.SetBounds(426, 23, 196, 33);
            updatesButton.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            StyleButton(updatesButton, Color.FromArgb(35, 54, 78), Color.White, Color.FromArgb(78, 103, 129));
            header.Controls.Add(updatesButton); updatesButton.Click += delegate { ShowUpdates(); };
            updatesTip.IsBalloon = true; updatesTip.SetToolTip(updatesButton, "Проверить новую версию и обновить AutoCQ");
            sessionInfo.SetBounds(640, 16, 294, 45);
            sessionInfo.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            sessionInfo.TextAlign = ContentAlignment.MiddleRight;
            sessionInfo.Font = new Font("Consolas", 13F, FontStyle.Bold);
            header.Controls.Add(sessionInfo);
            radio.SetBounds(26, 68, 908, 26);
            radio.Font = new Font("Segoe UI Semibold", 13F); header.Controls.Add(radio);
            counts.SetBounds(26, 102, 908, 22); counts.ForeColor = Color.FromArgb(193, 209, 224);
            header.Controls.Add(counts);

            status.Text = demo ? "Демонстрация без радио" : "AutoCQ выключен. Ожидаю подключение JTDX.";
            status.SetBounds(26, 142, 908, 43);
            status.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            status.BackColor = Color.White; status.Padding = new Padding(12, 0, 12, 0);
            status.TextAlign = ContentAlignment.MiddleLeft; Controls.Add(status);

            optionsPanel.SetBounds(26, 195, 908, 155);
            optionsPanel.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            optionsPanel.BackColor = Color.White; Controls.Add(optionsPanel);
            AddNumber(runMinutes, "Время автомата, мин", 14, 9, 193, 1440, engine.Options.RunMinutes);
            AddNumber(switchMinutes, "Смена периода без ответа, мин", 305, 9, 238, 1440, engine.Options.SwitchPeriodMinutes);
            AddNumber(queueMinutes, "Хранить позывной в очереди, мин", 605, 9, 231, 1440, engine.Options.QueueMinutes);
            AddNumber(waitPeriods, "Повторов 73 без ответа", 14, 42, 222, 20, engine.Options.Wait73Periods);
            AddNumber(callAttempts, "Вызовов без ответа", 305, 42, 238, 5, engine.Options.CallAttempts);
            AddNumber(cqAttempts, "CQ без ответа до поиска", 605, 42, 231, 20, engine.Options.CqAttemptsBeforeSearch);
            searchEnabled.SetBounds(14, 75, 225, 25); searchEnabled.Text = "Поиск станций CQ";
            searchEnabled.Checked = engine.Options.SearchEnabled; optionsPanel.Controls.Add(searchEnabled);
            searchEnabled.CheckedChanged += delegate { ChangeOptions(); };
            exclusions.SetBounds(305, 73, 222, 29);
            StyleButton(exclusions, Color.White, Ink, Color.FromArgb(201, 213, 224));
            exclusions.Click += delegate { Guard(() => EditCountries("Исключения DXCC", false)); };
            optionsPanel.Controls.Add(exclusions);
            priorities.SetBounds(605, 73, 222, 29);
            StyleButton(priorities, Color.White, Ink, Color.FromArgb(201, 213, 224));
            priorities.Click += delegate { Guard(() => EditCountries("Приоритетные DXCC", true)); };
            optionsPanel.Controls.Add(priorities);
            priorityAfter73.SetBounds(14, 103, 488, 24);
            priorityAfter73.Text = "Вызывать приоритетные DXCC после 73, RRR или RR73";
            priorityAfter73.Checked = engine.Options.PriorityAfter73;
            optionsPanel.Controls.Add(priorityAfter73);
            priorityAfter73.CheckedChanged += delegate { ChangeOptions(); cqUiDirty = true; };
            monitor.SetBounds(511, 103, 192, 24);
            monitor.Text = "Режим мониторинга"; monitor.Checked = engine.Options.MonitorEnabled;
            monitor.CheckedChanged += delegate { if(!monitor.Checked)engine.Cqs.ClearMonitorTimeouts(); ChangeOptions(); UpdateUi(); };
            monitorSetup.SetBounds(711, 102, 181, 26);
            StyleButton(monitorSetup, Color.White, Ink, Color.FromArgb(201, 213, 224));
            monitorSetup.Click += delegate { Guard(EditMonitorTargets); };
            optionsPanel.Controls.AddRange(new Control[] { monitor, monitorSetup });
            optionsHint.Font = new Font("Segoe UI", 9F); optionsHint.ForeColor = Slate;
            optionsHint.SetBounds(14, 128, 880, 21); optionsPanel.Controls.Add(optionsHint);

            open.Text = "1. Подготовить"; open.SetBounds(26, 361, 284, 40);
            start.Text = "2. Включить автомат"; start.SetBounds(338, 361, 284, 40);
            stop.Text = "СТОП — Halt TX"; stop.SetBounds(650, 361, 284, 40);
            StyleButton(open, Color.White, Ink, Color.FromArgb(201, 213, 224));
            StyleButton(start, Teal, Color.White, Teal);
            StyleButton(stop, Color.FromArgb(254, 232, 231), Color.FromArgb(139, 29, 27), Color.FromArgb(221, 160, 155));
            Controls.AddRange(new Control[] { open, start, stop });
            lists.SetBounds(26, 413, 908, 149); lists.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            TabPage waitTab=new TabPage("Очередь станций"),cqTab=new TabPage("Станции из левого окна JTDX");
            lists.TabPages.Add(waitTab);lists.TabPages.Add(cqTab);lists.TabPages.Add(timeoutTab);Controls.Add(lists);lists.SelectedIndexChanged+=delegate{UpdateEditors();};
            lists.SelectedIndex = 0;
            timeoutView.Dock=DockStyle.Fill;timeoutView.View=View.Details;timeoutView.FullRowSelect=true;timeoutView.GridLines=true;
            timeoutView.Columns.Add("Позывной",145);timeoutView.Columns.Add("Осталось",140);timeoutView.Columns.Add("Повтор после",175);timeoutView.Columns.Add("Причина",418);
            timeoutTab.Controls.Add(timeoutView);
            waitingView.Dock=DockStyle.Fill;
            waitingView.View = View.Details; waitingView.FullRowSelect = true; waitingView.HideSelection = false; waitingView.GridLines = true;
            waitingView.Columns.Add("№",38); waitingView.Columns.Add("Позывной",125); waitingView.Columns.Add("Состояние",230);
            waitingView.Columns.Add("SNR",58); waitingView.Columns.Add("RX, Гц",72); waitingView.Columns.Add("Принят",90); waitingView.Columns.Add("Диапазон / режим",245);
            waitingView.SelectedIndexChanged += delegate {
                if (!refreshingQueue && waitingView.SelectedItems.Count == 1) callEntry.Text = (string)waitingView.SelectedItems[0].Tag;
            };
            waitingView.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Delete) Guard(RemoveSelected); };
            waitTab.Controls.Add(waitingView);
            cqView.Dock=DockStyle.Fill;cqView.View=View.Details;cqView.FullRowSelect=true;cqView.HideSelection=false;cqView.GridLines=true;cqView.MultiSelect=false;
            cqView.Columns.Add("Позывной",110);cqView.Columns.Add("SNR",52);cqView.Columns.Add("RX, Гц",70);cqView.Columns.Add("Сообщение",290);cqView.Columns.Add("Время",85);cqView.Columns.Add("Состояние",235);
            cqView.DoubleClick+=delegate{Guard(CallSelectedCq);};cqTab.Controls.Add(cqView);
            callEntry.SetBounds(26, 572, 157, 28); callEntry.CharacterCasing = CharacterCasing.Upper; Controls.Add(callEntry);waitEditors.Add(callEntry);
            QueueButton("Добавить",190,88,() => engine.AddQueuedCall(callEntry.Text, DateTime.UtcNow));
            QueueButton("Изменить",283,91,() => engine.RenameQueuedCall(SelectedCall(),callEntry.Text));
            QueueButton("Удалить",379,88,RemoveSelected);
            QueueButton("Выше",472,75,() => engine.Waiting.Move(SelectedCall(),-1));
            QueueButton("Ниже",552,75,() => engine.Waiting.Move(SelectedCall(),1));
            QueueButton("Повторить",632,106,() => { foreach (string call in SelectedCalls()) engine.RetryQueuedCall(call); });
            callSelectedCq.Text="Позвать выбранную";callSelectedCq.SetBounds(26, 571, 205, 30);callSelectedCq.Click+=delegate{Guard(CallSelectedCq);};Controls.Add(callSelectedCq);
            retrySelectedCq.Text="Снять отметку попытки";retrySelectedCq.SetBounds(240, 571, 220, 30);retrySelectedCq.Click+=delegate{Guard(()=>engine.Cqs.Retry(SelectedCqCall()));};Controls.Add(retrySelectedCq);
            InitializeOperatorTools();
            listHint.Font = new Font("Segoe UI", 9F); listHint.ForeColor = Slate;
            listHint.SetBounds(26, 607, 908, 21); Controls.Add(listHint);
            journal.Multiline = true; journal.ReadOnly = true; journal.ScrollBars = ScrollBars.Vertical;
            journal.SetBounds(26, 630, 908, 54); journal.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom;
            journal.BackColor = Color.White; journal.ForeColor = Ink; journal.Font = new Font("Segoe UI", 9F); Controls.Add(journal);
            restore.Text = "Восстановить прежние настройки JTDX"; restore.SetBounds(26, 695, 360, 31);
            restore.Anchor = AnchorStyles.Left | AnchorStyles.Bottom; Controls.Add(restore);
            var website = BrandUi.Link(AppBrand.Author + " · " + AppBrand.WebsiteLabel, AppBrand.Website);
            website.Font = new Font("Segoe UI Semibold", 10F); website.SetBounds(405, 691, 154, 24);
            website.Anchor = AnchorStyles.Right | AnchorStyles.Bottom; Controls.Add(website);
            Label foot = new Label { Text = demo ? "ДЕМО без передачи" : "", Font = new Font("Segoe UI",8F) };
            foot.SetBounds(405, 716, 154, 18); foot.Anchor = AnchorStyles.Right | AnchorStyles.Bottom; foot.ForeColor = Slate; Controls.Add(foot);
            reportButton.Text = "Сообщить об ошибке"; reportButton.SetBounds(566, 695, 170, 31);
            reportButton.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            StyleButton(reportButton, Color.White, Ink, Color.FromArgb(201, 213, 224)); Controls.Add(reportButton);
            reportButton.Click += delegate { Guard(ShowBugReport); };
            helpButton.Text = "Как пользоваться"; helpButton.SetBounds(744, 695, 190, 31);
            helpButton.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            StyleButton(helpButton, Color.White, Ink, Color.FromArgb(201, 213, 224)); Controls.Add(helpButton);
            helpButton.Click += delegate { Guard(OpenGuide); };
            engine.Notice = Log;
            engine.RequestCq = SendCq; engine.RequestHalt = SendHalt; engine.RequestReply = SendReply;
            engine.RequestCallerReply = decode => SendReply(decode, true);
            engine.RequestDirectCall = SendDirectCall;
            engine.UseNativeSelection = !demo;
            engine.CanCall = CanCallCountry;
            engine.IsPriorityCall = IsPriorityCountry;
            engine.DxccForCall = call => { var country = dxcc == null ? null : dxcc.Find(call); return country == null ? null : country.Prefix; };
            engine.WorkedCallsOnBand = (band, call) => startupHistory.WorkedCalls(band, call);
            engine.WorkedCallsOnBandMode = (band, mode, call) => startupHistory.WorkedCalls(band, call, mode);
            engine.SessionFinished = SaveSessionSummary;
            open.Click += delegate { Guard(PrepareConnection); };
            start.Click += delegate { Guard(StartAutomation); };
            stop.Click += delegate { HaltAutomation(); };
            restore.Click += delegate { Guard(Restore); };
            FormClosing += ClosingForm;
            tray.Icon = Icon ?? SystemIcons.Application; tray.Text = AppBrand.DisplayName;
            tray.DoubleClick += delegate { closeWhenDone = false; Show(); WindowState = FormWindowState.Normal; Activate(); };
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Открыть", null, delegate { closeWhenDone = false; Show(); Activate(); });
            menu.Items.Add("О программе · " + AppBrand.Author, null, delegate { Guard(ShowAbout); });
            menu.Items.Add("Сообщить об ошибке", null, delegate { Guard(ShowBugReport); });
            menu.Items.Add("СТОП — Halt TX", null, delegate { HaltAutomation(); });
            tray.ContextMenuStrip = menu;
            if (demo) { SetDemoStatus(false, false, ""); open.Enabled = restore.Enabled = false; SeedDemoCqs(); }
            else
            {
                if (engine.Options.ExcludedDxcc.Length > 0 || engine.HasPriorityCountries)
                {
                    try { CountryTable(); }
                    catch (IOException e) { Log(e.Message); }
                    catch (UnauthorizedAccessException e) { Log(e.Message); }
                }
                tail = new LogTail(settings.Profile);
                bridge = new UdpBridge(SettingsManager.ListenPort, "JTDX");
                bridge.Error = message => Log("Передача в журнал: " + message);
                bridge.Received = HandlePacket;
                ConfigureUpstream();
                Log("Нажмите «1. Подготовить», затем запустите JTDX. При работе с LogHX используйте его значок в журнале.");
            }
            timer.Interval = 150; timer.Tick += Tick; UpdateEditors(); UpdateUi(); timer.Start();
            timer.Tick += delegate { CheckUpdatesAutomatically(); };
        }

        void ShowAbout()
        {
            using (var dialog = new AboutDialog(ModuleVersion)) dialog.ShowDialog(this);
        }

        void OpenGuide()
        { OpenGuide(false); }

        internal static void OpenGuide(bool awards)
        {
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "docs", "user-guide.ru.html");
            string target = File.Exists(local)
                ? awards ? new Uri(local).AbsoluteUri + "#hamlog-awards" : local
                : UpdateClient.RepositoryUrl + "/blob/main/USER-GUIDE.md" + (awards ? "#поиск-для-дипломов-hamlog" : "");
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        void ShowBugReport()
        {
            if (reportDialog != null && !reportDialog.IsDisposed)
            {
                if (reportDialog.WindowState == FormWindowState.Minimized) reportDialog.WindowState = FormWindowState.Normal;
                reportDialog.Activate();
                return;
            }
            DateTimeOffset clickedAt = DateTimeOffset.UtcNow;
            reportDialog = demo ? BugReportDialog.CreateDemo(ModuleVersion) :
                new BugReportDialog(new BugReportCapture(clickedAt, ModuleVersion, clientVersion,
                    ReportState(clickedAt.UtcDateTime), settings.StateDirectory, settings.Profile));
            reportDialog.FormClosed += delegate { reportDialog = null; };
            reportDialog.Show(this);
        }

        Dictionary<string, string> ReportState(DateTime now)
        {
            var options = engine.Options;
            var values = new Dictionary<string, string> {
                { "connected", (client != null && (now - lastSeen).TotalSeconds < 35).ToString() },
                { "armed", engine.Armed.ToString() },
                { "monitoring", engine.Monitoring.ToString() },
                { "finishing", engine.Finishing.ToString() },
                { "awaiting73", engine.Awaiting73.ToString() },
                { "waitingCall", engine.WaitingCall ?? "" },
                { "loggedQso", engine.LoggedCount.ToString(CultureInfo.InvariantCulture) },
                { "wait73Periods", options.Wait73Periods.ToString(CultureInfo.InvariantCulture) },
                { "callAttempts", options.CallAttempts.ToString(CultureInfo.InvariantCulture) },
                { "runMinutes", options.RunMinutes.ToString(CultureInfo.InvariantCulture) },
                { "queueMinutes", options.QueueMinutes.ToString(CultureInfo.InvariantCulture) },
                { "cqAttemptsBeforeSearch", options.CqAttemptsBeforeSearch.ToString(CultureInfo.InvariantCulture) },
                { "switchPeriodMinutes", options.SwitchPeriodMinutes.ToString(CultureInfo.InvariantCulture) },
                { "searchEnabled", options.SearchEnabled.ToString() },
                { "priorityAfter73", options.PriorityAfter73.ToString() },
                { "excludedDxcc", String.Join(", ", options.ExcludedDxcc) },
                { "priorityDxcc", String.Join(", ", options.PriorityDxcc) },
                { "monitorDxcc", String.Join(", ", options.MonitorDxcc) },
                { "monitorCalls", String.Join(", ", options.MonitorCalls) },
                { "monitorMasks", String.Join(", ", options.MonitorMasks) },
                { "queue", String.Join("\n", engine.Waiting.Rows.Take(80).Select(row => row.Call + " " + (row.PauseReason ?? ""))) },
            };
            if (engine.Status != null)
            {
                RadioStatus radioStatus = engine.Status;
                values.Add("frequencyHz", radioStatus.Frequency.ToString(CultureInfo.InvariantCulture));
                values.Add("mode", radioStatus.Mode ?? "");
                values.Add("dxCall", radioStatus.DxCall ?? "");
                values.Add("operatorCall", radioStatus.DeCall ?? "");
                values.Add("txFirst", radioStatus.TxFirst.ToString());
                values.Add("txEnabled", radioStatus.TxEnabled.ToString());
                values.Add("transmitting", radioStatus.Transmitting.ToString());
                values.Add("decoding", radioStatus.Decoding.ToString());
            }
            foreach (string key in values.Keys.ToArray())
                if (values[key].Length > 4096) values[key] = values[key].Substring(0, 4096);
            return values;
        }

        void ShowUpdates()
        {
            string directory = demo ? Path.Combine(Path.GetTempPath(), "AutoCQ-demo-updates") : settings.StateDirectory;
            using(var dialog = new UpdateDialog(updateClient, preferences, preferenceStore, directory, jtdxPath, demo,
                () => { engine.Stop("Установка обновления. Автомат выключен.", true); Close(); }, availableUpdate))
                dialog.ShowDialog(this);
            if(!preferences.AutomaticUpdates) nextAutomaticUpdate = DateTime.MaxValue;
            else if(nextAutomaticUpdate == DateTime.MaxValue) nextAutomaticUpdate = DateTime.UtcNow.AddSeconds(3);
        }
        void CheckUpdatesAutomatically()
        {
            if(demo || !preferences.AutomaticUpdates || IsDisposed || DateTime.UtcNow < nextAutomaticUpdate
                || automaticUpdateWorker != null && automaticUpdateWorker.IsBusy) return;
            nextAutomaticUpdate = DateTime.UtcNow.AddDays(1);
            automaticUpdateWorker = new BackgroundWorker();
            automaticUpdateWorker.DoWork += delegate(object sender, DoWorkEventArgs e) { e.Result = updateClient.Check(); };
            automaticUpdateWorker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs e) {
                if(IsDisposed) { automaticUpdateWorker.Dispose(); return; }
                preferences.LastUpdateCheckUtc = DateTime.UtcNow;
                if(e.Error == null)
                {
                    availableUpdate = (AvailableUpdate)e.Result;
                    if(availableUpdate.Version > updateClient.CurrentVersion && preferences.AutomaticUpdates)
                    {
                        updatesButton.Text = "Обновить: " + availableUpdate.Version.ToString(3);
                        StyleButton(updatesButton, Mint, Navy, Mint);
                        if(preferences.NotifiedVersion != availableUpdate.Version.ToString(3))
                        {
                            preferences.NotifiedVersion = availableUpdate.Version.ToString(3);
                            updatesTip.Show("Доступна новая версия AutoCQ. Нажмите, чтобы посмотреть и установить.", updatesButton, 0, updatesButton.Height, 12000);
                        }
                    }
                }
                else DiagnosticEvent("Не удалось проверить обновления. Повторите проверку кнопкой «Обновления».");
                try { preferenceStore.Save(preferences); } catch(IOException) {} catch(UnauthorizedAccessException) {}
                automaticUpdateWorker.Dispose();
            };
            automaticUpdateWorker.RunWorkerAsync();
        }
        void AddNumber(NumericUpDown input, string caption, int x, int y, int width, int maximum, int value)
        {
            var label = new Label { Text = caption, TextAlign = ContentAlignment.MiddleLeft };
            label.SetBounds(x, y, width, 28);
            optionsPanel.Controls.Add(label);
            input.SetBounds(x + width, y + 1, 59, 26);
            input.Minimum = 1; input.Maximum = maximum; input.Value = value;
            input.AccessibleName = caption;
            input.ValueChanged += delegate { ChangeOptions(); };
            optionsPanel.Controls.Add(input);
        }

        static void StyleButton(Button button, Color background, Color foreground, Color border)
        {
            button.FlatStyle = FlatStyle.Flat; button.UseVisualStyleBackColor = false;
            button.BackColor = background; button.ForeColor = foreground;
            button.FlatAppearance.BorderColor = border;
        }

        DxccCountries CountryTable()
        {
            if (dxcc != null) return dxcc;
            if (demo) return dxcc = DemoCountryTable();
            string profileTable = demo ? "" : Path.Combine(settings.Profile, "cty.dat");
            string installedTable = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(jtdxPath), "..", "share", "jtdx", "cty.dat"));
            string table = File.Exists(profileTable) ? profileTable : installedTable;
            if (!File.Exists(table)) throw new FileNotFoundException("Не найден справочник cty.dat JTDX.");
            dxcc = DxccCountries.Load(table);
            return dxcc;
        }

        bool CanCallCountry(string call)
        {
            string[] excluded = engine.Options.ExcludedDxcc;
            if (excluded.Length == 0) return true;
            if (dxcc == null) return false;
            DxccCountry country = dxcc.Find(call);
            return country == null || !excluded.Contains(country.Prefix);
        }

        bool IsPriorityCountry(string call)
        {
            string[] preferred = engine.Options.PriorityDxcc;
            if (preferred.Length == 0 || dxcc == null) return false;
            DxccCountry country = dxcc.Find(call);
            return country != null && preferred.Contains(country.Prefix);
        }

        void EditCountries(string title, bool editPriorities)
        {
            string[] chosen = ChooseCountries(title, editPriorities ? engine.Options.PriorityDxcc : engine.Options.ExcludedDxcc, this);
            if (chosen == null) return;
            if (editPriorities) engine.Options.PriorityDxcc = chosen;
            else engine.Options.ExcludedDxcc = chosen;
            queueDirty = queueUiDirty = cqUiDirty = true;
            SaveOperatorState(); UpdateUi();
        }

        string[] ChooseCountries(string title, string[] prefixes, IWin32Window owner)
        {
            DxccCountry[] countries = CountryTable().Countries.OrderBy(country => country.Name).ToArray();
            var checkedPrefixes = new HashSet<string>(prefixes);
            using (var dialog = new Form())
            using (var search = new TextBox())
            using (var matches = new CheckedListBox())
            {
                dialog.Text = title; dialog.Icon = Icon;
                dialog.Font = Font; dialog.ClientSize = new Size(480, 490);
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MaximizeBox = dialog.MinimizeBox = false;
                var label = new Label { Text = "Поиск по названию DXCC или основному префиксу", AutoSize = true };
                label.Location = new Point(14, 14);
                search.SetBounds(14, 39, 452, 27); search.AccessibleName = label.Text;
                matches.SetBounds(14, 77, 452, 340); matches.CheckOnClick = true;
                matches.IntegralHeight = false;
                DxccCountry[] visible = new DxccCountry[0];
                bool loading = false;
                Action filter = delegate
                {
                    string query = search.Text.Trim();
                    visible = countries.Where(country => country.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                        || country.Prefix.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToArray();
                    loading = true;
                    matches.BeginUpdate();
                    matches.Items.Clear();
                    foreach (DxccCountry country in visible)
                        matches.Items.Add(country.Name + "  ·  " + country.Prefix, checkedPrefixes.Contains(country.Prefix));
                    matches.EndUpdate();
                    loading = false;
                };
                matches.ItemCheck += delegate(object sender, ItemCheckEventArgs args)
                {
                    if (loading) return;
                    if (args.NewValue == CheckState.Checked) checkedPrefixes.Add(visible[args.Index].Prefix);
                    else checkedPrefixes.Remove(visible[args.Index].Prefix);
                };
                search.TextChanged += delegate { filter(); };
                var apply = new Button { Text = "Сохранить", DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel };
                var reset = new Button { Text = "Снять все" };
                apply.SetBounds(248, 439, 105, 33); cancel.SetBounds(361, 439, 105, 33); reset.SetBounds(14, 439, 120, 33);
                reset.Click += delegate { checkedPrefixes.Clear(); filter(); };
                StyleButton(apply, Teal, Color.White, Teal);
                dialog.Controls.AddRange(new Control[] { label, search, matches, apply, cancel, reset });
                dialog.AcceptButton = apply; dialog.CancelButton = cancel;
                filter();
                if (dialog.ShowDialog(owner) != DialogResult.OK) return null;
            }
            return checkedPrefixes.OrderBy(prefix => prefix).ToArray();
        }

        void EditMonitorTargets()
        {
            AutoCqOptions options = engine.Options;
            string[] countries = options.MonitorDxcc;
            using (var dialog = new Form
            {
                Text = "Цели мониторинга", Icon = Icon, Font = Font, ClientSize = new Size(574, 385), AutoScaleMode = AutoScaleMode.Dpi,
                StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false
            })
            {
                var description = new Label { Text = "AutoCQ вызывает цели по CQ, 73, RRR или RR73 из левого окна JTDX." };
                description.SetBounds(16, 15, 542, 24);
                var pick = new Button { Text = "Выбрать DXCC" };
                pick.SetBounds(16, 48, 155, 31);
                var selected = new Label { AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
                selected.SetBounds(183, 48, 375, 31);
                Action showCountries = () => selected.Text = countries.Length == 0 ? "Страны не выбраны" : String.Join(", ", countries);
                pick.Click += delegate
                {
                    try
                    {
                        string[] chosen = ChooseCountries("Страны DXCC для мониторинга", countries, dialog);
                        if (chosen != null) { countries = chosen; showCountries(); }
                    }
                    catch (IOException e) { MessageBox.Show(dialog, e.Message, dialog.Text); }
                    catch (UnauthorizedAccessException e) { MessageBox.Show(dialog, e.Message, dialog.Text); }
                };
                showCountries();
                var callsLabel = new Label { Text = "Точные позывные через пробел, запятую или с новой строки" };
                callsLabel.SetBounds(16, 88, 542, 23);
                var calls = new TextBox { AccessibleName = "Точные позывные", Multiline = true, ScrollBars = ScrollBars.Vertical,
                    CharacterCasing = CharacterCasing.Upper, Text = String.Join(Environment.NewLine, options.MonitorCalls) };
                calls.SetBounds(16, 115, 542, 66);
                var masksLabel = new Label { Text = "Маски  ·  * — любое число знаков, ? — один знак" };
                masksLabel.SetBounds(16, 194, 542, 23);
                var masks = new TextBox { AccessibleName = "Маски позывных", Multiline = true, ScrollBars = ScrollBars.Vertical,
                    CharacterCasing = CharacterCasing.Upper, Text = String.Join(Environment.NewLine, options.MonitorMasks) };
                masks.SetBounds(16, 221, 542, 66);
                var once = new CheckBox { Text = "Одна связь на цель на диапазоне", Checked = options.MonitorOnePerBand };
                once.SetBounds(16, 301, 542, 24);
                var save = new Button { Text = "Сохранить" };
                var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel };
                save.SetBounds(332, 340, 110, 32); cancel.SetBounds(448, 340, 110, 32);
                StyleButton(save, Teal, Color.White, Teal);
                save.Click += delegate
                {
                    char[] separators = { ' ', '\r', '\n', '\t', ',', ';' };
                    try
                    {
                        string[] exact = MonitorTargets.NormalizeCalls(calls.Text.Split(separators, StringSplitOptions.RemoveEmptyEntries));
                        string[] patterns = MonitorTargets.NormalizeMasks(masks.Text.Split(separators, StringSplitOptions.RemoveEmptyEntries));
                        options.MonitorDxcc = countries; options.MonitorCalls = exact; options.MonitorMasks = patterns;
                        options.MonitorOnePerBand = once.Checked;
                        dialog.DialogResult = DialogResult.OK;
                    }
                    catch (InvalidOperationException e) { MessageBox.Show(dialog, e.Message, dialog.Text); }
                };
                dialog.Controls.AddRange(new Control[] { description, pick, selected, callsLabel, calls, masksLabel, masks, once, save, cancel });
                dialog.AcceptButton = save; dialog.CancelButton = cancel;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
            }
            queueDirty = true;
            SaveOperatorState(); UpdateUi();
        }

        void QueueButton(string text, int x, int width, Action action)
        {
            Button button = new Button { Text = text }; button.SetBounds(x, 571, width, 30);
            button.Click += delegate { Guard(action); }; Controls.Add(button);waitEditors.Add(button);
        }
        void UpdateEditors()
        {
            bool cq=lists.SelectedIndex==1;foreach(Control control in waitEditors)control.Visible=lists.SelectedIndex==0;
            callSelectedCq.Visible=retrySelectedCq.Visible=whyCq.Visible=cq;
            UpdateListHint();
        }
        void UpdateListHint()
        {
            listHint.Text = lists.SelectedIndex==2 ? "После пяти минут станцию снова можно вызвать по свежему сообщению. При остановке мониторинга список очищается."
                : engine.Monitoring && lists.SelectedIndex==0 ? "Сначала ответы на ваш позывной, затем поиск целей. Исключения DXCC действуют."
                : engine.Monitoring ? "Можно вручную позвать любую станцию: выберите её слева в JTDX или здесь и нажмите «Позвать выбранную»."
                : lists.SelectedIndex == 1 ? "Свежие CQ, 73, RRR и RR73 из левого окна JTDX. Двойной щелчок выбирает станцию."
                : "Сначала ответы на ваш позывной, затем прямые вызовы из ручной очереди. Исключения DXCC действуют.";
        }
        string SelectedCqCall()
        {
            if(cqView.SelectedItems.Count!=1)throw new InvalidOperationException("Выберите станцию из левого окна JTDX.");
            return (string)cqView.SelectedItems[0].Tag;
        }
        void CallSelectedCq()
        {
            string call=SelectedCqCall();CqStation row=engine.Cqs.Find(call);
            if(!engine.Cqs.Fresh(row,DateTime.UtcNow))throw new InvalidOperationException("В левом окне нет свежего сообщения от этой станции.");
            if(!CanCallCountry(call))throw new InvalidOperationException("Станция входит в исключения DXCC.");
            if(engine.Armed){engine.SelectCq(call,DateTime.UtcNow);return;}
            if(!demo && (client==null || clientVersion!="2.2.160-32A-rc10" || (DateTime.UtcNow-lastSeen).TotalSeconds>35))throw new InvalidOperationException("Нет подключения к JTDX.");
            if(engine.Status!=null && !String.IsNullOrEmpty(engine.Status.DxCall) && engine.Status.TxEnabled)throw new InvalidOperationException("Сначала завершите текущую связь или остановите её кнопкой СТОП.");
            engine.Cqs.Attempt(call,DateTime.UtcNow);SendReply(row.Decode.Copy());Log("Ручной вызов " + call + ". Автомат остаётся выключенным.");
        }
        string[] SelectedCalls()
        {
            string[] calls = waitingView.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag).ToArray();
            if (calls.Length == 0) throw new InvalidOperationException("Выберите строку в листе ожидания.");
            return calls;
        }
        string SelectedCall()
        {
            string[] calls = SelectedCalls();
            if (calls.Length != 1) throw new InvalidOperationException("Выберите одну строку.");
            return calls[0];
        }
        void RemoveSelected() { foreach (string call in SelectedCalls()) engine.Waiting.Remove(call); }
        void ChangeOptions()
        {
            if (applyingProfile) return;
            engine.Options.Wait73Periods = (int)waitPeriods.Value; engine.Options.CallAttempts = (int)callAttempts.Value;
            engine.Options.RunMinutes=(int)runMinutes.Value;engine.Options.CqAttemptsBeforeSearch=(int)cqAttempts.Value;engine.Options.SearchEnabled=searchEnabled.Checked;
            engine.Options.SwitchPeriodMinutes = (int)switchMinutes.Value;
            engine.Options.QueueMinutes = (int)queueMinutes.Value;
            engine.Options.PriorityAfter73 = priorityAfter73.Checked;
            engine.Options.MonitorEnabled = monitor.Checked;
            queueDirty = true; SaveOperatorState();
        }
        void SaveOperatorState()
        {
            if (demo || !queueDirty) return;
            nextOperatorSave = DateTime.UtcNow.AddSeconds(1);
            try { operatorStore.Save(engine.Options,engine.Waiting.Rows); queueDirty = false; }
            catch (IOException e) { nextOperatorSave = DateTime.UtcNow.AddSeconds(30); Log("Не удалось сохранить очередь: " + e.Message); }
            catch (UnauthorizedAccessException e) { nextOperatorSave = DateTime.UtcNow.AddSeconds(30); Log("Не удалось сохранить очередь: " + e.Message); }
        }
        void RefreshQueue()
        {
            string[] selected = waitingView.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag).ToArray();
            refreshingQueue = true; waitingView.BeginUpdate();
            try
            {
                waitingView.Items.Clear(); int n = 0;
                foreach (WaitingStation row in engine.Waiting.Rows)
                {
                    DecodedMessage decode = row.Decode;
                    DateTime heardUtc = row.LastHeardUtc;
                    ulong frequency = row.Frequency;
                    string mode = row.Mode;
                    string state = WaitingState(row);
                    ListViewItem item = new ListViewItem((++n).ToString()); item.Tag = row.Call;
                    item.SubItems.Add(row.Call); item.SubItems.Add(state);
                    item.SubItems.Add(decode == null ? "" : decode.Snr.ToString("+0;-0;0"));
                    item.SubItems.Add(decode == null ? "" : decode.DeltaFrequency.ToString());
                    item.SubItems.Add(heardUtc == DateTime.MinValue ? "" : heardUtc.ToLocalTime().ToString("HH:mm:ss"));
                    item.SubItems.Add(frequency == 0 ? "" : (frequency / 1000000.0).ToString("F6") + " / " + mode);
                    if (!String.IsNullOrEmpty(row.PauseReason)) item.ForeColor = Color.FromArgb(130,80,30);
                    else if (state == "Идёт QSO") item.ForeColor = Color.FromArgb(20,115,50);
                    waitingView.Items.Add(item); if (selected.Contains(row.Call)) item.Selected = true;
                }
            }
            finally { waitingView.EndUpdate(); refreshingQueue = false; queueUiDirty = false; }
        }
        void RefreshCqs()
        {
            string selected=cqView.SelectedItems.Count==1?(string)cqView.SelectedItems[0].Tag:null;
            cqView.BeginUpdate();
            try
            {
                cqView.Items.Clear();DateTime now=DateTime.UtcNow;
                foreach(CqStation row in engine.Cqs.Rows)
                {
                    ListViewItem item=new ListViewItem(row.Call);item.Tag=row.Call;item.SubItems.Add(row.Decode.Snr.ToString("+0;-0;0"));item.SubItems.Add(row.Decode.DeltaFrequency.ToString());
                    item.SubItems.Add(row.Decode.Message);item.SubItems.Add(row.HeardUtc.ToLocalTime().ToString("HH:mm:ss"));
                    bool active=engine.Status!=null && engine.Status.DxCall==row.Call && engine.Status.TxEnabled;
                    string state = active ? "Идёт QSO" : CqState(row, now);
                    item.SubItems.Add(state);
                    if(active)item.ForeColor=Color.FromArgb(20,115,50);else if(!engine.Cqs.Fresh(row,now) || row.Worked)item.ForeColor=Color.Gray;
                    cqView.Items.Add(item);if(row.Call==selected)item.Selected=true;
                }
            }
            finally{cqView.EndUpdate();cqUiDirty=false;nextCqRefresh=DateTime.UtcNow.AddSeconds(1);}
        }
        void RefreshMonitorTimeouts()
        {
            DateTime now=DateTime.UtcNow;
            string selected=timeoutView.SelectedItems.Count==1?timeoutView.SelectedItems[0].Text:null;
            timeoutView.BeginUpdate();
            try
            {
                timeoutView.Items.Clear();
                foreach(CqStation row in engine.Cqs.MonitorTimeouts(now))
                {
                    TimeSpan remaining=TimeSpan.FromSeconds(Math.Ceiling((row.MonitorRetryUntilUtc-now).TotalSeconds));
                    var item=new ListViewItem(row.Call);
                    item.SubItems.Add(String.Format("{0:00}:{1:00}",(int)remaining.TotalMinutes,remaining.Seconds));
                    item.SubItems.Add(row.MonitorRetryUntilUtc.ToLocalTime().ToString("HH:mm:ss"));
                    item.SubItems.Add("Не ответил на вызовы");
                    timeoutView.Items.Add(item);if(row.Call==selected)item.Selected=true;
                }
                timeoutTab.Text="Пауза мониторинга"+(timeoutView.Items.Count==0?"":"  ·  "+timeoutView.Items.Count);
            }
            finally{timeoutView.EndUpdate();nextTimeoutRefresh=now.AddSeconds(1);}
        }
        void Guard(Action action)
        {
            try { action(); }
            catch (Exception e) { Log(e.Message); MessageBox.Show(this, e.Message, "JTDX AutoCQ", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }
        string WaitingState(WaitingStation row)
        {
            if (!CanCallCountry(row.Call)) return "Исключение DXCC";
            if (row.Call == engine.ManualMonitorCall) return "Ручное QSO · мониторинг";
            if (row.Call == engine.IncomingMonitorCall) return "Отвечаю · мониторинг";
            RadioStatus current = engine.Status;
            if (current != null && current.TxEnabled && Callsign.Clean(current.DxCall) == row.Call) return "Идёт QSO";
            string state = engine.Waiting.State(row, engine.Status);
            return state == "Ожидает" && IsPriorityCountry(row.Call) ? "Ожидает · приоритет" : state;
        }

        string CqState(CqStation row, DateTime now)
        {
            if (!CanCallCountry(row.Call)) return "Исключение DXCC";
            if (row.Call == engine.ManualMonitorCall) return "Ручное QSO · мониторинг";
            if (row.Call == engine.SelectedCqCall) return "Выбрана следующей";
            string state = engine.Cqs.State(row, now);
            if (engine.Monitoring)
            {
                if (engine.Cqs.CanTry(row, now, allowWorked: true) && engine.IsMonitorCandidate(row, now)) return "Цель мониторинга";
                return state == "Можно вызвать" ? "Пропущена в мониторинге" : state;
            }
            bool priority = IsPriorityCountry(row.Call);
            if (state == "Можно вызвать" && row.Signoff != null)
                return priority && engine.Options.PriorityAfter73 ? "Приоритет DXCC · " + row.Signoff
                    : "Только ручной вызов";
            return state == "Можно вызвать" && priority ? "Приоритет DXCC" : state;
        }

        void ConfigureUpstream()
        {
            upstream = null; bridge.Upstream = null;
            if (settings.Record == null || String.IsNullOrEmpty(settings.Record.Host) || settings.Record.Port == 0) return;
            IPAddress address = Dns.GetHostAddresses(settings.Record.Host).First(a => a.AddressFamily == AddressFamily.InterNetwork);
            upstream = new IPEndPoint(address, settings.Record.Port);
            bridge.Upstream = upstream;
        }
        void PrepareConnection()
        {
            if (JtdxPatcher.IsRunning()) throw new InvalidOperationException("JTDX уже запущен. Закройте его, нажмите «1. Подготовить», затем запустите JTDX обычным способом. При работе с LogHX используйте его значок в журнале.");
            if (!File.Exists(jtdxPath) || JtdxPatcher.HashFile(jtdxPath) != NativeQueueHash)
            {
                using(var setup = new SetupDialog(jtdxPath))
                {
                    if(setup.ShowDialog(this) != DialogResult.OK) return;
                    jtdxPath = setup.SelectedPath;
                }
            }
            preferences.JtdxPath = jtdxPath; preferenceStore.Save(preferences);
            string version = FileVersionInfo.GetVersionInfo(jtdxPath).FileVersion;
            if (version != "2.2.160-32A-rc10") throw new InvalidOperationException("Эта сборка подготовлена для JTDX 2.2.160-32A-rc10.");
            VerifyNativeQueueSupport();
            SaveOperatorState(); settings.Prepare(engine.Options); ConfigureUpstream(); direction = settings.Direction();
            client = null; clientId = null; clientVersion = null; bridge.ResetClient(); tail.Reset();
            replayRequested = nativeStatusSeen = false; engine.Waiting.Invalidate(); engine.Cqs.Invalidate();
            hadProcess = false;
            Log("Настройки окна и приоритеты Автовыбора сохранены. «Одиночное QSO» выключено.");
            Log("Подключение подготовлено. Запустите JTDX. При работе с LogHX используйте его значок в журнале.");
        }
        void Restore()
        {
            if (JtdxPatcher.IsRunning()) throw new InvalidOperationException("Закройте JTDX перед восстановлением его настроек.");
            settings.Restore(); upstream = null; bridge.Upstream = null; bridge.ResetClient(); client = null; clientId = null;
            replayRequested = nativeStatusSeen = false; engine.Waiting.Invalidate(); engine.Cqs.Invalidate();
            Log("Прежние настройки восстановлены. JTDX снова готов к запуску.");
        }
        void StartRadioSession(AwardCatalog awards)
        {
            AwardSearch search = null;
            if (awards != null)
            {
                if (engine.Status == null) throw new InvalidOperationException("Ожидается состояние JTDX.");
                search = new AwardSearch(awards, Callsign.Clean(engine.Status.DeCall), JtdxAdif.Band(engine.Status.Frequency),
                    engine.Status.Mode, engine.DxccForCall, engine.Status.DeLocator);
                DateTime now = DateTime.UtcNow;
                if (search.Available(now) == 0 && search.NeedsOperatorLocator(now))
                    throw new InvalidOperationException("Для диплома дальних связей укажите свой QTH-локатор в настройках JTDX.");
            }
            if (engine.Options.ExcludedDxcc.Length > 0 || engine.HasPriorityCountries
                || search == null && engine.Options.MonitorEnabled && engine.Options.MonitorDxcc.Length > 0
                || search != null && search.NeedsDxcc)
                CountryTable();
            if (!demo)
            {
                if (client == null || clientId != "JTDX" || clientVersion != "2.2.160-32A-rc10" || (DateTime.UtcNow - lastSeen).TotalSeconds > 35)
                    throw new InvalidOperationException("Нет подтверждённого подключения к JTDX. Нажмите «1. Подготовить», запустите JTDX и подождите до 15 секунд.");
                Process[] instances = Process.GetProcessesByName("jtdx");
                try
                {
                    if (instances.Length != 1 || !String.Equals(RunningExecutable(instances[0].Id), jtdxPath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Запустите единственный экземпляр JTDX из выбранной папки: " + jtdxPath);
                }
                finally
                {
                    foreach (Process instance in instances) instance.Dispose();
                }
                IniFile ini = new IniFile(settings.IniPath);
                VerifyNativeQueueSupport();
                int watchdog;
                if (!Int32.TryParse(ini.Get("Configuration","TxWatchdogTimer","60"),out watchdog) || watchdog < 0 || watchdog > 99)
                    throw new InvalidOperationException("Не удалось прочитать сторожевой таймер JTDX.");
                if (ini.Get("Configuration", "AutoQSOLogging", "false") != "true")
                    throw new InvalidOperationException("Для этой сборки JTDX нужно заново подготовить параметры записи. Закройте JTDX, нажмите «1. Подготовить» и снова запустите JTDX.");
                if (ini.Get("Common", "SingleShotQSO", "true") != "false"
                    || ini.Get("Common", "QuickCall", "false") != "true" || ini.Get("Common", "AcceptUDPReplyMessages", "1") != "3"
                    || ini.Get("Configuration","StrictDirectionalCQ","false") != "true")
                    throw new InvalidOperationException("Не подготовлено управление очередью. Закройте JTDX, нажмите «1. Подготовить» и снова запустите JTDX. «Одиночное QSO» должно быть выключено.");
                foreach (string phase in new[] {"AnswerCQ", "AnswerInCall", "SentRReport", "SentRR7373"})
                {
                    int count = phase == "SentRR7373" ? Math.Min(5,engine.Options.Wait73Periods) : engine.Options.CallAttempts;
                    if (ini.Get("Configuration", "Seq" + phase + "Count", "false") != "true" || ini.Get("Configuration", "Seq" + phase + "CounterValue", "0") != count.ToString())
                        throw new InvalidOperationException("Счётчики JTDX отличаются от выбранных в модуле. Закройте JTDX, повторите «1. Подготовить» и снова запустите JTDX.");
                }
                tail.Reset(); direction = settings.Direction();
            }
            engine.Start(DateTime.UtcNow, search);
            UpdateNativeSelection(DateTime.UtcNow);
            UpdateUi();
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        internal static string RunningExecutable(int processId)
        {
            const uint queryLimitedInformation = 0x1000;
            IntPtr handle = OpenProcess(queryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero) throw new Win32Exception();
            try
            {
                var path = new StringBuilder(32768);
                int size = path.Capacity;
                if (!QueryFullProcessImageName(handle, 0, path, ref size)) throw new Win32Exception();
                return path.ToString();
            }
            finally { CloseHandle(handle); }
        }
        void VerifyNativeQueueSupport()
        {
            if (JtdxPatcher.HashFile(jtdxPath) != NativeQueueHash)
                throw new InvalidOperationException("Для AutoCQ " + ModuleVersion + " закройте JTDX и нажмите «1. Подготовить», чтобы обновить дополнение.");
        }
        void Send(byte[] data)
        { if (!demo && client != null) bridge.SendToClient(data); }
        void SendCq()
        {
            if (demo) { demoPeer = "W1AW"; SetDemoStatus(true, false, "", engine.CqTxFirst); demoStep = 0; demoNext = DateTime.UtcNow.AddSeconds(2); return; }
            byte[] request = QtPacket.TriggerCq(clientId, schema, direction, engine.CqTxFirst, true);
            DiagnosticEvent("TX COMMAND TriggerCQ schema=" + schema + " period=" + engine.CqTxFirst + " endpoint=" + client + " bytes=" + BitConverter.ToString(request));
            Send(request);
        }
        void SendReply(DecodedMessage decode) { SendReply(decode, false); }

        void SendReply(DecodedMessage decode, bool queued)
        {
            if (demo)
            {
                CqStation candidate = queued ? null : engine.Cqs.Rows.FirstOrDefault(row => row.Decode.Message == decode.Message);
                demoPeer = candidate != null ? candidate.Call
                    : Callsign.Clean(decode.Message.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)[1]);
                DemoCall(demoPeer, CallerQueue.ReplyFirst(decode, engine.Status.Mode)); return;
            }
            UpdateNativeSelection(DateTime.UtcNow);
            DiagnosticEvent("TX COMMAND " + (queued ? "QueueReply" : "Reply") + " message=" + decode.Message + " time=" + decode.Milliseconds + " snr=" + decode.Snr + " df=" + decode.DeltaFrequency + " endpoint=" + client);
            Send(queued ? QtPacket.ReplyToCaller(clientId, schema, decode) : QtPacket.ReplyToVisibleCq(clientId, schema, decode));
        }
        void SendDirectCall(string call)
        {
            if (demo) { DemoCall(call, engine.Status.TxFirst); return; }
            UpdateNativeSelection(DateTime.UtcNow);
            DiagnosticEvent("TX COMMAND DirectCall call=" + call + " period=" + engine.Status.TxFirst);
            Send(QtPacket.CallStation(clientId, schema, call));
        }
        void DemoCall(string call, bool txFirst)
        {
            demoPeer = call;
            SetDemoStatus(true, false, call, txFirst);
            demoStep = 0; demoNext = DateTime.UtcNow.AddSeconds(2);
        }
        void SendHalt()
        {
            if (demo)
            {
                if (engine.Status != null)
                {
                    engine.OnLine("Halt Tx triggered at RX: UDP request ", DateTime.UtcNow);
                    SetDemoStatus(false, false, "");
                }
                return;
            }
            try
            {
                if (clientId != null)
                {
                    Send(QtPacket.Halt(clientId, schema));
                    DiagnosticEvent("TX COMMAND HaltTX endpoint=" + client);
                }
                UpdateNativeSelection(DateTime.UtcNow);
            }
            catch (SocketException e) { Log("Команда остановки: " + e.Message); }
        }
        void UpdateNativeSelection(DateTime now)
        {
            if (demo || clientId == null) return;
            bool hold = engine.HoldNativeSelection;
            bool active = engine.Armed;
            bool listen = engine.ListenAfterFinal73;
            bool repeat = engine.RepeatFinal73;
            int repeatMessage = engine.TerminalRepeatMessage;
            string repeatCall = repeatMessage == 0 ? null : engine.WaitingCall;
            if (hold == selectionHeld && active == managedSession && listen == listenAfter73 && repeat == repeatFinal73
                && repeatTerminalMessage == repeatMessage && repeatTerminalCall == repeatCall
                && (!active || now < nextSelectionPacket)) return;
            Send(QtPacket.HoldNativeSelection(clientId, schema, hold, active, listen, repeat, repeatMessage, engine.Status));
            selectionHeld = hold;
            managedSession = active;
            listenAfter73 = listen;
            repeatFinal73 = repeat;
            repeatTerminalMessage = repeatMessage;
            repeatTerminalCall = repeatCall;
            nextSelectionPacket = now.AddMilliseconds(750);
        }
        void HandlePacket(QtPacket packet)
        {
            if (client == null || !client.Equals(bridge.Client))
            {
                engine.Waiting.Invalidate(); engine.Cqs.Invalidate();
                replayRequested = nativeStatusSeen = selectionHeld = managedSession = listenAfter73 = repeatFinal73 = false;
                repeatTerminalMessage = 0;
                repeatTerminalCall = null;
                replayWanted = true;
                nextReplayRequest = DateTime.MinValue;
            }
            client = bridge.Client; clientId = packet.Id; schema = packet.Schema; lastSeen = DateTime.UtcNow;
            if (packet.Type == 0) clientVersion = packet.Version;
            if (packet.Status != null)
            {
                if (!nativeStatusSeen) { nativeStatusSeen = true; replayRequested = false; }
                if (engine.Status == null || engine.Status.Decoding && !packet.Status.Decoding)
                    replayWanted = true;
                if (engine.Status == null || engine.Status.DxCall != packet.Status.DxCall || engine.Status.TxFirst != packet.Status.TxFirst) queueUiDirty = true;
                engine.OnStatus(packet.Status, DateTime.UtcNow);
            }
            if (packet.Decode != null && nativeStatusSeen)
            {
                if (packet.Decode.IsNew) replayWanted = true;
                engine.OnDecode(packet.Decode, DateTime.UtcNow);
            }
            if (packet.Type == 3)
            {
                engine.Cqs.Invalidate();
                replayWanted = true;
            }
            if (!replayRequested && clientVersion == "2.2.160-32A-rc10")
            { replayRequested = true; Send(QtPacket.Replay(clientId,schema)); }
            if (packet.Type == 6)
            { engine.Stop("JTDX закрыт. Автомат выключен.", false); client = null; clientId = null; replayRequested = nativeStatusSeen = false; engine.Waiting.Invalidate(); engine.Cqs.Invalidate(); }
            UpdateNativeSelection(DateTime.UtcNow);
        }
        void Tick(object sender, EventArgs args)
        {
            try
            {
                if (demo) DemoTick();
                else
                {
                    // A user double-click is logged after JTDX sends its status.
                    // Drain the log on both sides of UDP polling so that the
                    // engine sees that explicit choice before validating TX.
                    if (engine.Armed) tail.Read(line => engine.OnLine(line, DateTime.UtcNow));
                    bridge.Poll(); tail.Read(line => engine.OnLine(line, DateTime.UtcNow));
                    bool running = JtdxPatcher.IsRunning();
                    jtdxPresent = running;
                    if (running && settings.Record != null) hadProcess = true;
                    if (!running && hadProcess)
                    {
                        hadProcess = false; engine.Stop("JTDX закрыт. Автомат выключен.", false);
                        settings.Restore(); upstream = null; bridge.Upstream = null; bridge.ResetClient(); client = null; clientId = null;
                        replayRequested = nativeStatusSeen = false; engine.Waiting.Invalidate(); engine.Cqs.Invalidate();
                        Log("Прежние настройки JTDX восстановлены.");
                        if (closeWhenDone) { closeWhenDone = false; Close(); return; }
                    }
                }
                DateTime now = DateTime.UtcNow;
                if (!demo && replayWanted && clientId != null && nativeStatusSeen && now >= nextReplayRequest)
                {
                    if (engine.Status == null || !engine.Status.Decoding)
                    {
                        replayWanted = false;
                        nextReplayRequest = now.AddMilliseconds(500);
                        engine.Cqs.Invalidate();
                        Send(QtPacket.Replay(clientId, schema));
                        pendingReplayUntil = now.AddMilliseconds(130);
                    }
                }
                engine.WaitForVisibleCqsUntil = pendingReplayUntil;
                engine.Tick(DateTime.UtcNow, demo || (client != null && (DateTime.UtcNow - lastSeen).TotalSeconds < 35));
                UpdateNativeSelection(now);
                if (DateTime.UtcNow >= nextOperatorSave) SaveOperatorState();
                UpdateUi();
                WriteDiagnostic();
            }
            catch (Exception e) { engine.Stop("Ошибка: " + e.Message, true); UpdateUi(); }
        }
        void UpdateUi()
        {
            start.Enabled = !startingWithHistory && !engine.Armed && (demo || (client != null && clientVersion == "2.2.160-32A-rc10"));
            start.BackColor = start.Enabled ? Teal : Color.FromArgb(226, 235, 235);
            start.FlatAppearance.BorderColor = start.Enabled ? Teal : Color.FromArgb(199, 213, 215);
            waitPeriods.Enabled = callAttempts.Enabled = !engine.Armed && (demo || !jtdxPresent);
            runMinutes.Enabled = cqAttempts.Enabled = searchEnabled.Enabled = priorityAfter73.Enabled =
                switchMinutes.Enabled = queueMinutes.Enabled = !engine.Armed;
            exclusions.Enabled = priorities.Enabled = monitor.Enabled = monitorSetup.Enabled = !engine.Armed;
            start.Text = engine.Awards != null ? "Поиск для дипломов"
                : monitor.Checked ? "2. Включить мониторинг" : "2. Включить автомат";
            int targetCount = engine.Options.MonitorDxcc.Length + engine.Options.MonitorCalls.Length + engine.Options.MonitorMasks.Length;
            monitorSetup.Text = targetCount == 0 ? "Цели мониторинга" : "Цели мониторинга  ·  " + targetCount;
            callSelectedCq.Enabled = !engine.Finishing;
            UpdateListHint();
            exclusions.Text = engine.Options.ExcludedDxcc.Length == 0 ? "Исключения DXCC"
                : "Исключения DXCC  ·  " + engine.Options.ExcludedDxcc.Length;
            priorities.Text = engine.Options.PriorityDxcc.Length == 0 ? "Приоритетные DXCC"
                : "Приоритетные DXCC  ·  " + engine.Options.PriorityDxcc.Length;
            if (!demo) { open.Enabled = !jtdxPresent && !engine.Armed; restore.Enabled = !jtdxPresent && !engine.Armed; }
            optionsHint.Text = engine.Armed ? "Параметры сеанса можно изменить после его завершения."
                : !demo && jtdxPresent ? "Для изменения числа повторов 73 и вызовов закройте JTDX. Остальные поля доступны."
                : "Задайте число повторов 73 и вызовов перед подготовкой JTDX.";
            status.ForeColor = engine.Armed ? Teal : Slate;
            if (engine.Awaiting73)
                status.Text = "Ожидаю 73 от " + engine.WaitingCall + ". Повторов без ответа — "
                    + engine.SilentTerminalRepeats + " из " + engine.Options.Wait73Periods + ".";
            bool connected = demo || client != null && (DateTime.UtcNow - lastSeen).TotalSeconds < 35;
            if (connected && engine.Status != null)
            {
                RadioStatus s = engine.Status;
                string state = s.Transmitting ? "передача" : s.TxEnabled ? "TX разрешён" : "приём";
                string independent = !engine.Armed && (s.TxEnabled || s.Transmitting) ? " (самостоятельно)" : "";
                string period = RadioStatus.FormatTxPeriod(s.Mode, s.TxFirst);
                radio.Text = String.Format("{2} МГц    {3}    TX {5}    ·    {0}{1}    ·    {4}", state, independent,
                    (s.Frequency / 1000000.0).ToString("F6"), s.Mode,
                    String.IsNullOrEmpty(s.DxCall) ? "DX —" : "DX " + s.DxCall, period);
                radio.ForeColor = s.Transmitting ? Amber : Color.White;
            }
            else
            {
                radio.Text = "JTDX не подключён";
                radio.ForeColor = counts.ForeColor;
            }
            counts.Text = "QSO в журнале  " + engine.LoggedCount + "    ·    73 получено  " + engine.ConfirmedCount + "    ·    Без 73  " + engine.Missing73Count
                + (engine.Awards != null ? "    ·    Дипломов в поиске  " + engine.Awards.Available(DateTime.UtcNow)
                    : engine.Monitoring ? "    ·    Целей мониторинга  " + targetCount
                    : "    ·    Очередь  " + engine.Waiting.Rows.Count + "    ·    CQ без ответа  " + engine.UnansweredCqs + "/" + engine.Options.CqAttemptsBeforeSearch);
            if(engine.Finishing)
            {sessionInfo.Text="Завершаю QSO и очередь";sessionInfo.ForeColor=Amber;}
            else if(engine.Armed)
            {
                TimeSpan remaining=engine.SessionDeadline-DateTime.UtcNow;if(remaining<TimeSpan.Zero)remaining=TimeSpan.Zero;
                sessionInfo.Text=String.Format("{0} {1:00}:{2:00}:{3:00}", engine.Awards != null ? "Дипломы" : engine.Monitoring ? "Мониторинг" : "Осталось",
                    (int)remaining.TotalHours,remaining.Minutes,remaining.Seconds);sessionInfo.ForeColor=Mint;
            }
            else{sessionInfo.Text="AutoCQ выключен";sessionInfo.ForeColor=counts.ForeColor;}
            if (queueUiDirty) RefreshQueue();
            if(cqUiDirty || DateTime.UtcNow>=nextCqRefresh)RefreshCqs();
            if(DateTime.UtcNow>=nextTimeoutRefresh)RefreshMonitorTimeouts();
        }
        void Log(string message)
        {
            lastNotice = message;
            status.Text = message;
            if (journal.TextLength > 30000) journal.Text = journal.Text.Substring(journal.TextLength - 15000);
            journal.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
            DiagnosticEvent(message);
        }
        void DiagnosticEvent(string message)
        {
            if (demo || settings == null) return;
            try
            {
                Directory.CreateDirectory(settings.StateDirectory);
                File.AppendAllText(Path.Combine(settings.StateDirectory, "runtime.log"), DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        void WriteDiagnostic()
        {
            if (demo || settings == null || DateTime.UtcNow < nextDiagnosticWrite) return;
            nextDiagnosticWrite = DateTime.UtcNow.AddSeconds(1);
            try
            {
                Directory.CreateDirectory(settings.StateDirectory);
                string path = Path.Combine(settings.StateDirectory, "status.xml"), temp = path + ".tmp";
                using (XmlWriter x = XmlWriter.Create(temp, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
                {
                    x.WriteStartElement("AutoCQ");
                    x.WriteElementString("Version", ModuleVersion + ".0");
                    x.WriteElementString("UpdatedUtc", DateTime.UtcNow.ToString("O"));
                    x.WriteElementString("Connected", (client != null && (DateTime.UtcNow - lastSeen).TotalSeconds < 35).ToString());
                    x.WriteElementString("JtdxVersion", clientVersion ?? "");
                    x.WriteElementString("Armed", engine.Armed.ToString());
                    x.WriteElementString("Monitoring", engine.Monitoring.ToString());
                    x.WriteElementString("ManualMonitorCall", engine.ManualMonitorCall);
                    x.WriteElementString("IncomingMonitorCall", engine.IncomingMonitorCall);
                    x.WriteElementString("Pending", engine.Pending.ToString());
                    x.WriteElementString("LoggedQsoCount", engine.LoggedCount.ToString());
                    x.WriteElementString("ConfirmedQsoCount", engine.ConfirmedCount.ToString());
                    x.WriteElementString("Missing73Count", engine.Missing73Count.ToString());
                    x.WriteElementString("Awaiting73", engine.Awaiting73.ToString());
                    x.WriteElementString("ListenAfterFinal73", engine.ListenAfterFinal73.ToString());
                    x.WriteElementString("RepeatFinal73", engine.RepeatFinal73.ToString());
                    x.WriteElementString("WaitingCall", engine.WaitingCall);
                    x.WriteElementString("ConfirmationDeadlineUtc", engine.Awaiting73 ? engine.ConfirmationDeadline.ToString("O") : "");
                    x.WriteElementString("CqCommandCount", engine.Resumes.ToString());
                    x.WriteElementString("ReplyCommandCount", engine.ReplyCommands.ToString());
                    x.WriteElementString("Wait73Periods", engine.Options.Wait73Periods.ToString());
                    x.WriteElementString("CallAttempts", engine.Options.CallAttempts.ToString());
                    x.WriteElementString("RunMinutes",engine.Options.RunMinutes.ToString());
                    x.WriteElementString("CqAttemptsBeforeSearch",engine.Options.CqAttemptsBeforeSearch.ToString());
                    x.WriteElementString("SwitchPeriodMinutes", engine.Options.SwitchPeriodMinutes.ToString());
                    x.WriteElementString("QueueMinutes", engine.Options.QueueMinutes.ToString());
                    x.WriteElementString("ExcludedDxcc", String.Join(",", engine.Options.ExcludedDxcc));
                    x.WriteElementString("PriorityDxcc", String.Join(",", engine.Options.PriorityDxcc));
                    x.WriteElementString("SearchEnabled",engine.Options.SearchEnabled.ToString());
                    x.WriteElementString("PriorityAfter73",engine.Options.PriorityAfter73.ToString());
                    x.WriteElementString("Searching",engine.Searching.ToString());
                    x.WriteElementString("Finishing",engine.Finishing.ToString());
                    x.WriteElementString("UnansweredCqs",engine.UnansweredCqs.ToString());
                    x.WriteElementString("SelectedCqCall",engine.SelectedCqCall);
                    x.WriteElementString("SessionDeadlineUtc", engine.Armed && engine.SessionDeadline != DateTime.MaxValue ? engine.SessionDeadline.ToString("O") : "");
                    x.WriteStartElement("WaitingList");
                    foreach (WaitingStation row in engine.Waiting.Rows)
                    {
                        x.WriteStartElement("Station"); x.WriteElementString("Call",row.Call); x.WriteElementString("State",WaitingState(row));
                        x.WriteElementString("QueuedUtc", row.QueuedUtc.ToString("O"));
                        x.WriteElementString("LastHeardUtc",row.LastHeardUtc.ToString("O")); x.WriteEndElement();
                    }
                    x.WriteEndElement();
                    x.WriteStartElement("HeardCQ");
                    foreach(CqStation row in engine.Cqs.Rows)
                    {
                        x.WriteStartElement("Station");x.WriteElementString("Call",row.Call);x.WriteElementString("State",CqState(row,DateTime.UtcNow));
                        x.WriteElementString("HeardUtc",row.HeardUtc.ToString("O"));x.WriteEndElement();
                    }
                    x.WriteEndElement();
                    x.WriteStartElement("MonitorTimeouts");
                    foreach(CqStation row in engine.Cqs.MonitorTimeouts(DateTime.UtcNow))
                    {
                        x.WriteStartElement("Station");x.WriteElementString("Call",row.Call);
                        x.WriteElementString("RetryUtc",row.MonitorRetryUntilUtc.ToString("O"));x.WriteEndElement();
                    }
                    x.WriteEndElement();
                    x.WriteElementString("Notice", lastNotice);
                    if (engine.Status != null)
                    {
                        RadioStatus s = engine.Status;
                        x.WriteElementString("FrequencyHz", s.Frequency.ToString());
                        x.WriteElementString("Mode", s.Mode);
                        x.WriteElementString("DxCall", s.DxCall ?? "");
                        x.WriteElementString("TxEnabled", s.TxEnabled.ToString());
                        x.WriteElementString("Transmitting", s.Transmitting.ToString());
                        x.WriteElementString("Decoding", s.Decoding.ToString());
                        x.WriteElementString("TxFirst", s.TxFirst.ToString());
                    }
                    x.WriteEndElement();
                }
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        void SetDemoStatus(bool enabled, bool transmitting, string call, bool? period = null)
        {
            engine.OnStatus(new RadioStatus { Frequency = 14074000, Mode = "FT8", DxCall = call, DeCall = "K1ABC",
                TxEnabled = enabled, Transmitting = transmitting, TxFirst = period ?? (engine.Status == null || engine.Status.TxFirst) }, DateTime.UtcNow);
        }
        void SeedDemoCqs()
        {
            DateTime now=DateTime.UtcNow,t=now.AddSeconds(-30);t=t.Date.AddHours(t.Hour).AddMinutes(t.Minute).AddSeconds((t.Second/30)*30+15);
            foreach(string call in new[]{"W4ABC","W5ABC","W6ABC"})
                engine.OnDecode(new DecodedMessage{IsNew=true,Milliseconds=(uint)t.TimeOfDay.TotalMilliseconds,Snr=-8,DeltaTime=0.1,DeltaFrequency=1750,Mode="~",Message="CQ "+call+" FN20"},now);
        }
        void DemoTick()
        {
            if (!engine.Status.TxEnabled || DateTime.UtcNow < demoNext) return;
            demoNext = DateTime.UtcNow.AddSeconds(2); demoStep++;
            if (demoStep == 1) { SetDemoStatus(true, true, demoPeer); Log("ДЕМО: проводится QSO с " + demoPeer + "."); }
            if (demoStep == 2)
            {
                engine.OnLine(DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + ".000(0) Transmitting 14.074 MHz + 1500Hz FT8: " + demoPeer + " K1ABC RR73", DateTime.UtcNow);
                engine.OnLine("QSO logged: " + demoPeer, DateTime.UtcNow); SetDemoStatus(true, false, demoPeer);
                if (!demoSeeded)
                {
                    demoSeeded = true; DateTime t = DateTime.UtcNow.AddSeconds(-30);
                    t = t.Date.AddHours(t.Hour).AddMinutes(t.Minute).AddSeconds((t.Second/30)*30+15);
                    foreach (string call in new[] {"W2ABC", "W3ABC"})
                        engine.OnDecode(new DecodedMessage { IsNew = true, Milliseconds = (uint)t.TimeOfDay.TotalMilliseconds, Snr = -11, DeltaTime = 0.2,
                            DeltaFrequency = 1500, Mode = "~", Message = "K1ABC " + call + " FN20" },DateTime.UtcNow);
                }
            }
            if (demoStep == 3)
            {
                engine.OnLine(DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + " -10 0.1 1500 ~ K1ABC " + demoPeer + " 73", DateTime.UtcNow);
                engine.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Singleshot 14.074 MHz", DateTime.UtcNow);
                SetDemoStatus(false, false, "");
            }
        }
        void ClosingForm(object sender, FormClosingEventArgs e)
        {
            engine.Stop("Автомат выключен.", true);
            SaveOperatorState();
            if (!demo && JtdxPatcher.IsRunning() && settings.Record != null)
            {
                e.Cancel = true; closeWhenDone = true; Hide(); tray.Visible = true;
                tray.ShowBalloonTip(5000, "Автомат остановлен", "Модуль поддерживает подключение журнала и закроется после закрытия JTDX.", ToolTipIcon.Info);
                return;
            }
            if (!demo) { settings.Restore(); bridge.Dispose(); }
            timer.Stop(); tray.Dispose(); updatesTip.Dispose();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool acquired;
            using (Mutex mutex = new Mutex(true, "Local\\JTDX-AutoCQ-2247", out acquired))
            {
                if (!acquired) { MessageBox.Show("Модуль JTDX AutoCQ уже запущен."); return; }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                if (args.Contains("--restore-settings"))
                {
                    try
                    {
                        if (JtdxPatcher.IsRunning()) throw new InvalidOperationException("Сначала закройте JTDX.");
                        new SettingsManager().Restore(); MessageBox.Show("Прежние настройки восстановлены.", "JTDX AutoCQ");
                    }
                    catch (Exception e) { MessageBox.Show(e.Message, "JTDX AutoCQ"); }
                    return;
                }
                bool demo = args.Contains("--demo") || Path.GetFileNameWithoutExtension(Application.ExecutablePath).EndsWith("-Demo", StringComparison.OrdinalIgnoreCase);
                try
                {
                    int pathArg = Array.IndexOf(args, "--jtdx");
                    if (pathArg >= 0 && (pathArg + 1 == args.Length || args[pathArg + 1].StartsWith("--", StringComparison.Ordinal)))
                        throw new ArgumentException("После --jtdx укажите путь к файлу jtdx.exe.");
                    string jtdxPath = pathArg < 0 ? null : args[pathArg + 1];
                    Application.Run(new MainForm(demo, jtdxPath));
                }
                catch (Exception e) { MessageBox.Show(e.Message, "JTDX AutoCQ", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }
    }
}
