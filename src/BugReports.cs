using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace JtdxAutoCq
{
    [DataContract]
    internal sealed class BugReportData
    {
        [DataMember(Name = "schema")] public int Schema = 1;
        [DataMember(Name = "requestId")] public string RequestId = Guid.NewGuid().ToString("N");
        [DataMember(Name = "fromUtc")] public string FromUtc;
        [DataMember(Name = "toUtc")] public string ToUtc;
        [DataMember(Name = "autoCqVersion")] public string AutoCqVersion;
        [DataMember(Name = "jtdxVersion")] public string JtdxVersion;
        [DataMember(Name = "state")] public Dictionary<string, string> State;
        [DataMember(Name = "autoCqLog")] public string AutoCqLog = "";
        [DataMember(Name = "jtdxLog")] public string JtdxLog = "";
        [DataMember(Name = "limited")] public bool Limited;
        [DataMember(Name = "title", EmitDefaultValue = false)] public string Title;
        [DataMember(Name = "description", EmitDefaultValue = false)] public string Description;

        internal const int MaxBody = 2 * 1024 * 1024;
        internal string Number { get { return "ACQ-" + RequestId; } }

        internal BugReportData ForSupport(string title, string description)
        {
            var report = (BugReportData)MemberwiseClone();
            report.Schema = 2;
            report.RequestId = Guid.NewGuid().ToString("N");
            report.Title = title;
            report.Description = description;
            return report;
        }

        internal byte[] Encode()
        {
            using (var buffer = new MemoryStream())
            {
                var serializer = new DataContractJsonSerializer(typeof(BugReportData),
                    new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
                serializer.WriteObject(buffer, this);
                return buffer.ToArray();
            }
        }
    }

    internal sealed class BugReportCapture
    {
        sealed class LogFile
        {
            public readonly string Path;
            public readonly long Length;
            public LogFile(string path)
            {
                Path = path;
                var file = new FileInfo(path);
                Length = file.Exists ? file.Length : 0;
            }
        }

        const int LogLimit = 400000;
        const int LineLimit = 4096;
        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        static readonly Regex PacketDetails = new Regex(@"\s+(?:endpoint|bytes)=.*$", RegexOptions.CultureInvariant);
        static readonly Regex PrivateDetails = new Regex(
            @"(?i)(?:\b[A-Z]:[\\/]|\\\\[^\\\s]+\\|/(?:home|Users|tmp|var|mnt)/|https?://|[\w.+-]+@[\w.-]+\.[a-z]{2,}|\b(?:\d{1,3}\.){3}\d{1,3}\b|\b(?:password|passwd|api[_ -]?key|access[_ -]?token|authorization)\s*[:=]|\b(?:gh[pousr]_|github_pat_|sk-proj-))",
            RegexOptions.CultureInvariant);
        static readonly Regex RadioDecode = new Regex(@"^\s*[+-]?\d+\s+[+-]?[\d.,]+\s+\d+\s+[~:+]\s+",
            RegexOptions.CultureInvariant);
        static readonly string[] RadioEvents = {
            "Transmitting ", "Retransmitting ", "Halt Tx ", "EnableTx", "DX Call ", "Decoded ",
            "Decoding finished", "UDP Reply ", "double click on call processed", "QSO logged:",
            "Call to log:", "QSO history ", "AF TX/RX ", "partial loss of data"
        };
        readonly DateTimeOffset until;
        readonly LogFile module;
        readonly List<LogFile> radio = new List<LogFile>();
        internal BugReportData Data { get; private set; }

        internal BugReportCapture(DateTimeOffset click, string version, string jtdxVersion,
            Dictionary<string, string> state, string stateDirectory, string jtdxProfile)
        {
            until = click.ToUniversalTime();
            Data = new BugReportData {
                FromUtc = until.AddMinutes(-20).ToString("O", Invariant),
                ToUtc = until.ToString("O", Invariant),
                AutoCqVersion = version, JtdxVersion = jtdxVersion ?? "", State = state,
            };
            module = Snapshot(Path.Combine(stateDirectory, "runtime.log"));
            string newest = until.ToString("yyyyMM", Invariant);
            string oldest = until.AddMinutes(-20).ToString("yyyyMM", Invariant);
            radio.Add(Snapshot(Path.Combine(jtdxProfile, newest + "_ALL.TXT")));
            if (oldest != newest) radio.Add(Snapshot(Path.Combine(jtdxProfile, oldest + "_ALL.TXT")));
        }

        LogFile Snapshot(string path)
        {
            try { return new LogFile(path); }
            catch (IOException) { Data.Limited = true; }
            catch (UnauthorizedAccessException) { Data.Limited = true; }
            return null;
        }

        internal BugReportData Read()
        {
            Data.AutoCqLog = ReadLogs(new[] { module }, false);
            Data.JtdxLog = ReadLogs(radio, true);
            if (Data.Encode().Length > BugReportData.MaxBody)
            {
                Data.AutoCqLog = LastLines(Data.AutoCqLog, 140000);
                Data.JtdxLog = LastLines(Data.JtdxLog, 140000);
                Data.Limited = true;
            }
            return Data;
        }

        static string LastLines(string log, int limit)
        {
            if (log.Length <= limit) return log;
            int newline = log.IndexOf('\n', log.Length - limit);
            return newline < 0 ? "" : log.Substring(newline + 1);
        }

        string ReadLogs(IEnumerable<LogFile> files, bool native)
        {
            var result = new List<string>();
            int length = 0;
            DateTimeOffset from = until.AddMinutes(-20);
            DateTimeOffset lookBack = from.AddMinutes(-1);
            foreach (LogFile file in files)
            {
                if (file == null || file.Length == 0) continue;
                try
                {
                    using (var reader = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (reader.Length < file.Length) Data.Limited = true;
                        foreach (string line in ReadBackwards(reader, Math.Min(reader.Length, file.Length)))
                        {
                            DateTimeOffset time;
                            int message;
                            if (!ReadTime(line, native, out time, out message)) continue;
                            if (time < lookBack) break;
                            if (time < from || time > until) continue;
                            string payload = line.Substring(message).Trim();
                            if (native && !RadioDecode.IsMatch(payload) &&
                                !Array.Exists(RadioEvents, word => payload.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0))
                                continue;
                            payload = PacketDetails.Replace(payload, "");
                            Match sensitive = PrivateDetails.Match(payload);
                            if (sensitive.Success)
                                payload = payload.Substring(0, sensitive.Index).TrimEnd() + " [details removed]";
                            string entry = time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", Invariant) + " " + payload;
                            if (length + entry.Length + 1 > LogLimit)
                            {
                                Data.Limited = true;
                                return InOrder(result);
                            }
                            result.Add(entry);
                            length += entry.Length + 1;
                        }
                    }
                }
                catch (IOException) { Data.Limited = true; }
                catch (UnauthorizedAccessException) { Data.Limited = true; }
            }
            return InOrder(result);
        }

        static string InOrder(List<string> lines)
        {
            lines.Reverse();
            return String.Join("\n", lines);
        }

        static bool ReadTime(string line, bool native, out DateTimeOffset time, out int message)
        {
            time = default(DateTimeOffset);
            message = line.IndexOf(' ');
            if (!native)
                return message > 0 && DateTimeOffset.TryParseExact(line.Substring(0, message), "O", Invariant,
                    DateTimeStyles.None, out time);

            DateTime utc;
            message = line.Length >= 19 && line[15] == '.' ? 19 : 15;
            if (line.Length <= message || !DateTime.TryParseExact(line.Substring(0, message),
                message == 19 ? "yyyyMMdd_HHmmss.fff" : "yyyyMMdd_HHmmss", Invariant,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc)) return false;
            time = new DateTimeOffset(utc);
            return true;
        }

        // Read complete lines from the captured length. Later appends and a
        // partially written line at the click cannot change the report.
        static IEnumerable<string> ReadBackwards(FileStream reader, long length)
        {
            byte[] block = new byte[32768];
            var line = new List<byte>();
            bool complete = false, tooLong = false;
            while (length > 0)
            {
                int count = (int)Math.Min(block.Length, length);
                length -= count;
                reader.Position = length;
                int read = 0;
                while (read < count)
                {
                    int bytes = reader.Read(block, read, count - read);
                    if (bytes == 0) throw new EndOfStreamException();
                    read += bytes;
                }
                for (int i = read - 1; i >= 0; i--)
                {
                    if (block[i] == (byte)'\n')
                    {
                        if (complete && !tooLong && line.Count > 0) yield return DecodeLine(line);
                        complete = true;
                        tooLong = false;
                        line.Clear();
                    }
                    else if (complete)
                    {
                        if (line.Count < LineLimit) line.Add(block[i]);
                        else tooLong = true;
                    }
                }
            }
            if (complete && !tooLong && line.Count > 0) yield return DecodeLine(line);
        }

        static string DecodeLine(List<byte> line)
        {
            line.Reverse();
            return Encoding.UTF8.GetString(line.ToArray()).Trim('\r', '\ufeff');
        }
    }

    internal static class BugReportClient
    {
        internal const string IssuePage = UpdateClient.RepositoryUrl + "/issues/new";
        internal static readonly Uri Endpoint = new Uri(AppBrand.Website + "api/autocq/reports/");

        [DataContract]
        sealed class Receipt
        {
            [DataMember(Name = "number")] public string Number { get; set; }
        }

        internal static string Send(BugReportData report, Uri endpoint)
        {
            byte[] bytes = report.Encode();
            if (bytes.Length > BugReportData.MaxBody) throw new InvalidDataException("Отчёт слишком большой.");
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.Method = "POST";
            request.AllowAutoRedirect = false;
            request.ContentType = "application/json; charset=utf-8";
            request.Accept = "application/json";
            request.UserAgent = "JTDX-AutoCQ/" + report.AutoCqVersion;
            request.Timeout = request.ReadWriteTimeout = 15000;
            request.ContentLength = bytes.Length;
            using (Stream body = request.GetRequestStream()) body.Write(bytes, 0, bytes.Length);
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var content = new MemoryStream())
            {
                if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.Created)
                    throw new InvalidDataException("Сайт не подтвердил приём отчёта.");
                byte[] buffer = new byte[1024];
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (content.Length + count > 4096) throw new InvalidDataException("Некорректный ответ сайта.");
                    content.Write(buffer, 0, count);
                }
                content.Position = 0;
                var result = (Receipt)new DataContractJsonSerializer(typeof(Receipt)).ReadObject(content);
                if (result == null || result.Number != report.Number)
                    throw new InvalidDataException("Сайт не подтвердил номер отчёта.");
                return result.Number;
            }
        }

        internal static string IssueBody(string description, string version, string number)
        {
            return description.Trim() + "\n\nAutoCQ " + version + "\nНомер диагностики " + number;
        }

        internal static string DraftUrl(string title, string body)
        {
            string url = IssuePage + "?title=" + Uri.EscapeDataString(title) + "&body=" + Uri.EscapeDataString(body);
            return url.Length <= 5500 ? url : null;
        }
    }
}
