using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace JtdxAutoCq
{
    internal sealed class HamlogRuleResult
    {
        internal string Id, Status;
        internal HamlogRulePlan Plan;
        internal DateTime CheckedUtc;
    }

    internal sealed class HamlogRuleCache
    {
        static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
        readonly string directory;
        internal HamlogRuleCache(string directory) { this.directory = directory; }

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
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
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
                            if (cancel.WaitHandle.WaitOne(900)) cancel.ThrowIfCancellationRequested();
                            document = HamlogRules.Read(program.Url, cancel);
                            Store(program.Url, document);
                        }
                        result.Status = document.Pdf == null && String.IsNullOrWhiteSpace(document.Text) ? "missing" : "downloaded";
                        if (HamlogAward.Find(program.Url) == null && result.Status == "downloaded")
                        {
                            string text = document.Pdf == null ? document.Text : pdf.Read(document.Pdf, cancel);
                            result.Plan = HamlogPlanCompiler.Compile(program.Url, text);
                        }
                        consecutiveErrors = 0;
                    }
                    catch (HamlogRequestLimited) { throw; }
                    catch (InvalidDataException)
                    {
                        result.Status = "missing";
                        consecutiveErrors = 0;
                    }
                    catch (IOException)
                    {
                        if (++consecutiveErrors == 4)
                            throw new IOException("Не удаётся получить положения HAMLOG. Уже загруженные положения сохранены.");
                        result.Status = "retry";
                    }
                    catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { result.Status = "downloaded"; }
                    report(result);
                }
        }
    }
}
