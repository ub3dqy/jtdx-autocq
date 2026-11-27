using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace JtdxAutoCq
{
    internal static class JtdxLogbookRecovery
    {
        internal static bool CanCompare(OperatingSession session, LogbookHistory target)
        {
            return target != null && target.IsLogHx && !String.IsNullOrWhiteSpace(session.OwnCall)
                && JtdxAdif.BandNames.Contains(session.Band) && (session.Mode == "FT8" || session.Mode == "FT4")
                && session.StartedUtc > DateTime.MinValue
                && session.Contacts.Any(row => row.LoggedUtc.HasValue);
        }

        internal static LogbookQso[] ReadMissing(OperatingSession session, string native, string target)
        {
            if (!File.Exists(native)) throw new FileNotFoundException("Файл журнала JTDX не найден.");
            // Take the LogHX snapshot last so that delayed journal writes can be noticed.
            var recorded = JtdxAdif.Contacts(native, preserveRecords: true);
            return FindMissing(session, LogbookHistory.Read(target), recorded);
        }

        internal static LogbookQso[] FindMissing(OperatingSession session, LogbookHistory target, IEnumerable<LogbookQso> native)
        {
            if (!CanCompare(session, target)) return new LogbookQso[0];
            var recorded = native.Where(qso => !String.IsNullOrEmpty(qso.RawAdif) && qso.Station == session.OwnCall
                && qso.Band == session.Band && qso.Mode == session.Mode && InSession(qso, session))
                .GroupBy(qso => new { qso.Call, qso.StartedUtc, qso.EndedUtc })
                .Select(group => group.First()).ToList();
            var verified = session.MatchLogbook(new LogbookHistory(null, recorded), loggedOnly: true);
            var inTarget = session.MatchLogbook(target, loggedOnly: true);
            var targetByCall = target.Records.Where(qso => qso.BelongsTo(session.OwnCall) && qso.Band == session.Band
                && (qso.Mode == session.Mode || String.IsNullOrEmpty(qso.Mode) || session.Mode == "FT4" && qso.Mode == "MFSK"))
                .ToLookup(qso => qso.Call);
            return verified.Where(pair => !inTarget.ContainsKey(pair.Key))
                .Select(pair => pair.Value)
                .Where(qso => !targetByCall[qso.Call].Any(existing => MayBeSameContact(qso, existing)))
                .OrderBy(qso => qso.StartedUtc).ToArray();
        }

        static bool InSession(LogbookQso qso, OperatingSession session)
        {
            if (!qso.StartedUtc.HasValue) return false;
            DateTime recorded = qso.EndedUtc ?? qso.StartedUtc.Value;
            return recorded >= qso.StartedUtc.Value && recorded >= session.StartedUtc.AddSeconds(-1)
                && (!session.StoppedUtc.HasValue || recorded <= session.StoppedUtc.Value.AddSeconds(1));
        }

        static bool MayBeSameContact(LogbookQso native, LogbookQso existing)
        {
            // Older LogHX exports may provide only one clock value or omit its seconds.
            return !existing.StartedUtc.HasValue && !existing.EndedUtc.HasValue
                || SameTime(native.StartedUtc, existing.StartedUtc) || SameTime(native.StartedUtc, existing.EndedUtc)
                || SameTime(native.EndedUtc, existing.StartedUtc) || SameTime(native.EndedUtc, existing.EndedUtc);
        }

        static bool SameTime(DateTime? left, DateTime? right)
        {
            if (!left.HasValue || !right.HasValue) return false;
            DateTime a = left.Value, b = right.Value;
            return a.Ticks / TimeSpan.TicksPerSecond == b.Ticks / TimeSpan.TicksPerSecond
                || (a.Second == 0 || b.Second == 0) && a.Ticks / TimeSpan.TicksPerMinute == b.Ticks / TimeSpan.TicksPerMinute;
        }

        internal static int Save(string file, IEnumerable<LogbookQso> records)
        {
            string[] raw = records.Select(qso => qso.RawAdif).Where(text => !String.IsNullOrEmpty(text)).Distinct().ToArray();
            if (raw.Length == 0) return 0;
            string destination = Path.GetFullPath(file);
            string ext = Path.GetExtension(destination).ToLowerInvariant();
            if (ext != ".adi" && ext != ".adif") throw new InvalidDataException("Укажите имя файла с расширением .adi или .adif.");
            if (File.Exists(destination)) throw new IOException("Файл уже существует. Выберите новое имя для экспорта.");
            string stage = Path.Combine(Path.GetDirectoryName(destination), ".autocq-export-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(output, new UTF8Encoding(false, true)))
                {
                    writer.Write("<ADIF_VER:5>3.1.4<PROGRAMID:6>AutoCQ<EOH>\r\n");
                    foreach (string record in raw) { writer.Write(record); writer.Write("\r\n"); }
                }
                File.Move(stage, destination);
            }
            finally { if (File.Exists(stage)) File.Delete(stage); }
            return raw.Length;
        }
    }
}
