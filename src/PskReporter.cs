using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Xml;

namespace JtdxAutoCq
{
    internal sealed class ReceptionReport
    {
        internal string Receiver, Grid, Band, Mode;
        internal DateTime HeardUtc;
        internal int? Snr;
    }

    internal static class PskReporter
    {
        const int MaxXml = 1024 * 1024;
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

        internal static ReceptionReport[] Fetch(string ownCall)
        {
            string url = "https://retrieve.pskreporter.info/query?senderCallsign=" + Uri.EscapeDataString(Callsign.Require(ownCall))
                + "&flowStartSeconds=-3600&rronly=1&nolocator=1&rptlimit=500";
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "AutoCQ/" + typeof(PskReporter).Assembly.GetName().Version.ToString(3) + " (ub3dqy.com)";
            request.AllowAutoRedirect = false;
            request.Timeout = request.ReadWriteTimeout = 10000;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (Stream input = response.GetResponseStream())
            {
                if (response.StatusCode != HttpStatusCode.OK || input == null || response.ContentLength > MaxXml)
                    throw new InvalidDataException("PSK Reporter вернул неожиданный ответ.");
                return Parse(input, ownCall, DateTime.UtcNow);
            }
        }

        internal static ReceptionReport[] Parse(Stream input, string ownCall, DateTime now)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = MaxXml, IgnoreWhitespace = true };
            var reports = new List<ReceptionReport>();
            string sender = Callsign.Clean(ownCall);
            long first = (long)(now.AddHours(-1) - Epoch).TotalSeconds;
            long last = (long)(now.AddMinutes(1) - Epoch).TotalSeconds;
            using (XmlReader reader = XmlReader.Create(input, settings))
            {
                if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "receptionReports")
                    throw new InvalidDataException("PSK Reporter вернул данные в неожиданном формате.");
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "receptionReport"
                        || Callsign.Clean(reader.GetAttribute("senderCallsign")) != sender) continue;
                    string mode = (reader.GetAttribute("mode") ?? "").Trim().ToUpperInvariant();
                    if (mode != "FT8" && mode != "FT4") continue;
                    long seconds, frequency;
                    if (!Int64.TryParse(reader.GetAttribute("flowStartSeconds"), NumberStyles.None, CultureInfo.InvariantCulture, out seconds)
                        || seconds < first || seconds > last
                        || !Int64.TryParse(reader.GetAttribute("frequency"), NumberStyles.None, CultureInfo.InvariantCulture, out frequency)) continue;
                    string band = JtdxAdif.Band(frequency), receiver = Callsign.Parse(reader.GetAttribute("receiverCallsign"));
                    if (receiver == null || band == null) continue;
                    int snr;
                    bool knownSnr = Int32.TryParse(reader.GetAttribute("sNR"), NumberStyles.Integer, CultureInfo.InvariantCulture, out snr);
                    reports.Add(new ReceptionReport { Receiver = receiver, Mode = mode, Band = band,
                        Grid = AwardLocator.Full(reader.GetAttribute("receiverLocator")), HeardUtc = Epoch.AddSeconds(seconds),
                        Snr = knownSnr ? (int?)snr : null });
                }
            }
            return reports.OrderByDescending(report => report.HeardUtc).Take(500).ToArray();
        }
    }
}
