using System;
using System.Collections.Generic;
using System.IO;

namespace JtdxAutoCq
{
    public sealed class HamlogReviewedEntry
    {
        public string Url, TextSha256, Title, Note;
        public HamlogRulePlan Plan;
    }

    internal static class HamlogReviewedPlans
    {
        static readonly Dictionary<string, HamlogReviewedEntry> Entries = Load();

        static Dictionary<string, HamlogReviewedEntry> Load()
        {
            var result = new Dictionary<string, HamlogReviewedEntry>(StringComparer.Ordinal);
            using (Stream source = typeof(HamlogReviewedPlans).Assembly.GetManifestResourceStream("HamlogReviewedAwards.json"))
            using (var buffer = new MemoryStream())
            {
                source.CopyTo(buffer);
                foreach (HamlogReviewedEntry entry in UpdateClient.Json<HamlogReviewedEntry[]>(buffer.ToArray()))
                {
                    if (HamlogLinks.Award(entry.Url) != entry.Url || entry.TextSha256 == null || entry.TextSha256.Length != 64
                        || entry.Plan == null && String.IsNullOrWhiteSpace(entry.Note))
                        throw new InvalidDataException("Неверная ссылка или контрольная сумма в каталоге условий дипломов.");
                    if (entry.Plan != null) entry.Plan.Validate();
                    result.Add(entry.Url, entry);
                }
            }
            return result;
        }

        internal static HamlogReviewedEntry Find(string url, string text)
        {
            HamlogReviewedEntry entry;
            return Entries.TryGetValue(url, out entry) && HamlogCatalogClient.Hash(text) == entry.TextSha256 ? entry : null;
        }
    }
}
