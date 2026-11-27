using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;

namespace JtdxAutoCq
{
    public sealed class RadioStatus
    {
        public ulong Frequency;
        public string Mode, DxCall, DeCall, DeLocator;
        public bool TxEnabled, Transmitting, Decoding, Watchdog, TxFirst;

        internal static string FormatTxPeriod(string mode, bool txFirst)
        {
            if (mode == "FT8") return txFirst ? "00/30" : "15/45";
            if (mode == "FT4") return txFirst ? "0/15/30/45" : "7,5/22,5/37,5/52,5";
            return "—";
        }
    }

    public sealed class QtPacket
    {
        public const uint Magic = 0xadbccbda;
        public uint Schema, Type;
        public string Id, Version;
        public RadioStatus Status;
        public DecodedMessage Decode;

        public static QtPacket Parse(byte[] data)
        {
            using (QtReader r = new QtReader(data))
            {
                if (r.U32() != Magic) throw new InvalidDataException("Неверный пакет");
                QtPacket p = new QtPacket();
                p.Schema = r.U32(); p.Type = r.U32(); p.Id = r.Text();
                if (p.Schema < 2 || p.Schema > 3) throw new InvalidDataException("Версия протокола");
                if (p.Type == 0) { r.U32(); p.Version = r.Text(); }
                if (p.Type == 1)
                {
                    RadioStatus s = new RadioStatus();
                    s.Frequency = r.U64(); s.Mode = r.Text(); s.DxCall = r.Text();
                    r.Text(); r.Text();
                    s.TxEnabled = r.Bool(); s.Transmitting = r.Bool(); s.Decoding = r.Bool();
                    r.U32(); r.U32(); s.DeCall = r.Text(); s.DeLocator = AwardLocator.Full(r.Text()); r.Text();
                    s.Watchdog = r.Bool(); r.Text(); r.Bool(); s.TxFirst = r.Bool();
                    p.Status = s;
                }
                if (p.Type == 2)
                {
                    DecodedMessage d = new DecodedMessage();
                    d.IsNew = r.Bool(); d.Milliseconds = r.U32(); d.Snr = unchecked((int)r.U32());
                    d.DeltaTime = r.Double(); d.DeltaFrequency = r.U32(); d.Mode = r.Text(); d.Message = r.Text();
                    d.LowConfidence = r.Bool(); d.OffAir = r.Bool();
                    if (d.Milliseconds >= 86400000 || Double.IsNaN(d.DeltaTime) || Double.IsInfinity(d.DeltaTime)) throw new InvalidDataException("Неверное декодирование.");
                    p.Decode = d;
                }
                return p;
            }
        }

        public static byte[] TriggerCq(string id, uint schema, string direction, bool txFirst, bool send)
        {
            using (QtWriter w = new QtWriter(51, id, schema))
            { w.Text(direction); w.Bool(txFirst); w.Bool(send); return w.Bytes(); }
        }
        public static byte[] Halt(string id, uint schema)
        {
            using (QtWriter w = new QtWriter(8, id, schema))
            { w.Bool(false); return w.Bytes(); }
        }
        public static byte[] Reply(string id, uint schema, DecodedMessage decode)
        { return WriteReply(id, schema, decode, 0); }

        public static byte[] ReplyToCaller(string id, uint schema, DecodedMessage decode)
        { return WriteReply(id, schema, decode, 0xa8); }

        public static byte[] ReplyToVisibleCq(string id, uint schema, DecodedMessage decode)
        { return WriteReply(id, schema, decode, 0xa9); }

        public static byte[] CallStation(string id, uint schema, string call)
        {
            return WriteReply(id, schema, new DecodedMessage { Message = Callsign.RequireDirect(call) }, 0xaa);
        }

        static byte[] WriteReply(string id, uint schema, DecodedMessage decode, byte marker)
        {
            using (QtWriter w = new QtWriter(marker == 0 ? 4u : 52u, id, schema))
            {
                w.U32(decode.Milliseconds); w.U32(unchecked((uint)decode.Snr)); w.Double(decode.DeltaTime);
                w.U32(decode.DeltaFrequency); w.Text(decode.Mode); w.Text(decode.Message);
                w.Bool(decode.LowConfidence); w.Byte(marker); return w.Bytes();
            }
        }
        public static byte[] HoldNativeSelection(string id, uint schema, bool hold, bool managedSession = false,
            bool listenAfter73 = false, bool repeatFinal73 = false, int repeatMessage = 0, RadioStatus radio = null,
            string holdRetryForCall = null)
        {
            using (QtWriter w = new QtWriter(53, id, schema))
            {
                int flags = (hold ? 1 : 0) | (managedSession ? 2 : 0);
                if (managedSession && listenAfter73) flags |= 8 | (repeatFinal73 ? 4 : 0);
                bool holdRetry = managedSession && !listenAfter73 && holdRetryForCall != null;
                string call = null;
                if (radio != null && (radio.Mode == "FT8" || radio.Mode == "FT4")
                    && (holdRetry || (flags & 4) != 0 && (repeatMessage == 4 || repeatMessage == 5)))
                    call = Callsign.Parse(holdRetry ? holdRetryForCall : radio.DxCall);
                bool scoped = call != null && call.Length <= 24;
                if (holdRetry && scoped) flags |= 16;
                w.Byte((byte)flags);
                if (scoped)
                {
                    w.Byte(holdRetry ? (byte)0 : (byte)repeatMessage);
                    w.Byte((byte)radio.Mode[2]);
                    w.U64(radio.Frequency);
                    foreach (char letter in call) w.Byte((byte)letter);
                }
                return w.Bytes();
            }
        }
        public static byte[] Replay(string id, uint schema)
        { using (QtWriter w = new QtWriter(7,id,schema)) return w.Bytes(); }
    }

    public sealed class QtReader : IDisposable
    {
        readonly MemoryStream stream;
        public QtReader(byte[] data) { stream = new MemoryStream(data, false); }
        public byte Byte()
        { int x = stream.ReadByte(); if (x < 0) throw new EndOfStreamException(); return (byte)x; }
        public uint U32() { return ((uint)Byte() << 24) | ((uint)Byte() << 16) | ((uint)Byte() << 8) | Byte(); }
        public ulong U64() { return ((ulong)U32() << 32) | U32(); }
        public double Double() { return BitConverter.Int64BitsToDouble(unchecked((long)U64())); }
        public bool Bool() { byte b = Byte(); if (b > 1) throw new InvalidDataException("bool"); return b != 0; }
        public string Text()
        {
            uint n = U32(); if (n == uint.MaxValue) return "";
            if (n > 65507 || n > stream.Length - stream.Position) throw new InvalidDataException("Строка");
            byte[] b = new byte[n]; if (stream.Read(b, 0, b.Length) != b.Length) throw new EndOfStreamException();
            return new UTF8Encoding(false, true).GetString(b);
        }
        public void Dispose() { stream.Dispose(); }
    }

    public sealed class QtWriter : IDisposable
    {
        readonly MemoryStream stream = new MemoryStream();
        public QtWriter(uint type, string id, uint schema)
        { U32(QtPacket.Magic); U32(schema); U32(type); Text(id); }
        public void U32(uint n)
        { stream.WriteByte((byte)(n >> 24)); stream.WriteByte((byte)(n >> 16)); stream.WriteByte((byte)(n >> 8)); stream.WriteByte((byte)n); }
        public void U64(ulong n) { U32((uint)(n >> 32)); U32((uint)n); }
        public void Double(double n) { U64(unchecked((ulong)BitConverter.DoubleToInt64Bits(n))); }
        public void Byte(byte b) { stream.WriteByte(b); }
        public void Bool(bool b) { stream.WriteByte(b ? (byte)1 : (byte)0); }
        public void Text(string s)
        { byte[] b = Encoding.UTF8.GetBytes(s ?? ""); U32((uint)b.Length); stream.Write(b, 0, b.Length); }
        public byte[] Bytes() { return stream.ToArray(); }
        public void Dispose() { stream.Dispose(); }
    }

    // Only a recognised JTDX automatic stop may schedule another CQ.
    // In particular, an arbitrary falling Enable Tx edge never schedules a restart.
    public sealed class AutoCqEngine
    {
        public bool Armed { get; private set; }
        public bool Monitoring { get { return Armed && (monitorTargets != null || Awards != null || Expeditions != null); } }
        internal AwardSearch Awards { get; private set; }
        internal ExpeditionSearch Expeditions { get; private set; }
        public string ManualMonitorCall { get { return manualMonitorCall ?? ""; } }
        public string IncomingMonitorCall { get { return incomingMonitorCall ?? ""; } }
        public RadioStatus Status { get; private set; }
        public int Resumes { get; private set; }
        public int LoggedCount { get; private set; }
        public int ConfirmedCount { get; private set; }
        public int Missing73Count { get; private set; }
        public int ReplyCommands { get; private set; }
        public readonly CallerQueue Waiting = new CallerQueue();
        public readonly HeardCqList Cqs = new HeardCqList();
        public AutoCqOptions Options = new AutoCqOptions();
        public DateTime SessionDeadline { get; private set; }
        public bool CqTxFirst { get { return replyTarget == null ? (expectedPeriod ?? sessionPeriod) : sessionPeriod; } }
        public int UnansweredCqs { get { return cqCount; } }
        public bool Searching { get { return hunting; } }
        public bool Finishing { get { return finishing; } }
        public bool HasPriorityCountries { get { return Options.PriorityDxcc != null && Options.PriorityDxcc.Length > 0; } }
        public string SelectedCqCall { get { return manualCq ?? ""; } }
        public string WaitingCall { get { return waitingCall ?? ""; } }
        public bool Awaiting73 { get { return waitingCall != null && !received73; } }
        public int SilentTerminalRepeats { get { return silentTerminalRepeats; } }
        public DateTime ConfirmationDeadline { get { return waitUntil; } }
        public Action<string> Notice = delegate { };
        internal Action<string> Diagnostic = delegate { };
        public Action<OperatingSession> SessionFinished = delegate { };
        public OperatingSession Session { get; private set; }
        public Action RequestCq = delegate { };
        public Action RequestHalt = delegate { };
        public Action<DecodedMessage> RequestReply = delegate { };
        public Action<DecodedMessage> RequestCallerReply;
        public Action<string> RequestDirectCall = delegate { };
        public bool UseNativeSelection { get; set; }
        public Predicate<string> CanCall = delegate { return true; };
        public Predicate<string> IsPriorityCall = delegate { return false; };
        public Func<string, string> DxccForCall = delegate { return null; };
        public Func<string, string, IEnumerable<string>> WorkedCallsOnBand = delegate { return new string[0]; };
        public Func<string, string, string, IEnumerable<string>> WorkedCallsOnBandMode = delegate { return new string[0]; };
        public DateTime WaitForVisibleCqsUntil { private get; set; }
        bool CountryExclusionsActive { get { return Options.ExcludedDxcc != null && Options.ExcludedDxcc.Length > 0; } }
        bool CallerRulesActive { get { return CountryExclusionsActive || HasPriorityCountries || workedCallers.Count > 0; } }
        public bool RepeatFinal73 { get { return Armed && terminalRepeatSlot.HasValue && (Awaiting73 || reverseNeedsReply) && CanRepeatTerminal; } }
        public bool ListenAfterFinal73 { get { return Armed && lastTerminalSlot.HasValue; } }
        bool CanRepeatTerminal
        {
            get { return lastTerminalSlot.HasValue && Status != null && Status.TxEnabled && CleanCall(Status.DxCall) == waitingCall; }
        }
        internal int TerminalRepeatMessage
        {
            get
            {
                if (!RepeatFinal73) return 0;
                return sentTerminalAcknowledgement ? 4 : 5;
            }
        }
        public bool HoldNativeSelection
        {
            get
            {
                return Armed && (Monitoring || CallerRulesActive || NextManual() != null || finishing || Awaiting73 || stoppingRetry != null
                    || terminalHaltRequested || periodSwitchHalt || activeSearchCall != null || ReverseExchangePending
                    || selectedQueuedCall != null || attemptCall != null && !attemptAnswered
                    || commandOutstanding && replyTarget != null);
            }
        }
        bool InitialCallLimitReached
        {
            get { return lastInitialSlot >= 0 && initialTransmissions >= sessionCallAttempts && !attemptAnswered && waitingCall == null; }
        }
        // Arm the native PTT gate during the last allowed TX, ahead of the next TX boundary.
        public string HoldRetryForCall { get { return Armed && InitialCallLimitReached ? attemptCall : null; } }
        ulong sessionFrequency;
        string sessionMode, loggedCall, lastContact;
        bool sessionPeriod, pending, commandOutstanding, everEnabled;
        static readonly TimeSpan CommandConfirmationTimeout = TimeSpan.FromSeconds(6);
        DateTime due, expires, loggedAt, commandIssuedAt, commandDeadline;
        string expectedReplyMessage;
        string waitingCall, pendingOwner, attemptCall, lastRemoteFinalCall, resumedAcknowledgementCall;
        string stoppingRetry;
        bool received73, missing73Recorded, allowClearDx, attemptAnswered;
        int initialTransmissions;
        DateTime waitUntil, lastRemoteFinalAt;
        DateTime? terminalRadioTime, firstTerminalSlot, lastTerminalSlot, receivedFinalAt, terminalRepeatSlot;
        DateTime? lastTerminalAttemptSlot;
        bool terminalHaltRequested, sentTerminalAcknowledgement;
        bool terminalReportReceived, reportReplacedTerminalRepeat;
        int silentTerminalRepeats, silentTerminalAttempts;
        const int MaxConsecutiveMissedTerminalRepeats = 2;
        int missedTerminalRepeats;
        bool reverse73, reverseNeedsReply, repeatSentInSilentWindow, lastRemoteFinalWas73;
        bool ReverseExchangePending
        {
            get { return reverse73 && (reverseNeedsReply || !pending); }
        }
        DateTime? unexplainedStop;
        string replyTarget, selectedQueuedCall, replyPreviousCall;
        bool replyDuringCq;
        int sessionWaitPeriods, sessionCallAttempts, sessionSwitchMinutes;
        DateTime lastResponseAt, lastDecodeFinished;
        long lastInitialSlot = -1;
        long lastAttemptReplySlot = -1;
        bool callLimitExceeded, yieldingToCaller;
        readonly Dictionary<string, DateTime> sentAcknowledgedReports = new Dictionary<string, DateTime>();
        readonly HashSet<string> workedCallers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hunting, cqThreshold, cqTxSeen, cqTxEnded, cqHaltExpected, cqListening, completedCurrent;
        bool handoffHaltAcknowledged, periodSwitchHalt;
        bool? expectedPeriod;
        int cqCount;
        long lastCqSlot = -1;
        DateTime lastCqTime;
        string manualCq, activeSearchCall;
        bool finishing;
        MonitorTargets monitorTargets;
        SessionContact sessionContact;
        string manualMonitorCall, incomingMonitorCall, unconfirmedManualCall;
        DateTime sessionStartedAt, manualSelectionDeadline;
        static readonly Regex TxMessage = new Regex(@"\b(?:Transmitting|Retransmitting)\s+.*?\b(?:FT8|FT4):\s*(.+)$", RegexOptions.CultureInvariant);
        static readonly Regex RxMessage = new Regex(@"^\d{8}_\d{6}\s+[+-]?\d+\s+[+-]?[\d.,]+\s+\d+\s+\S+\s+(.+)$", RegexOptions.CultureInvariant);
        static readonly Regex ReplySelected = new Regex(@"double click on call processed: (\S+); EnableTx On\b", RegexOptions.CultureInvariant);
        static readonly Regex NoDecodeHalt = new Regex(@"^Halt Tx triggered at (?:RX|TX):\s*Called station not decoded\b", RegexOptions.CultureInvariant);
        static readonly Regex BusyTargetHalt = new Regex(@"^Halt Tx triggered at (?:RX|TX):\s*readFromStdout, not owner of the frequency or reply to other(?:\s|$)", RegexOptions.CultureInvariant);
        static readonly Regex DxClearedAtRetryLimit = new Regex(@"\bDX Call (\S+) cleared, (?:RCQ/SCALL/SREPORT|RCALL/SREPORT|RREPORT/SRREPORT) count reached\s*$",
            RegexOptions.CultureInvariant);
        static readonly Regex Report = new Regex(@"^R?[+-]\d{2}$", RegexOptions.CultureInvariant);
        static readonly Regex Grid = new Regex(@"^[A-R]{2}\d{2}(?:[A-X]{2})?$", RegexOptions.CultureInvariant);
        public bool Pending { get { return pending; } }

