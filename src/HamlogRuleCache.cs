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
        internal string Id, Status, Error;
        internal HamlogRulePlan Plan;
        internal DateTime CheckedUtc;
    }

    internal sealed class HamlogRuleCache
    {
        static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
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
                if (!info.Exists || info.Length > 25 * 1024 * 1024
                    || freshOnly && DateTime.UtcNow - info.LastWriteTimeUtc > Lifetime) return null;
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                using (var data = new BinaryReader(file, Encoding.UTF8))
                {
                    string source = data.ReadString();
                    if (HamlogLinks.Resolve(source, url) != source) return null;
                    switch (data.ReadByte())
                    {
                        case 0: return new HamlogRules(source, null, null);
                        case 1: return new HamlogRules(source, data.ReadString(), null);
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
                foreach (AwardProgram program in programs)
                {
                    cancel.ThrowIfCancellationRequested();
                    var result = new HamlogRuleResult { Id = program.Id, CheckedUtc = DateTime.UtcNow };
                    try
                    {
                        HamlogRules document = Cached(program.Url, true);
                        if (document == null)
                        {
                            document = Download(program.Url, cancel);
                            Store(program.Url, document);
                        }
                        consecutiveErrors = 0;
                        result.Status = document.Pdf == null && String.IsNullOrWhiteSpace(document.Text) ? "missing" : "downloaded";
                        if (HamlogAward.Find(program.Url) == null && result.Status == "downloaded")
                        {
                            string text = document.Pdf == null ? document.Text : pdf.Read(document.Pdf, cancel);
                            result.Plan = HamlogPlanCompiler.Compile(program.Url, text);
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
