using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Serialization;

namespace JtdxAutoCq
{
    public sealed class AutoCqOptions
    {
        public int Wait73Periods = 2;
        public int CallAttempts = 2;
        public int RunMinutes = 60;
        public int CqAttemptsBeforeSearch = 3;
        public int SwitchPeriodMinutes = 5;
        public int QueueMinutes = 5;
        public string[] ExcludedDxcc = new string[0];
        public string[] PriorityDxcc = new string[0];
        public bool SearchEnabled = true;
        public bool PriorityAfter73 = true;
        public bool MonitorEnabled;
        public bool MonitorOnePerBand = true;
        public string[] MonitorDxcc = new string[0];
        public string[] MonitorCalls = new string[0];
        public string[] MonitorMasks = new string[0];
        public void Validate()
        {
            if (ExcludedDxcc == null) ExcludedDxcc = new string[0];
            if (PriorityDxcc == null) PriorityDxcc = new string[0];
            if (MonitorDxcc == null) MonitorDxcc = new string[0];
            MonitorCalls = MonitorTargets.NormalizeCalls(MonitorCalls);
            MonitorMasks = MonitorTargets.NormalizeMasks(MonitorMasks);
            if (Wait73Periods < 1 || Wait73Periods > 20 || CallAttempts < 1 || CallAttempts > 5)
                throw new InvalidOperationException("Ожидание 73: от 1 до 20 ответных периодов; вызовов без ответа: от 1 до 5.");
            if (RunMinutes < 1 || RunMinutes > 1440 || CqAttemptsBeforeSearch < 1 || CqAttemptsBeforeSearch > 20)
                throw new InvalidOperationException("Время автомата: 1–1440 минут; CQ до поиска: 1–20.");
            if (SwitchPeriodMinutes < 1 || SwitchPeriodMinutes > 1440 || QueueMinutes < 1 || QueueMinutes > 1440)
                throw new InvalidOperationException("Смена периода и хранение позывного: от 1 до 1440 минут.");
        }
    }

    public sealed class DecodedMessage
    {
        public bool IsNew, LowConfidence, OffAir;
        public uint Milliseconds, DeltaFrequency;
        public int Snr;
        public double DeltaTime;
        public string Mode, Message;
        public DateTime UtcTime(DateTime now)
        {
            if (Milliseconds >= 86400000) throw new InvalidDataException("Неверное время декодирования.");
            DateTime time = now.Date.AddMilliseconds(Milliseconds);
            if (time > now.AddHours(12)) time = time.AddDays(-1);
            if (time < now.AddHours(-12)) time = time.AddDays(1);
            return time;
        }
        public DecodedMessage Copy() { return (DecodedMessage)MemberwiseClone(); }
    }

    public sealed class WaitingStation
    {
        public string Call, OwnCall, Mode, PauseReason;
        public ulong Frequency;
        [XmlElement("FirstHeardUtc")] public DateTime QueuedUtc;
        public DateTime LastHeardUtc, PausedUtc;
        public DecodedMessage Decode;
        public bool AddedByOperator;
        [XmlIgnore] public bool Available;
    }

    public static class Callsign
    {
        static readonly Regex Valid = new Regex(@"^[A-Z0-9][A-Z0-9/]{1,28}[A-Z0-9]$", RegexOptions.CultureInvariant);
        public static string Clean(string call) { return (call ?? "").Trim().Trim('<','>').ToUpperInvariant(); }
        internal static string Parse(string call)
        {
            call = Clean(call);
            return Valid.IsMatch(call) && call.Any(Char.IsDigit) && call.Any(c => c >= 'A' && c <= 'Z') ? call : null;
        }
        public static string Require(string call)
        {
            string parsed = Parse(call);
            if (parsed == null) throw new InvalidOperationException("Введите позывной латинскими буквами и цифрами.");
            return parsed;
        }
        public static string RequireDirect(string call)
        {
            string parsed = Require(call);
            if (!IsDirect(parsed)) throw new InvalidOperationException("Для прямого вызова введите позывной до 11 знаков. Можно использовать один знак /.");
            return parsed;
        }
        public static bool IsDirect(string call)
        { return call != null && call.Length <= 11 && call.Count(c => c == '/') <= 1; }
    }