        public void Start(DateTime now)
        { Start(now, null); }

        internal void Start(DateTime now, AwardSearch awards, ExpeditionSearch expeditions = null)
        {
            if (Status == null) throw new InvalidOperationException("Ожидается состояние JTDX.");
            if (Status.Mode != "FT8" && Status.Mode != "FT4") throw new InvalidOperationException("Поддерживаются FT8 и FT4.");
            if (Status.Watchdog) throw new InvalidOperationException("Сработал сторожевой таймер JTDX.");
            if (Status.TxEnabled && !String.IsNullOrEmpty(Status.DxCall) && !CanCall(Status.DxCall))
                throw new InvalidOperationException("Текущий позывной входит в исключения DXCC. Завершите связь перед запуском AutoCQ.");
            Options.Validate(); sessionWaitPeriods = Options.Wait73Periods; sessionCallAttempts = Options.CallAttempts;
            sessionSwitchMinutes = Options.SwitchPeriodMinutes;
            SessionDeadline = now.AddMinutes(Options.RunMinutes);
            sessionStartedAt = now;
            unconfirmedManualCall = null;
            if (Options.MonitorEnabled || awards != null || expeditions != null)
            {
                if (awards == null && expeditions == null && Options.MonitorDxcc.Length + Options.MonitorCalls.Length + Options.MonitorMasks.Length == 0)
                    throw new InvalidOperationException("Добавьте хотя бы одну цель мониторинга.");
                if (expeditions != null && !expeditions.HasTargets)
                    throw new InvalidOperationException("Выберите, какие экспедиции и станции искать.");
                if (Status.TxEnabled || Status.Transmitting)
                    throw new InvalidOperationException("Выключите передачу в JTDX для запуска мониторинга.");
                string band = JtdxAdif.Band(Status.Frequency);
                if ((awards != null || expeditions != null || Options.MonitorOnePerBand) && band == null)
                    throw new InvalidOperationException("Не удалось определить диапазон JTDX для мониторинга.");
                if (awards != null)
                {
                    if (awards.Available(now) == 0)
                        throw new InvalidOperationException("Для текущего диапазона и вида работы выберите действующий диплом с правилами поиска.");
                }
                else if (expeditions == null) monitorTargets = new MonitorTargets(Options, DxccForCall, Options.MonitorOnePerBand
                        ? WorkedCallsOnBand(band, CleanCall(Status.DeCall)) : null);
            }
            else if (!Status.TxEnabled && !String.IsNullOrEmpty(Status.DxCall) && NextManual() == null)
                throw new InvalidOperationException("Очистите DX в JTDX для начала CQ или сначала запустите выбранную связь.");
            Awards = awards;
            Expeditions = expeditions;
            sessionFrequency = Status.Frequency; sessionMode = Status.Mode; sessionPeriod = Status.TxFirst;
            workedCallers.Clear();
            string callerBand = JtdxAdif.Band(sessionFrequency);
            if (awards == null && callerBand != null)
                foreach (string call in WorkedCallsOnBandMode(callerBand, sessionMode, CleanCall(Status.DeCall)))
                    workedCallers.Add(CleanCall(call));
            hunting = finishing = false; manualCq = null; ResetCqRound();
            lastResponseAt = now; lastDecodeFinished = DateTime.MinValue;
            Armed = true; pending = false; commandOutstanding = false; loggedCall = null; lastContact = Status.DxCall;
            everEnabled = Status.TxEnabled; Resumes = 0; LoggedCount = 0;
            ConfirmedCount = 0; Missing73Count = 0; ReplyCommands = 0; ResetContact();
            Session = new OperatingSession {
                StartedUtc = now, Band = callerBand, Mode = sessionMode, OwnCall = CleanCall(Status.DeCall), Monitoring = Monitoring,
            };
            sentAcknowledgedReports.Clear();
            unexplainedStop = null;
            if (Expeditions != null)
                Notice("Поиск экспедиций и редких станций включён на " + Options.RunMinutes + " мин. Слушаю эфир и отвечаю на входящие вызовы.");
            else if (Awards != null)
                Notice("Мониторинг включён на " + Options.RunMinutes + " мин. Ищу станции для выбранных дипломов и отвечаю на входящие вызовы.");
            else if (Monitoring)
                Notice("Мониторинг включён на " + Options.RunMinutes + " мин. Ищу цели и отвечаю на входящие вызовы.");
            else
            {
                Notice("Автомат включён на " + Options.RunMinutes + " мин. CQ без ответа до поиска: " + Options.CqAttemptsBeforeSearch + ".");
                if (!Status.TxEnabled)
                {
                    allowClearDx = NextManual() != null;
                    pendingOwner = Status.DxCall;
                    Schedule(now, allowClearDx ? "Проверяю очередь перед началом передачи." : "Первый вызов CQ");
                }
            }
        }

        public void Stop(string reason, bool halt)
        {
            OperatingSession finished = Armed ? Session : null;
            if(Monitoring)Cqs.ClearMonitorTimeouts();
            Armed = false; pending = false; commandOutstanding = false; loggedCall = null;
            unexplainedStop = null;
            manualCq = null; hunting = finishing = false; ResetCqRound();
            monitorTargets = null;
            Awards = null;
            Expeditions = null;
            unconfirmedManualCall = null;
            ResetContact();
            sentAcknowledgedReports.Clear();
            Notice(reason);
            if (halt) RequestHalt();
            if (finished != null)
            {
                finished.StoppedUtc = DateTime.UtcNow;
                finished.StopReason = reason;
                SessionFinished(finished);
            }
        }

