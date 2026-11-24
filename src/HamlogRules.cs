using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace JtdxAutoCq
{
    internal sealed class HamlogRules
    {
        internal readonly string Source, Text;
        internal readonly byte[] Pdf;

        internal HamlogRules(string source, string text, byte[] pdf)
        { Source = source; Text = text; Pdf = pdf; }

        static readonly Regex Links = Pattern(@"<a\b[^>]*\bhref\s*=\s*([""'])(.*?)\1[^>]*>(.*?)</a>");
        static readonly Regex Article = Pattern(@"<(main|article)\b[^>]*>(.*?)</\1>");
        static readonly Regex Body = Pattern(@"<body\b[^>]*>(.*?)</body>");
        static readonly Regex Decorations = Pattern(@"<!--.*?-->|<(script|style|nav|header|footer|form|svg)\b[^>]*>.*?</\1>");
        static readonly Regex NewLine = Pattern(@"</(?:p|h[1-6]|li|tr|section|div)>|<br\b[^>]*>");
        static readonly Regex Tags = Pattern(@"<[^>]+>");
        static Regex Pattern(string expression)
        { return new Regex(expression, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(3)); }

        internal static HamlogRules Read(string award, CancellationToken cancel)
        {
            string url = HamlogLinks.Award(award);
            if (url == null) throw new InvalidDataException("У диплома нет опубликованной страницы HAMLOG.");
            string known = HamlogLinks.PublishedRules(url);
            if (known != null)
            {
                HamlogRules rules = ReadDocument(known, cancel);
                if (rules != null) return rules;
            }
            byte[] page = HamlogPublicRequest.Read(url, "text/html", 3 * 1024 * 1024, cancel, publicRedirects: 3);
            string advertised = page == null ? null : FindPublishedLink(Encoding.UTF8.GetString(page), url);
            if (advertised != null && advertised != known)
            {
                HamlogRules rules = ReadDocument(advertised, cancel);
                if (rules != null) return rules;
            }
            return new HamlogRules(url, null, null);
        }

        static HamlogRules ReadDocument(string address, CancellationToken cancel)
        {
            byte[] data = HamlogPublicRequest.Read(address, "application/pdf, text/html;q=0.9, text/plain;q=0.7",
                24 * 1024 * 1024, cancel, publicRedirects: 3);
            return data == null ? null : ParseDocument(address, data);
        }

        internal static HamlogRules ParseDocument(string address, byte[] data)
        {
            if (data.Length >= 5 && data[0] == '%' && data[1] == 'P' && data[2] == 'D' && data[3] == 'F' && data[4] == '-')
                return new HamlogRules(address, null, data);
            string html = Encoding.UTF8.GetString(data);
            Match section = Article.Match(html);
            if (!section.Success) section = Body.Match(html);
            string document = section.Success ? section.Groups[section.Groups.Count - 1].Value : html;
            if (document.IndexOf('\0') >= 0) return new HamlogRules(address, null, null);
            return new HamlogRules(address, PlainText(document), null);
        }

        internal static string FindPublishedLink(string html, string award)
        {
            foreach (Match link in Links.Matches(Decorations.Replace(html, "")))
            {
                string target = HamlogLinks.Resolve(WebUtility.HtmlDecode(link.Groups[2].Value), award);
                if (target == null || target == award) continue;
                string title = PlainText(link.Groups[3].Value);
                if (Regex.IsMatch(new Uri(target).AbsolutePath, @"/rules(?:\.pdf|/)?\z", RegexOptions.IgnoreCase)
                    || Regex.IsMatch(title, @"\b(?:rules|regulations|award criteria|условия|положение)\b", RegexOptions.IgnoreCase))
                    return target;
            }
            return null;
        }

        static string PlainText(string html)
        {
            string withBreaks = NewLine.Replace(Decorations.Replace(html, ""), "\n");
            string text = WebUtility.HtmlDecode(Tags.Replace(withBreaks, " ")).Replace("\r\n", "\n").Replace('\r', '\n');
            text = Regex.Replace(text, @"[^\S\n]+", " ");
            return Regex.Replace(text, @"(?:\n\s*){2,}", "\n\n").Trim().Replace("\n", Environment.NewLine);
        }
    }
}
