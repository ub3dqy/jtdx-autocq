using System;
using System.Collections.Generic;
using System.IO;

namespace JtdxAutoCq
{
    public sealed class HamlogReviewedEntry
    {
        public string Url, TextSha256, PdfSha256, Title, Note;
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
                    string fingerprint = entry.TextSha256 ?? entry.PdfSha256;
                    if (HamlogLinks.Award(entry.Url) != entry.Url || fingerprint == null || fingerprint.Length != 64
                        || entry.TextSha256 != null && entry.PdfSha256 != null
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
            return Entries.TryGetValue(url, out entry) && entry.TextSha256 != null
                && HamlogCatalogClient.Hash(text) == entry.TextSha256 ? entry : null;
        }

        internal static HamlogReviewedEntry FindPdf(string url, byte[] content)
        {
            HamlogReviewedEntry entry;
            return content != null && Entries.TryGetValue(url, out entry) && entry.PdfSha256 != null
                && HamlogCatalogClient.HashBytes(content) == entry.PdfSha256 ? entry : null;
        }
    }
}