        public void OnStatus(RadioStatus status, DateTime now)
        {
            string previousCall = Status == null ? "" : CleanCall(Status.DxCall);
            string nextCall = CleanCall(status.DxCall);
            bool radioChanged = status.Frequency != sessionFrequency || status.Mode != sessionMode;
            // The TX log can reach the companion before the queued status for
            // the previous DX. Wait for the status acknowledging our new reply.
            if (Armed && commandOutstanding && replyTarget != null && waitingCall == replyTarget
                && lastTerminalSlot.HasValue && nextCall.Length > 0 && nextCall == replyPreviousCall && nextCall != replyTarget
                && now < commandDeadline && !radioChanged && !status.Watchdog
                && (status.TxFirst == sessionPeriod || status.TxFirst == expectedPeriod))
            {
                Diagnostic("При переходе на " + replyTarget + " получен статус прежнего корреспондента " + nextCall
                    + ". Ожидаю подтверждение нового вызова.");
                return;
            }

            if (Awards != null) Awards.SetOperatorLocator(status.DeLocator);
            bool previousTxConfirmed = unconfirmedManualCall == null;
            bool wasEnabled = previousTxConfirmed && Status != null && Status.TxEnabled;
            bool wasTransmitting = previousTxConfirmed && Status != null && Status.Transmitting;
            bool nativeIdleSelection = Status != null && String.IsNullOrEmpty(Status.DxCall) && !String.IsNullOrEmpty(status.DxCall)
                && waitingCall == null && (!pending || UseNativeSelection && (replyDuringCq || cqListening || allowClearDx))
                && !commandOutstanding && selectedQueuedCall == null && (!UseNativeSelection || status.TxEnabled);
            bool confirmedCurrent = received73 && (completedCurrent || receivedFinalAt.HasValue);
            bool nativeHandoff = UseNativeSelection && !Monitoring && !CallerRulesActive && activeSearchCall == null && !ReverseExchangePending
                && status.TxEnabled && !terminalHaltRequested && stoppingRetry == null
                && previousCall != nextCall && nextCall.Length > 0
                && !commandOutstanding && (selectedQueuedCall == null || confirmedCurrent) && !finishing
                && (!Awaiting73 || now >= waitUntil)
                && (previousCall.Length > 0 || nativeIdleSelection || completedCurrent || confirmedCurrent);
            Status = status;
            Waiting.SetContext(status);
            Cqs.SetContext(status);
            if (!Armed) return;
            bool adoptNativeSelection = !radioChanged
                && (nativeHandoff || nativeIdleSelection && !Monitoring && !CallerRulesActive);
            bool permittedPeriod = adoptNativeSelection || status.TxFirst == sessionPeriod
                || commandOutstanding && (expectedPeriod.HasValue && status.TxFirst == expectedPeriod.Value
                    || !expectedPeriod.HasValue && nextCall == manualMonitorCall);
            if (Monitoring && (radioChanged || status.Watchdog))
            { StopForRadioStatus(status, !permittedPeriod); return; }
            bool outsideMonitorCall = Monitoring && nextCall.Length > 0
                && nextCall != selectedQueuedCall && nextCall != pendingOwner;
            bool txActive = status.TxEnabled || status.Transmitting;
            bool uncommandedCqSelection = !Monitoring && !radioChanged && !status.Watchdog && nextCall.Length > 0
                && (nextCall != previousCall || unconfirmedManualCall == nextCall)
                && nextCall != selectedQueuedCall && nextCall != pendingOwner && nextCall != replyTarget && nextCall != waitingCall
                && (!permittedPeriod || selectedQueuedCall != null && !Awaiting73 && !ReverseExchangePending);
            if (outsideMonitorCall && txActive || uncommandedCqSelection)
            {
                // The Enable Tx status can arrive before the corresponding double-click line.
                // Treat a new target as an operator handoff only after that line confirms it.
                if (!CanCall(nextCall))
                { Stop("JTDX выбрал станцию из исключений DXCC. Автомат выключен.", true); return; }
                if (unconfirmedManualCall != nextCall)
                { unconfirmedManualCall = nextCall; manualSelectionDeadline = now.AddSeconds(1); }
                return;
            }
            // A later status for the accepted call or for receive supersedes
            // an unconfirmed TX status, including one for the previous target.
            unconfirmedManualCall = null;
            if (outsideMonitorCall) return;
            if (Monitoring && status.TxEnabled
                && !pending && nextCall.Length == 0)
            { Stop("JTDX включил передачу вне цели мониторинга. Мониторинг выключен.", true); return; }
            if (CountryExclusionsActive && status.TxEnabled && nextCall.Length > 0 && !CanCall(nextCall))
            { Stop("JTDX выбрал станцию из исключений DXCC. Автомат выключен.", true); return; }
            if (wasEnabled && !status.TxEnabled)
            {
                if (replyDuringCq && !cqListening) { pending = false; replyDuringCq = false; }
                if (!pending || UseNativeSelection && cqListening && !cqHaltExpected && !handoffHaltAcknowledged)
                    unexplainedStop = now.AddMilliseconds(900);
            }
            if (status.Watchdog) { StopForRadioStatus(status, !permittedPeriod); return; }
            if (adoptNativeSelection)
            {
                if (nativeHandoff)
                {
                    CompleteConfirmedContact();
                    if (Awaiting73 && !missing73Recorded) Missing73Count++;
                    loggedCall = null;
                    ResetContact();
                    Notice("Автовыбор JTDX: " + nextCall + ".");
                }
                SetSessionPeriod(status.TxFirst, now);
                pending = replyDuringCq = false;
                ResetCqRound(keepPendingHalt: cqListening);
            }
            if (radioChanged || !permittedPeriod)
            { StopForRadioStatus(status, !permittedPeriod); return; }
            if (Awaiting73 && (now < waitUntil || status.Decoding) && !String.IsNullOrEmpty(status.DxCall) && CleanCall(status.DxCall) != waitingCall)
            { Stop("JTDX сообщил о выборе " + nextCall + " во время ожидания 73 от " + waitingCall + ". Автомат выключен.", true); return; }
            if (ReverseExchangePending && nextCall.Length > 0 && nextCall != waitingCall)
            { Stop("JTDX сообщил о выборе " + nextCall + " во время обмена 73 с " + waitingCall + ". Автомат выключен.", true); return; }
            if (UseNativeSelection && status.TxEnabled && !commandOutstanding && waitingCall == null && selectedQueuedCall == null)
            {
                bool dxCleared = previousCall.Length > 0 && nextCall.Length == 0;
                if (cqListening && (nextCall.Length > 0 || dxCleared))
                {
                    // A selected DX takes over the receive window after CQ.
                    pending = replyDuringCq = hunting = false;
                    ResetCqRound(keepPendingHalt: true);
                }
                else if (dxCleared)
                    ResetCqRound();
            }
            if (UseNativeSelection && nextCall.Length == 0 && waitingCall != null
                && !commandOutstanding && status.TxEnabled && !pending)
            {
                // Native sequencing can drop its DX field before a logged
                // terminal exchange has consumed all configured RX slots.
                pendingOwner = waitingCall;
                allowClearDx = true;
                Schedule(now, Awaiting73 ? "Ожидаю финальный 73 от " + waitingCall + "."
                    : "Связь завершена. Выбираю следующую станцию или CQ.");
                due = now;
                if ((Awaiting73 || finishing) && !status.Transmitting)
                {
                    cqHaltExpected = true;
                    RequestHalt();
                }
            }
            if (UseNativeSelection && previousCall.Length > 0 && nextCall.Length == 0
                && !commandOutstanding && waitingCall == null && selectedQueuedCall != null && status.TxEnabled && stoppingRetry == null)
                ContinueAfterRetry(previousCall, now);
            if (status.TxEnabled)
            {
                unexplainedStop = null;
                everEnabled = true;
                if (commandOutstanding && (!expectedPeriod.HasValue || status.TxFirst == expectedPeriod.Value) && (replyTarget == null || CleanCall(status.DxCall) == replyTarget))
                {
                    SetSessionPeriod(status.TxFirst, now);
                    commandOutstanding = pending = false;
                    replyTarget = replyPreviousCall = null;
                    expectedReplyMessage = null;
                    expectedPeriod = null;
                }
            }
            if (!commandOutstanding && selectedQueuedCall != null && !String.IsNullOrEmpty(status.DxCall) && CleanCall(status.DxCall) != selectedQueuedCall)
            { Stop("Выбрана другая станция во время QSO из очереди. Автомат выключен.", true); return; }
            if (!String.IsNullOrEmpty(status.DxCall) && status.DxCall != lastContact)
            { lastContact = status.DxCall; loggedCall = null; }
            if (lastCqTime != DateTime.MinValue && String.IsNullOrEmpty(status.DxCall))
            {
                if (status.Transmitting) cqTxSeen = true;
                if (wasTransmitting && !status.Transmitting && cqTxSeen) cqTxEnded = true;
            }
        }

        void StopForRadioStatus(RadioStatus status, bool unexpectedPeriod)
        {
            var details = new List<string> { Monitoring ? "Мониторинг остановлен." : "Автомат остановлен." };
            if (status.Frequency != sessionFrequency)
            {
                decimal differenceHz = (decimal)status.Frequency - sessionFrequency;
                details.Add(String.Format(CultureInfo.GetCultureInfo("ru-RU"),
                    "Частота изменилась с {0:F6} до {1:F6} МГц ({2:+0;-0;0} Гц).",
                    sessionFrequency / 1000000m, status.Frequency / 1000000m, differenceHz));
            }
            if (status.Mode != sessionMode)
                details.Add("Режим изменился с " + sessionMode + " на " + (String.IsNullOrEmpty(status.Mode) ? "неизвестный" : status.Mode) + ".");
            if (unexpectedPeriod)
                details.Add("Период TX изменился с " + RadioStatus.FormatTxPeriod(sessionMode, sessionPeriod)
                    + " на " + RadioStatus.FormatTxPeriod(status.Mode, status.TxFirst) + ".");
            if (status.Watchdog)
                details.Add("Сработал сторожевой таймер JTDX.");
            Stop(String.Join(" ", details), true);
        }

        // Native selection sees an addressed decode before JTDX's DecodeFinished
        // callback. Give it that RX period, then use the saved FIFO as fallback.
        bool LetNativeChoose(DateTime now, WaitingStation next)
        {
            if (!UseNativeSelection || CallerRulesActive || finishing || !Status.TxEnabled || manualCq != null || NextManual() != null) return false;
            if (next == null || !SameRxPeriod(next.LastHeardUtc, now)) return false;
            if (!Status.Decoding && lastDecodeFinished >= next.LastHeardUtc.AddMilliseconds(500)) return false;
            if (lastCqTime != DateTime.MinValue)
            {
                double slot = sessionMode == "FT4" ? 7.5 : 15;
                if (now < lastCqTime.AddSeconds(2 * slot + 0.5)) return true;
            }
            return false;
        }

        void SetSessionPeriod(bool period, DateTime now)
        {
            if (sessionPeriod != period) lastResponseAt = now;
            sessionPeriod = period;
        }

        bool SameRxPeriod(DateTime heard, DateTime now)
        {
            return heard <= now && now < heard.Add(SlotDuration).AddMilliseconds(500);
        }

