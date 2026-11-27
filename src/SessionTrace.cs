using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace JtdxAutoCq
{
    public enum TraceEvent { Status, Decode, Native, Decision, Command, Timer, Clear, Stop, Pause, Resume }

    public sealed class TraceFrame
    {
        public DateTime AtUtc, WaitForReplayUntil;
        public TraceEvent Kind;
        public RadioStatus Status;
        public DecodedMessage Decode;
        public string Text;
        public bool Connected;
    }

    public sealed class SessionTrace
    {
        internal const int MaxFrames = 30000;
        public string Operation, OwnCall;
        public bool NativeSelection, Shortened;
        public DateTime StartUtc;
        public AutoCqOptions Options;
        public RadioStatus FirstStatus;
        public TraceFrame[] Frames = new TraceFrame[0];
        internal bool SupportsReplay { get { return Operation != "HAMLOG" && Operation != "Экспедиции"; } }

        internal static SessionTrace Read(string path)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = 32 * 1024 * 1024 };
            using (XmlReader reader = XmlReader.Create(path, settings))
            {
                var trace = (SessionTrace)new XmlSerializer(typeof(SessionTrace)).Deserialize(reader);
                if (trace == null || trace.Options == null || trace.Frames == null || trace.Frames.Length > MaxFrames
                    || trace.Frames.Any(frame => frame == null) || trace.FirstStatus == null)
                    throw new InvalidDataException("Не удалось прочитать запись сеанса AutoCQ.");
                return trace;
            }
        }

        internal void Save(string path)
        {
            var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = true };
            using (XmlWriter writer = XmlWriter.Create(path, settings)) new XmlSerializer(typeof(SessionTrace)).Serialize(writer, this);
        }

        internal static string Command(string action, string text = null)
        { return String.IsNullOrEmpty(text) ? action : action + " · " + text; }

        internal string Replay(AutoCqOptions options, Func<string, string> country)
        {
            if (!SupportsReplay) return "Эта запись доступна для просмотра. Автономный прогон поддерживает CQ и обычный мониторинг.";
            var engine = new AutoCqEngine { Options = options.Copy(), UseNativeSelection = NativeSelection, DxccForCall = country };
            engine.CanCall = call => !engine.Options.ExcludedDxcc.Contains(country(call));
            engine.IsPriorityCall = call => engine.Options.PriorityDxcc.Contains(country(call));
            var actions = new Queue<string>();
            engine.RequestCq = () => actions.Enqueue(Command("Общий вызов"));
            engine.RequestDirectCall = call => actions.Enqueue(Command("Вызов", call));
            engine.RequestCallerReply = message => actions.Enqueue(Command("Ответ вызывающей станции", message.Message));
            engine.RequestReply = message => actions.Enqueue(Command("Вызов по декоду", message.Message));
            engine.RequestHalt = () => actions.Enqueue(Command("Стоп TX"));

            RadioStatus status = FirstStatus;
            DateTime start = StartUtc;
            int begin = 0;
            if (status.Transmitting || (options.MonitorEnabled && status.TxEnabled))
            {
                begin = Array.FindIndex(Frames, frame => frame.Kind == TraceEvent.Status && frame.Status != null
                    && !frame.Status.TxEnabled && !frame.Status.Transmitting);
                if (begin < 0) return "Для прогона нужен отрезок, в котором JTDX перешёл на приём.";
                status = Frames[begin].Status;
                start = Frames[begin++].AtUtc;
            }
            engine.OnStatus(status, start);
            engine.Start(start);
            int played = 0;
            for (int index = begin; index < Frames.Length; index++)
            {
                TraceFrame frame = Frames[index];
                if (frame.Kind == TraceEvent.Command)
                {
                    string testAction = actions.Count == 0 ? "Жду следующего события" : actions.Dequeue();
                    if (testAction != frame.Text) return Difference(frame.AtUtc, frame.Text, testAction, played);
                    played++;
                    continue;
                }
                if (frame.Kind == TraceEvent.Decision) continue;
                if (actions.Count > 0) return Difference(frame.AtUtc, "Команды в записи нет", actions.Peek(), played);
                switch (frame.Kind)
                {
                    case TraceEvent.Status:
                        if (frame.Status != null) engine.OnStatus(frame.Status, frame.AtUtc);
                        break;
                    case TraceEvent.Decode:
                        if (frame.Decode != null) engine.OnDecode(frame.Decode.Copy(), frame.AtUtc);
                        break;
                    case TraceEvent.Native: engine.OnLine(frame.Text ?? "", frame.AtUtc); break;
                    case TraceEvent.Clear: engine.Cqs.Invalidate(); break;
                    case TraceEvent.Timer:
                        engine.WaitForVisibleCqsUntil = frame.WaitForReplayUntil;
                        engine.Tick(frame.AtUtc, frame.Connected);
                        break;
                    case TraceEvent.Stop: engine.Stop(frame.Text ?? "", frame.Connected); break;
                    case TraceEvent.Pause: engine.RequestPause(frame.AtUtc); break;
                    case TraceEvent.Resume: engine.Resume(frame.AtUtc); break;
                }
            }
            if (actions.Count > 0)
                return Difference(Frames.Length == 0 ? start : Frames[Frames.Length - 1].AtUtc,
                    "Команды в записи нет", actions.Peek(), played);
            return "Расхождений в командах нет. Проверено команд — " + played + ".";
        }

        static string Difference(DateTime time, string recorded, string proposed, int same)
        {
            return "Первое расхождение в " + time.ToString("HH:mm:ss.fff") + " UTC."
                + Environment.NewLine + "В сеансе · " + recorded
                + Environment.NewLine + "С выбранными настройками · " + proposed
                + Environment.NewLine + "Совпало команд — " + same + ".";
        }
    }

    internal sealed class SessionTraceBuffer
    {
        readonly Queue<TraceFrame> frames = new Queue<TraceFrame>();
        SessionTrace header;
        internal bool Active { get; private set; }

        internal void Start(DateTime now, RadioStatus status, AutoCqOptions options, bool native, string operation)
        {
            frames.Clear();
            header = new SessionTrace { StartUtc = now, OwnCall = status.DeCall, FirstStatus = status,
                Options = options.Copy(), NativeSelection = native, Operation = operation };
            Active = true;
        }

        internal void Status(RadioStatus status, DateTime now)
        {
            if (!Active) return;
            Add(new TraceFrame { Kind = TraceEvent.Status, AtUtc = now, Status = status });
        }

        internal void Add(TraceFrame frame)
        {
            if (!Active) return;
            frames.Enqueue(frame);
            while (frames.Count > SessionTrace.MaxFrames || frame.AtUtc - frames.Peek().AtUtc > TimeSpan.FromMinutes(20))
            {
                TraceFrame first = frames.Dequeue();
                if (first.Status != null) header.FirstStatus = first.Status;
                header.StartUtc = first.AtUtc;
                header.Shortened = true;
            }
        }

        internal void Stop() { Active = false; }

        internal SessionTrace Snapshot()
        {
            return header == null ? null : new SessionTrace { OwnCall = header.OwnCall, Operation = header.Operation,
                StartUtc = header.StartUtc, NativeSelection = header.NativeSelection, Shortened = header.Shortened,
                Options = header.Options.Copy(), FirstStatus = header.FirstStatus, Frames = frames.ToArray() };
        }
    }
}
