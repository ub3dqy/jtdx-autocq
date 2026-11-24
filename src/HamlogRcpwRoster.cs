using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace JtdxAutoCq
{
    internal static class HamlogRcpwRoster
    {
        internal const string Url = "https://rcpw.ru/members.html";
        const int MaximumBytes = 2 * 1024 * 1024;
        static readonly object Gate = new object();
        static readonly Regex Row = new Regex(@"<tr(?:\s[^>]*)?>\s*<td\b[^>]*>([^<]*(?:<(?!/td\b)[^>]*>[^<]*)*)</td>"
            + @"\s*<td\b[^>]*>(.*?)</td>\s*<td\b[^>]*>(.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromSeconds(2));
        static readonly Regex Markup = new Regex(@"<[^>]+>");
        static readonly Regex MemberNumber = new Regex(@"\A#?\s*\d{1,5}\z");
        static Dictionary<string, string> current;
        static DateTime loadedUtc;

        internal static Dictionary<string, string> Read()
        {
            lock (Gate)
            {
                if (current != null && DateTime.UtcNow - loadedUtc < TimeSpan.FromHours(6)) return current;
                var request = (HttpWebRequest)WebRequest.Create(Url);
                request.AllowAutoRedirect = false;
                request.Timeout = request.ReadWriteTimeout = 12000;
                request.UserAgent = "JTDX-AutoCQ";
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (var body = new MemoryStream())
                {
                    if (response.StatusCode != HttpStatusCode.OK || response.ContentLength > MaximumBytes)
                        throw new IOException("Сайт RCPW не вернул список членов клуба.");
                    var buffer = new byte[8192];
                    int count;
                    while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (body.Length + count > MaximumBytes) throw new IOException("Список членов RCPW слишком большой.");
                        body.Write(buffer, 0, count);
                    }
                    current = ParseMembers(Encoding.UTF8.GetString(body.ToArray()));
                    if (current.Count < 100)
                    {
                        current = null;
                        throw new InvalidDataException("Не удалось прочитать список членов RCPW.");
                    }
                    loadedUtc = DateTime.UtcNow;
                    return current;
                }
            }
        }

        internal static string[] Parse(string html)
        { return ParseMembers(html).Keys.ToArray(); }

        internal static Dictionary<string, string> ParseMembers(string html)
        {
            return Row.Matches(html).Cast<Match>().Where(row => MemberNumber.IsMatch(Text(row.Groups[1].Value))
                && Text(row.Groups[3].Value).IndexOf("(SK)", StringComparison.OrdinalIgnoreCase) < 0)
                .SelectMany(row => Regex.Split(Text(row.Groups[2].Value), @"[\s,;]+")
                    .Select(token => new { Call = Callsign.Parse(token), Member = Text(row.Groups[1].Value).TrimStart('#').Trim() }))
                .Where(entry => entry.Call != null)
                .GroupBy(entry => entry.Call, StringComparer.Ordinal)
                .Where(group => group.Select(entry => entry.Member).Distinct(StringComparer.Ordinal).Count() == 1)
                .ToDictionary(group => group.Key, group => group.First().Member, StringComparer.Ordinal);
        }

        static string Text(string markup)
        { return WebUtility.HtmlDecode(Markup.Replace(markup, " ")).Trim(); }
    }
}