        public void OnLine(string line, DateTime now)
        {
            int record = line.IndexOf("QSO logged: ", StringComparison.Ordinal);
            if (record >= 0)
            {
                string worked = line.Substring(record + 12).Trim().Split(' ')[0];
                Waiting.Complete(worked, RadioTime(line) ?? now);
                Cqs.Complete(worked);
                if (Status != null && Status.Frequency == sessionFrequency && Status.Mode == sessionMode)
                    workedCallers.Add(CleanCall(worked));
                if (monitorTargets != null && Status != null && Status.Frequency == sessionFrequency && Status.Mode == sessionMode)
                    monitorTargets.Complete(worked);
            }
            if (!Armed) return;
            if (line.EndsWith("  Decoding finished", StringComparison.Ordinal))
                lastDecodeFinished = RadioTime(line) ?? now;
            if (commandOutstanding && expectedReplyMessage != null)
                ObserveReplyReceipt(line);
            if(commandOutstanding && replyTarget!=null && line.IndexOf("UDP Reply request ignored, decode not found",StringComparison.Ordinal)>=0
                && Regex.IsMatch(line,@"(?:[ <])"+Regex.Escape(replyTarget)+@"(?:[ >]|$)"))
            {
                string unavailable=replyTarget;Waiting.InvalidateCall(unavailable);Cqs.InvalidateCall(unavailable);
                commandOutstanding=false;loggedCall=null;lastContact=null;ResetContact();
                if (Monitoring)
                {
                    pending = false;
                    Notice("Строка " + unavailable + " исчезла из левого окна. Продолжаю мониторинг.");
                    return;
                }
                Schedule(now,"Вызов "+unavailable+" больше не найден в окне JTDX. Жду его повторения; продолжаю очередь.");
                // JTDX explicitly rejected the request without starting a QSO.
                // There is no native stop transition to settle before retrying.
                due=now;
                replyDuringCq=Status!=null && Status.TxEnabled && String.IsNullOrEmpty(Status.DxCall);return;
            }
            Match selected = line.IndexOf("double click on call processed:", StringComparison.Ordinal) >= 0
                ? ReplySelected.Match(line) : Match.Empty;
            if (selected.Success) AdoptManualJtdxCall(selected.Groups[1].Value, RadioTime(line), now);
            if (selected.Success && CleanCall(selected.Groups[1].Value) == resumedAcknowledgementCall)
            {
                lastRemoteFinalCall = resumedAcknowledgementCall;
                lastRemoteFinalAt = now;
                lastRemoteFinalWas73 = false;
            }
            Match tx = line.IndexOf("ransmitting ", StringComparison.Ordinal) >= 0 ? TxMessage.Match(line) : Match.Empty;
            if (tx.Success) Transmitted(tx.Groups[1].Value, RadioTime(line), now);
            if (!Armed) return;
            Match rx = RxMessage.Match(line);
            if (rx.Success) Decoded(rx.Groups[1].Value, RadioTime(line), now);
            int log = line.IndexOf("QSO logged: ", StringComparison.Ordinal);
            if (log >= 0)
            {
                string call = line.Substring(log + 12).Trim().Split(' ')[0];
                if (Monitoring && CleanCall(call) != selectedQueuedCall && CleanCall(call) != waitingCall) return;
                if (Awaiting73 && now < waitUntil && CleanCall(call) != waitingCall)
                { Stop("Записана другая связь до окончания ожидания 73. Автомат выключен.", true); return; }
                if (call.Length > 0 && (call != loggedCall || (now - loggedAt).TotalSeconds > 120))
                {
                    sentAcknowledgedReports.Remove(CleanCall(call));
                    loggedCall = call; loggedAt = now; LoggedCount++;
                    SessionRow(CleanCall(call)).LoggedUtc = RadioTime(line) ?? now;
                    completedCurrent = true;
                    if (waitingCall == null) BeginWaiting(CleanCall(call), now, RadioTime(line));
                    Notice("QSO в журнале: " + call + (Awaiting73 ? ". Ожидаю заключительное 73." : "."));
                }
            }
            if (!pending && !commandOutstanding && waitingCall == null && stoppingRetry == null
                && line.IndexOf(" count reached", StringComparison.Ordinal) >= 0)
            {
                Match cleared = DxClearedAtRetryLimit.Match(line);
                if (cleared.Success && (RadioTime(line) ?? now) >= sessionStartedAt)
                {
                    string call = CleanCall(cleared.Groups[1].Value);
                    if ((call == selectedQueuedCall || call == attemptCall) && call == CleanCall(lastContact))
                    {
                        ContinueAfterRetry(call, now);
                        return;
                    }
                }
            }
            int halt = line.IndexOf("Halt Tx", StringComparison.Ordinal);
            if (halt < 0) return;
            string reason = line.Substring(halt);
            if (stoppingRetry != null && reason.IndexOf("UDP request", StringComparison.Ordinal) >= 0)
            {
                ContinueAfterRetry(stoppingRetry, now);
                handoffHaltAcknowledged = true;
                return;
            }
            if (cqHaltExpected && reason.IndexOf("UDP request",StringComparison.Ordinal) >= 0)
            { cqHaltExpected = false; handoffHaltAcknowledged = cqListening; unexplainedStop = null; return; }
            bool finalStop = reason.IndexOf(": FIN, end of QSO", StringComparison.Ordinal) >= 0;
            bool normalEnd = reason.IndexOf("end of QSO", StringComparison.Ordinal) >= 0
                || reason.IndexOf("S73, none received", StringComparison.Ordinal) >= 0
                || reason.IndexOf("SRR73, none received", StringComparison.Ordinal) >= 0;
            double logWindow = Math.Max(120, sessionWaitPeriods * (sessionMode == "FT4" ? 15 : 30) + 30);
            bool loggedRecently = loggedCall != null && loggedCall == lastContact && (now - loggedAt).TotalSeconds < logWindow;
            bool confirmedRecently = receivedFinalAt.HasValue && waitingCall == lastContact
                && now >= receivedFinalAt.Value && now - receivedFinalAt.Value <= SlotDuration + SlotDuration;
            // The native decoder ends this attempt if the frequency is in use
            // or the DX replies elsewhere. JTDX has disabled TX for this call.
            bool busyStop = BusyTargetHalt.IsMatch(reason);
            string currentContact = waitingCall ?? selectedQueuedCall ?? attemptCall;
            if (busyStop && currentContact == null && !pending && Status != null
                && (Status.TxEnabled || unexplainedStop.HasValue))
                currentContact = Callsign.Parse(Status.DxCall);
            bool busyTarget = busyStop && currentContact != null && currentContact == CleanCall(lastContact);
            bool noDecode = NoDecodeHalt.IsMatch(reason);
            bool counter = reason.IndexOf("counter triggered", StringComparison.Ordinal) >= 0;
            bool nativeRetryEnd = reason.IndexOf("m_houndMode", StringComparison.Ordinal) < 0 && (counter || noDecode);
            // JTDX can report a second retry stop after the attempt was released.
            if (currentContact == null && (busyStop || Monitoring && nativeRetryEnd)) return;
            bool retryEnd = busyTarget || nativeRetryEnd && (counter || !String.IsNullOrEmpty(lastContact));
            if ((everEnabled || busyTarget) && ((normalEnd && loggedRecently) || (finalStop && confirmedRecently) || retryEnd))
            {
                if (retryEnd && !loggedRecently && waitingCall == null)
                {
                    // The module may already have asked for Halt on the same
                    // boundary. Consume that reply too if it is still in flight.
                    if (stoppingRetry != null) cqHaltExpected = true;
                    ContinueAfterRetry(stoppingRetry ?? lastContact, now, busyTarget, noDecode);
                    return;
                }
                unexplainedStop = null;
                terminalRepeatSlot = null;
                if (Awaiting73 && lastTerminalSlot.HasValue)
                {
                    // The radio already halted. Keep the original receive
                    // opportunities and finish listening to the last frame.
                    DateTime lastReply = lastTerminalAttemptSlot.Value.AddTicks(2 * SlotDuration.Ticks);
                    DateTime nativeWait = firstTerminalSlot.Value.AddTicks(2 * SlotDuration.Ticks * sessionWaitPeriods);
                    waitUntil = lastReply > nativeWait ? lastReply : nativeWait;
                }
                allowClearDx = true; pendingOwner = waitingCall ?? lastContact;
                string continuation = Awaiting73 ? "Дослушиваю заключительное 73 от " + waitingCall + "."
                    : Monitoring ? "Обмен с целью завершён. Возвращаюсь на приём."
                    : retryEnd ? "JTDX завершил вызов. Выбираю следующую станцию или CQ." : "Завершение подтверждено: следующая станция или CQ";
                Schedule(now, busyTarget ? "JTDX прервал передачу для " + currentContact + ". " + continuation : continuation);
                // FIN/counter is an explicit native acknowledgement. Tick still
                // waits for RX, completed decoding and the final-73 condition.
                due=now;
            }
            else Stop("JTDX остановлен. Автомат выключен: " + reason, false);
        }

        void ContinueAfterRetry(string call, DateTime now, bool busyTarget = false, bool noDecode = false)
        {
            string next = Monitoring ? "Возвращаюсь на приём." : "Выбираю следующую станцию или CQ.";
            string station = String.IsNullOrEmpty(call) ? "" : " " + call;
            string notice = "JTDX завершил вызов" + station + ". " + next;
            if (busyTarget) notice = "JTDX прервал вызов" + station + ". " + next;
            else if (yieldingToCaller) notice = "Нет ответа от " + call + ". Отвечаю станции, которая зовёт нас сейчас.";
            else if (noDecode) notice = "JTDX не декодировал станцию " + call + ". " + next;
            else if (attemptCall == call && initialTransmissions >= sessionCallAttempts && !attemptAnswered)
                notice = "Лимит попыток " + call + " исчерпан. " + next;
            Waiting.Pause(call, CallerQueue.NoAnswer, now);
            Cqs.Failed(call, now, Monitoring);
            ResetContact();
            ResetCqRound(keepPendingHalt: true);
            loggedCall = null;
            unexplainedStop = null;
            allowClearDx = true;
            pendingOwner = call;
            hunting = false;
            Schedule(now, notice);
            due = now;
        }

        void Schedule(DateTime now, string reason)
        {
            pending = true; commandOutstanding = false; replyDuringCq = false; due = now.AddMilliseconds(900);
            expires = now.AddSeconds(45);
            if (Awaiting73 && waitUntil.AddSeconds(30) > expires) expires = waitUntil.AddSeconds(30);
            Notice(reason);
        }

        void BeginCommand(DateTime now, DecodedMessage decode = null)
        {
            commandOutstanding = true;
            commandIssuedAt = now;
            commandDeadline = now + CommandConfirmationTimeout;
            expectedReplyMessage = decode == null || String.IsNullOrWhiteSpace(decode.Message) ? null : decode.Message.Trim();
        }

        void ObserveReplyReceipt(string line)
        {
            const string marker = "UDP Reply request received: ";
            int start = line.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0 || !String.Equals(line.Substring(start + marker.Length).Trim(), expectedReplyMessage,
                StringComparison.OrdinalIgnoreCase)) return;

            DateTime? received = RadioTime(line);
            // The log records milliseconds. Allow its rounding at the send boundary.
            if (!received.HasValue || received.Value < commandIssuedAt.AddMilliseconds(-1) || received.Value > commandDeadline) return;

            // JTDX logged receipt, but TX is enabled only after it processes the reply.
            // Give it one more TX/RX cycle. A duplicate receipt cannot extend this deadline.
            expectedReplyMessage = null;
            commandDeadline = commandDeadline.AddTicks(2 * SlotDuration.Ticks);
            Diagnostic("JTDX получил команду вызова " + replyTarget + ". Ожидаю подтверждение TX до "
                + commandDeadline.ToLocalTime().ToString("HH:mm:ss") + ".");
        }

