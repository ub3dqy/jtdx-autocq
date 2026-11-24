using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace JtdxAutoCq
{
    [DataContract]
    public sealed class AwardGoal
    {
        [DataMember(Name = "kind")] public string Kind;
        [DataMember(Name = "value")] public string Value;
        [DataMember(Name = "band")] public string Band;
        [DataMember(Name = "mode")] public string Mode;
        [DataMember(Name = "count")] public int? Count;

        internal int Required { get { return Count ?? 1; } }
        internal AwardGoal Copy() { return (AwardGoal)MemberwiseClone(); }

        internal void Validate()
        {
            Kind = (Kind ?? "").Trim().ToLowerInvariant();
            Band = (Band ?? "").Trim().ToLowerInvariant();
            Mode = (Mode ?? "").Trim().ToUpperInvariant();
            if (Kind == "call") Value = Callsign.Require(Value);
            else if (Kind == "mask") Value = MonitorTargets.NormalizeMasks(new[] { Value }).Single();
            else if (Kind == "dxcc")
            {
                Value = Callsign.Clean(Value);
                if (!Regex.IsMatch(Value, @"\A[A-Z0-9]{1,8}\z"))
                    throw new InvalidDataException("Укажите префикс страны DXCC, например JA или PY.");
            }
            else throw new InvalidDataException("Выберите тип цели. Поддерживаются позывные, страны DXCC и маски.");
            if (Required < 1 || Required > 1000 || Kind == "call" && Required != 1)
                throw new InvalidDataException("Для отдельного позывного нужна одна связь. Для DXCC или маски укажите от 1 до 1000 разных станций.");
            if (Band.Length != 0 && !JtdxAdif.BandNames.Contains(Band))
                throw new InvalidDataException("Неизвестный диапазон " + Band + ".");
            if (Mode.Length != 0 && Mode != "FT8" && Mode != "FT4")
                throw new InvalidDataException("Для цели можно выбрать FT8, FT4 или любой вид работы.");
        }
    }

    [DataContract]
    public sealed class AwardProgram
    {
        [DataMember(Name = "id")] public string Id;
        [DataMember(Name = "title")] public string Title;
        [DataMember(Name = "url")] public string Url;
        [DataMember(Name = "fromUtc")] public string FromUtc;
        [DataMember(Name = "untilUtc")] public string UntilUtc;
        [DataMember(Name = "hasIssued")] public bool? HasIssued;
        [DataMember(Name = "goals")] public List<AwardGoal> Goals = new List<AwardGoal>();
        [DataMember(Name = "manualSearch", EmitDefaultValue = false)] public bool ManualSearch;
        public bool Selected;
        public bool OtherLevel;
        public string Section, Sponsor, PublishedFromUtc, PublishedUntilUtc;
        public bool PublicEntry, Withdrawn;
        public string RuleDocumentStatus, RuleNote;
        public DateTime RulesCheckedUtc;
        public int RuleCompilerVersion;
        public HamlogRulePlan DownloadedRules, EditedRules;
        // The result belongs to the local operator's profile, not a catalog export.
        public HamlogStanding Hamlog;

        internal HamlogRulePlan EffectiveRules { get { return EditedRules ?? DownloadedRules; } }
        internal HamlogAward Template
        {
            get { return HamlogAward.Find(Url) ?? (ManualSearch && Goals.Count != 0 ? null : HamlogAward.FromDocument(this)); }
        }
        internal bool HasEditedRules { get { return EditedRules != null || ManualSearch && Goals.Count != 0; } }
        internal bool UsesTemplate
        {
            get
            {
                HamlogAward template = Template;
                return template != null && (Goals.Count == 0 || template.HasOnlineRules || template.DocumentPlan != null);
            }
        }
        internal bool UsesPointRules { get { return UsesTemplate && Template.HasPointRules; } }
        internal bool UsesOnlineRules { get { return UsesTemplate && Template.HasOnlineRules; } }
        internal bool UsesLocalTargets { get { return Goals.Count != 0 && (String.IsNullOrEmpty(Url) || ManualSearch && Template == null); } }
        internal bool HasSearchRules { get { return UsesLocalTargets || UsesPointRules || UsesOnlineRules; } }
        internal DateTime? From
        {
            get { return new[] { AwardCatalog.ReadTime(UsesTemplate ? Template.From : FromUtc), AwardCatalog.ReadTime(PublishedFromUtc) }.Max(); }
        }
        internal DateTime? Until
        {
            get { return new[] { AwardCatalog.ReadTime(UsesTemplate ? Template.Until : UntilUtc, true), AwardCatalog.ReadTime(PublishedUntilUtc, true) }.Min(); }
        }
        internal bool ClosedByIssuance
        {
            get { return HasIssued == true && !OtherLevel && Template != HamlogAward.Russia
                && !(EffectiveRules != null && EffectiveRules.MultiLevel); }
        }
        internal string CatalogKey { get { return HamlogLinks.Award(Url) ?? Id; } }

        internal AwardProgram Copy()
        {
            var copy = (AwardProgram)MemberwiseClone();
            copy.Goals = (Goals ?? new List<AwardGoal>()).Select(goal => goal.Copy()).ToList();
            return copy;
        }

        internal void Validate()
        {
            Title = (Title ?? "").Trim();
            Url = (Url ?? "").Trim();
            if (Title.Length == 0 || Title.Length > 300)
                throw new InvalidDataException("Укажите название диплома длиной до 300 символов.");
            if (Url.Length != 0)
            {
                Url = HamlogLinks.Award(Url);
                if (Url == null)
                    throw new InvalidDataException("Вставьте ссылку на страницу диплома на hamlog.online.");
            }
            if (String.IsNullOrWhiteSpace(Id))
                Id = Url.Length != 0 ? Url : "local-" + Guid.NewGuid().ToString("N");
            if (Id.Length > 512 || Id.Any(Char.IsControl))
                throw new InvalidDataException("Не удалось прочитать идентификатор диплома.");
            Goals = Goals ?? new List<AwardGoal>();
            FromUtc = (FromUtc ?? "").Trim();
            UntilUtc = (UntilUtc ?? "").Trim();
            DateTime? from = AwardCatalog.ReadTime(FromUtc), until = AwardCatalog.ReadTime(UntilUtc, true);
            if (from.HasValue && until.HasValue && from >= until)
                throw new InvalidDataException("Проверьте даты начала и окончания для «" + Title + "».");
            if (Goals.Count > 300 || Goals.Any(goal => goal == null))
                throw new InvalidDataException("У одного диплома может быть до 300 целей.");
            foreach (AwardGoal goal in Goals) goal.Validate();
            if (ClosedByIssuance || !HasSearchRules) Selected = false;
        }

        internal void SetPublication(AwardProgram source)
        {
            PublicEntry = source.PublicEntry;
            Withdrawn = source.Withdrawn;
            Section = source.Section; Sponsor = source.Sponsor;
            PublishedFromUtc = source.PublishedFromUtc; PublishedUntilUtc = source.PublishedUntilUtc;
        }

        internal void ApplyDocument(HamlogRuleResult result)
        {
            if (result.Status != "retry" || RulesCheckedUtc == DateTime.MinValue)
                RuleDocumentStatus = result.Status;
            if (result.Status == "retry") return;
            RuleNote = result.Note;
            DownloadedRules = result.Plan;
            RulesCheckedUtc = result.CheckedUtc;
            RuleCompilerVersion = HamlogPlanCompiler.Version;
        }
    }

    [DataContract]
    public sealed class AwardCatalog
    {
        [DataMember(Name = "version")] public int Version = 1;
        [DataMember(Name = "callsign")] public string Callsign;
        [DataMember(Name = "generatedUtc")] public string GeneratedUtc;
        [DataMember(Name = "programs")] public List<AwardProgram> Programs = new List<AwardProgram>();
        public DateTime PublicCatalogUtc;
        internal AwardCatalog Copy()
        {
            var copy = (AwardCatalog)MemberwiseClone();
            copy.Programs = (Programs ?? new List<AwardProgram>()).Select(program => program.Copy()).ToList();
            return copy;
        }

        internal static DateTime? ReadTime(string text, bool endOfDay = false)
        {
            if (String.IsNullOrWhiteSpace(text)) return null;
            var formats = new[] { "yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'" };
            DateTime value;
            if (!DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value)
                || value.Year < 1900 || value.Year > 9998)
                throw new InvalidDataException("Укажите дату в формате ГГГГ-ММ-ДД. Для точного времени используйте UTC.");
            return endOfDay && text.Length == 10 ? value.AddDays(1) : value;
        }

        internal void CheckCallsign(string own)
        {
            if (!String.IsNullOrEmpty(Callsign) && !String.IsNullOrEmpty(own)
                && !String.Equals(Callsign, own, StringComparison.Ordinal))
                throw new InvalidOperationException("Каталог дипломов относится к " + Callsign + ". В JTDX выбран " + own + ".");
        }

        internal void Import(string file, string own, bool personal = false)
        {
            var info = new FileInfo(file);
            if (info.Length > 8 * 1024 * 1024) throw new InvalidDataException("Файл дипломов превышает 8 МБ.");
            AwardCatalog incoming = UpdateClient.Json<AwardCatalog>(File.ReadAllBytes(file));
            if (incoming == null || incoming.Version != 1 || incoming.Programs == null || incoming.Programs.Count > 12000)
                throw new InvalidDataException("Выберите файл дипломов AutoCQ версии 1.");
            incoming.Callsign = String.IsNullOrWhiteSpace(incoming.Callsign) ? "" : JtdxAutoCq.Callsign.Require(incoming.Callsign);
            incoming.CheckCallsign(own);
            incoming.CheckCallsign(Callsign);
            DateTime? imported = ReadTime(incoming.GeneratedUtc), saved = ReadTime(GeneratedUtc);
            if (personal && (incoming.Callsign.Length == 0 || !imported.HasValue))
                throw new InvalidDataException("Выберите JSON с позывным и датой выгрузки или сохранённую страницу HAMLOG.");
            if (saved.HasValue && imported.HasValue && imported < saved)
                throw new InvalidDataException("Этот каталог старее последней сверки HAMLOG.");

            var merged = Programs.ToDictionary(program => program.Id, program => program.Copy(), StringComparer.Ordinal);
            var byPage = merged.Values.GroupBy(row => row.CatalogKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var pages = new HashSet<string>(StringComparer.Ordinal);
            foreach (AwardProgram row in incoming.Programs)
            {
                if (row == null) throw new InvalidDataException("В файле есть пустая запись диплома.");
                if (String.IsNullOrWhiteSpace(row.Id) && String.IsNullOrWhiteSpace(row.Url))
                    throw new InvalidDataException("Для строки «" + row.Title + "» укажите ссылку HAMLOG или постоянный идентификатор.");
                row.Validate();
                if (!ids.Add(row.Id) || !pages.Add(row.CatalogKey))
                    throw new InvalidDataException("В файле повторяется диплом «" + row.Title + "».");
                AwardProgram old;
                if (byPage.TryGetValue(row.CatalogKey, out old) || merged.TryGetValue(row.Id, out old))
                {
                    if (old.PublicEntry && old.CatalogKey != row.CatalogKey)
                        throw new InvalidDataException("Адрес диплома «" + row.Title + "» не совпадает с каталогом HAMLOG.");
                    row.Id = old.Id;
                    if (old.HasIssued == true || !row.HasIssued.HasValue) row.HasIssued = old.HasIssued;
                    if (row.CatalogKey == old.CatalogKey) row.Hamlog = old.Hamlog;
                    if (row.CatalogKey == old.CatalogKey)
                    {
                        row.DownloadedRules = old.DownloadedRules;
                        row.EditedRules = old.EditedRules;
                        row.RuleDocumentStatus = old.RuleDocumentStatus;
                        row.RuleNote = old.RuleNote;
                        row.RulesCheckedUtc = old.RulesCheckedUtc;
                        row.RuleCompilerVersion = old.RuleCompilerVersion;
                    }
                    row.SetPublication(old);
                    if (old.PublicEntry) row.Title = old.Title;
                    if (row.Goals.Count == 0)
                    {
                        row.Goals = old.Goals;
                        row.ManualSearch = old.ManualSearch;
                        if (String.IsNullOrEmpty(row.FromUtc) || old.ManualSearch) row.FromUtc = old.FromUtc;
                        if (String.IsNullOrEmpty(row.UntilUtc) || old.ManualSearch) row.UntilUtc = old.UntilUtc;
                    }
                    row.OtherLevel = old.OtherLevel;
                    row.Selected = old.Selected && !row.ClosedByIssuance && row.HasSearchRules;
                }
                merged[row.Id] = row;
            }
            Programs = merged.Values.ToList();
            NormalizeOnlinePrograms();
            if (imported.HasValue) GeneratedUtc = incoming.GeneratedUtc;
            if (incoming.Callsign.Length > 0) Callsign = incoming.Callsign;
        }

        internal void ApplyPublicCatalog(HamlogCatalogSnapshot snapshot, DateTime fetched)
        {
            var programs = Programs.Select(row => row.Copy()).ToList();
            var existing = programs.ToLookup(row => row.CatalogKey, StringComparer.Ordinal);
            foreach (AwardProgram row in programs)
                if (row.PublicEntry) row.Withdrawn = true;
            foreach (AwardProgram publication in snapshot.Programs)
            {
                AwardProgram[] rows = existing[publication.CatalogKey].ToArray();
                if (rows.Length == 0)
                {
                    AwardProgram added = publication.Copy();
                    if (added.Template != null) added.Title = added.Template.Title;
                    programs.Add(added);
                }
                foreach (AwardProgram row in rows)
                {
                    row.SetPublication(publication);
                    row.Url = publication.Url;
                    HamlogAward builtIn = HamlogAward.Find(publication.Url);
                    row.Title = builtIn == null ? publication.Title : builtIn.Title;
                }
            }
            Programs = programs;
            IncludeBuiltInPrograms();
            NormalizeOnlinePrograms();
            PublicCatalogUtc = fetched;
        }

        internal void IncludeBuiltInPrograms()
        {
            var identities = new HashSet<string>(Programs.Select(row => row.CatalogKey), StringComparer.Ordinal);
            foreach (HamlogAward template in HamlogAward.Templates)
                if (identities.Add(template.Url)) Programs.Add(template.Create());
        }

        internal int ImportIssuedPage(string file, string own)
        {
            own = JtdxAutoCq.Callsign.Require(own);
            CheckCallsign(own);
            HamlogIssuedPage issued = HamlogIssuedPage.Read(file, own);
            var programs = Programs.Select(row => row.Copy()).ToList();
            var byPage = programs.ToLookup(row => row.CatalogKey, StringComparer.Ordinal);
            var byTitle = programs.Where(row => !String.IsNullOrEmpty(row.Url))
                .ToLookup(row => HamlogIssuedPage.TitleKey(row.Title), StringComparer.Ordinal);
            var added = new Dictionary<string, AwardProgram>(StringComparer.Ordinal);
            int unmatched = issued.Unreadable;
            foreach (AwardProgram record in issued.Awards)
            {
                AwardProgram[] matches = record.Url == null ? new AwardProgram[0] : byPage[record.Url].ToArray();
                if (matches.Length == 0)
                {
                    string group = HamlogLinks.ProgramGroup(record.Url);
                    AwardProgram[] titles = byTitle[HamlogIssuedPage.TitleKey(record.Title)].Where(row =>
                        record.Url == null || row.Url.StartsWith(record.Url, StringComparison.Ordinal)
                        || group != null && HamlogLinks.ProgramGroup(HamlogLinks.Award(row.Url)) == group).ToArray();
                    if (titles.Length == 1) matches = titles;
                }
                if (matches.Length == 0 && record.Url != null && HamlogLinks.PublishedRules(record.Url) != null)
                {
                    AwardProgram newEntry;
                    if (!added.TryGetValue(record.Url, out newEntry))
                    {
                        newEntry = record.Copy();
                        newEntry.Validate();
                        added.Add(record.Url, newEntry);
                    }
                    matches = new[] { newEntry };
                }
                if (matches.Length == 0) { unmatched++; continue; }
                foreach (AwardProgram row in matches)
                {
                    row.HasIssued = true;
                    if (row.ClosedByIssuance) row.Selected = false;
                }
            }
            programs.AddRange(added.Values);
            Programs = programs;
            Callsign = own;
            NormalizeOnlinePrograms();
            return unmatched;
        }

        internal void NormalizeOnlinePrograms()
        {
            var result = new List<AwardProgram>();
            var known = new Dictionary<string, AwardProgram>(StringComparer.Ordinal);
            foreach (AwardProgram row in Programs)
            {
                if (!row.UsesOnlineRules || HamlogAward.Find(row.Url) == null) { result.Add(row); continue; }
                HamlogAward award = row.Template;
                AwardProgram prior;
                if (known.TryGetValue(award.Url, out prior))
                {
                    prior.Selected |= row.Selected;
                    prior.OtherLevel |= row.OtherLevel;
                    if (row.HasIssued == true) prior.HasIssued = true;
                    if (row.PublicEntry && (!prior.PublicEntry || prior.Withdrawn && !row.Withdrawn))
                        prior.SetPublication(row);
                    if (prior.Hamlog == null || row.Hamlog != null && row.Hamlog.CheckedUtc > prior.Hamlog.CheckedUtc)
                        prior.Hamlog = row.Hamlog;
                    continue;
                }
                var current = row.Copy();
                current.Id = current.Url = award.Url;
                current.Title = award.Title;
                current.Goals.Clear();
                known.Add(award.Url, current);
                result.Add(current);
            }
            Programs = result;
        }
    }

    internal sealed class AwardProgress
    {
        internal readonly AwardProgram Program;
        readonly DateTime? from, until;
        readonly GoalProgress[] goals;
        internal readonly HamlogPointProgress Points;
        internal readonly HamlogOnlineProgress Online;

        sealed class GoalProgress
        {
            readonly AwardGoal goal;
            readonly Predicate<string> matches;
            readonly HashSet<string> logged = new HashSet<string>(StringComparer.Ordinal);
            internal int Count { get { return logged.Count; } }
            internal bool Complete { get { return Count >= goal.Required; } }

            internal GoalProgress(AwardGoal goal, Func<string, string> dxcc)
            {
                this.goal = goal;
                if (goal.Kind == "dxcc") matches = call => String.Equals(dxcc(call), goal.Value, StringComparison.OrdinalIgnoreCase);
                else if (goal.Kind == "mask") matches = MonitorTargets.MaskPattern(goal.Value).IsMatch;
                else matches = call => call == goal.Value;
            }

            internal bool OnBand(string band, string mode)
            { return (goal.Band.Length == 0 || goal.Band == band) && (goal.Mode.Length == 0 || goal.Mode == mode); }

            internal bool Needs(string call, string band, string mode)
            { return !Complete && OnBand(band, mode) && !logged.Contains(call) && matches(call); }

            internal void Record(string call, string band, string mode)
            { if (Needs(call, band, mode)) logged.Add(call); }
        }

        internal AwardProgress(AwardProgram program, LogbookHistory history, string own, Func<string, string> dxcc)
        {
            Program = program;
            from = program.From; until = program.Until;
            goals = program.Goals.Select(goal => new GoalProgress(goal, dxcc)).ToArray();
            if (program.UsesOnlineRules)
            {
                Online = new HamlogOnlineProgress(program, history, own, dxcc);
                return;
            }
            if (program.UsesTemplate)
            {
                Points = new HamlogPointProgress(program, history, own, dxcc);
                return;
            }
            if (!program.UsesLocalTargets || program.ClosedByIssuance) return;
            foreach (LogbookQso row in history.Records)
                if (row.BelongsTo(own)) Record(row.Call, row.Band, row.Mode, row.StartedUtc ?? row.EndedUtc);
        }

        bool Within(DateTime? time)
        { return (!from.HasValue || time.HasValue && time >= from) && (!until.HasValue || time.HasValue && time < until); }

        internal int GoalsCount { get { return goals.Length; } }
        internal int CompletedGoals { get { return goals.Count(goal => goal.Complete); } }
        internal bool Complete { get { return Program.ClosedByIssuance
            || (Online != null ? Online.Complete : Points != null ? Points.ConfirmedComplete
                : Program.UsesLocalTargets && CompletedGoals == goals.Length); } }
        internal bool Expired(DateTime now) { return until.HasValue && now >= until.Value; }
        internal bool Upcoming(DateTime now) { return from.HasValue && now < from.Value; }
        internal bool CanSelectForSearch(DateTime now) { return Program.HasSearchRules && Within(now) && !Complete; }
        internal DateTime NextValidityChange(DateTime now)
        {
            if (Expired(now)) return DateTime.MaxValue;
            return Upcoming(now) && (!until.HasValue || from < until) ? from.Value : until ?? DateTime.MaxValue;
        }
        internal int LoggedFor(int index) { return goals[index].Count; }
        internal bool Available(string band, string mode, DateTime time)
        {
            return CanSelectForSearch(time) && (Online != null ? Online.Available(band, mode, time)
                : Points != null ? Points.Available(band, mode, time)
                : Program.UsesLocalTargets && goals.Any(goal => !goal.Complete && goal.OnBand(band, mode)));
        }
        internal bool Needs(string call, string band, string mode, DateTime time, string grid = null)
        {
            return CanSelectForSearch(time) && (Online != null ? Online.Needs(call, band, mode, time, grid)
                : Points != null ? Points.CandidatePoints(call, band, mode, time) > 0
                : Program.UsesLocalTargets && goals.Any(goal => goal.Needs(call, band, mode)));
        }
        internal void Record(string call, string band, string mode, DateTime? time)
        {
            if (!Within(time)) return;
            if (Program.UsesLocalTargets)
                foreach (GoalProgress goal in goals) goal.Record(call, band, mode);
            else if (Points != null && time.HasValue) Points.Record(call, band, mode, time.Value);
        }
    }

    internal sealed class AwardSearch
    {
        readonly AwardProgress[] programs;
        readonly string band, mode, own;

        internal AwardSearch(AwardCatalog catalog, LogbookHistory history, string own, string band, string mode, Func<string, string> dxcc)
        {
            catalog.CheckCallsign(own);
            this.band = band; this.mode = mode;
            this.own = own;
            programs = catalog.Programs.Where(program => program.Selected && !program.ClosedByIssuance && program.HasSearchRules)
                .Select(program => new AwardProgress(program, history, own, dxcc)).ToArray();
        }

        internal int Available(DateTime now) { return programs.Count(program => program.Available(band, mode, now)); }
        internal int Score(string call, DateTime now, string grid = null)
        { return programs.Count(program => program.Needs(call, band, mode, now, grid)); }
        internal string Reason(string call, DateTime now, string grid = null)
        {
            AwardProgress[] matches = programs.Where(program => program.Needs(call, band, mode, now, grid)).ToArray();
            if (matches.Length == 0) return "Станция не подходит для выбранных дипломов на текущем диапазоне и в этом виде работы.";
            string names = String.Join(", ", matches.Select(program => program.Program.Title + (program.Points == null ? ""
                : " (+" + program.Points.CandidatePoints(call, band, mode, now).ToString("0.##") + " очк.)")));
            string square = matches.Any(program => program.Program.Template == HamlogAward.Russia)
                ? " Незачтённый квадрат " + AwardLocator.Parse(grid) + "." : "";
            var districts = matches.Where(program => program.Online != null).Select(program => program.Online.NeededDistrict(call, grid))
                .Where(county => county != null).Distinct().ToArray();
            string district = districts.Length == 0 ? "" : " Незачтённые районы RDA " + String.Join(", ", districts) + ".";
            return "Подходит для " + names + "." + square + district;
        }
        internal void Record(string call, DateTime at)
        {
            foreach (AwardProgress program in programs)
                if (program.Program.UsesLocalTargets) program.Record(call, band, mode, at);
        }

        internal void Record(LogbookQso contact)
        {
            // Use the recorded start date for awards with daily repeat rules.
            if (!contact.StartedUtc.HasValue || contact.Band != band || contact.Mode != mode || !contact.BelongsTo(own)) return;
            foreach (AwardProgress program in programs)
            {
                if (program.Online != null) program.Online.Record(contact);
                else if (program.Points != null) program.Record(contact.Call, band, mode, contact.StartedUtc);
            }
        }
    }
}