    // Keep the original decoded fields for Reply, including when the
    // left window has already been cleared.
    public sealed class CallerQueue
    {
        public const string NoAnswer = "Нет ответа";
        public const string ReplyRejected = "Ответ не принят JTDX";
        readonly List<WaitingStation> rows = new List<WaitingStation>();
        readonly Dictionary<string, WaitingStation> heard = new Dictionary<string, WaitingStation>();
        readonly Dictionary<string, DateTime> completed = new Dictionary<string, DateTime>();
        readonly Dictionary<string,DateTime> removedThrough = new Dictionary<string,DateTime>();
        string context;
        public Action Changed = delegate { };
        public bool AnswerWorkedB4 { get; set; }
        public ReadOnlyCollection<WaitingStation> Rows { get { return rows.AsReadOnly(); } }
        public void Load(IEnumerable<WaitingStation> saved)
        {
            rows.Clear(); heard.Clear(); completed.Clear(); removedThrough.Clear();
            foreach (WaitingStation item in saved ?? new WaitingStation[0])
            {
                item.Call = Callsign.Require(item.Call);
                if (rows.Any(r => r.Call == item.Call)) continue;
                // Older versions stored an empty decode only for manual entries.
                if (item.Decode == null && item.Frequency == 0) item.AddedByOperator = true;
                item.Available = false; rows.Add(item);
                if (item.QueuedUtc == DateTime.MinValue) item.QueuedUtc = DateTime.UtcNow;
                if(item.PauseReason==ReplyRejected)item.PauseReason=null;
            }
            Changed();
        }
        public void SetContext(RadioStatus status)
        {
            string next = status.Frequency + "/" + status.Mode + "/" + Callsign.Clean(status.DeCall);
            if (next == context) return;
            if (context != null) foreach (WaitingStation row in rows) row.Available = false;
            context = next; heard.Clear(); completed.Clear(); removedThrough.Clear(); Changed();
        }
        public void Invalidate()
        {
            foreach (WaitingStation row in rows) row.Available = false;
            heard.Clear(); Changed();
        }
        public void InvalidateCall(string call)
        {
            WaitingStation row=rows.FirstOrDefault(r=>r.Call==Callsign.Clean(call));
            if(row==null)return;row.Available=false;row.PauseReason=null;heard.Remove(row.Call);Changed();
        }
        public void Expire(DateTime now, TimeSpan lifetime, string currentCall, string selectedCall)
        {
            currentCall = Callsign.Clean(currentCall);
            int expired = rows.RemoveAll(row =>
            {
                if (row.Call == currentCall || row.Call == selectedCall || now - row.QueuedUtc < lifetime)
                    return false;
                removedThrough[row.Call] = row.LastHeardUtc;
                heard.Remove(row.Call);
                return true;
            });
            if (expired > 0) Changed();
        }
        internal static bool IsCaller(string payload)
        {
            if (payload == "RR73" || payload == "RRR" || payload == "73") return false;
            return Regex.IsMatch(payload, @"^(?:R?[+-]\d{2}|[A-R]{2}\d{2}(?:[A-X]{2})?)$", RegexOptions.CultureInvariant);
        }
        public void Observe(DecodedMessage decode, RadioStatus status, string activeCall, DateTime now)
        { Observe(decode,status,activeCall,now,true); }
        public void Observe(DecodedMessage decode, RadioStatus status, string activeCall, DateTime now, bool reactivateFailed)
        { Observe(decode, status, activeCall, now, reactivateFailed, false); }

        public bool QueueAcknowledgement(DecodedMessage decode, RadioStatus status, string activeCall, DateTime now, bool reactivateFailed)
        { return Observe(decode, status, activeCall, now, reactivateFailed, true); }

        public static bool IsAcknowledgement(string payload)
        { return payload == "RR73" || payload == "RRR"; }