        static string CleanCall(string call) { return Callsign.Clean(call); }
        static string[] Words(string text) { return text.ToUpperInvariant().Split(new[] {' '}, StringSplitOptions.RemoveEmptyEntries); }
        static DateTime? RadioTime(string line)
        {
            DateTime value;
            if (line.Length >= 19 && line[15] == '.' && DateTime.TryParseExact(line.Substring(0,19), "yyyyMMdd_HHmmss.fff", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value)) return value;
            if (line.Length >= 15 && DateTime.TryParseExact(line.Substring(0,15), "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value)) return value;
            return null;
        }
        void ResetContact(string previousCall = null)
        {
            sessionContact = null;
            waitingCall = null; received73 = false; missing73Recorded = false; terminalRadioTime = null;
            receivedFinalAt = null;
            firstTerminalSlot = lastTerminalSlot = null; terminalHaltRequested = sentTerminalAcknowledgement = false;
            terminalRepeatSlot = lastTerminalAttemptSlot = null;
            terminalReportReceived = reportReplacedTerminalRepeat = false;
            silentTerminalRepeats = silentTerminalAttempts = missedTerminalRepeats = 0;
            reverse73 = reverseNeedsReply = repeatSentInSilentWindow = false;
            allowClearDx = false; pendingOwner = null; attemptCall = null; initialTransmissions = 0;
            attemptAnswered = lastRemoteFinalWas73 = false; lastRemoteFinalCall = null;
            callLimitExceeded = yieldingToCaller = false; stoppingRetry = null;
            lastAttemptReplySlot = -1;
            resumedAcknowledgementCall = null;
            expectedReplyMessage = null;
            replyTarget = null; selectedQueuedCall = null; replyDuringCq = false; lastInitialSlot = -1;
            replyPreviousCall = CleanCall(previousCall);
            expectedPeriod = null; activeSearchCall = null; completedCurrent = false;
            manualMonitorCall = null;
            incomingMonitorCall = null;
        }
        void ResetCqRound(bool keepCount = false, bool keepPendingHalt = false)
        {
            if (!keepCount) cqCount = 0;
            lastCqSlot = -1; lastCqTime = DateTime.MinValue;
            cqThreshold = cqTxSeen = cqTxEnded = cqListening = false;
            if (!keepPendingHalt) cqHaltExpected = false;
            handoffHaltAcknowledged = periodSwitchHalt = false;
        }

        TimeSpan SlotDuration
        { get { return TimeSpan.FromSeconds(sessionMode == "FT4" ? 7.5 : 15); } }

        long SlotNumber(DateTime time)
        { return (time.Ticks + TimeSpan.FromMilliseconds(500).Ticks) / SlotDuration.Ticks; }

        DateTime SlotStart(long slot)
        { return new DateTime(slot * SlotDuration.Ticks, DateTimeKind.Utc); }

        void TrackCq(DateTime time, DateTime now)
        {
            TimeSpan period = SlotDuration;
            long slot = SlotNumber(time);
            if (slot == lastCqSlot) return;

            DateTime slotStart = SlotStart(slot);
            if (Options.SearchEnabled && cqThreshold && cqCount >= Options.CqAttemptsBeforeSearch && cqTxSeen)
            {
                // With native AutoSelect enabled, JTDX can start its CQ at
                // the TX boundary and replace it when decoding finishes.
                // Allow only another TX slot of the same phase. Preserve
                // the pending search and wait for either the native choice
                // or the end of the frame before issuing a Reply ourselves.
                long elapsedSlots = slot - lastCqSlot;
                bool nativeContinuation = UseNativeSelection && !commandOutstanding
                    && Status != null && Status.TxEnabled && !cqHaltExpected && !unexplainedStop.HasValue
                    && elapsedSlots >= 2 && elapsedSlots % 2 == 0;
                if (!nativeContinuation)
                {
                    Stop("JTDX начал CQ вне ожидаемого периода. AutoCQ выключен.", true);
                    return;
                }

                if (!cqListening)
                {
                    due = lastCqTime.AddTicks(2 * period.Ticks);
                    expires = now.AddSeconds(45);
                }
                cqListening = pending = true;
                replyDuringCq = false;
                Notice("JTDX начал CQ. Жду его Автовыбор или окончания передачи для продолжения поиска.");
            }

            lastCqSlot = slot;
            lastCqTime = slotStart;
            cqCount++;
            cqTxSeen = Status != null && Status.Transmitting;
            cqTxEnded = false;
            if (Options.SearchEnabled && (cqCount >= Options.CqAttemptsBeforeSearch || hunting)
                || manualCq != null || finishing)
                cqThreshold = true;
        }
        public void SelectCq(string call,DateTime now)
        {
            if(!Armed)throw new InvalidOperationException("Автомат выключен.");
            if(finishing || now >= SessionDeadline)throw new InvalidOperationException("Время вышло: завершается текущая связь и очередь. Новый поиск не запускается.");
            if(!CanCall(call))throw new InvalidOperationException("Станция входит в исключения DXCC.");
            CqStation row=Cqs.Find(call);
            if(!Cqs.Fresh(row,now))throw new InvalidOperationException("В левом окне нет свежего сообщения от этой станции.");
            manualCq=row.Call;
            if(waitingCall==null && String.IsNullOrEmpty(Status.DxCall) && lastCqTime!=DateTime.MinValue)cqThreshold=true;
            Notice(Monitoring ? "Ручной выбор " + row.Call + ". Вызову после текущей связи, затем продолжу мониторинг."
                : "Выбрана станция " + row.Call + ". Вызов после текущей связи и ответов на ваш позывной.");
        }

        void AdoptManualJtdxCall(string call, DateTime? selectedAt, DateTime now)
        {
            call = Callsign.Parse(call);
            if (Status == null || call == null || call == selectedQueuedCall || call == pendingOwner || call == replyTarget || call == waitingCall
                || call == CleanCall(Status.DeCall) || !CanCall(call) || finishing || now >= SessionDeadline
                || !selectedAt.HasValue || selectedAt.Value < sessionStartedAt
                || selectedAt.Value > now.AddSeconds(1) || now - selectedAt.Value > TimeSpan.FromSeconds(3)) return;
            if (unconfirmedManualCall != null && call != unconfirmedManualCall) return;
            CompleteConfirmedContact();
            bool haltInFlight = cqHaltExpected || stoppingRetry != null;
            ResetContact(waitingCall ?? pendingOwner ?? Status.DxCall);
            ResetCqRound(keepPendingHalt: haltInFlight);
            cqHaltExpected = haltInFlight;
            pending = hunting = false; loggedCall = null; unexplainedStop = null; manualCq = null;
            unconfirmedManualCall = null;
            manualMonitorCall = selectedQueuedCall = lastContact = call;
            Cqs.Retry(call); Cqs.Attempt(call, now);
            BeginCommand(now);
            replyTarget = call;
            if (Status.TxEnabled && CleanCall(Status.DxCall) == call)
            {
                SetSessionPeriod(Status.TxFirst, now);
                commandOutstanding = false; replyTarget = replyPreviousCall = null; everEnabled = true;
            }
            Notice("Ручной вызов " + call + " в JTDX. После связи или лимита вызовов продолжу "
                + (Monitoring ? "мониторинг." : "автоматическую работу."));
        }

        public void AddQueuedCall(string call, DateTime now)
        {
            call = Callsign.RequireDirect(call);
            EnsureOtherCall(call);
            Waiting.Add(call, now);
            Cqs.Retry(call);
        }

        public void RenameQueuedCall(string oldCall, string newCall)
        {
            newCall = Callsign.RequireDirect(newCall);
            EnsureOtherCall(newCall);
            if (Callsign.Clean(oldCall) == newCall) return;
            Waiting.Rename(oldCall, newCall);
            Cqs.Retry(newCall);
        }

        public void RetryQueuedCall(string call)
        {
            Waiting.Retry(call);
            Cqs.Retry(call);
        }

        void EnsureOtherCall(string call)
        {
            if (Status != null && Callsign.BaseCall(call) == Callsign.BaseCall(CleanCall(Status.DeCall)))
                throw new InvalidOperationException("Введите позывной другой станции.");
        }
        WaitingStation FreshCaller(DateTime now, string exceptCall = null)
        {
            Predicate<WaitingStation> fresh = row => row.Call != exceptCall && row.Decode.IsNew
                && row.LastHeardUtc <= now && now - row.LastHeardUtc < SlotDuration + SlotDuration
                && CallerWithinSession(row);
            return RankedCaller(Monitoring, fresh);
        }
        WaitingStation RankedCaller(bool allowPeriodChange, Predicate<WaitingStation> eligible = null)
        {
            WaitingStation unworked = PriorityCaller(allowPeriodChange,
                row => !workedCallers.Contains(row.Call) && (eligible == null || eligible(row)));
            return unworked ?? PriorityCaller(allowPeriodChange, eligible);
        }
        WaitingStation PriorityCaller(bool allowPeriodChange, Predicate<WaitingStation> eligible)
        {
            if (HasPriorityCountries)
            {
                WaitingStation priority = Waiting.Next(Status, allowPeriodChange, CanCallWithPriority, eligible);
                if (priority != null) return priority;
            }
            return Waiting.Next(Status, allowPeriodChange, CanCall, eligible);
        }
        WaitingStation NextCaller(DateTime now)
        {
            WaitingStation fresh = FreshCaller(now);
            if (fresh != null) return fresh;
            return RankedCaller(true, row => !Monitoring || row.Decode.IsNew && row.LastHeardUtc <= now
                && CallerWithinSession(row));
        }

        bool CallerWithinSession(WaitingStation row)
        {
            return !Monitoring || !finishing || row.QueuedUtc < SessionDeadline || row.LastHeardUtc < SessionDeadline;
        }

        void ReplyToQueuedCaller(WaitingStation next)
        {
            hunting = false; ResetCqRound(); expectedPeriod = CallerQueue.ReplyFirst(next.Decode, sessionMode);
            replyTarget = selectedQueuedCall = lastContact = next.Call;
            if (Monitoring) incomingMonitorCall = next.Call;
            if (CallerQueue.IsAcknowledgement(next.Decode)) resumedAcknowledgementCall = next.Call;
            ReplyCommands++;
            Notice((Monitoring ? "Входящий вызов в мониторинге: "
                : !finishing && HasPriorityCountries && IsPriorityCall(next.Call) ? "Отвечаю из очереди, приоритет DXCC: "
                : "Отвечаю из очереди: ") + next.Call + ".");
            (RequestCallerReply ?? RequestReply)(next.Decode.Copy());
        }

        bool CanCallWithPriority(string call)
        { return CanCall(call) && IsPriorityCall(call); }

        WaitingStation NextManual()
        { return Status == null ? null : Waiting.NextManual(Status.DeCall, CanCall); }

        CqStation NextCq(DateTime now, bool switchPeriod)
        {
            if (finishing) return null;
            if(manualCq!=null)
            {
                CqStation selected=Cqs.Find(manualCq);
                if(Cqs.Fresh(selected,now) && CanCall(selected.Call))return selected;
                Notice("Выбранное сообщение устарело: " + manualCq + ". Ожидаю свежий приём."); manualCq=null;
            }
            if (HasPriorityCountries)
            {
                CqStation priority = NextPriorityCq(now);
                if (priority != null) return priority;
            }
            return !switchPeriod && hunting && Options.SearchEnabled ? Cqs.Next(now, CanCall, eligible: Waiting.CanUseCq) : null;
        }

        CqStation NextPriorityCq(DateTime now)
        { return Cqs.Next(now, CanCallWithPriority, Options.PriorityAfter73, Waiting.CanUseCq); }
        void BeginWaiting(string peer, DateTime now, DateTime? radioTime)
        {
            if (waitingCall == peer) return;
            waitingCall = peer; received73 = false; missing73Recorded = false; terminalRadioTime = radioTime;
            // Count reply opportunities of the OTHER station: every second slot.
            waitUntil = now.AddSeconds((sessionMode == "FT4" ? 7.5 : 15.0) * 2 * sessionWaitPeriods);
            Notice("Жду 73 от " + peer + ". Повторов без ответа — " + sessionWaitPeriods + ".");
        }

        void TerminalTransmitted(string peer, DateTime now, DateTime? radioTime, bool acknowledgement)
        {
            if (waitingCall != peer)
            {
                firstTerminalSlot = lastTerminalSlot = lastTerminalAttemptSlot = null;
                receivedFinalAt = terminalRepeatSlot = null;
                terminalReportReceived = reportReplacedTerminalRepeat = false;
                silentTerminalRepeats = silentTerminalAttempts = missedTerminalRepeats = 0;
                terminalHaltRequested = reverse73 = reverseNeedsReply = repeatSentInSilentWindow = false;
            }
            BeginWaiting(peer, now, radioTime);
            DateTime slot = SlotStart(SlotNumber(radioTime ?? now));
            if (lastTerminalSlot.HasValue && slot < lastTerminalSlot.Value) return;
            sentTerminalAcknowledgement = acknowledgement;
            if (lastTerminalSlot == slot) return;
            bool repeat = lastTerminalSlot.HasValue;
            bool alreadyCounted = lastTerminalAttemptSlot == slot;
            lastTerminalSlot = slot;
            if (!lastTerminalAttemptSlot.HasValue || lastTerminalAttemptSlot.Value < slot)
                lastTerminalAttemptSlot = slot;
            terminalRepeatSlot = null;
            reportReplacedTerminalRepeat = false;
            missedTerminalRepeats = 0;
            if (!firstTerminalSlot.HasValue) firstTerminalSlot = slot;
            if (reverse73 || received73) return;

            // The original sign-off and a response to a new report do not use
            // up a retry. Count only subsequent transmissions after silence.
            if (repeat && !terminalReportReceived)
            {
                silentTerminalRepeats++;
                if (!alreadyCounted) silentTerminalAttempts++;
            }
            terminalReportReceived = false;
            SetTerminalDeadline(lastTerminalAttemptSlot.Value);
        }

        void SetTerminalDeadline(DateTime slot)
        {
            int remaining = Math.Max(0, sessionWaitPeriods - silentTerminalAttempts);
            waitUntil = slot.AddTicks(2 * SlotDuration.Ticks * (remaining + 1));
        }
        void MissTerminalRepeat(DateTime slot, DateTime now)
        {
            // Advance past elapsed TX slots in the same phase without counting an unsent message.
            TimeSpan cycle = SlotDuration + SlotDuration;
            long elapsedCycles = (now - slot - SlotDuration).Ticks / cycle.Ticks;
            DateTime lastMissed = slot.AddTicks(elapsedCycles * cycle.Ticks);
            lastTerminalAttemptSlot = lastMissed;
            terminalRepeatSlot = null;
            bool exhausted = ++missedTerminalRepeats >= MaxConsecutiveMissedTerminalRepeats;
            if (reverse73 || exhausted)
            {
                waitUntil = lastMissed.Add(cycle);
                if (exhausted) reverseNeedsReply = false;
            }
            else SetTerminalDeadline(lastMissed);
            Notice("JTDX не подтвердил завершающую передачу для " + waitingCall
                + (exhausted ? " два периода подряд. Дослушиваю ответ." : ". Повторю в следующем периоде TX."));
        }
        SessionContact SessionRow(string call)
        {
            if (sessionContact == null || sessionContact.Call != call)
            {
                sessionContact = new SessionContact { Call = call };
                Session.Contacts.Add(sessionContact);
            }
            return sessionContact;
        }

        void Confirm73(string reason, DateTime at)
        {
            if (received73) return;
            received73 = true; ConfirmedCount++;
            SessionRow(waitingCall).ConfirmedUtc = at;
            Notice(reason);
        }
        void CompleteConfirmedContact()
        {
            if (completedCurrent || waitingCall == null) return;
            DateTime? confirmedAt = receivedFinalAt;
            // When our 73 ends the exchange, use its TX slot to reject old decodes.
            if (reverse73 && received73 && !reverseNeedsReply)
                confirmedAt = confirmedAt ?? lastTerminalSlot;
            if (!confirmedAt.HasValue) return;
            completedCurrent = true;
            workedCallers.Add(waitingCall);
            Waiting.Complete(waitingCall, confirmedAt.Value);
            sentAcknowledgedReports.Remove(waitingCall);
            Cqs.Complete(waitingCall);
            if (Monitoring && monitorTargets != null) monitorTargets.Complete(waitingCall);
            Notice("QSO с " + waitingCall + " подтверждено, но в журнале JTDX нет записи. Продолжаю работу.");
        }
        void Transmitted(string text, DateTime? radioTime, DateTime now)
        {
            string[] w = Words(text);
            bool callsignPair = CallerQueue.IsCallsignPair(w);
            if (w.Length < 3 && !callsignPair) return;
            if (w[0] == "CQ")
            {
                if (Monitoring)
                { Stop("JTDX начал общий вызов во время мониторинга. Мониторинг выключен.", true); return; }
                if ((Awaiting73 && now < waitUntil) || ReverseExchangePending)
                    Stop("CQ начался до окончания обмена 73. Автомат выключен.", true);
                if (Armed) TrackCq(radioTime ?? now, now);
                return;
            }
            // A TX line can be written just before JTDX consumes our Halt.
            // Keep the pending acknowledgement across that late log entry.
            ResetCqRound(keepPendingHalt: cqHaltExpected);
            string peer = CleanCall(w[0]), own = CleanCall(w[1]);
            if (Status != null && !String.IsNullOrEmpty(Status.DeCall) && own != CleanCall(Status.DeCall)) return;
            if (Monitoring && peer != selectedQueuedCall && peer != pendingOwner)
            { Stop("JTDX начал передачу вне цели мониторинга. Мониторинг выключен.", true); return; }
            if (peer != waitingCall && ((Awaiting73 && now < waitUntil) || ReverseExchangePending))
            { Stop("Передача другой станции началась до окончания ожидания 73. Автомат выключен.", true); return; }
            lastContact = peer;
            string payload = callsignPair ? "" : w[2];
            if (payload == "RR73" || payload == "RRR")
            {
                if (waitingCall != peer) lastRemoteFinalCall = null;
                TerminalTransmitted(peer, now, radioTime, acknowledgement: true);
            }
            else if (payload == "73")
            {
                DateTime? previousTerminalSlot = lastTerminalSlot;
                TerminalTransmitted(peer, now, radioTime, acknowledgement: false);
                // Our 73 answers a terminal message already received from the peer.
                if (reverse73 || (lastRemoteFinalCall == peer && (now - lastRemoteFinalAt).TotalSeconds < 120))
                {
                    reverse73 = true;
                    if (previousTerminalSlot != lastTerminalSlot && (!received73 || reverseNeedsReply))
                    {
                        waitUntil = lastTerminalSlot.Value.AddTicks(2 * SlotDuration.Ticks);
                        reverseNeedsReply = repeatSentInSilentWindow = false;
                    }
                    else if (previousTerminalSlot != lastTerminalSlot && !reverseNeedsReply)
                        repeatSentInSilentWindow = true;
                    if (lastRemoteFinalWas73 && !received73) receivedFinalAt = firstTerminalSlot;
                    Confirm73(lastRemoteFinalWas73 ? "Получено заключительное 73 от " + peer + "."
                        : peer + " передал RR73; наше 73 завершает обмен.", radioTime ?? now);
                }
            }
            else if (peer == waitingCall && Awaiting73 && terminalRepeatSlot.HasValue && Report.IsMatch(payload)
                && terminalRepeatSlot.Value == SlotStart(SlotNumber(radioTime ?? now)))
            {
                reportReplacedTerminalRepeat = true;
            }
            else if (callsignPair || Report.IsMatch(payload) || Grid.IsMatch(payload))
            {
                if (attemptCall != peer)
                {
                    attemptCall = peer; initialTransmissions = 0; attemptAnswered = false; lastInitialSlot = -1; callLimitExceeded = false;
                    lastAttemptReplySlot = -1;
                }
                if (payload.StartsWith("R", StringComparison.Ordinal))
                {
                    sentAcknowledgedReports[peer] = radioTime ?? now;
                    attemptAnswered = true;
                }
                if (!attemptAnswered)
                {
                    long slot = SlotNumber(radioTime ?? now);
                    if (slot != lastInitialSlot) { initialTransmissions++; lastInitialSlot = slot; }
                    callLimitExceeded = initialTransmissions > sessionCallAttempts;
                }
            }
        }
        void Decoded(string text, DateTime? radioTime, DateTime now)
        {
            string[] w = Words(text);
            if (w.Length < 2 || Status == null || CleanCall(w[0]) != CleanCall(Status.DeCall)) return;
            bool callsignPair = CallerQueue.IsCallsignPair(w);
            if (w.Length == 2 && !callsignPair) return;
            string peer = CleanCall(w[1]), payload = callsignPair ? "" : w[2];
            bool acknowledgement = CallerQueue.IsAcknowledgement(payload);
            bool response = CanCall(peer) && (callsignPair || CallerQueue.IsCaller(payload)
                || waitingCall == peer && (payload == "73" || acknowledgement));
            if (response && (!radioTime.HasValue || now - radioTime.Value <= SlotDuration + SlotDuration))
                lastResponseAt = now;
            // An early RR73 or RRR cannot advance an exchange that still
            // needs a report. A report or an outgoing acknowledged report
            // already marks legitimate exchanges as answered.
            if (peer == attemptCall && Report.IsMatch(payload)) attemptAnswered = true;
            if (peer == attemptCall && radioTime.HasValue && lastInitialSlot >= 0
                && SlotNumber(radioTime.Value) > lastInitialSlot && response)
                lastAttemptReplySlot = SlotNumber(radioTime.Value);
            if((payload=="73" || acknowledgement) && waitingCall==peer && terminalRadioTime.HasValue && radioTime.HasValue && radioTime.Value<terminalRadioTime.Value)return;
            bool freshRadioTime = radioTime.HasValue && radioTime.Value <= now.AddSeconds(2)
                && now - radioTime.Value <= SlotDuration + SlotDuration;
            // Round FT4 decodes logged at a whole second back to their 7.5-second slot.
            bool freshReply = peer == waitingCall && lastTerminalSlot.HasValue && freshRadioTime
                && SlotNumber(radioTime.Value) > SlotNumber(lastTerminalSlot.Value);
            if (freshReply && Awaiting73 && Report.IsMatch(payload) && !terminalReportReceived)
            {
                terminalReportReceived = true;
                silentTerminalRepeats = silentTerminalAttempts = 0;
                DateTime nextTx = SlotStart(SlotNumber(radioTime.Value) + 1);
                waitUntil = nextTx.AddTicks(2 * SlotDuration.Ticks * (sessionWaitPeriods + 1));
                Notice("Повторный рапорт от " + peer + ". Повторяю завершающее сообщение.");
            }
            if (acknowledgement)
            {
                // Other stations can send a late acknowledgement in the same
                // receive period. It must not replace this exchange's sign-off.
                string currentPeer = waitingCall ?? selectedQueuedCall ?? attemptCall ?? CleanCall(Status.DxCall);
                if (peer == currentPeer && freshRadioTime)
                {
                    lastRemoteFinalCall = peer;
                    lastRemoteFinalAt = now;
                    lastRemoteFinalWas73 = false;
                }
                if (freshReply)
                {
                    if (reverse73)
                    {
                        if (!reverseNeedsReply && missedTerminalRepeats < MaxConsecutiveMissedTerminalRepeats)
                        {
                            reverseNeedsReply = true;
                            waitUntil = SlotStart(SlotNumber(radioTime.Value) + 1);
                        }
                    }
                    else if (sentTerminalAcknowledgement)
                    {
                        receivedFinalAt = radioTime;
                        Confirm73("Получено заключительное " + payload + " от " + peer + ".", radioTime ?? now);
                    }
                }
            }
            // Some operators answer an acknowledged report directly with 73.
            // Remember only the current exchange's immediately following RX slot.
            if (payload == "73" && waitingCall != peer && peer == attemptCall && peer == lastContact && freshRadioTime)
            {
                DateTime sent;
                if (sentAcknowledgedReports.TryGetValue(peer, out sent) && SlotNumber(radioTime.Value) == SlotNumber(sent) + 1)
                {
                    lastRemoteFinalCall = peer;
                    lastRemoteFinalAt = now;
                    lastRemoteFinalWas73 = true;
                }
            }
            if(payload!="73" || waitingCall!=peer) return;
            if (freshReply)
            {
                receivedFinalAt = radioTime;
                if (reverse73) reverseNeedsReply = false;
            }
            Confirm73("Получено заключительное 73 от " + peer + ".", radioTime ?? now);
        }

        // Each permitted repeat follows a complete reply window. Keep TX gated
        // even in the last window, where a new report can restart the count.
        bool FinishTerminal(DateTime now)
        {
            if (waitingCall == null || pending || commandOutstanding || Status == null || cqHaltExpected
                || unexplainedStop.HasValue) return false;
            string call = CleanCall(Status.DxCall);
            if (call.Length > 0 && call != waitingCall) return false;
            bool exchangeDone = received73 && (!firstTerminalSlot.HasValue || now >= firstTerminalSlot.Value.Add(SlotDuration))
                && (!reverse73 || (!reverseNeedsReply && ReplyWindowComplete(waitUntil, now)));
            bool canManageRepeat = CanRepeatTerminal;
            DateTime nextTx = canManageRepeat && !reverse73 ? lastTerminalAttemptSlot.Value.AddTicks(2 * SlotDuration.Ticks) : waitUntil;
            bool replyComplete = ReplyWindowComplete(nextTx, now) && !Status.Decoding;
            bool overrun = canManageRepeat && silentTerminalAttempts > sessionWaitPeriods && !terminalReportReceived;
            bool timedOut = Awaiting73 && (overrun || replyComplete && (!canManageRepeat
                || missedTerminalRepeats >= MaxConsecutiveMissedTerminalRepeats
                || silentTerminalAttempts >= sessionWaitPeriods && !terminalReportReceived));
            bool completed = (exchangeDone || timedOut) && !Status.Decoding
                && (!Status.Transmitting || overrun || repeatSentInSilentWindow);
            if (!completed)
            {
                if (canManageRepeat && (Awaiting73 || reverseNeedsReply) && replyComplete && !Status.Transmitting && !terminalRepeatSlot.HasValue)
                {
                    if (now >= nextTx.Add(SlotDuration)) MissTerminalRepeat(nextTx, now);
                    else
                    {
                        terminalRepeatSlot = nextTx;
                        reportReplacedTerminalRepeat = false;
                    }
                }
                return false;
            }

            terminalRepeatSlot = null;
            if (timedOut) waitUntil = overrun ? lastTerminalSlot.Value : nextTx;
            pendingOwner = waitingCall;
            allowClearDx = true;
            Schedule(now, Awaiting73 ? "Дослушиваю заключительное 73 от " + waitingCall + "."
                : Monitoring ? "Связь завершена. Возвращаюсь на приём." : "Связь завершена. Выбираю следующую станцию или CQ.");
            due = now;
            if (!Status.TxEnabled) return false;
            terminalHaltRequested = cqHaltExpected = true;
            RequestHalt();
            return true;
        }

        public void OnDecode(DecodedMessage decode, DateTime now)
        {
            Waiting.Observe(decode, Status, waitingCall ?? selectedQueuedCall, now,!finishing);
            Cqs.Observe(decode, Status, now, !UseNativeSelection || !decode.IsNew);
            if (!Armed || Status == null || decode.OffAir || !decode.IsNew || String.IsNullOrEmpty(decode.Message)) return;
            if ((sessionMode == "FT8" && decode.Mode != "~")
                || (sessionMode == "FT4" && decode.Mode != ":" && decode.Mode != "+")) return;
            DateTime heard = decode.UtcTime(now);
            Decoded(decode.Message, heard, now);
            if (Status.Frequency != sessionFrequency || Status.Mode != sessionMode) return;
            string[] words = Words(decode.Message);
            if (words.Length != 3 || !CallerQueue.IsAcknowledgement(words[2])) return;
            string peer = CleanCall(words[1]);
            DateTime sent;
            if (!sentAcknowledgedReports.TryGetValue(peer, out sent) || heard <= sent || heard - sent > TimeSpan.FromMinutes(5)) return;
            if (!Monitoring && Waiting.QueueAcknowledgement(decode, Status, waitingCall ?? selectedQueuedCall, now, !finishing))
                Notice("Получено позднее " + words[2] + " от " + peer + ". Позывной в очереди на завершение связи.");
        }


        void ExpireWaiting(DateTime now)
        {
            string currentCall = Status != null && (Status.TxEnabled || Status.Transmitting) ? Status.DxCall : null;
            Waiting.Expire(now, TimeSpan.FromMinutes(Options.QueueMinutes), currentCall, waitingCall ?? selectedQueuedCall);
        }

        bool ReplyWindowComplete(DateTime boundary, DateTime now)
        {
            DateTime receiveStart = boundary.Subtract(SlotDuration);
            return now >= boundary || lastDecodeFinished >= receiveStart.AddMilliseconds(500);
        }

        public bool IsMonitorCandidate(CqStation row, DateTime now)
        {
            if (!Monitoring || !IsMonitorMessage(row, now)) return false;
            if (Expeditions != null) return ExpeditionTarget(row.Call, now) != null;
            return Awards == null ? monitorTargets.CanTry(row.Call) : Awards.MatchCount(row.Call, now, Cqs.LocatorFor(row.Call, now)) > 0;
        }

        internal ExpeditionMatch ExpeditionTarget(string call, DateTime now)
        {
            return Expeditions == null ? null : Expeditions.Match(call, JtdxAdif.Band(sessionFrequency), sessionMode, now);
        }

        int MonitorPriority(CqStation row, DateTime now)
        {
            if (Expeditions != null)
            {
                ExpeditionMatch match = ExpeditionTarget(row.Call, now);
                return match == null ? 0 : match.Priority;
            }
            return Awards == null ? 0 : Awards.MatchCount(row.Call, now, Cqs.LocatorFor(row.Call, now));
        }

        bool IsMonitorMessage(CqStation row, DateTime now)
        {
            return now - row.HeardUtc <= SlotDuration + SlotDuration + TimeSpan.FromMilliseconds(500)
                && (row.Signoff == "73" || row.Signoff == "RRR" || row.Signoff == "RR73" || row.Signoff == null
                    && !row.Decode.Message.TrimStart().StartsWith("QRZ ", StringComparison.OrdinalIgnoreCase));
        }

        public string ExplainCq(string call, DateTime now)
        {
            CqStation row = Cqs.Find(call);
            if (!CanCall(call)) return "Действует исключение DXCC.";
            if (Status != null && CleanCall(Status.DxCall) == call && Status.TxEnabled)
                return "Эта станция уже выбрана в JTDX.";
            if (row == null) return "В списке ещё нет принятого CQ, 73, RRR или RR73 от этой станции.";
            if (row.Busy) return String.IsNullOrEmpty(row.Activity) ? "Станция ведёт другой обмен." : row.Activity + ".";
            if (now < row.NextTryUtc)
                return "Пауза после вызова до " + row.NextTryUtc.ToLocalTime().ToString("HH:mm:ss") + ". Затем нужно свежее сообщение.";
            if (!Cqs.Fresh(row, now)) return "Жду свежее сообщение от этой станции.";
            if (!Armed) return "Автомат выключен. Сообщение можно выбрать для ручного вызова.";
            if (finishing) return "Время сеанса вышло. AutoCQ заканчивает текущую связь и очередь.";
            if (Monitoring)
            {
                if (Expeditions != null)
                {
                    if (row.Worked) return "Связь с этой станцией уже проведена на текущем диапазоне.";
                    if (ExpeditionTarget(call, now) == null) return "Станция пока не подходит под цели поиска экспедиций.";
                }
                else if (Awards != null)
                {
                    if (Awards.MatchCount(call, now, Cqs.LocatorFor(call, now)) == 0) return Awards.Reason(call, now, Cqs.LocatorFor(call, now));
                }
                else
                {
                    MonitorEligibility eligible = monitorTargets.Eligibility(call);
                    if (eligible == MonitorEligibility.Outside) return "Станция не входит в цели мониторинга.";
                    if (eligible == MonitorEligibility.Worked) return "Цель уже выполнена на этом диапазоне. Включено ограничение «Одна связь на цель на диапазоне».";
                }
                if (!IsMonitorMessage(row, now)) return "Для мониторинга нужно свежее CQ, 73, RRR или RR73.";
            }
            else if (manualCq != call)
            {
                if (row.Worked) return "Связь с этой станцией уже завершена в текущем сеансе.";
                if (!Waiting.CanUseCq(row)) return "Эта станция уже обрабатывается в очереди вызовов.";
                bool priority = IsPriorityCall(call) && (row.Signoff == null || Options.PriorityAfter73);
                if (row.Signoff != null && !priority) return "Для вызова после прощания нужен приоритет DXCC.";
                if (!priority && !Options.SearchEnabled) return "Поиск станций из левого окна выключен.";
                if (!priority && !hunting && !cqThreshold) return "Поиск начнётся после заданного числа CQ без ответа.";
            }
            if (Status != null && !String.IsNullOrEmpty(Status.DxCall) && (Status.TxEnabled || Awaiting73))
                return "Сейчас идёт обмен с " + Status.DxCall + ". Выбор продолжится после связи.";
            WaitingStation next = Status == null ? null : NextCaller(now);
            if (next != null) return "Сначала AutoCQ ответит на входящий вызов " + next.Call + ".";
            if (Expeditions != null) return ExpeditionTarget(call, now).Reason + ".";
            if (Awards != null) return Awards.Reason(call, now, Cqs.LocatorFor(call, now));
            return "Станция подходит. AutoCQ проверит свежесть сообщения при следующем выборе.";
        }

        void ListenForTarget(DateTime now, string previousCall = null)
        {
            if (selectedQueuedCall != null || Status == null || Status.TxEnabled || Status.Transmitting) return;
            if (Status.Decoding) return;
            // Addressed callers are independent of the targets we search for.
            // Their original decode is retained even when the left pane clears.
            WaitingStation caller = NextCaller(now);
            if (caller != null)
            {
                loggedCall = null; ResetContact(previousCall ?? Status.DxCall);
                BeginCommand(now, caller.Decode);
                ReplyToQueuedCaller(caller);
                return;
            }
            if (finishing)
            {
                Stop("Время вышло. Мониторинг завершён, JTDX на приёме.", false);
                return;
            }
            if (now < WaitForVisibleCqsUntil) return;
            bool manual = manualCq != null;
            CqStation station = manual ? Cqs.Find(manualCq) : null;
            if (manual && (!Cqs.Fresh(station, now) || !CanCall(manualCq)))
            {
                Notice("Сообщение выбранной вручную станции " + manualCq + " больше недоступно. Продолжаю мониторинг.");
                station = null; manual = false; manualCq = null;
            }
            if (!manual) station = Cqs.Next(now, CanCall, includeSignoffs: true,
                eligible: row => IsMonitorCandidate(row, now), allowWorked: Expeditions == null,
                priority: row => MonitorPriority(row, now));
            if (station == null) return;
            loggedCall = null; ResetContact(previousCall ?? Status.DxCall);
            manualCq = null;
            if (manual) manualMonitorCall = station.Call;
            BeginCommand(now, station.Decode);
            expectedPeriod = CallerQueue.ReplyFirst(station.Decode, sessionMode);
            replyTarget = selectedQueuedCall = lastContact = station.Call;
            Cqs.Attempt(station.Call, now);
            ReplyCommands++;
            ExpeditionMatch expedition = manual ? null : ExpeditionTarget(station.Call, now);
            Notice((manual ? "Ручной вызов " : "Цель мониторинга ") + station.Call + ". Вызываю по " + (station.Signoff ?? "CQ") + "."
                + (manual ? " После связи или лимита вызовов продолжу мониторинг."
                    : expedition != null ? " " + expedition.Reason + "."
                    : Awards == null ? "" : " " + Awards.Reason(station.Call, now, Cqs.LocatorFor(station.Call, now))));
            RequestReply(station.Decode.Copy());
        }

        public void Tick(DateTime now, bool connectionFresh)
        {
            ExpireWaiting(now);
            if (!Armed) return;
            if (now >= SessionDeadline && !finishing)
            {
                finishing=true;hunting=false;manualCq=null;
                Notice(Monitoring ? "Время вышло. Завершаю QSO и входящие вызовы, принятые до окончания времени." :
                    "Время вышло. Завершаю текущую связь и очередь; станции «Нет ответа» пропускаю.");
                if(commandOutstanding && replyTarget==null && Status!=null && !Status.Transmitting && String.IsNullOrEmpty(Status.DxCall) && lastCqTime==DateTime.MinValue)
                {commandOutstanding=false;expectedPeriod=null;pending=false;}
            }
            if (!connectionFresh) { Stop("Потеряна связь с JTDX: автомат выключен.", true); return; }
            if (unconfirmedManualCall != null)
            {
                if (now >= manualSelectionDeadline)
                {
                    if (!Monitoring && Status.TxFirst != sessionPeriod) StopForRadioStatus(Status, true);
                    else
                        Stop("JTDX включил передачу для " + unconfirmedManualCall + " без подтверждения выбора. "
                            + (Monitoring ? "Мониторинг выключен." : "Автомат выключен."), true);
                }
                return;
            }
            if (stoppingRetry != null)
            {
                if (now >= expires) Stop("JTDX не подтвердил остановку после лимита вызовов. Автомат выключен.", true);
                return;
            }
            if (unexplainedStop.HasValue && now >= unexplainedStop.Value)
            { Stop("JTDX остановлен без подтверждённого автоматического завершения. Автомат выключен.", false); return; }
            if (commandOutstanding && now >= commandDeadline)
            {
                if (replyTarget != null) Waiting.Pause(replyTarget, CallerQueue.ReplyRejected, now);
                Stop("JTDX не подтвердил " + (replyTarget == null ? "включение TX для CQ" : "ответ станции " + replyTarget) + ". Автомат выключен.", true); return;
            }
            DateTime? expectedRepeat = reverseNeedsReply ? waitUntil : terminalRepeatSlot;
            if (expectedRepeat.HasValue && (Awaiting73 || reverseNeedsReply) && now >= expectedRepeat.Value.Add(SlotDuration)
                && Status != null && !Status.Decoding && !Status.Transmitting && !unexplainedStop.HasValue)
            {
                if (reportReplacedTerminalRepeat)
                {
                    // An actual TX to the right station still needs a full reply window.
                    lastTerminalAttemptSlot = expectedRepeat;
                    terminalRepeatSlot = null;
                    terminalReportReceived = reportReplacedTerminalRepeat = false;
                    missedTerminalRepeats = 0;
                    silentTerminalAttempts++;
                    SetTerminalDeadline(expectedRepeat.Value);
                    Notice("JTDX передал рапорт вместо завершающего сообщения для " + waitingCall + ". Дослушиваю ответ.");
                }
                else if (CanRepeatTerminal && !pending && !commandOutstanding)
                    MissTerminalRepeat(expectedRepeat.Value, now);
                else
                {
                    string message = reverseNeedsReply ? "повтор 73" : "завершающую передачу";
                    Stop("JTDX не подтвердил " + message + " для " + waitingCall + ". Автомат выключен.", true);
                    return;
                }
            }
            if (callLimitExceeded && (attemptAnswered || attemptCall != CleanCall(Status == null ? null : Status.DxCall)
                || waitingCall != null || pending && allowClearDx))
                callLimitExceeded = false;
            if (FinishTerminal(now)) return;
            if (!pending && !commandOutstanding && !attemptAnswered && waitingCall == null
                && lastInitialSlot >= 0 && Status != null && Status.TxEnabled && !Status.Transmitting && !Status.Decoding
                && attemptCall == CleanCall(Status.DxCall) && lastAttemptReplySlot != lastInitialSlot + 1
                && lastDecodeFinished >= SlotStart(lastInitialSlot + 1).AddMilliseconds(500)
                && FreshCaller(now, attemptCall) != null)
            {
                stoppingRetry = attemptCall;
                yieldingToCaller = true;
                expires = now.AddSeconds(6);
                RequestHalt();
                return;
            }
            bool finalReplyWindowComplete = InitialCallLimitReached && !pending
                && ReplyWindowComplete(SlotStart(lastInitialSlot + 2), now);
            if ((callLimitExceeded || finalReplyWindowComplete) && Status != null && Status.TxEnabled && !Status.Decoding && !commandOutstanding
                && attemptCall == CleanCall(Status.DxCall))
            {
                stoppingRetry = attemptCall;
                callLimitExceeded = false;
                expires = now.AddSeconds(6);
                RequestHalt();
                return;
            }
            if (Monitoring && !pending)
            {
                if (!commandOutstanding && !unexplainedStop.HasValue) ListenForTarget(now);
                return;
            }
            if(!Monitoring && finishing && !pending && !commandOutstanding && unexplainedStop==null && Status!=null && waitingCall==null && selectedQueuedCall==null && (String.IsNullOrEmpty(Status.DxCall) || !Status.TxEnabled))
            {
                if(lastCqTime!=DateTime.MinValue)cqThreshold=true;
                else if(!Status.Transmitting && !Status.Decoding)
                {
                    if(Status.TxEnabled)
                    {
                        pending=true;cqHaltExpected=true;replyDuringCq=false;due=now.AddMilliseconds(900);expires=now.AddSeconds(45);unexplainedStop=null;RequestHalt();
                    }
                    else if(NextCaller(now)!=null || NextManual()!=null)
                    {allowClearDx=true;pendingOwner=Status.DxCall;Schedule(now,"Дорабатываю очередь после окончания времени.");}
                    else {Stop("Время вышло. Текущая связь и доступная очередь завершены; автомат выключен.",false);return;}
                }
            }
            bool switchPeriod = !Monitoring && !finishing && now >= lastResponseAt.AddMinutes(sessionSwitchMinutes) && manualCq == null;
            bool checkPriorityCq = !Monitoring && !finishing && HasPriorityCountries;
            bool checkManualCall = !Monitoring && NextManual() != null;
            if ((cqThreshold || switchPeriod || checkPriorityCq || checkManualCall) && cqTxEnded && !cqListening && !pending && !commandOutstanding
                && Status != null && Status.TxEnabled && !unexplainedStop.HasValue && String.IsNullOrEmpty(Status.DxCall))
            {
                cqListening = pending = true;
                replyDuringCq = false;
                due = lastCqTime.AddTicks(2 * SlotDuration.Ticks);
                expires = due.AddSeconds(45);
                // Pause the repeat for priority calls. Otherwise, leave the
                // receive window open for native AutoSelect and the queue.
                cqHaltExpected = !UseNativeSelection || finishing || switchPeriod || checkPriorityCq;
                if (cqHaltExpected)
                {
                    periodSwitchHalt = switchPeriod;
                    if (!checkPriorityCq || cqThreshold || switchPeriod)
                        Notice("CQ без ответа: " + cqCount + ". Дослушиваю последний ответный период.");
                    RequestHalt();
                }
            }
            if (!pending && !commandOutstanding && Status != null && Status.TxEnabled && waitingCall == null && String.IsNullOrEmpty(Status.DxCall))
            {
                WaitingStation waiting = NextCaller(now);
                bool cqBeforeFirstTransmission = lastCqTime == DateTime.MinValue
                    && (manualCq != null || checkManualCall || checkPriorityCq && NextPriorityCq(now) != null);
                if (cqBeforeFirstTransmission || waiting != null && !LetNativeChoose(now, waiting))
                { pending = true; replyDuringCq = true; due = now; expires = now.AddSeconds(45); }
            }
            if (!pending || commandOutstanding || Status == null) return;
            if (now >= expires)
            {
                Stop(terminalHaltRequested ? "JTDX не подтвердил переход на приём. Автомат выключен."
                    : cqListening ? "Не завершён переход после CQ: автомат выключен."
                    : "Не получено подтверждение завершения QSO: автомат выключен.", false);
                return;
            }
            if ((finishing || switchPeriod) && cqListening && cqTxEnded && Status.TxEnabled && !Status.Transmitting
                && !cqHaltExpected && !handoffHaltAcknowledged && !unexplainedStop.HasValue && String.IsNullOrEmpty(Status.DxCall))
            {
                // Stop between frames and still accept replies to the last
                // CQ that actually aired, including a delayed native CQ.
                DateTime endOfResponse = lastCqTime.AddTicks(2 * SlotDuration.Ticks);
                if (endOfResponse > due) due = endOfResponse;
                expires = due.AddSeconds(45);
                cqHaltExpected = true;
                periodSwitchHalt = switchPeriod;
                RequestHalt();
                return;
            }
            if ((now < due && (!cqListening || !ReplyWindowComplete(due, now)))
                || Status.Transmitting || Status.Decoding || unexplainedStop.HasValue) return;
            if (UseNativeSelection && allowClearDx && Status.TxEnabled && String.IsNullOrEmpty(Status.DxCall)
                && (Awaiting73 || finishing) && !cqHaltExpected && !terminalHaltRequested && !handoffHaltAcknowledged)
            {
                cqHaltExpected = true;
                RequestHalt();
                return;
            }
            if (cqHaltExpected || ((terminalHaltRequested || handoffHaltAcknowledged) && Status.TxEnabled)
                || (Awaiting73 && !ReplyWindowComplete(waitUntil, now)) || reverseNeedsReply) return;
            if (!String.IsNullOrEmpty(Status.DxCall) && (!allowClearDx || CleanCall(Status.DxCall) != CleanCall(pendingOwner))) return;
            if (Monitoring)
            {
                if (Status.TxEnabled)
                {
                    terminalHaltRequested = cqHaltExpected = true;
                    RequestHalt();
                    return;
                }
                CompleteConfirmedContact();
                RecordMissing73();
                string previousCall = waitingCall ?? pendingOwner ?? Status.DxCall;
                ResetContact();
                pending = false;
                if (!finishing) Notice("Мониторинг: проверяю входящие вызовы, затем продолжаю поиск целей.");
                ListenForTarget(now, previousCall);
                return;
            }
            if (cqListening)
            {
                bool nativeCqWindow = UseNativeSelection && Status.TxEnabled && !cqHaltExpected;
                if (nativeCqWindow && finishing)
                {
                    cqHaltExpected = true;
                    RequestHalt();
                    return;
                }
                if (nativeCqWindow) replyDuringCq = true;
                hunting = Options.SearchEnabled && !finishing
                    && (cqThreshold || !checkPriorityCq && !checkManualCall && !periodSwitchHalt);
            }
            CompleteConfirmedContact();
            if (completedCurrent) { hunting = false; ResetCqRound(); }
            WaitingStation next = NextCaller(now);
            WaitingStation direct = next == null ? NextManual() : null;
            if (next == null && direct == null && (checkPriorityCq || !switchPeriod && (hunting || manualCq != null))
                && now < WaitForVisibleCqsUntil) return;
            bool keepCqCount = cqListening && checkPriorityCq && !cqThreshold && !switchPeriod && !finishing;
            cqListening = false;
            CqStation cq = next == null && direct == null ? NextCq(now, switchPeriod) : null;
            bool nativeCleared = UseNativeSelection && allowClearDx && String.IsNullOrEmpty(Status.DxCall);
            bool hasDirectedCall = next != null || direct != null || cq != null;
            if (Status.TxEnabled && nativeCleared && !hasDirectedCall)
            {
                // TriggerCQ can leave an already enabled Status packet unchanged. Get a
                // confirmed receive state first so the next command has a distinct TX acknowledgement.
                terminalHaltRequested = cqHaltExpected = true;
                expires = now.AddSeconds(6);
                Notice("Жду подтверждения приёма перед новым CQ.");
                RequestHalt();
                return;
            }
            if (Status.TxEnabled && !nativeCleared && (!replyDuringCq || !hasDirectedCall))
            {
                if (replyDuringCq)
                {
                    pending = false;
                    if (UseNativeSelection && !keepCqCount) ResetCqRound();
                }
                return;
            }
            RecordMissing73();
            if(finishing && next==null && direct==null)
            {
                Stop("Время вышло. Текущая связь и доступная очередь завершены; автомат выключен.", false);
                return;
            }
            loggedCall = null; lastContact = null; ResetContact(waitingCall ?? pendingOwner ?? Status.DxCall);
            BeginCommand(now, next != null ? next.Decode : cq == null ? null : cq.Decode);
            if (next != null)
            {
                ReplyToQueuedCaller(next);
            }
            else if (direct != null)
            {
                hunting = false; ResetCqRound(); expectedPeriod = Status.TxFirst;
                activeSearchCall = replyTarget = selectedQueuedCall = lastContact = direct.Call;
                ReplyCommands++;
                Notice("Вызываю " + direct.Call + " из ручной очереди.");
                RequestDirectCall(direct.Call);
            }
            else if(cq!=null)
            {
                ResetCqRound();expectedPeriod=CallerQueue.ReplyFirst(cq.Decode,sessionMode);
                activeSearchCall=replyTarget=selectedQueuedCall=lastContact=cq.Call;manualCq=null;hunting=true;Cqs.Attempt(cq.Call,now);
                ReplyCommands++;
                if (cq.Signoff != null)
                    Notice("Вызываю " + cq.Call + " по " + cq.Signoff + " из левого окна.");
                else
                    Notice((checkPriorityCq && IsPriorityCall(cq.Call) ? "Вызываю приоритетную станцию из CQ: "
                        : "Вызываю станцию из CQ: ") + cq.Call + ".");
                RequestReply(cq.Decode.Copy());
            }
            else
            {
                hunting = false;
                ResetCqRound(keepCqCount);
                expectedPeriod = switchPeriod ? !sessionPeriod : sessionPeriod;
                Resumes++;
                if (!keepCqCount)
                    Notice(switchPeriod ? "Нет ответа " + sessionSwitchMinutes + " мин. CQ в другом периоде TX."
                        : "Нет готовых станций в очереди. Возобновляю CQ…");
                RequestCq();
            }
        }

        void RecordMissing73()
        {
            if (!Awaiting73 || missing73Recorded) return;
            missing73Recorded = true; Missing73Count++;
            SessionRow(waitingCall).MissingUtc = waitUntil;
            Notice("73 от " + waitingCall + " не получено. Повторов без ответа — " + silentTerminalRepeats + ".");
        }
    }

    public sealed class LogTail
    {
        readonly string directory;
        readonly Decoder decoder = Encoding.UTF8.GetDecoder();
        string file, remainder = "";
        long offset;
        bool atStart;
        public LogTail(string directory) { this.directory = directory; Reset(); }
        public void Reset()
        {
            file = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMM") + "_ALL.TXT");
            ResetReadState(File.Exists(file) ? new FileInfo(file).Length : 0);
        }
        void ResetReadState(long position)
        {
            offset = position;
            remainder = "";
            atStart = position == 0;
            decoder.Reset();
        }
        public void Read(Action<string> onLine)
        {
            string current = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMM") + "_ALL.TXT");
            if (current != file) { file = current; ResetReadState(0); }
            if (!File.Exists(file)) return;
            byte[] bytes;
            int count;
            try
            {
                using (FileStream s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long length = s.Length;
                    if (length < offset) ResetReadState(0);
                    if (length == offset) return;
                    s.Position = offset;
                    // Bound each read and release the file before invoking callbacks.
                    bytes = new byte[(int)Math.Min(262144, length - offset)];
                    count = s.Read(bytes, 0, bytes.Length);
                }
            }
            catch (IOException e)
            {
                int error = e.HResult & 0xffff;
                // Retry on the next tick if JTDX is recreating the file or a writer holds it.
                if (error == 2 || error == 3 || error == 32 || error == 33) return;
                throw;
            }
            offset += count;
            var characters = new char[Encoding.UTF8.GetMaxCharCount(count)];
            int lengthDecoded = decoder.GetChars(bytes, 0, count, characters, 0, false);
            if (lengthDecoded == 0) return;
            int start = atStart && characters[0] == '\uFEFF' ? 1 : 0;
            atStart = false;
            string text = remainder + new string(characters, start, lengthDecoded - start);
            int end = text.LastIndexOf('\n');
            if (end < 0) { remainder = text.Length < 65536 ? text : ""; return; }
            foreach (string line in text.Substring(0, end).Split('\n')) onLine(line.TrimEnd('\r'));
            remainder = text.Substring(end + 1);
        }
    }

    public sealed class UdpBridge : IDisposable
    {
        readonly UdpClient socket;
        readonly string expectedId;
        public IPEndPoint Upstream;
        public IPEndPoint Client { get; private set; }
        public int Port { get { return ((IPEndPoint)socket.Client.LocalEndPoint).Port; } }
        public Action<QtPacket> Received = delegate { };
        public Action<string> Error = delegate { };
        public UdpBridge(int port, string expectedId)
        {
            this.expectedId = expectedId;
            socket = new UdpClient(AddressFamily.InterNetwork);
            socket.Client.ExclusiveAddressUse = true;
            socket.Client.Bind(new IPEndPoint(IPAddress.Loopback, port));
            // An absent logbook must not turn a local UDP ICMP reply into a radio stop.
            try { socket.Client.IOControl(unchecked((IOControlCode)(-1744830452)), new byte[4], null); }
            catch (SocketException) { }
        }
        public void ResetClient() { Client = null; }
        public void SendToClient(byte[] data)
        { if (Client != null) socket.Send(data, data.Length, Client); }
        public void Poll()
        {
            for (int n = 0; n < 300 && socket.Available > 0; n++)
            {
                IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = socket.Receive(ref remote);
                if (Upstream != null && remote.Equals(Upstream))
                {
                    try
                    {
                        uint type = QtPacket.Parse(data).Type;
                        if (type == 52 || type == 53) continue;
                    }
                    catch (InvalidDataException) { continue; }
                    catch (EndOfStreamException) { continue; }
                    catch (DecoderFallbackException) { continue; }
                    SendToClient(data);
                    continue;
                }
                QtPacket packet;
                try { packet = QtPacket.Parse(data); } catch (Exception) { continue; }
                if (packet.Id != expectedId || (Client != null && !Client.Equals(remote))) continue;
                Client = remote; Received(packet);
                if (Upstream != null)
                {
                    try { socket.Send(data, data.Length, Upstream); }
                    catch (SocketException e) { Error(e.Message); }
                }
                if (packet.Type == 6) Client = null;
            }
        }
        public void Dispose() { socket.Close(); }
    }
}
