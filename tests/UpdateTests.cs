using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using JtdxAutoCq;

static class UpdateTests
{
    static void Assert(bool valid,string message){if(!valid)throw new Exception(message);}
    static string Fixture(){string p=Path.GetFullPath(Path.Combine("work","tests","update-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(p);return p;}
    static byte[] Json<T>(T value){using(var output=new MemoryStream()){new DataContractJsonSerializer(typeof(T)).WriteObject(output,value);return output.ToArray();}}
    sealed class Server : IUpdateTransport
    {
        public byte[] Metadata,Archive,Checksums;
        public readonly List<string> Requests=new List<string>();
        public byte[] Read(string url,int maximumBytes){Requests.Add(url);byte[] b=url==UpdateClient.LatestApi?Metadata:Checksums;if(b.Length>maximumBytes)throw new IOException("Too large");return b;}
        public void Download(string url,string file,int maximumBytes,Action<int> progress){Requests.Add(url);if(Archive.Length>maximumBytes)throw new IOException("Too large");File.WriteAllBytes(file,Archive);if(progress!=null)progress(100);}
    }
    static string ModuleVersion { get { return new Version(FileVersionInfo.GetVersionInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"JTDX-AutoCQ.exe")).FileVersion).ToString(3); } }
    static ReleaseDocument Release(string version,string digest)
    {
        string tag="v"+version,prefix=UpdateClient.RepositoryUrl+"/releases/download/"+tag+"/";
        return new ReleaseDocument{Tag=tag,Url=UpdateClient.RepositoryUrl+"/releases/tag/"+tag,Body="Описание выпуска / Release notes",Assets=new[]{
            new ReleaseAsset{Name="JTDX-AutoCQ-"+version+"-win64.zip",Url=prefix+"JTDX-AutoCQ-"+version+"-win64.zip",Digest="sha256:"+digest},
            new ReleaseAsset{Name="SHA256SUMS.txt",Url=prefix+"SHA256SUMS.txt"}}};
    }
    static string Archive(string directory,string extra=null,string manifestVersion=null)
    {
        string file=Path.Combine(directory,Guid.NewGuid().ToString("N")+".zip");
        using(var zip=ZipFile.Open(file,ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"JTDX-AutoCQ.exe"),"JTDX-AutoCQ.exe");
            zip.CreateEntryFromFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"JTDX-AutoCQ-Patch.exe"),"JTDX-AutoCQ-Patch.exe");
            using(var writer=new StreamWriter(zip.CreateEntry("update.json").Open(),new UTF8Encoding(false)))writer.Write(Encoding.UTF8.GetString(Json(new UpdatePackageManifest{Version=manifestVersion??ModuleVersion,JtdxSha256=JtdxPatcher.PatchedHash})));
            if(extra!=null)using(var writer=new StreamWriter(zip.CreateEntry(extra).Open()))writer.Write("fixture");
        }
        return file;
    }
    public static void RunAll(Action<string,Action> run,string original)
    {
        run("Updater accepts only a stable release and exact files from the project repository",()=>{
            foreach(string bad in new[]{"none","foreign-url","prerelease","duplicate","tag"})
            {
                ReleaseDocument release=Release(ModuleVersion,new string('a',64));
                if(bad=="foreign-url")release.Assets[0].Url="https://example.invalid/module.zip";
                if(bad=="prerelease")release.Prerelease=true;
                if(bad=="duplicate")release.Assets=new[]{release.Assets[0],release.Assets[0],release.Assets[1]};
                if(bad=="tag")release.Tag="main";
                bool rejected=false;try{AvailableUpdate parsed=UpdateClient.ParseRelease(Json(release));Assert(bad=="none" && parsed.Version.ToString(3)==ModuleVersion,"An unexpected release was accepted");}catch(System.Runtime.Serialization.SerializationException){rejected=true;}catch(InvalidDataException){rejected=true;}
                Assert(rejected==(bad!="none"),"Release validation failed: "+bad);
            }
        });
        run("Updater verifies the download and package version without transmitting operator data",()=>{
            string folder=Fixture(),archive=Archive(folder);string digest=JtdxPatcher.HashFile(archive),version=ModuleVersion;
            var server=new Server{Archive=File.ReadAllBytes(archive),Metadata=Json(Release(version,digest)),Checksums=Encoding.UTF8.GetBytes(digest+"  JTDX-AutoCQ-"+version+"-win64.zip\n")};
            var client=new UpdateClient("0.0.1",server);PreparedUpdate prepared=client.Download(client.Check(),folder);
            Assert(prepared.Version==version && JtdxPatcher.HashFile(prepared.Archive)==digest && File.Exists(Path.Combine(prepared.Folder,"package","JTDX-AutoCQ.exe")),"Verified update was not prepared");
            Assert(server.Requests.Count==3 && server.Requests[0]==UpdateClient.LatestApi && server.Requests.All(url=>url.StartsWith("https://api.github.com/repos/ub3dqy/jtdx-autocq/") || url.StartsWith(UpdateClient.RepositoryUrl+"/releases/download/")),"Update request contained an unrelated endpoint or operator data");
        });
        run("Updater rejects mismatched checksums before extracting executable files",()=>{
            string directory=Fixture(),archive=Archive(directory);bool rejected=false;
            try{UpdateClient.VerifyAndExtract(archive,new string('0',64),ModuleVersion,Path.Combine(directory,"output"));}catch(InvalidDataException){rejected=true;}
            Assert(rejected && !Directory.Exists(Path.Combine(directory,"output","package")),"An unverified archive was extracted");
            rejected=false;try{UpdateClient.ExpectedChecksum(new string('a',64)+"  module.zip\n"+new string('b',64)+"  module.zip","module.zip");}catch(InvalidDataException){rejected=true;}
            Assert(rejected,"Ambiguous checksum entry was accepted");
        });
        run("Updater rejects escaping paths, private profile files and inconsistent package versions",()=>{
            foreach(string bad in new[]{"../escape.txt","C:/escape.txt","operator.xml","JTDX.ini","wrong-version"})
            {
                string directory=Fixture(),archive=Archive(directory,bad=="wrong-version"?null:bad,bad=="wrong-version"?"0.0.0":null);bool rejected=false;
                try{UpdateClient.VerifyAndExtract(archive,JtdxPatcher.HashFile(archive),ModuleVersion,Path.Combine(directory,"output"));}catch(InvalidDataException){rejected=true;}
                Assert(rejected && !File.Exists(Path.Combine(directory,"escape.txt")),"Unsafe package accepted: "+bad);
            }
        });
        run("Automatic update preferences preserve the operator profile and default to enabled",()=>{
            string directory=Fixture(),operatorFile=Path.Combine(directory,"operator.xml");File.WriteAllText(operatorFile,"keep-operator-settings");
            var store=new AppPreferencesStore(directory);AppPreferences preferences=store.Load();Assert(preferences.AutomaticUpdates,"Automatic checks were not enabled by default");
            preferences.AutomaticUpdates=false;preferences.JtdxPath=@"C:\Radio Tools\JTDX\bin\jtdx.exe";preferences.NotifiedVersion="9.8.7";store.Save(preferences);
            AppPreferences loaded=store.Load();Assert(!loaded.AutomaticUpdates && loaded.JtdxPath==preferences.JtdxPath && loaded.NotifiedVersion=="9.8.7" && File.ReadAllText(operatorFile)=="keep-operator-settings","Preference save changed user data or lost the notification setting");
        });
        run("Updater refuses reinstalling the same or older version",()=>{
            var server=new Server();var client=new UpdateClient(ModuleVersion,server);bool rejected=false;
            try{client.Download(new AvailableUpdate{Version=Version.Parse(ModuleVersion)},Fixture());}catch(InvalidOperationException){rejected=true;}
            Assert(rejected && server.Requests.Count==0,"An older or identical build started downloading");
        });
        run("Built-in patcher rejects an unrelated executable without changing it",()=>{
            string directory=Fixture(),file=Path.Combine(directory,"jtdx.exe");File.WriteAllText(file,"unsupported fixture");string before=JtdxPatcher.HashFile(file);bool rejected=false;
            try{JtdxPatcher.Apply(file,false,()=>false);}catch(InvalidDataException){rejected=true;}
            Assert(rejected && JtdxPatcher.HashFile(file)==before && !File.Exists(file+".autocq-original"),"Unsupported JTDX was modified");
        });
        if(!String.IsNullOrEmpty(original) && File.Exists(original))run("Built-in patcher creates the verified native build and restores its original in an isolated folder",()=>{
            string directory=Fixture(),file=Path.Combine(directory,"jtdx.exe");File.Copy(original,file);
            Assert(JtdxPatcher.Apply(file,false,()=>false) && JtdxPatcher.HashFile(file)==JtdxPatcher.PatchedHash && JtdxPatcher.HashFile(file+".autocq-original")==JtdxPatcher.OriginalHash,"Built-in native patch did not match the validated output");
            Assert(!JtdxPatcher.Apply(file,false,()=>false),"Applying the same patch changed files again");
            Assert(JtdxPatcher.Apply(file,true,()=>false) && JtdxPatcher.HashFile(file)==JtdxPatcher.OriginalHash,"Built-in restore did not recover the original");
        });
    }
}
