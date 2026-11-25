using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace JtdxAutoCq
{
    internal sealed class LogbookQso
    {
        public string Call, Station, Band, Mode, Locator, StationLocator, County;
        public DateTime? StartedUtc, EndedUtc;

        public bool BelongsTo(string station)
        { return String.IsNullOrEmpty(Station) || String.IsNullOrEmpty(station) || Station == station; }

        public static DateTime? ReadUtc(string date, string time)
        {
            DateTime value;
            return DateTime.TryParseExact(date + time, new[] { "yyyyMMddHHmmss", "yyyyMMddHHmm" },
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out value) ? (DateTime?)value : null;
        }
    }

    internal sealed class LogbookHistory
    {
        internal static readonly LogbookHistory Empty = new LogbookHistory(null, new List<LogbookQso>());
        internal readonly string Path;
        internal readonly IReadOnlyList<LogbookQso> Records;
        internal bool IsLogHx { get { return String.Equals(System.IO.Path.GetExtension(Path), ".lhx2", StringComparison.OrdinalIgnoreCase); } }

        internal LogbookHistory(string path, List<LogbookQso> records)
        {
            Path = path;
            Records = records.AsReadOnly();
        }

        internal static LogbookHistory Read(string path)
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".lhx2") return new LogbookHistory(path, ReadLogHx(path));
            if (ext != ".adi" && ext != ".adif")
                throw new InvalidDataException("Выберите файл журнала LogHX (.lhx2) или экспорт ADIF (.adi, .adif).");
            if (!File.Exists(path)) throw new FileNotFoundException("Файл журнала не найден.", path);
            return new LogbookHistory(path, JtdxAdif.Contacts(path));
        }

        internal HashSet<string> WorkedCalls(string band, string ownCall, string mode = null)
        {
            return new HashSet<string>(Records.Where(q => q.BelongsTo(ownCall) && q.Band == band
                && (mode == null || q.Mode == mode)).Select(q => q.Call), StringComparer.OrdinalIgnoreCase);
        }

        internal HashSet<string> WorkedDxcc(DxccCountries countries, string band, string ownCall, string mode)
        {
            return new HashSet<string>(WorkedCalls(band, ownCall, mode).Select(countries.Find)
                .Where(country => country != null).Select(country => country.Prefix), StringComparer.OrdinalIgnoreCase);
        }

        static List<LogbookQso> ReadLogHx(string path)
        {
            string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AutoCQ-logbook-" + Guid.NewGuid().ToString("N"));
            string snapshot = System.IO.Path.Combine(directory, "snapshot.mdb");
            string reader = System.IO.Path.Combine(directory, "LogHxReader.exe");
            Directory.CreateDirectory(directory);
            try
            {
                CopyLogHx(path, snapshot);
                using (Stream asset = typeof(LogbookHistory).Assembly.GetManifestResourceStream("LogHxReader.exe"))
                {
                    if (asset == null) throw new InvalidDataException("В файлах AutoCQ отсутствует средство чтения LogHX.");
                    using (var executable = new FileStream(reader, FileMode.CreateNew, FileAccess.Write)) asset.CopyTo(executable);
                }
                var start = new ProcessStartInfo(reader) {
                    CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                start.EnvironmentVariables["AUTOCQ_LOGBOOK_SNAPSHOT"] = snapshot;
                start.EnvironmentVariables["AUTOCQ_LOGBOOK_TABLE"] = System.IO.Path.GetFileNameWithoutExtension(path);
                using (Process process = Process.Start(start))
                {
                    var output = process.StandardOutput.ReadToEndAsync();
                    var errors = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(30000))
                    {
                        if (!process.HasExited) process.Kill();
                        process.WaitForExit();
                        throw new IOException("LogHX не успел открыть журнал.");
                    }
                    if (process.ExitCode != 0)
                        throw new InvalidDataException(errors.Result.Trim());
                    return ReadLogHxRows(output.Result);
                }
            }
            finally
            {
                File.Delete(snapshot);
                File.Delete(System.IO.Path.ChangeExtension(snapshot, ".ldb"));
                File.Delete(reader);
                Directory.Delete(directory);
            }
        }

        static void CopyLogHx(string path, string snapshot)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var before = new FileInfo(path);
                if (!before.Exists) throw new FileNotFoundException("Файл журнала LogHX не найден.", path);
                using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var copy = new FileStream(snapshot, FileMode.Create, FileAccess.Write))
                    source.CopyTo(copy);
                var after = new FileInfo(path);
                if (before.Length == after.Length && before.LastWriteTimeUtc == after.LastWriteTimeUtc) return;
                Thread.Sleep(100);
            }
            throw new IOException("LogHX обновляет журнал. Повторите чтение через несколько секунд.");
        }

        internal static List<LogbookQso> ReadLogHxRows(string text)
        {
            var result = new List<LogbookQso>();
            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] columns = line.Split('\t');
                    if (columns.Length != 8 && columns.Length != 10 && columns.Length != 11)
                        throw new InvalidDataException("Не удалось прочитать записи LogHX.");
                    string call = Callsign.Parse(columns[0]);
                    if (call == null || String.IsNullOrWhiteSpace(columns[1])) continue;
                    result.Add(new LogbookQso {
                        Call = call, Band = columns[1].Trim().ToLowerInvariant(), Mode = columns[2].Trim().ToUpperInvariant(),
                        Station = Callsign.Clean(columns[3]),
                        Locator = columns.Length >= 10 ? AwardLocator.Full(columns[8]) : null,
                        County = columns.Length >= 10 ? columns[9].ToUpperInvariant() : null,
                        StationLocator = columns.Length == 11 ? AwardLocator.Full(columns[10]) : null,
                        StartedUtc = LogbookQso.ReadUtc(columns[4], columns[5]),
                        EndedUtc = LogbookQso.ReadUtc(columns[6].Length == 0 ? columns[4] : columns[6], columns[7]),
                    });
                }
            }
            return result;
        }
    }
}
