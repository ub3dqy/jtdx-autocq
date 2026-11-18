using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Reflection;
using System.Windows.Forms;
using System.Runtime.Serialization.Json;
using System.Text;
using JtdxAutoCq;

static class UpdateTests
{
    static void Assert(bool valid,string message){if(!valid)throw new Exception(message);}
    static string Fixture(){return TestWorkspace.Create();}
    static byte[] Json<T>(T value){using(var output=new MemoryStream()){new DataContractJsonSerializer(typeof(T)).WriteObject(output,value);return output.ToArray();}}
    sealed class Server : IUpdateTransport
    {
        public byte[] Metadata,Archive,Checksums;
        public readonly List<string> Requests=new List<string>();
        public byte[] Read(string url,int maximumBytes){Requests.Add(url);byte[] b=url==UpdateClient.LatestApi?Metadata:Checksums;if(b.Length>maximumBytes)throw new IOException("Too large");return b;}
        public Action<UpdateDownloadProgress> Transferring;
        public bool Interrupt;
        public void Download(string url,string file,int maximumBytes,Action<UpdateDownloadProgress> progress)
        {
            Requests.Add(url);if(Archive.Length>maximumBytes)throw new IOException("Too large");
            if(progress!=null)
            {
                progress(new UpdateDownloadProgress(UpdateDownloadStage.Connecting));
                var sample=new UpdateDownloadProgress(UpdateDownloadStage.Downloading,Archive.Length/2,Archive.Length,TimeSpan.FromSeconds(2));
                progress(sample);if(Transferring!=null)Transferring(sample);
            }
            if(Interrupt)throw new WebException("Simulated interrupted download");
            File.WriteAllBytes(file,Archive);
            if(progress!=null)progress(new UpdateDownloadProgress(UpdateDownloadStage.Downloading,Archive.Length,Archive.Length,TimeSpan.FromSeconds(4)));
        }
    }
    sealed class HttpFixture : IDisposable
    {
        readonly TcpListener listener;
        readonly Thread thread;
        public readonly string Url;
        Exception error;
        public HttpFixture(byte[] bytes, bool chunked, bool truncated=false)
        {
            listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            Url="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+"/update.zip";
            thread=new Thread(()=>{
                try
                {
                    using(TcpClient socket=listener.AcceptTcpClient())
                    using(NetworkStream stream=socket.GetStream())
                    {
                        socket.ReceiveTimeout=socket.SendTimeout=5000;
                        var headers=new StringBuilder();
                        while(headers.Length<16384 && !headers.ToString().EndsWith("\r\n\r\n",StringComparison.Ordinal))
                        {int value=stream.ReadByte();if(value<0)throw new IOException("Request ended early");headers.Append((char)value);}
                        Write(stream,"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n"
                            +(chunked?"Transfer-Encoding: chunked":("Content-Length: "+(bytes.Length+(truncated?100:0))))+"\r\n\r\n");
                        for(int offset=0;offset<bytes.Length;offset+=8192)
                        {
                            int count=Math.Min(8192,bytes.Length-offset);
                            if(chunked)Write(stream,count.ToString("X")+"\r\n");
                            stream.Write(bytes,offset,count);
                            if(chunked)Write(stream,"\r\n");
                        }
                        if(chunked)Write(stream,"0\r\n\r\n");
                    }
                }
                catch(Exception failure){error=failure;}
            });thread.IsBackground=true;thread.Start();
        }
        static void Write(Stream stream,string text){byte[] bytes=Encoding.ASCII.GetBytes(text);stream.Write(bytes,0,bytes.Length);}
        public void Dispose()
        {listener.Stop();if(!thread.Join(5000))throw new Exception("HTTP fixture did not finish");if(error!=null)throw new Exception("HTTP fixture failed",error);}
    }
    static object Field(object instance,string name)
    {return instance.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(instance);}
    static void PumpUi(Func<bool> ready)
    {
        var watch=Stopwatch.StartNew();
        while(!ready() && watch.ElapsedMilliseconds<5000){Application.DoEvents();Thread.Sleep(5);}
        Application.DoEvents();Assert(ready(),"Download UI did not respond before the deadline");
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
        run("HTTP downloads report real bytes for fixed-length and chunked responses",()=>{
            byte[] body=Enumerable.Range(0,65537).Select(value=>(byte)value).ToArray();
            foreach(bool chunked in new[]{false,true})
            using(var server=new HttpFixture(body,chunked))
            {
                var samples=new List<UpdateDownloadProgress>();string file=Path.Combine(Fixture(),"update.zip");
                new HttpUpdateTransport().Download(server.Url,file,body.Length,samples.Add);
                var transferred=samples.Where(value=>value.Stage==UpdateDownloadStage.Downloading).ToArray();
                Assert(File.ReadAllBytes(file).SequenceEqual(body) && samples[0].Stage==UpdateDownloadStage.Connecting
                    && transferred.First().ReceivedBytes==0 && transferred.Last().ReceivedBytes==body.Length,
                    "The HTTP byte count or payload was incorrect");
                Assert(transferred.Select(value=>value.ReceivedBytes).SequenceEqual(transferred.Select(value=>value.ReceivedBytes).OrderBy(value=>value)),"Progress went backwards");
                Assert(chunked?transferred.All(value=>!value.Percent.HasValue && !value.TotalBytes.HasValue && !value.Remaining.HasValue)
                    :transferred.Last().Percent==100 && transferred.Last().TotalBytes==body.Length
                        && transferred.Any(value=>value.Percent>0 && value.Percent<100),"Known or unknown file size was misrepresented");
            }
        });
        run("A truncated HTTP response cannot report a fully downloaded archive",()=>{
            byte[] body=new byte[32768];var samples=new List<UpdateDownloadProgress>();bool rejected=false;
            using(var server=new HttpFixture(body,false,true))
            {
                try{new HttpUpdateTransport().Download(server.Url,Path.Combine(Fixture(),"partial.zip"),body.Length+100,samples.Add);}
                catch(IOException){rejected=true;}catch(WebException){rejected=true;}
            }
            Assert(rejected && samples.All(value=>value.Percent!=100),"An incomplete transfer appeared finished");
        });
        run("Download speed and remaining time derive from received bytes and elapsed time",()=>{
            var sample=new UpdateDownloadProgress(UpdateDownloadStage.Downloading,1024,4096,TimeSpan.FromSeconds(2));
            Assert(sample.Percent==25 && sample.BytesPerSecond==512 && sample.Remaining==TimeSpan.FromSeconds(6),"Progress estimates did not match actual transfer counts");
            var unknown=new UpdateDownloadProgress(UpdateDownloadStage.Downloading,1024,null,TimeSpan.FromSeconds(2));
            var initial=new UpdateDownloadProgress(UpdateDownloadStage.Downloading,0,4096);
            Assert(!unknown.Percent.HasValue && !unknown.Remaining.HasValue && !initial.Remaining.HasValue,"A missing size or rate produced a fabricated estimate");
        });
        run("Download progress separates receiving, archive verification and preparation",()=>{
            foreach(bool corrupt in new[]{false,true})
            {
                string folder=Fixture(),archive=Archive(folder),digest=JtdxPatcher.HashFile(archive),version=ModuleVersion;
                var server=new Server{Archive=File.ReadAllBytes(archive),Metadata=Json(Release(version,digest)),Checksums=Encoding.UTF8.GetBytes(digest+"  JTDX-AutoCQ-"+version+"-win64.zip\n")};
                if(corrupt)server.Archive[0]^=1;
                var client=new UpdateClient("0.0.1",server);var samples=new List<UpdateDownloadProgress>();bool rejected=false;
                try{client.Download(client.Check(),folder,samples.Add);}catch(InvalidDataException){rejected=true;}
                var stages=samples.Select(value=>value.Stage).Distinct().ToArray();
                Assert(rejected==corrupt && stages.SequenceEqual(corrupt
                    ?new[]{UpdateDownloadStage.Preparing,UpdateDownloadStage.Connecting,UpdateDownloadStage.Downloading,UpdateDownloadStage.Verifying}
                    :new[]{UpdateDownloadStage.Preparing,UpdateDownloadStage.Connecting,UpdateDownloadStage.Downloading,UpdateDownloadStage.Verifying,UpdateDownloadStage.Extracting,UpdateDownloadStage.Ready}),
                    "Download stages claimed readiness before validation or hid the verification phase");
            }
        });
        run("The update window receives worker progress and handles success and interruption",()=>{
            foreach(bool fail in new[]{false,true})
            {
                string folder=Fixture(),archive=Archive(folder),digest=JtdxPatcher.HashFile(archive),version=ModuleVersion;
                var server=new Server{Archive=File.ReadAllBytes(archive),Metadata=Json(Release(version,digest)),Checksums=Encoding.UTF8.GetBytes(digest+"  JTDX-AutoCQ-"+version+"-win64.zip\n"),Interrupt=fail};
                var client=new UpdateClient("0.0.1",server);
                using(var proceed=new ManualResetEvent(false))
                using(var paused=new ManualResetEvent(false))
                using(var dialog=new UpdateDialog(client,new AppPreferences(),new AppPreferencesStore(null),folder,@"C:\Radio\JTDX\bin\jtdx.exe",true,()=>{},client.Check()))
                {
                    server.Transferring=value=>{paused.Set();if(!proceed.WaitOne(5000))throw new Exception("UI never acknowledged transfer progress");};
                    IntPtr handle=dialog.Handle;
                    var bar=(ProgressBar)Field(dialog,"progress");var details=(Label)Field(dialog,"transferDetails");
                    var notice=(Label)Field(dialog,"notice");var check=(Button)Field(dialog,"check");var install=(Button)Field(dialog,"install");
                    try
                    {
                        typeof(UpdateDialog).GetMethod("DownloadNow",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(dialog,null);
                        int expected=(int)(100L*(server.Archive.Length/2)/server.Archive.Length);
                        PumpUi(()=>paused.WaitOne(0) && bar.Value==expected && notice.Text.Contains(expected+"%"));
                        Assert(bar.Style==ProgressBarStyle.Continuous && details.Text.Contains(" из ") && details.Text.Contains("/с")
                            && details.Text.Contains("осталось") && !install.Enabled,"The window still showed an unconnected marquee or hid transfer details");
                        using(var bitmap=new Bitmap(bar.Width,bar.Height))
                        {
                            bar.DrawToBitmap(bitmap,new Rectangle(0,0,bar.Width,bar.Height));
                            Assert(bitmap.GetPixel(bar.Width/4,bar.Height/2).ToArgb()!=bitmap.GetPixel(3*bar.Width/4,bar.Height/2).ToArgb(),
                                "The progress value changed but the visible bar remained empty");
                        }
                        proceed.Set();PumpUi(()=>check.Enabled);
                        Assert(install.Enabled==!fail && (fail?bar.Value<100:bar.Value==100)
                            && bar.Style==ProgressBarStyle.Continuous,"An interrupted transfer enabled installation or a completed transfer reset its progress");
                    }
                    finally{proceed.Set();}
                }
            }
        });
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
