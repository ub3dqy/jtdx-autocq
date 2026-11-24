using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Serialization;

namespace JtdxAutoCq
{
    public sealed class AppPreferences
    {
        public bool AutomaticUpdates = true;
        public string JtdxPath = "", NotifiedVersion = "";
        public DateTime LastUpdateCheckUtc;
        public string LogbookPath = "";
        public List<OperatorProfile> Profiles = new List<OperatorProfile>();
        public AwardCatalog Awards = new AwardCatalog();
        public OperatingSession LastSession;
    }
    public sealed class AppPreferencesStore
    {
        readonly string path;
        public AppPreferencesStore(string directory) { path=directory==null?null:Path.Combine(directory,"preferences.xml"); }
        public AppPreferences Load()
        {
            if(path==null || !File.Exists(path))return new AppPreferences();
            try
            {
                using (var input = File.OpenRead(path))
                {
                    var preferences = (AppPreferences)new XmlSerializer(typeof(AppPreferences)).Deserialize(input);
                    if (preferences.Awards == null) preferences.Awards = new AwardCatalog();
                    preferences.Awards.NormalizeOnlinePrograms();
                    return preferences;
                }
            }
            catch(IOException){return new AppPreferences();}catch(InvalidOperationException){return new AppPreferences();}catch(UnauthorizedAccessException){return new AppPreferences();}
        }
        public void Save(AppPreferences value)
        {
            if(path==null)return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try { using(var output=File.Create(temporary))new XmlSerializer(typeof(AppPreferences)).Serialize(output,value);
                if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path); }
            finally { if(File.Exists(temporary))File.Delete(temporary); }
        }
    }
    [DataContract] public sealed class ReleaseAsset
    {
        [DataMember(Name="name")] public string Name;
        [DataMember(Name="browser_download_url")] public string Url;
        [DataMember(Name="digest")] public string Digest;
    }
    [DataContract] public sealed class ReleaseDocument
    {
        [DataMember(Name="tag_name")] public string Tag;
        [DataMember(Name="html_url")] public string Url;
        [DataMember(Name="body")] public string Body;
        [DataMember(Name="draft")] public bool Draft;
        [DataMember(Name="prerelease")] public bool Prerelease;
        [DataMember(Name="assets")] public ReleaseAsset[] Assets;
    }
    [DataContract] public sealed class UpdatePackageManifest
    {
        [DataMember(Name="version")] public string Version;
        [DataMember(Name="jtdxSha256")] public string JtdxSha256;
    }
    public sealed class AvailableUpdate
    {
        public Version Version; public string Tag, Url, Notes, ArchiveUrl, ChecksumUrl, ArchiveDigest;
    }
    public sealed class PreparedUpdate
    {
        public string Folder, Archive, ArchiveSha256, JtdxSha256, Version;
    }
    [DataContract] public sealed class UpdateInstallRequest
    {
        [DataMember] public string TargetExe;
        [DataMember] public string JtdxExe;
        [DataMember] public string Archive;
        [DataMember] public string ArchiveSha256;
        [DataMember] public string Version;
        [DataMember] public string JtdxSha256;
        [DataMember] public int ParentId;
        [DataMember] public string ParentStartUtc;
        [DataMember] public bool Demo;
    }
    public interface IUpdateTransport
    {
        byte[] Read(string url, int maximumBytes);
        void Download(string url, string file, int maximumBytes, Action<UpdateDownloadProgress> progress);
    }
    public sealed class HttpUpdateTransport : IUpdateTransport
    {
        static HttpWebRequest Request(string url)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var request=(HttpWebRequest)WebRequest.Create(url);
            request.UserAgent="JTDX-AutoCQ";request.Accept="application/vnd.github+json";
            request.Timeout=20000;request.ReadWriteTimeout=20000;request.AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate;
            return request;
        }
        static void Transfer(string url, Stream destination, int maximumBytes, Action<UpdateDownloadProgress> progress, bool archive = false)
        {
            if (progress != null) progress(new UpdateDownloadProgress(UpdateDownloadStage.Connecting));
            var request = Request(url);
            // Archive bytes must match their checksum and Content-Length.
            // Metadata can still use HTTP compression.
            if (archive) { request.AutomaticDecompression = DecompressionMethods.None; request.Headers[HttpRequestHeader.AcceptEncoding] = "identity"; }
            using(var response=(HttpWebResponse)request.GetResponse())
            using(Stream input=response.GetResponseStream())
            {
                if(response.ContentLength>maximumBytes)throw new InvalidDataException("Размер загрузки превышает допустимый.");
                long? size = response.ContentLength >= 0 ? (long?)response.ContentLength : null;
                var watch = Stopwatch.StartNew(); long nextReport = 0;
                if (progress != null) progress(new UpdateDownloadProgress(UpdateDownloadStage.Downloading, 0, size));
                byte[] buffer=new byte[16384];int total=0,read;
                while((read=input.Read(buffer,0,buffer.Length))>0)
                {
                    total=checked(total+read);if(total>maximumBytes)throw new InvalidDataException("Размер загрузки превышает допустимый.");
                    destination.Write(buffer,0,read);
                    if(progress!=null && watch.ElapsedMilliseconds >= nextReport)
                    {
                        progress(new UpdateDownloadProgress(UpdateDownloadStage.Downloading, total, size, watch.Elapsed));
                        nextReport = watch.ElapsedMilliseconds + 150;
                    }
                }
                if (archive && size.HasValue && total != size.Value) throw new IOException("Загрузка оборвалась до получения всего файла. Повторите скачивание.");
                if (progress != null) progress(new UpdateDownloadProgress(UpdateDownloadStage.Downloading, total, size, watch.Elapsed));
            }
        }
        public byte[] Read(string url,int maximumBytes)
        { using(var output=new MemoryStream()){Transfer(url,output,maximumBytes,null);return output.ToArray();} }
        public void Download(string url,string file,int maximumBytes,Action<UpdateDownloadProgress> progress)
        { using(var output=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None))Transfer(url,output,maximumBytes,progress,true); }
    }
    public sealed class UpdateClient
    {
        public const string RepositoryUrl="https://github.com/ub3dqy/jtdx-autocq";
        public const string LatestApi="https://api.github.com/repos/ub3dqy/jtdx-autocq/releases/latest";
        public readonly Version CurrentVersion;
        readonly IUpdateTransport transport;
        static readonly Regex VersionTag=new Regex(@"\Av?(\d+\.\d+\.\d+)\z",RegexOptions.CultureInvariant);
        static readonly Regex Sha=new Regex(@"\A[0-9a-fA-F]{64}\z",RegexOptions.CultureInvariant);
        static readonly HashSet<string> RequiredFiles=new HashSet<string>(new[]{"JTDX-AutoCQ.exe","JTDX-AutoCQ-Patch.exe","update.json"},StringComparer.OrdinalIgnoreCase);
        public UpdateClient(string currentVersion,IUpdateTransport transport=null)
        { CurrentVersion=Version.Parse(currentVersion);this.transport=transport??new HttpUpdateTransport(); }
        public static T Json<T>(byte[] bytes)
        { int start=bytes.Length>=3 && bytes[0]==0xef && bytes[1]==0xbb && bytes[2]==0xbf?3:0;
            using(var input=new MemoryStream(bytes,start,bytes.Length-start))return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(input); }
        public static void WriteJson<T>(string file,T value)
        { using(var output=File.Create(file))new DataContractJsonSerializer(typeof(T)).WriteObject(output,value); }
        public AvailableUpdate Check()
        { return ParseRelease(transport.Read(LatestApi,2*1024*1024)); }
        public static AvailableUpdate ParseRelease(byte[] bytes)
        {
            var document=Json<ReleaseDocument>(bytes);Match tag=VersionTag.Match(document.Tag??"");
            if(!tag.Success || document.Draft || document.Prerelease)throw new InvalidDataException("GitHub не вернул готовый выпуск AutoCQ.");
            string version=tag.Groups[1].Value;
            string prefix=RepositoryUrl+"/releases/download/"+document.Tag+"/";
            string archive="JTDX-AutoCQ-"+version+"-win64.zip";
            ReleaseAsset[] assets=document.Assets??new ReleaseAsset[0];
            ReleaseAsset[] packages=assets.Where(a=>a.Name==archive).ToArray(),checksums=assets.Where(a=>a.Name=="SHA256SUMS.txt").ToArray();
            if(packages.Length!=1 || checksums.Length!=1 || packages[0].Url!=prefix+archive || checksums[0].Url!=prefix+"SHA256SUMS.txt"
                || document.Url!=RepositoryUrl+"/releases/tag/"+document.Tag)
                throw new InvalidDataException("В выпуске отсутствуют проверенные файлы обновления AutoCQ.");
            string digest=packages[0].Digest;
            if(!String.IsNullOrEmpty(digest) && (!digest.StartsWith("sha256:",StringComparison.Ordinal) || !Sha.IsMatch(digest.Substring(7))))
                throw new InvalidDataException("Неверная контрольная сумма в описании выпуска.");
            return new AvailableUpdate{Version=Version.Parse(version),Tag=document.Tag,Url=document.Url,Notes=document.Body??"",
                ArchiveUrl=packages[0].Url,ChecksumUrl=checksums[0].Url,ArchiveDigest=String.IsNullOrEmpty(digest)?null:digest.Substring(7)};
        }
        public static string ExpectedChecksum(string manifest,string fileName)
        {
            string result=null;
            foreach(string line in manifest.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries))
            {
                Match match=Regex.Match(line,@"\A([0-9a-fA-F]{64})\s+\*?(.+?)\s*\z");
                if(!match.Success || match.Groups[2].Value!=fileName)continue;
                if(result!=null)throw new InvalidDataException("В списке контрольных сумм повторяется имя архива.");
                result=match.Groups[1].Value.ToUpperInvariant();
            }
            if(result==null)throw new InvalidDataException("Не найдена контрольная сумма архива AutoCQ.");return result;
        }
        public PreparedUpdate Download(AvailableUpdate release,string directory,Action<UpdateDownloadProgress> progress=null)
        {
            if(release.Version<=CurrentVersion)throw new InvalidOperationException("Уже установлена эта или более новая версия AutoCQ.");
            if (progress != null) progress(new UpdateDownloadProgress(UpdateDownloadStage.Preparing));
            string name="JTDX-AutoCQ-"+release.Version.ToString(3)+"-win64.zip";
            string expected=ExpectedChecksum(Encoding.UTF8.GetString(transport.Read(release.ChecksumUrl,65536)),name);
            if(release.ArchiveDigest!=null && !expected.Equals(release.ArchiveDigest,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Контрольные суммы GitHub и архива не совпадают. Загрузка остановлена.");
            string folder=Path.Combine(directory,"updates",release.Version.ToString(3)+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
            UpdateDownloadProgress last = null;
            Action<UpdateDownloadProgress> report = value => { last = value; if (progress != null) progress(value); };
            string archive=Path.Combine(folder,name);transport.Download(release.ArchiveUrl,archive,64*1024*1024,report);
            if (last == null) last = new UpdateDownloadProgress(UpdateDownloadStage.Downloading, new FileInfo(archive).Length, new FileInfo(archive).Length);
            return VerifyAndExtract(archive,expected,release.Version.ToString(3),folder,stage => report(last.AtStage(stage)));
        }
        public static PreparedUpdate VerifyAndExtract(string archive,string expected,string version,string directory,Action<UpdateDownloadStage> progress=null)
        {
            if (progress != null) progress(UpdateDownloadStage.Verifying);
            if(!Sha.IsMatch(expected??"") || !JtdxPatcher.HashFile(archive).Equals(expected,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Контрольная сумма загруженного архива не совпала. Установка отменена.");
            if (progress != null) progress(UpdateDownloadStage.Extracting);
            string extracted=Path.GetFullPath(Path.Combine(directory,"package"));Directory.CreateDirectory(extracted);
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
            using(var zip=ZipFile.OpenRead(archive))
            {
                if(zip.Entries.Count>200)throw new InvalidDataException("Неожиданное содержимое архива.");
                foreach(ZipArchiveEntry entry in zip.Entries)
                {
                    string name=entry.FullName.Replace('\\','/');
                    if(name.StartsWith("/",StringComparison.Ordinal) || name.Contains(":") || name.Split('/').Any(part=>part==".." || part=="."))
                        throw new InvalidDataException("В архиве обнаружен недопустимый путь.");
                    string target=Path.GetFullPath(Path.Combine(extracted,name.Replace('/',Path.DirectorySeparatorChar)));
                    if(!target.StartsWith(extracted+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) || !names.Add(name))
                        throw new InvalidDataException("В архиве повторяется или выходит за папку имя файла.");
                    if(name.EndsWith("/",StringComparison.Ordinal)){Directory.CreateDirectory(target);continue;}
                    total=checked(total+entry.Length);if(total>128*1024*1024)throw new InvalidDataException("Распакованное обновление слишком велико.");
                    if(Regex.IsMatch(name,@"(?:^|/)(?:operator\.xml|connection\.xml|status\.xml|preferences\.xml|JTDX\.ini|runtime\.log|jtdx\.exe)$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant))
                        throw new InvalidDataException("Обновление содержит личные настройки или неподходящий файл.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target));entry.ExtractToFile(target,false);
                }
            }
            if(RequiredFiles.Any(file=>!names.Contains(file)))throw new InvalidDataException("В архиве не хватает файлов для установки из программы.");
            var manifest=Json<UpdatePackageManifest>(File.ReadAllBytes(Path.Combine(extracted,"update.json")));
            if(manifest.Version!=version || !Sha.IsMatch(manifest.JtdxSha256??"")
                || FileVersionInfo.GetVersionInfo(Path.Combine(extracted,"JTDX-AutoCQ.exe")).FileVersion!=version+".0")
                throw new InvalidDataException("Версия файлов обновления не совпала с выпуском GitHub.");
            if (progress != null) progress(UpdateDownloadStage.Ready);
            return new PreparedUpdate{Folder=directory,Archive=archive,ArchiveSha256=expected.ToUpperInvariant(),Version=version,JtdxSha256=manifest.JtdxSha256.ToUpperInvariant()};
        }
        public static Process LaunchInstaller(PreparedUpdate prepared,string targetExe,string jtdxExe,bool demo)
        {
            if(JtdxPatcher.IsRunning())throw new InvalidOperationException("Закройте JTDX перед установкой обновления. LogHX можно оставить открытым.");
            string script=Path.Combine(prepared.Folder,"install-update.ps1"),request=Path.Combine(prepared.Folder,"request.json");
            File.WriteAllText(script,Encoding.UTF8.GetString(JtdxPatcher.Resource("Update-AutoCQ.ps1")).TrimStart('\ufeff'),new UTF8Encoding(true));
            using(Process parent=Process.GetCurrentProcess())
                WriteJson(request,new UpdateInstallRequest{TargetExe=Path.GetFullPath(targetExe),JtdxExe=Path.GetFullPath(jtdxExe),
                    Archive=prepared.Archive,ArchiveSha256=prepared.ArchiveSha256,Version=prepared.Version,JtdxSha256=prepared.JtdxSha256,
                    ParentId=parent.Id,ParentStartUtc=parent.StartTime.ToUniversalTime().ToString("O"),Demo=demo});
            string powershell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe");
            var start=new ProcessStartInfo(powershell,"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File "+Quote(script)+" -RequestFile "+Quote(request))
                {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
            bool protectedDirectory=!CanWriteDirectory(Path.GetDirectoryName(Path.GetFullPath(targetExe)));
            if(File.Exists(jtdxExe) && JtdxPatcher.HashFile(jtdxExe)!=prepared.JtdxSha256)
                protectedDirectory|=!CanWriteDirectory(Path.GetDirectoryName(Path.GetFullPath(jtdxExe)));
            if(protectedDirectory){start.UseShellExecute=true;start.Verb="runas";}
            return Process.Start(start);
        }
        static bool CanWriteDirectory(string directory)
        {
            string probe=Path.Combine(directory,".autocq-write-"+Guid.NewGuid().ToString("N"));
            try{using(File.Create(probe)){}return true;}
            catch(UnauthorizedAccessException){return false;}catch(System.Security.SecurityException){return false;}
            finally{if(File.Exists(probe))File.Delete(probe);}
        }
        internal static string Quote(string value)
        { if(value.IndexOf('"')>=0 || value.IndexOf('\0')>=0)throw new ArgumentException("Неверный путь.");return "\""+value+"\""; }
    }
}
