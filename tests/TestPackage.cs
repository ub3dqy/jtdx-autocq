using System;
using System.IO;
using System.Linq;
using JtdxAutoCq;

static class TestPackage
{
    static int Main(string[] args)
    {
        string destination = null;
        try
        {
            string archive=Path.GetFullPath(args[0]),version=typeof(MainForm).Assembly.GetName().Version.ToString(3);
            destination=Path.Combine(Path.GetDirectoryName(archive),"package-check-"+Guid.NewGuid().ToString("N"));
            PreparedUpdate prepared=UpdateClient.VerifyAndExtract(archive,JtdxPatcher.HashFile(archive),version,destination);
            if(prepared.JtdxSha256!=JtdxPatcher.PatchedHash)throw new Exception("Native patch version does not match the updater manifest.");
            string directory=Path.Combine(destination,"package");
            foreach(string name in new[]{"JTDX-AutoCQ-Patch.exe","Update-AutoCQ.ps1","USER-GUIDE.md","USER-GUIDE.en.md","README.md","README.en.md","CONTRIBUTING.md",
                "docs/user-guide.ru.html","docs/user-guide.en.html","docs/guide.css","docs/autocq-demo.png","docs/setup-demo.png","docs/updates-demo.png","docs/monitor-demo.png","docs/monitor-timeouts-demo.png","docs/monitor-manual-demo.png","docs/monitor-callers-demo.png","docs/about-demo.png","docs/updates-download-demo.png"})
                if(!File.Exists(Path.Combine(directory,name)))throw new Exception("The release is missing "+name);
            if(!JtdxPatcher.Resource("Update-AutoCQ.ps1").SequenceEqual(File.ReadAllBytes(Path.Combine(directory,"Update-AutoCQ.ps1"))))throw new Exception("Embedded installer differs from the packaged source.");
            Console.WriteLine("PASS  Final archive is accepted by the updater and contains the patcher, both guides and current screenshots.");
            return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e.Message);return 1;}
        finally { if (destination != null && Directory.Exists(destination)) Directory.Delete(destination, true); }
    }
}
