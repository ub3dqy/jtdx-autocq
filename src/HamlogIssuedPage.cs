using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace JtdxAutoCq
{
    internal sealed class HamlogIssuedPage
    {
        static readonly Regex Panel = HamlogCatalogClient.Tag(@"<div\b[^>]*\bid\s*=\s*[""']myawards-md[""'][^>]*>(.*?)</fieldset>");
        static readonly Regex Table = HamlogCatalogClient.Tag(@"<table\b[^>]*\bid\s*=\s*[""']id[12][""'][^>]*>(.*?)</table>");
        static readonly Regex Head = HamlogCatalogClient.Tag(@"<thead\b[^>]*>(.*?)</thead>");
        static readonly Regex Heading = HamlogCatalogClient.Tag(@"<th\b[^>]*>(.*?)</th>");
        static readonly Regex Body = HamlogCatalogClient.Tag(@"<tbody\b[^>]*>(.*?)</tbody>");
        static readonly Regex Empty = HamlogCatalogClient.Tag(@"\A(?:нет выданных дипломов|нет данных(?: в таблице)?|записи отсутствуют|no data available in table|no matching records found|no awards issued)[.!…]*\z");
        static readonly Regex AccountHeading = HamlogCatalogClient.Tag(@"\A(?:позывной|call\s?sign)\z");
        static readonly Regex TitleHeading = HamlogCatalogClient.Tag(@"\A(?:название(?: диплома)?|award name|title)\z");
        static readonly Regex IssuedHeading = HamlogCatalogClient.Tag(@"\A(?:дата(?: выдачи)?|issue date|date issued|date)\z");
        internal readonly List<AwardProgram> Awards = new List<AwardProgram>();
        internal int Unreadable;

        internal static HamlogIssuedPage Read(string file, string own)
        {
            if (new FileInfo(file).Length > 16 * 1024 * 1024)
                throw new InvalidDataException("Страница выданных дипломов превышает 16 МБ.");
            return Parse(File.ReadAllText(file, Encoding.UTF8), own);
        }

        internal static HamlogIssuedPage Parse(string html, string own)
        {
            own = Callsign.Require(own);
            string table = Table.Match(Panel.Match(html).Groups[1].Value).Groups[1].Value;
            string[] headings = Heading.Matches(Head.Match(table).Groups[1].Value).Cast<Match>()
                .Select(tag => HamlogCatalogClient.Label(tag.Groups[1].Value)).ToArray();
            Match body = Body.Match(table);
            if (headings.Length != 6 || !TitleHeading.IsMatch(headings[1]) || !AccountHeading.IsMatch(headings[2])
                || !IssuedHeading.IsMatch(headings[3]) || !body.Success)
                throw new InvalidDataException("Сохраните страницу «Дипломы» из своего кабинета, открыв «Мои полученные дипломы».");

            var result = new HamlogIssuedPage();
            bool hasAnyStation = false, hasOwnStation = false;
            foreach (Match row in HamlogCatalogClient.Row.Matches(body.Groups[1].Value))
            {
                string[] cells = HamlogCatalogClient.Cell.Matches(row.Groups[1].Value).Cast<Match>()
                    .Select(tag => tag.Groups[1].Value).ToArray();
                if (cells.Length == 1 && Empty.IsMatch(HamlogCatalogClient.Label(cells[0]))) continue;
                if (cells.Length != 5 && cells.Length != 6) { result.Unreadable++; continue; }
                string call = Callsign.Parse(HamlogCatalogClient.Label(cells[2]));
                if (call == null) { result.Unreadable++; continue; }
                hasAnyStation = true;
                if (call != own) continue;
                hasOwnStation = true;
                string title = HamlogCatalogClient.Label(cells[1]);
                if (title.Length == 0 || title.Length > 300 || !HamlogCatalogClient.Label(cells[3]).Any(Char.IsDigit)
                    || !HamlogCatalogClient.Label(cells[4]).Any(Char.IsDigit))
                { result.Unreadable++; continue; }
                string href = HamlogCatalogClient.Link.Match(cells[1]).Groups[2].Value;
                result.Awards.Add(new AwardProgram {
                    Title = title, Url = HamlogLinks.Award(WebUtility.HtmlDecode(href), HamlogLinks.Catalog), HasIssued = true
                });
            }
            if (hasAnyStation && !hasOwnStation)
                throw new InvalidDataException("В сохранённой таблице нет позывного " + own + ".");
            if (result.Awards.Count == 0 && result.Unreadable > 0)
                throw new InvalidDataException("Не удалось прочитать выданные дипломы в сохранённой странице HAMLOG.");
            return result;
        }

        internal static string TitleKey(string title)
        {
            return Regex.Replace(title.Normalize(NormalizationForm.FormKC).ToUpperInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
        }
    }
}
