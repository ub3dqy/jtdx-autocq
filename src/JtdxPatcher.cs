using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace JtdxAutoCq
{
    public static class JtdxPatcher
    {
        public const string OriginalHash = "6B6718432EDB55B3E3C8A5B884522BDC5EF5E466BF074E9DAC09CC0B64CE399C";
        public const string PatchedHash = "6CF75A0AAFD0FB6F71FF4CFD26BA346B62A3EC207BB13F8ABB61B5B72EC1C25C";
        const string StubHash = "252D9AD1F7196659CC60FCEB5D4D83748B7839EEB4195A39A91FC42A2DA75A03";
        static readonly string[] Prior = {
            "0ABBA5EA8429CE4158DF0D2822D9A3108DD629A7C17E8332A8D40736ED6A413A",
            "EB846375E60FB972467246B8A3C1061A8B2B32984BBF838EAA47571F9814C5A9",
            "B615BFBDC23291A7CA297FE1EAE4EB7D2AF0E8966C962D4FB0EA620C6FF6B12F",
            "7F96272A867CA7050A7149A7A3A22106B5295BF3A89D66829E0F2CEF6208B89B",
            "2A41CB063463ED67C4BA30E06283661D57FF6A9BBEDA2118F0377ABF6726B6B0",
            "523B3BE7FB893B0166808C3169D059F53CB849E16BFA804B5C30E255469E8326"
        };
        sealed class Section { public int Address, Size, Raw; }
        sealed class Hook
        {
            public int Address, Index, Parent; public byte[] Bytes;
            public Hook(int address, string bytes, int index, int parent)
            { Address=address; Bytes=Hex(bytes); Index=index; Parent=parent; }
        }
        sealed class Unwind { public int Begin, End, Index; }
        static int Align(int n, int alignment) { return checked((n+alignment-1)/alignment*alignment); }
        static int U32(byte[] b, int p) { return BitConverter.ToInt32(b,p); }
        static void Put(byte[] b, int p, int value) { Buffer.BlockCopy(BitConverter.GetBytes(value),0,b,p,4); }
        static void Check(bool valid, string reason) { if(!valid) throw new InvalidDataException(reason); }
        static byte[] Hex(string text)
        { var b=new byte[text.Length/2]; for(int i=0;i<b.Length;i++)b[i]=Convert.ToByte(text.Substring(i*2,2),16); return b; }
        public static string Hash(byte[] bytes)
        { using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-",""); }
        public static string HashFile(string file)
        { using(var sha=SHA256.Create())using(var input=File.OpenRead(file))return BitConverter.ToString(sha.ComputeHash(input)).Replace("-",""); }
        public static byte[] Resource(string name)
        {
            using(Stream input=typeof(JtdxPatcher).Assembly.GetManifestResourceStream(name))
            { if(input==null)throw new InvalidDataException("В комплекте AutoCQ отсутствует файл: "+name); using(var memory=new MemoryStream()){input.CopyTo(memory);return memory.ToArray();} }
        }
        public static byte[] Build(byte[] original, byte[] stub)
        {
            Check(Hash(original)==OriginalHash,"Нужна оригинальная JTDX 2.2.160-32A-rc10 для Windows 64 бита.");
            Check(Hash(stub)==StubHash,"Не совпала контрольная сумма патча AutoCQ.");
            int pe=U32(original,0x3c), opt=pe+24, count=BitConverter.ToUInt16(original,pe+6);
            int sectionsAt=opt+BitConverter.ToUInt16(original,pe+20), imageSize=U32(original,opt+56);
            int headers=U32(original,opt+60), fileAlign=U32(original,opt+36), sectionAlign=U32(original,opt+32);
            int exception=opt+112+3*8;
            Check(BitConverter.ToUInt16(original,opt)==0x20b && imageSize==0x2d17000,"Неизвестная структура файла JTDX.");
            Check(sectionsAt+(count+3)*40<=headers && U32(original,opt+112+4*8)==0,"Файл JTDX отличается от проверенной сборки.");
            var sections=new List<Section>();
            for(int i=0;i<count;i++){int p=sectionsAt+i*40;sections.Add(new Section{Address=U32(original,p+12),Size=U32(original,p+8),Raw=U32(original,p+20)});}
            Func<int,int> raw=rva=>{Section s=sections.FirstOrDefault(x=>rva>=x.Address && rva<x.Address+x.Size && x.Raw!=0);Check(s!=null,"Не найдена секция JTDX.");return s.Raw+rva-s.Address;};
            var hooks=new[] {
                new Hook(0x1eefbe,"488d5424404889f1e855edffff",0,0x1eef00),
                new Hook(0x1edd33,"488d9c24f0000000",1,0x1edd20),
                new Hook(0x1eddf0,"83f82f0f875dffffff",2,0x1edd20),
                new Hook(0x1ee2ce,"85c00f849b010000",3,0x1edd20),
                new Hook(0x1ee2e2,"0fb644246388442438",4,0x1edd20),
                new Hook(0x09ef5b,"8b8394050000c6834c06000000",32,0x09eeb0),
                new Hook(0x09f3ad,"488b9038060000488b05dd18b80248898424d0000000ffd0",5,0x09eeb0),
                new Hook(0x09f766,"488b83900000004889f9488b9038060000488b8424d0000000ffd0",6,0x09eeb0),
                new Hook(0x09f85e,"4889d9e8ba3a0200",22,0x09eeb0),
                new Hook(0x0c4fd2,"e849622500",23,0x0c3320),
                new Hook(0x0a8984,"80bb8b060000000f85cf020000",9,0x0a8770),
                new Hook(0x0a8ec5,"80bb86060000000f859e150000",10,0x0a8770),
                new Hook(0x10eef1,"8b8348050000",24,0x10d440),
                new Hook(0x0ab020,"488d15c9c22a01",25,0x0a8770),
                new Hook(0x0ab26a,"488d15afc02a01",26,0x0a8770),
                new Hook(0x0ab4e0,"488d1561be2a01",27,0x0a8770),
                new Hook(0x0a9e7d,"80bb86060000000f84b6070000",28,0x0a8770),
                new Hook(0x0a8dd6,"80bbf9050000000f852b240000",29,0x0a8770),
                new Hook(0x0a9e51,"80bb86060000000f84f5080000",30,0x0a8770),
                new Hook(0x0a9f49,"488d15bbd52a01",31,0x0a8770),
                new Hook(0x10f4c4,"84c00f8404190000",34,0x10d440),
                new Hook(0x110de1,"84c00f85e3e6ffff",35,0x10d440),
                new Hook(0x0e8edc,"e89f2f0400",38,0x0e8cc0),
                new Hook(0x110754,"e91ae1ffff",41,0x10d440),
                new Hook(0x1104d0,"e99ee3ffff",42,0x10d440),
                new Hook(0x12a241,"443bac24700100000f83c1010000",43,0x129740),
                new Hook(0x12a1c1,"488b8c2408010000",45,0x129740)
            };
            int packet=raw(0x21e86a);
            Check(original[packet]==51 && original[packet-2]==0x83 && original[packet-1]==0xf8,"Неизвестный обработчик сети JTDX.");
            int codeSize=2*sectionAlign;
            int[] offsets=Enumerable.Range(0,47).Select(i=>U32(stub,i*4)).ToArray();
            Check(offsets[14]==stub.Length && stub.Length<=codeSize,"Неизвестная карта патча.");
            Action<int,byte[]> verify=(rva,bytes)=>{int at=raw(rva);for(int i=0;i<bytes.Length;i++)Check(original[at+i]==bytes[i],"Проверка инструкций JTDX не пройдена.");};
            foreach(Hook h in hooks){verify(h.Address,h.Bytes);Check(offsets[h.Index]>=offsets.Length*4 && offsets[h.Index]<stub.Length,"Неизвестная точка патча.");}
            verify(0x3078f9,Hex("e822390100")); verify(0x307974,Hex("e8a7380100"));
            verify(0x110e0c,Hex("83f8ff410f95c6e9b7e6ffff")); verify(0x10f5b6,Hex("4c8b35bbedb002"));
            int codeRva=imageSize,stateRva=codeRva+codeSize,unwindRva=stateRva+sectionAlign;
            int oldTable=raw(U32(original,exception)),oldBytes=U32(original,exception+4);
            Check(oldBytes%12==0,"Неизвестная таблица обработки исключений.");
            var functions=new Dictionary<int,byte[]>();
            for(int p=0;p<oldBytes;p+=12){var entry=new byte[12];Buffer.BlockCopy(original,oldTable+p,entry,0,12);functions[U32(original,oldTable+p)]=entry;}
            var info=new List<byte[]>(); var extra=new List<Unwind>();
            var ranges=hooks.Select(h=>new[]{h.Index,h.Parent}).ToList(); ranges.Add(new[]{19,0x0a8770}); ranges.Add(new[]{20,0x0a8770});
            foreach(int[] range in ranges)
            {
                byte[] chain; Check(functions.TryGetValue(range[1],out chain),"Неизвестная исходная функция JTDX.");
                var entry=new byte[16];entry[0]=0x21;Buffer.BlockCopy(chain,0,entry,4,12);info.Add(entry);
                extra.Add(new Unwind{Begin=codeRva+offsets[range[0]],End=codeRva+offsets.Where(x=>x>offsets[range[0]]).Min(),Index=info.Count-1});
            }
            foreach(int[] range in new[]{new[]{15,16,4,0x28},new[]{13,18,offsets[17]-offsets[13],0x28},new[]{36,37,4,0x58},new[]{39,40,4,0x68}})
            {
                info.Add(new byte[]{1,(byte)range[2],1,0,(byte)range[2],(byte)((((range[3]-8)/8)<<4)|2),0,0});
                extra.Add(new Unwind{Begin=codeRva+offsets[range[0]],End=codeRva+offsets[range[1]],Index=info.Count-1});
            }
            extra=extra.OrderBy(x=>x.Begin).ToList();
            for(int i=1;i<extra.Count;i++)Check(extra[i-1].End<=extra[i].Begin,"Перекрываются области патча.");
            int tableBytes=oldBytes+extra.Count*12,infoAt=tableBytes;
            var unwind=new byte[tableBytes+info.Sum(x=>x.Length)]; Buffer.BlockCopy(original,oldTable,unwind,0,oldBytes);
            for(int i=0;i<extra.Count;i++)
            {
                Unwind e=extra[i];int p=oldBytes+i*12;Put(unwind,p,e.Begin);Put(unwind,p+4,e.End);Put(unwind,p+8,unwindRva+infoAt);
                byte[] bytes=info[e.Index];Buffer.BlockCopy(bytes,0,unwind,infoAt,bytes.Length);infoAt+=bytes.Length;
            }
            int codeRaw=Align(original.Length,fileAlign),stateRaw=codeRaw+Align(stub.Length,fileAlign),unwindRaw=stateRaw+sectionAlign;
            var output=new byte[unwindRaw+Align(unwind.Length,fileAlign)];Buffer.BlockCopy(original,0,output,0,original.Length);
            output[packet]=52;
            foreach(int rva in new[]{0x3078f9,0x307974})Buffer.BlockCopy(Hex("b801000000"),0,output,raw(rva),5);
            output[raw(0x110e0c)+5]=0x94;Buffer.BlockCopy(stub,0,output,codeRaw,stub.Length);Buffer.BlockCopy(unwind,0,output,unwindRaw,unwind.Length);
            Action<int,int,int> jump=(rva,length,target)=>{int p=raw(rva);for(int i=0;i<length;i++)output[p+i]=0x90;output[p]=0xe9;Put(output,p+1,target-rva-5);};
            foreach(Hook h in hooks)jump(h.Address,h.Bytes.Length,codeRva+offsets[h.Index]); jump(0x10f5b6,7,0x10f5f2);
            Action<int,string,int,int,int,uint> section=(i,name,va,size,ptr,flags)=>{
                int p=sectionsAt+(count+i)*40;Array.Clear(output,p,40);byte[] label=Encoding.ASCII.GetBytes(name);Buffer.BlockCopy(label,0,output,p,label.Length);
                Put(output,p+8,size);Put(output,p+12,va);Put(output,p+16,Align(size,fileAlign));Put(output,p+20,ptr);Put(output,p+36,unchecked((int)flags));
            };
            section(0,".acq",codeRva,stub.Length,codeRaw,0x60000020);section(1,".acqdata",stateRva,sectionAlign,stateRaw,0xc0000040);section(2,".acqinfo",unwindRva,unwind.Length,unwindRaw,0x40000040);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)(count+3)),0,output,pe+6,2);
            Put(output,opt+4,U32(original,opt+4)+Align(stub.Length,fileAlign));Put(output,opt+8,U32(original,opt+8)+sectionAlign+Align(unwind.Length,fileAlign));
            Put(output,opt+56,Align(unwindRva+unwind.Length,sectionAlign));Put(output,opt+64,0);Put(output,exception,unwindRva);Put(output,exception+4,tableBytes);
            Check(Hash(output)==PatchedHash,"Подготовленный патч не совпал с проверенной сборкой AutoCQ."); return output;
        }
        public static bool IsRunning()
        {
            Process[] processes = Process.GetProcessesByName("jtdx");
            try { return processes.Length > 0; }
            finally { foreach (Process process in processes) process.Dispose(); }
        }
        public static bool Apply(string executable, bool restore=false, Func<bool> running=null)
        {
            string file=Path.GetFullPath(executable),backup=file+".autocq-original";
            if(!String.Equals(Path.GetFileName(file),"jtdx.exe",StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
                throw new IOException("Выберите установленный файл jtdx.exe.");
            string before=HashFile(file);
            Check(before==OriginalHash || before==PatchedHash || Prior.Contains(before),"Эта сборка JTDX не подходит. Нужна 2.2.160-32A-rc10, 64 бита.");
            if(File.Exists(backup))Check(HashFile(backup)==OriginalHash,"Резервная копия JTDX не совпадает с оригиналом. Переустановите исходную сборку.");
            string expected=restore?OriginalHash:PatchedHash;
            if(before==expected)return false;
            if(restore || Prior.Contains(before))Check(File.Exists(backup),"Не найдена исходная копия JTDX. Переустановите JTDX 2.2.160-32A-rc10.");
            Func<bool> isRunning=running??new Func<bool>(IsRunning);
            if(isRunning())throw new IOException("Закройте JTDX перед подготовкой.");
            byte[] result=restore?File.ReadAllBytes(backup):Build(File.ReadAllBytes(before==OriginalHash?file:backup),Resource("queue-reply.bin"));
            string temporary=file+"."+Guid.NewGuid().ToString("N")+".autocq.tmp",previous=temporary+".previous";
            try
            {
                File.WriteAllBytes(temporary,result);Check(HashFile(temporary)==expected,"Не удалось проверить подготовленный файл.");
                if(isRunning() || HashFile(file)!=before)throw new IOException("JTDX был запущен или изменён во время подготовки. Закройте его и повторите.");
                if(!File.Exists(backup))File.Copy(file,backup,false);
                Check(HashFile(backup)==OriginalHash,"Не удалось сохранить исходную копию JTDX.");
                File.Replace(temporary,file,previous);
                if(HashFile(file)!=expected){File.Replace(previous,file,null);throw new IOException("Проверка установки не пройдена. Предыдущий файл восстановлен.");}
                return true;
            }
            finally { if(File.Exists(temporary))File.Delete(temporary);if(File.Exists(previous))File.Delete(previous); }
        }
    }
    static class PatchProgram
    {
        static int Main(string[] args)
        {
            Console.OutputEncoding=new UTF8Encoding(false);
            try
            {
                int at=Array.IndexOf(args,"--jtdx");
                if(at<0 || at+1>=args.Length)throw new ArgumentException("Use --jtdx followed by the path to jtdx.exe.");
                JtdxPatcher.Apply(args[at+1],args.Contains("--restore"));Console.WriteLine("JTDX patch verified.");return 0;
            }
            catch(Exception e){Console.Error.WriteLine(e.Message);return 1;}
        }
    }
}
