using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace JtdxAutoCq
{
    internal sealed class HamlogRuleResult
    {
        internal string Id, Status, Error, Note;
        internal HamlogRulePlan Plan;
        internal DateTime CheckedUtc;
    }

    internal sealed class HamlogRuleCache
    {
        static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
        const int MaximumCacheBytes = HamlogRules.MaximumBytes + 2048;
        readonly string directory;
        readonly Func<string, CancellationToken, HamlogRules> read;
        readonly Action<int, CancellationToken> wait;

        internal HamlogRuleCache(string directory, Func<string, CancellationToken, HamlogRules> read = null,
            Action<int, CancellationToken> wait = null)
        {
            this.directory = directory;
            this.read = read ?? HamlogRules.Read;
            this.wait = wait ?? Wait;
        }

        static void Wait(int milliseconds, CancellationToken cancel)
        {
            if (cancel.WaitHandle.WaitOne(milliseconds)) cancel.ThrowIfCancellationRequested();
        }

        string DocumentPath(string url)
        { return Path.Combine(directory, HamlogCatalogClient.Hash(url) + ".bin"); }

        internal HamlogRules Cached(string url, bool freshOnly = false)
        {
            string path = DocumentPath(url);
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaximumCacheBytes
                    || freshOnly && DateTime.UtcNow - info.LastWriteTimeUtc > Lifetime) return null;
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                using (var data = new BinaryReader(file, Encoding.UTF8))
                {
                    string source = data.ReadString();
                    if (HamlogLinks.Resolve(source, url) != source) return null;
                    switch (data.ReadByte())
                    {
                        case 0: return new HamlogRules(source, null, null);
                        case 1:
                            string text = data.ReadString();
                            return String.IsNullOrWhiteSpace(text) ? null : new HamlogRules(source, text, null);
                        case 2: return HamlogRules.ParseDocument(source, data.ReadBytes((int)(file.Length - file.Position)));
                        default: return null;
                    }
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (FormatException) { return null; }
        }

        void Store(string url, HamlogRules document)
        {
            Directory.CreateDirectory(directory);
            string path = DocumentPath(url);
            string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var data = new BinaryWriter(File.Create(temporary), Encoding.UTF8))
                {
                    data.Write(document.Source);
                    data.Write((byte)(document.Pdf != null ? 2 : document.Text != null ? 1 : 0));
                    if (document.Pdf != null) data.Write(document.Pdf);
                    else if (document.Text != null) data.Write(document.Text);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static bool NeedsRefresh(AwardProgram program, DateTime now)
        {
            return program.PublicEntry && !program.Withdrawn && !String.IsNullOrEmpty(program.Url)
                && (program.RuleCompilerVersion != HamlogPlanCompiler.Version || now - program.RulesCheckedUtc > Lifetime);
        }

        internal static bool CanResume(AwardCatalog catalog, DateTime now)
        {
            return catalog.PublicCatalogUtc >= now.AddDays(-1) && catalog.Programs.Any(program => NeedsRefresh(program, now));
        }

        internal bool CanRecompile(AwardCatalog catalog)
        { return catalog.Programs.Any(CanRecompile); }

        bool CanRecompile(AwardProgram program)
        {
            return program.PublicEntry && !program.Withdrawn && !String.IsNullOrEmpty(program.Url)
                && program.RuleCompilerVersion != HamlogPlanCompiler.Version && File.Exists(DocumentPath(program.Url));
        }

        bool HasLocalDocument(AwardProgram program)
        {
            if (!CanRecompile(program)) return false;
            try
            {
                using (var file = new FileStream(DocumentPath(program.Url), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                using (var reader = new BinaryReader(file, Encoding.UTF8))
                {
                    if (file.Length > MaximumCacheBytes) return false;
                    reader.ReadString();
                    byte format = reader.ReadByte();
                    return format == 2 || format == 1 && !String.IsNullOrWhiteSpace(reader.ReadString());
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (FormatException) { return false; }
        }

        HamlogRules Download(string url, CancellationToken cancel)
        {
            wait(900, cancel);
            for (int attempt = 0; ; attempt++)
            {
                cancel.ThrowIfCancellationRequested();
                try { return read(url, cancel); }
                catch (HamlogRequestError error)
                {
                    if (!error.Retryable || attempt >= (error.PauseRequested ? 1 : 2)
                        || error.Status == 429 && !error.RetryAfter.HasValue
                        || error.RetryAfter.HasValue && error.RetryAfter.Value.TotalSeconds > 30) throw;
                    int delay = (int)Math.Ceiling(error.RetryAfter.GetValueOrDefault().TotalMilliseconds);
                    wait(Math.Max(delay, attempt == 0 ? 2000 : 5000), cancel);
                }
            }
        }

        internal void Scan(IEnumerable<AwardProgram> programs, CancellationToken cancel, Action<HamlogRuleResult> report)
        {
            int consecutiveErrors = 0;
            using (var pdf = new HamlogPdfReader())
                foreach (AwardProgram program in programs.OrderByDescending(HasLocalDocument))
                {
                    cancel.ThrowIfCancellationRequested();
                    var result = new HamlogRuleResult { Id = program.Id, CheckedUtc = DateTime.UtcNow };
                    try
                    {
                        HamlogRules document = Cached(program.Url, program.RuleCompilerVersion == HamlogPlanCompiler.Version);
                        if (document == null)
                        {
                            document = Download(program.Url, cancel);
                            Store(program.Url, document);
                        }
                        consecutiveErrors = 0;
                        result.Status = document.Pdf == null && String.IsNullOrWhiteSpace(document.Text) ? "missing" : "downloaded";
                        if (HamlogAward.Find(program.Url) == null && result.Status == "downloaded")
                        {
                            HamlogReviewedEntry reviewed = HamlogReviewedPlans.FindPdf(program.Url, document.Pdf);
                            if (reviewed != null)
                            {
                                result.Plan = reviewed.Plan;
                                result.Note = reviewed.Note;
                            }
                            else
                            {
                                string text = document.Pdf == null ? document.Text : pdf.Read(document.Pdf, cancel);
                                if (String.IsNullOrWhiteSpace(text)) result.Status = "unreadable";
                                else result.Plan = HamlogPlanCompiler.CompileWithReview(program.Url, text, out result.Note);
                            }
                        }
                    }
                    catch (HamlogRequestError error)
                    {
                        result.Status = "retry";
                        result.Error = error.Message;
                        report(result);
                        if (error.PauseRequested || ++consecutiveErrors == 4)
                            throw new IOException("Загрузка положений приостановлена. " + error.Message
                                + " Скачанные файлы сохранены.", error);
                        continue;
                    }
                    catch (InvalidDataException error)
                    {
                        result.Status = "missing";
                        result.Error = error.Message;
                        consecutiveErrors = 0;
                    }
                    catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
                    {
                        result.Status = result.Status ?? "retry";
                        result.Error = "Превышено время разбора положения.";
                    }
                    report(result);
                }
        }
    }
}
