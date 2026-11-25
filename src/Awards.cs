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
        public string Section, Sponsor, PublishedFromUtc, PublishedUntilUtc;
        public bool PublicEntry, Withdrawn;
        public string RuleDocumentStatus, RuleNote;
        public DateTime RulesCheckedUtc;
        public int RuleCompilerVersion;
        public HamlogRulePlan DownloadedRules, EditedRules;

        internal HamlogRulePlan EffectiveRules { get { return EditedRules ?? DownloadedRules; } }
        internal HamlogAward Template
        {
            get { return HamlogAward.Find(Url) ?? (ManualSearch && Goals.Count != 0 ? null : HamlogAward.FromDocument(this)); }
        }
        internal bool HasEditedRules { get { return EditedRules != null || ManualSearch && Goals.Count != 0; } }
        internal bool UsesLocalTargets { get { return Goals.Count != 0 && (String.IsNullOrEmpty(Url) || ManualSearch && Template == null); } }
        internal bool HasSearchRules { get { return UsesLocalTargets || Template != null && Template.HasSearchRules; } }
        internal DateTime? From
        {
            get { return new[] { AwardCatalog.ReadTime(Template == null ? FromUtc : Template.From), AwardCatalog.ReadTime(PublishedFromUtc) }.Max(); }
        }
        internal DateTime? Until
        {
            get { return new[] { AwardCatalog.ReadTime(Template == null ? UntilUtc : Template.Until, true), AwardCatalog.ReadTime(PublishedUntilUtc, true) }.Min(); }
        }
        internal bool ClosedByIssuance { get { return HasIssued == true; } }
        internal bool Expired(DateTime now) { return Until <= now; }
        internal bool Upcoming(DateTime now) { return From > now; }
        internal bool CanSelectForSearch(DateTime now)
        { return !ClosedByIssuance && HasSearchRules && !Upcoming(now) && !Expired(now); }

        internal DateTime NextValidityChange(DateTime now)
        {
            if (ClosedByIssuance || Expired(now)) return DateTime.MaxValue;
            return Upcoming(now) ? From.Value : Until ?? DateTime.MaxValue;
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
                throw new InvalidDataException("Этот файл старее последнего загруженного списка HAMLOG.");

            var merged = Programs.ToDictionary(program => program.Id, program => program.Copy(), StringComparer.Ordinal);
            var byPage = merged.Values.GroupBy(row => row.CatalogKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var pages = new HashSet<string>(StringComparer.Ordinal);
            foreach (AwardProgram item in incoming.Programs)
            {
                if (item == null) throw new InvalidDataException("В файле есть пустая запись диплома.");
                if (personal && item.HasIssued != true) continue;
                AwardProgram row = personal ? new AwardProgram { Id = item.Id, Url = item.Url, Title = item.Title, HasIssued = true } : item;
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
                    if (old.PublicEntry || personal) row.Title = old.Title;
                    if (row.Goals.Count == 0)
                    {
                        row.Goals = old.Goals;
                        row.ManualSearch = old.ManualSearch;
                        if (String.IsNullOrEmpty(row.FromUtc) || old.ManualSearch) row.FromUtc = old.FromUtc;
                        if (String.IsNullOrEmpty(row.UntilUtc) || old.ManualSearch) row.UntilUtc = old.UntilUtc;
                    }
                    row.Selected = old.Selected && !row.ClosedByIssuance && row.HasSearchRules;
                }
                merged[row.Id] = row;
            }
            Programs = merged.Values.ToList();
            NormalizeBuiltInPrograms();
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
            NormalizeBuiltInPrograms();
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
            NormalizeBuiltInPrograms();
            return unmatched;
        }

        internal void NormalizeBuiltInPrograms()
        {
            var result = new List<AwardProgram>();
            var known = new Dictionary<string, AwardProgram>(StringComparer.Ordinal);
            foreach (AwardProgram row in Programs)
            {
                if (row.ClosedByIssuance) row.Selected = false;
                HamlogAward award = HamlogAward.Find(row.Url);
                if (award == null || !award.HasSearchRules) { result.Add(row); continue; }
                AwardProgram prior;
                if (known.TryGetValue(award.Url, out prior))
                {
                    prior.Selected |= row.Selected;
                    if (row.HasIssued == true) prior.HasIssued = true;
                    if (prior.ClosedByIssuance) prior.Selected = false;
                    if (row.PublicEntry && (!prior.PublicEntry || prior.Withdrawn && !row.Withdrawn))
                        prior.SetPublication(row);
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
}
