using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace JtdxAutoCq
{
    internal sealed class HamlogPdfReader : IDisposable
    {
        string folder;

        internal string Read(byte[] pdf, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            if (folder == null)
            {
                folder = Path.Combine(Path.GetTempPath(), "AutoCQ-Hamlog-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                using (var bundle = typeof(HamlogPdfReader).Assembly.GetManifestResourceStream("PdfRulesReader.zip"))
                using (var zip = new ZipArchive(bundle, ZipArchiveMode.Read))
                    foreach (ZipArchiveEntry entry in zip.Entries)
                        if (entry.FullName == entry.Name)
                            entry.ExtractToFile(Path.Combine(folder, entry.Name));
            }
            string input = Path.Combine(folder, "rules.pdf"), output = Path.Combine(folder, "rules.txt");
            try
            {
                File.WriteAllBytes(input, pdf);
                using (var worker = Process.Start(new ProcessStartInfo {
                    FileName = Path.Combine(folder, "PdfRulesReader.exe"),
                    Arguments = "\"" + input + "\" \"" + output + "\"",
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder
                }))
                {
                    var elapsed = Stopwatch.StartNew();
                    try
                    {
                        while (!worker.WaitForExit(100))
                        {
                            cancel.ThrowIfCancellationRequested();
                            if (elapsed.Elapsed >= TimeSpan.FromSeconds(8)) return null;
                        }
                        return worker.ExitCode == 0 && File.Exists(output) ? File.ReadAllText(output) : null;
                    }
                    finally
                    {
                        if (!worker.HasExited)
                        {
                            try { worker.Kill(); } catch (InvalidOperationException) { }
                            worker.WaitForExit(1000);
                        }
                    }
                }
            }
            finally { Remove(input); Remove(output); }
        }

        static void Remove(string file)
        {
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public void Dispose()
        {
            if (folder == null || !Directory.Exists(folder)) return;
            foreach (string file in Directory.GetFiles(folder)) Remove(file);
            try { Directory.Delete(folder); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