        public static bool IsAcknowledgement(DecodedMessage decode)
        {
            string[] words = (decode == null ? "" : decode.Message ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return words.Length == 3 && IsAcknowledgement(words[2].ToUpperInvariant());
        }

        bool Observe(DecodedMessage decode, RadioStatus status, string activeCall, DateTime now, bool reactivateFailed, bool acknowledgement)
        {
            if (status == null || decode == null || decode.OffAir || (status.Mode != "FT8" && status.Mode != "FT4")) return false;
            if ((status.Mode == "FT8" && decode.Mode != "~") || (status.Mode == "FT4" && decode.Mode != ":" && decode.Mode != "+")) return false;
            string[] words = (decode.Message ?? "").ToUpperInvariant().Split(new[] {' '}, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length != 3 || Callsign.Clean(words[0]) != Callsign.Clean(status.DeCall)) return false;
            if (acknowledgement ? (!decode.IsNew || !IsAcknowledgement(words[2])) : !IsCaller(words[2])) return false;
            string call;
            try { call = Callsign.Require(words[1]); } catch (InvalidOperationException) { return false; }
            if (call == Callsign.Clean(status.DeCall)) return false;
            DateTime time = decode.UtcTime(now);
            if (time > now.AddSeconds(2) || (decode.IsNew && (now - time).TotalSeconds > 120)) return false;
            WaitingStation row = rows.FirstOrDefault(r => r.Call == call);
            DateTime workedAt;
            bool worked = completed.TryGetValue(call, out workedAt);
            if (acknowledgement && (worked || call == Callsign.Clean(activeCall)
                || call == Callsign.Clean(status.DxCall) || !reactivateFailed && (row == null || row.PauseReason == NoAnswer)))
                return false;
            if (!acknowledgement && row != null && row.Available && IsAcknowledgement(row.Decode)) return false;
            WaitingStation cached;
            if (heard.TryGetValue(call, out cached) && (cached.LastHeardUtc > time
                || acknowledgement && cached.LastHeardUtc == time && cached.Decode.Message == decode.Message)) return false;
            cached = new WaitingStation { Call = call, OwnCall = Callsign.Clean(status.DeCall), Mode = status.Mode,
                Frequency = status.Frequency, QueuedUtc = now, LastHeardUtc = time, Decode = decode.Copy(), Available = true };
            heard[call] = cached;
            if (row == null)
            {
                if (!decode.IsNew || call == Callsign.Clean(activeCall) || call == Callsign.Clean(status.DxCall)) return false;
                if (worked && (!AnswerWorkedB4 || time <= workedAt || words[2].StartsWith("R+", StringComparison.Ordinal)
                    || words[2].StartsWith("R-", StringComparison.Ordinal))) return false;
                DateTime removed;if(removedThrough.TryGetValue(call,out removed) && time<=removed)return false;
                rows.Add(cached); Changed(); return true;
            }
            row.OwnCall = cached.OwnCall; row.Mode = cached.Mode; row.Frequency = cached.Frequency;
            row.LastHeardUtc = time; row.Decode = cached.Decode; row.Available = true;
            if (reactivateFailed && decode.IsNew && (acknowledgement || time > row.PausedUtc)) row.PauseReason = null;
            Changed();
            return true;
        }
        public void Add(string call, DateTime? now = null)
        {
            call = Callsign.Require(call);
            WaitingStation existing = rows.FirstOrDefault(r => r.Call == call);
            if (existing != null)
            {
                if (existing.AddedByOperator) throw new InvalidOperationException("Этот позывной уже добавлен вручную.");
                existing.AddedByOperator = true;
                Retry(call);
                return;
            }
            completed.Remove(call);
            removedThrough.Remove(call);
            WaitingStation cached;
            if (heard.TryGetValue(call, out cached))
                rows.Add(new WaitingStation { Call = call, AddedByOperator = true, OwnCall = cached.OwnCall, Mode = cached.Mode, Frequency = cached.Frequency,
                    QueuedUtc = now ?? DateTime.UtcNow, LastHeardUtc = cached.LastHeardUtc, Decode = cached.Decode.Copy(), Available = cached.Available });
            else rows.Add(new WaitingStation { Call = call, AddedByOperator = true, QueuedUtc = now ?? DateTime.UtcNow });
            Changed();
        }
        public void Rename(string oldCall, string newCall)
        {
            newCall = Callsign.Require(newCall); oldCall = Callsign.Clean(oldCall);
            if (oldCall == newCall) return;
            int index = rows.FindIndex(r => r.Call == oldCall);
            if (index < 0) return;
            if (rows.Any(r => r.Call == newCall)) throw new InvalidOperationException("Этот позывной уже есть в очереди.");
            Add(newCall); WaitingStation added = rows[rows.Count - 1]; rows.RemoveAt(rows.Count - 1);
            added.QueuedUtc = rows[index].QueuedUtc;
            DateTime removed=rows[index].LastHeardUtc;rows[index] = added;removedThrough[oldCall]=removed; Changed();
        }
        public void Remove(string call)
        {
            call = Callsign.Clean(call);WaitingStation row=rows.FirstOrDefault(r=>r.Call==call);
            if(row!=null)removedThrough[call]=row.LastHeardUtc;
            rows.RemoveAll(r => r.Call == call);Changed();
        }
        public void Complete(string call, DateTime at)
        {
            call = Callsign.Clean(call);
            completed[call] = at;
            Remove(call);
        }
        public void Move(string call, int direction)
        {
            int index = rows.FindIndex(r => r.Call == Callsign.Clean(call)), next = index + direction;
            if (index < 0 || next < 0 || next >= rows.Count) return;
            WaitingStation row = rows[index]; rows.RemoveAt(index); rows.Insert(next, row); Changed();
        }
        public void Pause(string call, string reason, DateTime now)
        {
            WaitingStation row = rows.FirstOrDefault(r => r.Call == Callsign.Clean(call));
            if (row == null) return;
            row.PauseReason = reason; row.PausedUtc = now; Changed();
        }
        public void Retry(string call)
        {
            WaitingStation row = Find(call);
            if (row == null) return;
            row.PauseReason = null; row.PausedUtc = DateTime.MinValue; completed.Remove(row.Call); Changed();
        }
        public WaitingStation Find(string call)
        {
            string key = Callsign.Clean(call);
            return rows.FirstOrDefault(row => row.Call == key);
        }

        public bool CanUseCq(CqStation candidate)
        {
            WaitingStation row = Find(candidate.Call);
            if (row == null) return true;
            return row.PauseReason != ReplyRejected
                && (row.PauseReason != NoAnswer || candidate.HeardUtc > row.PausedUtc);
        }
        public static bool SamePeriod(WaitingStation row, RadioStatus status)
        {
            if (row.Decode == null) return false;
            return ReplyFirst(row.Decode,status.Mode) == status.TxFirst;
        }
        public static bool ReplyFirst(DecodedMessage decode, string mode)
        { double slot = mode == "FT4" ? 7500 : 15000; return ((long)Math.Floor((decode.Milliseconds + 500) / slot) % 2) != 0; }
        public string State(WaitingStation row, RadioStatus status)
        {
            if (!String.IsNullOrEmpty(row.PauseReason)) return row.PauseReason;
            if (row.AddedByOperator && !CanReply(row, status)) return "Готов к прямому вызову";
            if (!row.Available || row.Decode == null) return "Ждёт принятого вызова";
            if (status == null || row.Frequency != status.Frequency || row.Mode != status.Mode || row.OwnCall != Callsign.Clean(status.DeCall)) return "Другой диапазон/режим";
            if (!SamePeriod(row,status)) return "Другой период TX";
            if (Callsign.Clean(status.DxCall) == row.Call) return "Идёт QSO";
            return IsAcknowledgement(row.Decode) ? "Завершение QSO" : "Ожидает";
        }
        static bool CanReply(WaitingStation row, RadioStatus status)
        {
            return status != null && row.Available && row.Decode != null
                && row.Frequency == status.Frequency && row.Mode == status.Mode && row.OwnCall == Callsign.Clean(status.DeCall);
        }
        public WaitingStation NextManual(string ownCall, Predicate<string> canCall)
        {
            string own = Callsign.Clean(ownCall);
            return rows.FirstOrDefault(row => row.AddedByOperator && String.IsNullOrEmpty(row.PauseReason)
                && row.Call != own && Callsign.IsDirect(row.Call) && canCall(row.Call));
        }
        public WaitingStation Next(RadioStatus status)
        { return Next(status,false); }
        public WaitingStation Next(RadioStatus status, bool allowPeriodChange, Predicate<string> canCall = null)
        {
            return rows.FirstOrDefault(row =>
            {
                if (!CanReply(row, status) || canCall != null && !canCall(row.Call)) return false;
                string state = State(row, status);
                return state == "Ожидает" || state == "Завершение QSO" || allowPeriodChange && state == "Другой период TX";
            });
        }
    }

    public sealed class OperatorState
    {
        public AutoCqOptions Options = new AutoCqOptions();
        public List<WaitingStation> Stations = new List<WaitingStation>();
    }
    public sealed class OperatorStateStore
    {
        readonly string file;
        public OperatorStateStore(string directory) { file = Path.Combine(directory, "operator.xml"); }
        public OperatorState Load()
        {
            if (!File.Exists(file)) return new OperatorState();
            OperatorState state;
            using (FileStream s = File.OpenRead(file)) state = (OperatorState)new XmlSerializer(typeof(OperatorState)).Deserialize(s);
            if (state.Options == null) state.Options = new AutoCqOptions();
            state.Options.Validate(); return state;
        }
        public void Save(AutoCqOptions options, IEnumerable<WaitingStation> stations)
        {
            options.Validate(); Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temp = file + ".tmp";
            using (StreamWriter s = new StreamWriter(temp, false, new UTF8Encoding(false)))
                new XmlSerializer(typeof(OperatorState)).Serialize(s, new OperatorState { Options = options, Stations = stations.ToList() });
            if (File.Exists(file)) File.Replace(temp, file, null); else File.Move(temp,file);
        }
    }
}
