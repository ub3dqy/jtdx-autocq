using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace JtdxAutoCq
{
    internal static class ExpeditionFeeds
    {
        internal const string CalendarUrl = "https://www.ng3k.com/Misc/adxoplain.html";
        internal const string RdaUrl = "https://rdaward.ru/?p=cluster";
        internal const string WantedRdaUrl = "https://rdaward.ru/?p=rda_wtd";
        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        static readonly Regex Paragraphs = Html(@"<p\b[^>]*>(.*?)</p>");
        static readonly Regex Breaks = Html(@"<br\s*/?>");
        static readonly Regex Tags = Html(@"<[^>]+>");
        static readonly Regex Whitespace = Html(@"\s+");
        static readonly Regex DateRange = Html(@"\A([a-z]{3})\s+(\d{1,2})(?:,\s*(\d{4}))?(?:\s*[-–]\s*(?:([a-z]{3})\s+)?(\d{1,2}))?,\s*(\d{4})\z");
        static readonly Regex CallTokens = Html(@"[A-Z0-9/]+");
        static readonly Regex OperatingAs = Html(@"\bas\s+([A-Z0-9/]+)\b");
        static readonly Regex DigitalMode = Html(@"\bFT[48]\b");
        static readonly Regex DigitalHint = Html(@"\b(?:DIGI(?:TAL)?|FT\s+modes?)\b");
        static readonly Regex Analog = Html(@"\b(?:CW|SSB|FM|RTTY)\b");
        static readonly Regex BandRange = Html(@"\b(\d{1,3})\s*[-–]\s*(\d{1,3})m\b");
        static readonly Regex RdaGroups = Html(@"class=""activator_\d+""[^>]*>.*?<a\b[^>]*class=""rdalnkc""[^>]*>(.*?)</a>.*?<table\b[^>]*>(.*?)</table>");
        static readonly Regex HtmlRows = Html(@"<tr\b[^>]*>(.*?)</tr>");
        static readonly Regex HtmlCells = Html(@"<td\b[^>]*>(.*?)</td>");
        static readonly Regex RdaReference = Html(@"\A[A-Z]{2}-\d{2}\z");
        static readonly Regex ParkReference = Html(@"\A[A-Z0-9]{1,4}-\d{4,5}\z");
        static readonly Regex NatureReference = Html(@"\A[A-Z0-9]{1,5}FF-\d{4,5}\z");
        static readonly Regex OffAir = Html(@"\A(?:QRT|OFF\s*AIR|OFFLINE)\b");

        static Regex Html(string pattern)
        { return new Regex(pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)); }

        internal static IEnumerable<ExpeditionSource> Enabled(ExpeditionOptions options)
        {
            if (options.DxTrips) yield return ExpeditionSource.Ng3k;
            if (options.Parks) yield return ExpeditionSource.Pota;
            if (options.Nature) yield return ExpeditionSource.Wwff;
            if (options.WantedRda || options.PortableRda) yield return ExpeditionSource.Rda;
            if (options.WantedRda) yield return ExpeditionSource.WantedRda;
            if (options.RareDx) yield return ExpeditionSource.DxRanks;
            if (options.DxTrips || options.RareDx || options.WantedDxcc.Length > 0) yield return ExpeditionSource.DxSpots;
        }

        internal static string Name(ExpeditionSource source)
        {
            switch (source)
            {
                case ExpeditionSource.Ng3k: return "NG3K";
                case ExpeditionSource.Pota: return "POTA";
                case ExpeditionSource.Wwff: return "WWFF";
                case ExpeditionSource.Rda: return "Кластер RDA";
                case ExpeditionSource.WantedRda: return "Wanted RDA";
                case ExpeditionSource.DxRanks: return "Club Log";
                default: return "Споты CQDX";
            }
        }

        internal static TimeSpan Interval(ExpeditionSource source)
        {
            switch (source)
            {
                case ExpeditionSource.Ng3k: return TimeSpan.FromHours(1);
                case ExpeditionSource.WantedRda:
                case ExpeditionSource.DxRanks: return TimeSpan.FromHours(6);
                default: return TimeSpan.FromMinutes(2);
            }
        }

        internal static ExpeditionFeedData Fetch(ExpeditionSource source)
        {
            string url;
            switch (source)
            {
                case ExpeditionSource.Ng3k: url = CalendarUrl; break;
                case ExpeditionSource.Pota: url = "https://api.pota.app/spot/activator"; break;
                case ExpeditionSource.Wwff: url = "https://spots.wwff.co/static/spots.json"; break;
                case ExpeditionSource.Rda: url = RdaUrl; break;
                case ExpeditionSource.WantedRda: url = WantedRdaUrl; break;
                case ExpeditionSource.DxRanks: url = "https://cqdx.io/api/v1/entities"; break;
                default: url = "https://cqdx.io/api/v1/spots?live=true"; break;
            }

            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "AutoCQ/" + typeof(ExpeditionFeeds).Assembly.GetName().Version.ToString(3) + " (ub3dqy.com)";
            request.AllowAutoRedirect = false;
            request.Timeout = request.ReadWriteTimeout = 8000;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            const int maxBody = 4 * 1024 * 1024;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (Stream input = response.GetResponseStream())
            using (var output = new MemoryStream())
            {
                if (response.StatusCode != HttpStatusCode.OK || response.ContentLength > maxBody || input == null)
                    throw new IOException("Недоступный ответ источника.");
                byte[] buffer = new byte[16384];
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + count > maxBody) throw new IOException("Ответ источника слишком большой.");
                    output.Write(buffer, 0, count);
                }
                return Parse(source, Encoding.UTF8.GetString(output.ToArray()), DateTime.UtcNow);
            }
        }

        internal static ExpeditionFeedData Parse(ExpeditionSource source, string content, DateTime now)
        {
            var data = new ExpeditionFeedData();
            switch (source)
            {
                case ExpeditionSource.Ng3k:
                    Require(content.IndexOf("Announced DX Operations", StringComparison.OrdinalIgnoreCase) >= 0);
                    data.Activities = Announcements(content, now).ToArray();
                    break;
                case ExpeditionSource.Pota:
                    data.Activities = Json<ParkSpot[]>(content).Where(row => row != null).Select(row => Spot(source, row.Call, row.Reference,
                        row.Mode, Kilohertz(row.Frequency), Utc(row.Time), 0, row.Comment)).Where(row => row != null).ToArray();
                    break;
                case ExpeditionSource.Wwff:
                    data.Activities = Json<NatureSpot[]>(content).Where(row => row != null).Select(row => Spot(source, row.Call, row.Reference,
                        row.Mode, row.Frequency * 1000, Unix(row.Time), 0, row.Comment)).Where(row => row != null).ToArray();
                    break;
                case ExpeditionSource.Rda:
                    Require(content.IndexOf("RDA CLUSTER", StringComparison.OrdinalIgnoreCase) >= 0);
                    data.Activities = RdaSpots(content, now).ToArray();
                    break;
                case ExpeditionSource.WantedRda:
                    foreach (Match row in HtmlRows.Matches(content))
                    {
                        string[] fields = Cells(row);
                        int rank, old;
                        if (fields.Length >= 6 && Int32.TryParse(fields[0], out rank) && rank >= 1 && rank <= 100
                            && RdaReference.IsMatch(fields[1]) && (!data.RdaRanks.TryGetValue(fields[1], out old) || rank < old))
                            data.RdaRanks[fields[1]] = rank;
                    }
                    Require(data.RdaRanks.Count >= 100);
                    break;
                case ExpeditionSource.DxRanks:
                    CqdxEntity[] entities = Json<EntityList>(content).Entities;
                    Require(entities != null && entities.Length > 0);
                    data.Entities = entities.Where(row => row != null && !row.Deleted && row.Id > 0 && !String.IsNullOrWhiteSpace(row.Prefix))
                        .Select(row => new ExpeditionEntity {
                            Dxcc = row.Id, Prefix = Callsign.Clean(row.Prefix).TrimStart('*'),
                            Name = Bounded(row.Name), Rank = row.Rank.GetValueOrDefault()
                        }).ToArray();
                    break;
                case ExpeditionSource.DxSpots:
                    CqdxSpot[] groups = Json<ClusterList>(content).Groups;
                    Require(groups != null);
                    data.Activities = groups.Where(row => row != null).Select(row => Spot(source, row.Call, "",
                        row.Mode, row.Frequency, Unix(row.Seen), row.Dxcc.GetValueOrDefault(), row.Comment))
                        .Where(row => row != null).ToArray();
                    break;
            }
            data.Activities = data.Activities.Where(row => row.Announced || row.Current(now))
                .OrderByDescending(row => row.SpottedUtc).ThenBy(row => row.StartUtc).Take(2000).ToArray();
            return data;
        }

        static IEnumerable<ExpeditionActivity> Announcements(string html, DateTime now)
        {
            foreach (Match paragraph in Paragraphs.Matches(html))
            {
                string[] lines = Breaks.Split(paragraph.Groups[1].Value).Select(Text).ToArray();
                DateTime from, until;
                if (lines.Length < 3 || !Dates(lines[0], out from, out until) || until <= now || from > now.AddDays(60)) continue;
                string place = Field(lines, "DXCC:"), info = Field(lines, "Info:");
                if (place.Length == 0) continue;
                string modes = String.Join(" ", DigitalMode.Matches(info).Cast<Match>().Select(mode => mode.Value.ToUpperInvariant()).Distinct());
                if (modes.Length == 0 && Analog.IsMatch(info) && !DigitalHint.IsMatch(info)) continue;
                string bands = Bands(info);
                IEnumerable<string> calls = CallTokens.Matches(Field(lines, "Callsign:")).Cast<Match>().Select(match => match.Value);
                calls = calls.Concat(OperatingAs.Matches(info).Cast<Match>().Select(match => match.Groups[1].Value));
                foreach (string call in calls.Select(FullCall).Where(value => value != null).Distinct())
                    yield return new ExpeditionActivity { Source = ExpeditionSource.Ng3k, Call = call, Place = place,
                        StartUtc = from, EndUtc = until, Mode = modes, Bands = bands };
            }
        }

        static string Bands(string text)
        {
            Match range = BandRange.Match(text);
            if (!range.Success) return "";
            IList<string> bands = JtdxAdif.BandNames;
            int first = bands.IndexOf(range.Groups[1].Value + "m"), last = bands.IndexOf(range.Groups[2].Value + "m");
            return first < 0 || last < first ? "" : String.Join(" ", bands.Skip(first).Take(last - first + 1));
        }

        static bool Dates(string text, out DateTime start, out DateTime until)
        {
            start = until = DateTime.MinValue;
            Match date = DateRange.Match(text);
            if (!date.Success) return false;
            string firstMonth = date.Groups[1].Value, lastMonth = date.Groups[4].Success ? date.Groups[4].Value : firstMonth;
            string lastYear = date.Groups[6].Value, firstYear = date.Groups[3].Success ? date.Groups[3].Value : lastYear;
            string lastDay = date.Groups[5].Success ? date.Groups[5].Value : date.Groups[2].Value;
            const DateTimeStyles utc = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
            if (!DateTime.TryParseExact(firstMonth + " " + date.Groups[2].Value + " " + firstYear, "MMM d yyyy", Invariant, utc, out start)
                || !DateTime.TryParseExact(lastMonth + " " + lastDay + " " + lastYear, "MMM d yyyy", Invariant, utc, out until)
                || start.Year < 2000 || until.Year > 2100) return false;
            if (until < start && !date.Groups[3].Success) start = start.AddYears(-1);
            until = until.AddDays(1);
            return until > start && until - start <= TimeSpan.FromDays(400);
        }

        static string Field(IEnumerable<string> lines, string key)
        {
            string line = lines.FirstOrDefault(value => value.StartsWith(key, StringComparison.OrdinalIgnoreCase));
            return line == null ? "" : Bounded(line.Substring(key.Length));
        }

        static IEnumerable<ExpeditionActivity> RdaSpots(string content, DateTime now)
        {
            foreach (Match group in RdaGroups.Matches(content))
            {
                string call = Text(group.Groups[1].Value);
                foreach (Match row in HtmlRows.Matches(group.Groups[2].Value))
                {
                    string[] fields = Cells(row);
                    TimeSpan time;
                    if (fields.Length != 9 || !TimeSpan.TryParseExact(fields[1], @"hh\:mm", Invariant, out time)) continue;
                    DateTime seen = now.Date.Add(time);
                    if (seen > now.AddMinutes(1)) seen = seen.AddDays(-1);
                    string reference = fields[6].ToUpperInvariant(), comment = fields[8];
                    Match mode = DigitalMode.Match(comment);
                    ExpeditionActivity spot = Spot(ExpeditionSource.Rda, call, reference, mode.Success ? mode.Value : fields[7],
                        Kilohertz(fields[5]), seen, 0, comment);
                    if (spot != null) yield return spot;
                }
            }
        }

        static ExpeditionActivity Spot(ExpeditionSource source, string call, string place, string mode, double frequency,
            DateTime time, int dxcc, string comment)
        {
            call = FullCall(call);
            place = (place ?? "").Trim().ToUpperInvariant();
            mode = (mode ?? "").Trim().ToUpperInvariant();
            if (call == null || (mode != "FT8" && mode != "FT4") || JtdxAdif.Band(frequency) == null
                || time == DateTime.MinValue || OffAir.IsMatch((comment ?? "").Trim())) return null;
            if (source == ExpeditionSource.Rda && !RdaReference.IsMatch(place)
                || source == ExpeditionSource.Pota && !ParkReference.IsMatch(place)
                || source == ExpeditionSource.Wwff && !NatureReference.IsMatch(place)) return null;
            return new ExpeditionActivity { Source = source, Call = call, Place = place, Mode = mode,
                Frequency = frequency, SpottedUtc = time, Dxcc = dxcc };
        }

        static string FullCall(string value)
        {
            string call = Callsign.Parse((value ?? "").Replace('Ø', '0').Replace('ø', '0'));
            return call != null && call.Split('/').Any(part => part.Length >= 3 && Char.IsLetter(part[part.Length - 1])
                && part.Any(Char.IsDigit)) ? call : null;
        }

        static DateTime Utc(string value)
        {
            DateTime parsed;
            return DateTime.TryParse(value, Invariant, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed)
                ? parsed : DateTime.MinValue;
        }
        static DateTime Unix(long seconds)
        { return seconds > 0 && seconds < 4102444800L ? Epoch.AddSeconds(seconds) : DateTime.MinValue; }
        static double Kilohertz(string value)
        {
            double parsed;
            return Double.TryParse(value, NumberStyles.Float, Invariant, out parsed) ? parsed * 1000 : 0;
        }
        static string Bounded(string value)
        { value = (value ?? "").Trim(); return value.Length <= 240 ? value : value.Substring(0, 240); }
        static string Text(string html)
        { return Whitespace.Replace(WebUtility.HtmlDecode(Tags.Replace(html, "")), " ").Trim(); }
        static string[] Cells(Match row)
        { return HtmlCells.Matches(row.Groups[1].Value).Cast<Match>().Select(cell => Text(cell.Groups[1].Value)).ToArray(); }

        static T Json<T>(string input) where T : class
        {
            using (var reader = new MemoryStream(Encoding.UTF8.GetBytes(input.TrimStart('\uFEFF'))))
            {
                var result = (T)new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings {
                    MaxItemsInObjectGraph = 250000
                }).ReadObject(reader);
                Require(result != null);
                return result;
            }
        }

        static void Require(bool valid)
        { if (!valid) throw new InvalidDataException("Источник вернул данные в неожиданном формате."); }

        [DataContract] sealed class ParkSpot
        {
            [DataMember(Name = "activator")] public string Call { get; set; }
            [DataMember(Name = "frequency")] public string Frequency { get; set; }
            [DataMember(Name = "mode")] public string Mode { get; set; }
            [DataMember(Name = "reference")] public string Reference { get; set; }
            [DataMember(Name = "spotTime")] public string Time { get; set; }
            [DataMember(Name = "comments")] public string Comment { get; set; }
        }

        [DataContract] sealed class NatureSpot
        {
            [DataMember(Name = "activator")] public string Call { get; set; }
            [DataMember(Name = "frequency_khz")] public double Frequency { get; set; }
            [DataMember(Name = "mode")] public string Mode { get; set; }
            [DataMember(Name = "reference")] public string Reference { get; set; }
            [DataMember(Name = "spot_time")] public long Time { get; set; }
            [DataMember(Name = "remarks")] public string Comment { get; set; }
        }

        [DataContract] sealed class EntityList { [DataMember(Name = "entities")] public CqdxEntity[] Entities { get; set; } }
        [DataContract] sealed class CqdxEntity
        {
            [DataMember(Name = "adifNumber")] public int Id { get; set; }
            [DataMember(Name = "prefix")] public string Prefix { get; set; }
            [DataMember(Name = "entityName")] public string Name { get; set; }
            [DataMember(Name = "rarityRank")] public int? Rank { get; set; }
            [DataMember(Name = "isDeleted")] public bool Deleted { get; set; }
        }

        [DataContract] sealed class ClusterList { [DataMember(Name = "groups")] public CqdxSpot[] Groups { get; set; } }
        [DataContract] sealed class CqdxSpot
        {
            [DataMember(Name = "dxCall")] public string Call { get; set; }
            [DataMember(Name = "mode")] public string Mode { get; set; }
            [DataMember(Name = "frequency")] public double Frequency { get; set; }
            [DataMember(Name = "lastSeen")] public long Seen { get; set; }
            [DataMember(Name = "dxDxcc")] public int? Dxcc { get; set; }
            [DataMember(Name = "latestComment")] public string Comment { get; set; }
        }
    }
}
