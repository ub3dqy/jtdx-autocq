using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace JtdxAutoCq
{
    internal static class HamlogLinks
    {
        internal const string Origin = "https://hamlog.online/";
        internal const string Catalog = Origin + "account/awards.php";
        static readonly Regex Standard = new Regex(
            @"\A/(?:club/[\w-]+|global/[\w-]+/[\w-]+)/(?:\d+|[a-f0-9]{32})(?:/rules)?/?\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static bool IsPublicHost(Uri url)
        {
            return url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps && url.Host == "hamlog.online"
                && url.IsDefaultPort && url.UserInfo.Length == 0;
        }

        internal static string Resolve(string value, string page)
        {
            Uri url;
            if (String.IsNullOrWhiteSpace(value) || !Uri.TryCreate(new Uri(page), value.Trim(), out url)
                || !IsPublicHost(url) || url.AbsolutePath == "/" || url.Query.Length != 0 || url.Fragment.Length != 0)
                return null;
            return url.GetLeftPart(UriPartial.Path);
        }

        internal static string Award(string value, string page = null)
        {
            Uri input;
            if (page == null && !Uri.TryCreate(value, UriKind.Absolute, out input)) return null;
            string url = Resolve(value, page ?? Origin);
            if (url == null || url.Length > 512) return null;
            string path = new Uri(url).AbsolutePath.TrimEnd('/');
            if (path == "/neca/info.php") path = "/neca";
            if (path == "/rrnars/rules.pdf") path = "/rrnars";
            if (Standard.IsMatch(path) && path.EndsWith("/rules", StringComparison.OrdinalIgnoreCase))
                path = path.Substring(0, path.Length - 6);
            return Origin.TrimEnd('/') + path + (path.EndsWith(".php", StringComparison.OrdinalIgnoreCase) ? "" : "/");
        }

        internal static string PublishedRules(string award)
        {
            if (award == Origin + "rrnars/") return award + "rules.pdf";
            string path = new Uri(award).AbsolutePath;
            return Standard.IsMatch(path) || Regex.IsMatch(path, @"\A/geo/[\w-]+/\z") ? award + "rules/" : null;
        }

        internal static string ProgramGroup(string award)
        {
            if (award == null || !Standard.IsMatch(new Uri(award).AbsolutePath)) return null;
            int lastSlash = award.LastIndexOf('/', award.Length - 2);
            return award.Substring(0, lastSlash + 1);
        }
    }

    internal static class HamlogPublicRequest
    {
        internal static byte[] Read(string address, string accept, int maximumBytes, CancellationToken cancel, string form = null)
        {
            var url = new Uri(address);
            if (!HamlogLinks.IsPublicHost(url)) throw new InvalidDataException("Ожидалась ссылка HAMLOG.");
            cancel.ThrowIfCancellationRequested();
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "JTDX-AutoCQ";
            request.Accept = accept;
            request.Headers[HttpRequestHeader.AcceptLanguage] = "en-US,en;q=0.9";
            request.Headers[HttpRequestHeader.CacheControl] = "no-cache";
            request.AllowAutoRedirect = false;
            request.Timeout = request.ReadWriteTimeout = 16000;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (cancel.Register(request.Abort))
            try
            {
                if (form != null)
                {
                    byte[] data = Encoding.UTF8.GetBytes(form);
                    request.Method = "POST";
                    request.ContentType = "application/x-www-form-urlencoded";
                    request.Headers["X-Requested-With"] = "XMLHttpRequest";
                    request.Referer = HamlogLinks.Catalog + "?cr";
                    request.ContentLength = data.Length;
                    using (Stream body = request.GetRequestStream()) body.Write(data, 0, data.Length);
                }
                using (var response = (HttpWebResponse)request.GetResponse())
                using (Stream input = response.GetResponseStream())
                using (var output = new MemoryStream())
                {
                    if (response.StatusCode != HttpStatusCode.OK || response.ContentLength > maximumBytes)
                        throw new IOException("HAMLOG не вернул ожидаемый файл.");
                    var buffer = new byte[8192];
                    int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (output.Length + count > maximumBytes) throw new InvalidDataException("Ответ HAMLOG слишком большой.");
                        output.Write(buffer, 0, count);
                    }
                    return output.ToArray();
                }
            }
            catch (WebException error)
            {
                cancel.ThrowIfCancellationRequested();
                using (var response = error.Response as HttpWebResponse)
                {
                    if (response != null && (int)response.StatusCode == 429)
                        throw new IOException("HAMLOG временно ограничил запросы. Повторите позже.");
                    if (response != null && response.StatusCode == HttpStatusCode.NotFound) return null;
                }
                throw new IOException("Не удалось получить ответ от HAMLOG.", error);
            }
        }
    }

    [DataContract]
    internal sealed class HamlogCatalogPage
    {
        [DataMember(Name = "draw")] public int Draw { get; set; }
        [DataMember(Name = "recordsFiltered")] public int Total { get; set; }
        [DataMember(Name = "recordsTotal")] public int? Unfiltered { get; set; }
        [DataMember(Name = "data")] public string[][] Rows { get; set; }
    }

    internal sealed class HamlogCatalogSnapshot
    {
        internal readonly IReadOnlyList<AwardProgram> Programs;
        internal HamlogCatalogSnapshot(IEnumerable<AwardProgram> programs)
        { Programs = programs.ToList().AsReadOnly(); }
    }

    internal static class HamlogCatalogClient
    {
        internal const int PageSize = 100;
        static readonly Regex GlobalTable = Tag(@"<table\b[^>]*\bid\s*=\s*[""']listgn[""'][^>]*>(.*?)</table>");
        static readonly Regex TerritoriesTab = Tag(@"<div\b[^>]*\bid\s*=\s*[""']tcawards-md[""'][^>]*>(.*?)</fieldset>");
        static readonly Regex TerritoryTable = Tag(@"(?:<h4\b[^>]*>(.*?)</h4>\s*)?<table\b[^>]*>(.*?)</table>");
        internal static readonly Regex Row = Tag(@"<tr\b[^>]*>(.*?)</tr>");
        internal static readonly Regex Cell = Tag(@"<td\b[^>]*>(.*?)</td>");
        internal static readonly Regex Link = Tag(@"<a\b[^>]*\bhref\s*=\s*([""'])(.*?)\1[^>]*>(.*?)</a>");
        static readonly Regex Markup = Tag(@"<!--.*?-->|<[^>]+>");
        static readonly Regex Comments = Tag(@"<!--.*?-->");
        static readonly string[] DateFormats = { "d MMM yyyy", "dd MMM yyyy", "d MMMM yyyy" };
        static readonly string World = "Международные и национальные";
        static readonly string Club = "Клубные и региональные";
        static readonly string Territory = "Территориальные";

        internal static Regex Tag(string expression)
        { return new Regex(expression, RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(3)); }

        internal static HamlogCatalogSnapshot Download(CancellationToken cancel, Action<int, int> progress)
        {
            byte[] publicPage = HamlogPublicRequest.Read(HamlogLinks.Catalog, "text/html", 2 * 1024 * 1024, cancel);
            if (publicPage == null) throw new IOException("Страница каталога HAMLOG недоступна.");
            return Parse(Encoding.UTF8.GetString(publicPage), (draw, offset) => {
                if (draw > 1 && cancel.WaitHandle.WaitOne(850)) cancel.ThrowIfCancellationRequested();
                byte[] page = HamlogPublicRequest.Read(HamlogLinks.Origin + "account/awardsshow.php?tc",
                    "application/json, */*;q=0.1", 2 * 1024 * 1024, cancel, Form(draw, offset));
                if (page == null) throw new IOException("Клубный каталог HAMLOG недоступен.");
                return UpdateClient.Json<HamlogCatalogPage>(page);
            }, progress);
        }

        static string Form(int draw, int start)
        {
            var values = new Dictionary<string, string> {
                { "draw", draw.ToString(CultureInfo.InvariantCulture) }, { "start", start.ToString(CultureInfo.InvariantCulture) },
                { "length", PageSize.ToString(CultureInfo.InvariantCulture) }, { "search[value]", "" }, { "search[regex]", "false" }
            };
            for (int column = 0; column < 3; column++)
            {
                string key = "columns[" + column + "]";
                values.Add(key + "[data]", column.ToString(CultureInfo.InvariantCulture));
                values.Add(key + "[name]", "");
                values.Add(key + "[searchable]", "true");
                values.Add(key + "[orderable]", "false");
                values.Add(key + "[search][value]", "");
                values.Add(key + "[search][regex]", "false");
            }
            return String.Join("&", values.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        }

        internal static HamlogCatalogSnapshot Parse(string html, Func<int, int, HamlogCatalogPage> request, Action<int, int> progress)
        {
            html = Comments.Replace(html ?? "", "");
            Match world = GlobalTable.Match(html), territories = TerritoriesTab.Match(html);
            if (!world.Success || !territories.Success) throw new InvalidDataException("HAMLOG не показал все разделы каталога.");
            var programs = new Dictionary<string, AwardProgram>(StringComparer.Ordinal);
            foreach (string[] row in DataRows(world.Groups[1].Value))
                Add(programs, World, Label(row[0]), row[1], "");
            foreach (Match table in TerritoryTable.Matches(territories.Groups[1].Value))
                foreach (string[] row in DataRows(table.Groups[2].Value))
                    Add(programs, Territory, Label(table.Groups[1].Value), row[0], row[1]);
            if (!programs.Values.Any(row => row.Section == World) || !programs.Values.Any(row => row.Section == Territory))
                throw new InvalidDataException("HAMLOG вернул пустой раздел каталога.");

            int total = 0, offset = 0, draw = 1;
            do
            {
                HamlogCatalogPage page = request(draw, offset);
                if (page == null || page.Draw != draw || page.Total < 1 || page.Total > 12000
                    || page.Unfiltered.HasValue && page.Unfiltered.Value != page.Total
                    || draw > 1 && page.Total != total || page.Rows == null
                    || page.Rows.Length != Math.Min(PageSize, page.Total - offset))
                    throw new InvalidDataException("Каталог HAMLOG изменился во время загрузки. Повторите обновление.");
                total = page.Total;
                foreach (string[] row in page.Rows)
                {
                    if (row == null || row.Length != 3 || row.Any(cell => cell == null))
                        throw new InvalidDataException("HAMLOG изменил формат списка клубных дипломов.");
                    Add(programs, Club, Label(row[0]), row[1], row[2]);
                }
                offset += page.Rows.Length;
                ++draw;
                if (progress != null) progress(offset, total);
            } while (offset < total);
            return new HamlogCatalogSnapshot(programs.Values);
        }

        static IEnumerable<string[]> DataRows(string table)
        {
            foreach (Match row in Row.Matches(table))
            {
                string[] cells = Cell.Matches(row.Groups[1].Value).Cast<Match>().Select(cell => cell.Groups[1].Value).ToArray();
                if (cells.Length == 0) continue;
                if (cells.Length < 2) throw new InvalidDataException("HAMLOG изменил формат строк каталога.");
                yield return cells;
            }
        }

        internal static string Label(string html)
        { return Regex.Replace(WebUtility.HtmlDecode(Markup.Replace(html, " ")), @"\s+", " ").Trim(); }

        static string Date(string value)
        {
            DateTime date;
            return DateTime.TryParseExact(value.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
                || DateTime.TryParseExact(value.Trim(), DateFormats, CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.None, out date)
                ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
        }

        static void Add(Dictionary<string, AwardProgram> programs, string section, string sponsor, string markup, string validity)
        {
            Match link = Link.Match(markup);
            string title = Label(link.Success ? link.Groups[3].Value : markup);
            if (title.Length == 0) throw new InvalidDataException("В каталоге HAMLOG есть строка без названия.");
            string url = HamlogLinks.Award(WebUtility.HtmlDecode(link.Groups[2].Value), HamlogLinks.Catalog);
            string id = url ?? "hamlog-announcement-" + Hash(section + "\n" + sponsor + "\n" + title);
            if (programs.ContainsKey(id)) return;
            string[] dates = Regex.Split(Label(validity), @"\s+[–—-]\s*");
            string start = Date(dates[0]), end = dates.Length == 2 ? Date(dates[1]) : "";
            if (end.Length != 0 && start.Length != 0 && String.CompareOrdinal(end, start) < 0) start = end = "";
            var award = new AwardProgram {
                Id = id, Title = title, Url = url, Section = section, Sponsor = sponsor, PublicEntry = true,
                PublishedFromUtc = start, PublishedUntilUtc = end
            };
            award.Validate();
            programs.Add(id, award);
        }

        internal static string Hash(string value)
        {
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }
    }
}
