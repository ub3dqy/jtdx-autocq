using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Xml.Serialization;
using JtdxAutoCq;

class AutoCqTests
{
    static readonly DateTime At = new DateTime(2025, 11, 7, 12, 0, 0, DateTimeKind.Utc);
    static StreamWriter report;
    static int passed;
    static void Assert(bool b, string text) { if (!b) throw new Exception(text); }
    static void Run(string name, Action test)
    { test(); passed++; string s = "PASS  " + name; report.WriteLine(s); Console.WriteLine(s); }
    static RadioStatus S(bool enabled, string dx)
    { return new RadioStatus { Frequency = 14074000, Mode = "FT8", DxCall = dx, DeCall = "K1ABC", TxEnabled = enabled, TxFirst = true }; }
    static AutoCqEngine Active(out List<string> commands)
    {
        AutoCqEngine e = new AutoCqEngine(); List<string> list = new List<string>(); commands = list;
        e.RequestCq = () => list.Add("CQ"); e.RequestHalt = () => list.Add("HALT");
        e.OnStatus(S(true, "W1AW"), At); e.Start(At); return e;
    }
    static void Finish(AutoCqEngine e, DateTime now)
    {
        e.OnLine(Tx(now,"W1AW","RR73"),now);
        e.OnLine("QSO logged: W1AW", now);
        e.OnLine(Rx(now.AddSeconds(1),"W1AW","73"),now.AddSeconds(1));
        e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Singleshot 14.074 MHz", now.AddSeconds(1));
        e.OnStatus(S(false, ""), now.AddSeconds(1));
    }
    static string Tx(DateTime time,string peer,string payload)
    {return time.ToString("yyyyMMdd_HHmmss")+".000(0) Transmitting 14.074 MHz + 1500Hz FT8: "+peer+" K1ABC "+payload;}
    static string Rx(DateTime time,string peer,string payload)
    {return time.ToString("yyyyMMdd_HHmmss")+" -10 0.1 1500 ~ K1ABC "+peer+" "+payload;}
    static string DecodeFinished(DateTime time)
    { return time.ToString("yyyyMMdd_HHmmss.fff") + "(0)  Decoding finished"; }
    static DecodedMessage D(DateTime time,string peer,string payload)
    {return new DecodedMessage {IsNew=true,Milliseconds=(uint)time.TimeOfDay.TotalMilliseconds,Snr=-11,DeltaTime=0.2,DeltaFrequency=1736,Mode="~",Message="K1ABC "+peer+" "+payload};}
    static byte[] DecodePacket(DecodedMessage d)
    {
        using(QtWriter w=new QtWriter(2,"JTDX",2))
        {w.Bool(d.IsNew);w.U32(d.Milliseconds);w.U32(unchecked((uint)d.Snr));w.Double(d.DeltaTime);w.U32(d.DeltaFrequency);w.Text(d.Mode);w.Text(d.Message);w.Bool(d.LowConfidence);w.Bool(d.OffAir);return w.Bytes();}
    }
    static DecodedMessage CqD(DateTime time,string peer)
    {DecodedMessage d=D(time,peer,"FN20");d.Message="CQ "+peer+" FN20";return d;}
    static AutoCqEngine SearchActive(out List<string> commands)
    {
        AutoCqEngine e=new AutoCqEngine();e.Options.CqAttemptsBeforeSearch=1;List<string> list=new List<string>();commands=list;
        e.RequestCq=()=>list.Add("CQ");e.RequestHalt=()=>list.Add("HALT");e.RequestReply=d=>list.Add("REPLY "+d.Message.Split(' ')[1]);
        e.OnStatus(S(true,""),At);e.Start(At);return e;
    }
    static AutoCqEngine MonitorActive(out List<string> commands, int minutes = 10)
    {
        var list = new List<string>(); commands = list;
        var e = new AutoCqEngine { UseNativeSelection = true };
        e.Options.MonitorEnabled = true;
        e.Options.MonitorDxcc = new[] { "JA" };
        e.Options.RunMinutes = minutes;
        e.DxccForCall = call => call.StartsWith("JA") ? "JA" : null;
        e.RequestCq = () => list.Add("CQ");
        e.RequestHalt = () => list.Add("HALT");
        e.RequestDirectCall = call => list.Add("DIRECT " + call);
        e.RequestReply = message => list.Add("REPLY " + message.Message);
        e.OnStatus(S(false, ""), At); e.Start(At);
        return e;
    }
    static void CqListen(AutoCqEngine e)
    {
        e.OnLine(Tx(At,"CQ","FN20"),At);RadioStatus tx=S(true,"");tx.Transmitting=true;e.OnStatus(tx,At.AddSeconds(1));e.OnStatus(S(true,""),At.AddSeconds(14));e.Tick(At.AddSeconds(14),true);
        e.OnLine("Halt Tx triggered at RX: UDP request ",At.AddSeconds(14.1));e.OnStatus(S(false,""),At.AddSeconds(14.1));
    }
    static byte[] StatusPacket(RadioStatus s, string id)
    {
        using (QtWriter w = new QtWriter(1, id, 2))
        {
            w.U64(s.Frequency); w.Text(s.Mode); w.Text(s.DxCall); w.Text("-10"); w.Text(s.Mode);
            w.Bool(s.TxEnabled); w.Bool(s.Transmitting); w.Bool(s.Decoding);
            w.U32(1500); w.U32(1500); w.Text(s.DeCall); w.Text("FN20"); w.Text("FN31");
            w.Bool(s.Watchdog); w.Text(""); w.Bool(false); w.Bool(s.TxFirst); return w.Bytes();
        }
    }
    static void Pump(UdpBridge bridge, Func<bool> ready)
    {
        for (int n = 0; n < 150; n++) { bridge.Poll(); if (ready()) return; Thread.Sleep(5); }
        throw new Exception("UDP timeout");
    }
    static byte[] Receive(UdpClient socket)
    { IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0); return socket.Receive(ref remote); }
    static void Send(UdpClient socket, byte[] bytes, int port)
    { socket.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, port)); }
    static string Fixture()
    {
        string p = Path.Combine("work", "tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return Path.GetFullPath(p);
    }

    [STAThread]
    static int Main(string[] args)
    {
        using (report = new StreamWriter(args[0], false, new UTF8Encoding(true)))
        {
            report.AutoFlush = true;
            report.WriteLine("JTDX AutoCQ — офлайн-проверка " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm zzz"));
            report.WriteLine("Рабочий профиль JTDX и трансивер в этих тестах не используются.\n");
            try
            {
                Run("Initial CQ is sent once and requires TX acknowledgement", () => {
                    AutoCqEngine e = new AutoCqEngine(); int cq = 0; e.RequestCq = () => cq++;
                    e.OnStatus(S(false, ""), At); e.Start(At); e.Tick(At.AddSeconds(1), true); e.Tick(At.AddSeconds(2), true);
                    Assert(cq == 1, "Duplicate CQ"); e.OnStatus(S(true, ""), At.AddSeconds(2)); e.Tick(At.AddSeconds(12), true);
                    Assert(cq == 1 && e.Armed, "TX acknowledgement failed");
                });
                Run("Logged QSO returns to CQ after automatic FIN stop", () => {
                    List<string> c; AutoCqEngine e = Active(out c); Finish(e, At);
                    e.Tick(At.AddMilliseconds(1001), true);
                    Assert(c.SequenceEqual(new[] {"CQ"}) && e.LoggedCount == 1, "Ready native FIN inserted an unnecessary delay");
                });
                Run("Manual Halt during pending restart cancels automation", () => {
                    List<string> c; AutoCqEngine e = Active(out c); Finish(e, At);
                    e.OnLine("Halt Tx triggered at RX: Halt Tx button clicked 14.074 MHz", At.AddMilliseconds(1600));
                    e.Tick(At.AddSeconds(2), true); Assert(!e.Armed && c.Count == 0, "Manual stop was overridden");
                });
                Run("Stop button prevents later automatic events from restarting TX", () => {
                    List<string> c; AutoCqEngine e = Active(out c); e.Stop("Stop", true); Finish(e, At); e.Tick(At.AddSeconds(4), true);
                    Assert(!e.Armed && c.SequenceEqual(new[] {"HALT"}), "Restart after stop");
                });
                Run("Retry counter exhaustion returns to CQ without inventing a QSO", () => {
                    List<string> c; AutoCqEngine e = Active(out c);
                    e.OnLine("Halt Tx triggered at RX: counter triggered 14.074 MHz", At); e.OnStatus(S(false, ""), At); e.Tick(At.AddSeconds(1), true);
                    Assert(c.SequenceEqual(new[] {"CQ"}) && e.LoggedCount == 0, "Retry recovery failed");
                });
                Run("Called station not decoded resumes the queue or CQ after JTDX finishes decoding", () => {
                    const string reason = "Halt Tx triggered at RX:  Called station not decoded + QString::number(m_no_decode) + periods in row";
                    foreach (bool hasNextCaller in new[] { false, true })
                    foreach (bool statusBeforeHalt in new[] { false, true })
                    {
                        List<string> commands;
                        AutoCqEngine engine = Active(out commands);
                        engine.UseNativeSelection = true;
                        engine.RequestCallerReply = decode => commands.Add(decode.Message);
                        engine.AddQueuedCall("W1AW", At);
                        engine.OnLine(Tx(At, "W1AW", "R-17"), At);
                        DateTime halt = At.AddSeconds(29.3);
                        if (hasNextCaller)
                            engine.OnDecode(D(At.AddSeconds(15), "W2ABC", "FN20"), At.AddSeconds(29));
                        RadioStatus decoding = S(false, "");
                        decoding.Decoding = true;
                        if (statusBeforeHalt) engine.OnStatus(decoding, halt.AddMilliseconds(-100));
                        engine.OnLine(reason, halt);
                        engine.Tick(halt, true);
                        Assert(engine.Armed && commands.Count == 0, "Automatic no-decode stop disarmed AutoCQ or started TX too soon");
                        if (!statusBeforeHalt) engine.OnStatus(decoding, halt.AddMilliseconds(100));
                        engine.Tick(halt.AddMilliseconds(200), true);
                        Assert(commands.Count == 0, "AutoCQ started TX before decoding finished");

                        engine.OnStatus(S(false, ""), halt.AddMilliseconds(300));
                        engine.Tick(halt.AddMilliseconds(300), true);
                        Assert(engine.Armed && engine.LoggedCount == 0
                            && commands.SequenceEqual(new[] { hasNextCaller ? "K1ABC W2ABC FN20" : "CQ" })
                            && engine.Waiting.Find("W1AW").PauseReason == CallerQueue.NoAnswer,
                            "No-decode recovery did not skip the unanswered station");
                    }
                });
                Run("Hound and unknown stops never restart", () => {
                    foreach (string reason in new[] {"m_houndMode, counter triggered", "Rig control error", "Halt Tx button clicked"})
                    {
                        List<string> c; AutoCqEngine e = Active(out c); e.OnLine("QSO logged: W1AW", At);
                        e.OnLine("Halt Tx triggered at RX: " + reason + " 14.074 MHz", At); e.OnStatus(S(false, ""), At); e.Tick(At.AddSeconds(2), true);
                        Assert(!e.Armed && c.Count == 0, reason);
                    }
                });
                Run("Unlogged and stale QSOs do not trigger another CQ", () => {
                    foreach (int age in new[] {-1, 121})
                    {
                        List<string> c; AutoCqEngine e = Active(out c);
                        if (age >= 0) e.OnLine("QSO logged: W1AW", At.AddSeconds(-age));
                        e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Autolog 14.074 MHz", At);
                        e.OnStatus(S(false, ""), At); e.Tick(At.AddSeconds(2), true);
                        Assert(!e.Armed && c.Count == 0, "Used missing/stale log event");
                    }
                });
                Run("A previously logged station cannot complete a different QSO", () => {
                    List<string> c; AutoCqEngine e = Active(out c); e.OnLine("QSO logged: W1AW", At);
                    e.OnStatus(S(true, "W2ABC"), At.AddSeconds(1));
                    e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Autolog 14.074 MHz", At.AddSeconds(2));
                    e.OnStatus(S(false, ""), At.AddSeconds(2)); e.Tick(At.AddSeconds(4), true);
                    Assert(!e.Armed && !c.Contains("CQ"), "Wrong station used");
                });
                Run("DL1ZZA replay continues after final 73, RR73 or RRR without a log entry", () => {
                    foreach (string final in new[] { "73", "RR73", "RRR" })
                    foreach (bool hasNextCaller in new[] { false, true })
                    foreach (bool statusBeforeFin in new[] { false, true })
                    {
                        DateTime tx = new DateTime(2025, 11, 14, 16, 3, 0, DateTimeKind.Utc);
                        var engine = new AutoCqEngine { UseNativeSelection = true };
                        var commands = new List<string>();
                        var notices = new List<string>();
                        engine.RequestCq = () => commands.Add("CQ");
                        engine.RequestHalt = () => commands.Add("HALT");
                        engine.RequestCallerReply = d => commands.Add(d.Message);
                        engine.Notice = notices.Add;
                        RadioStatus working = S(true, "DL1ZZA");
                        working.Frequency = 3573000; working.DeCall = "K1ABC";
                        engine.OnStatus(working, tx.AddSeconds(-1)); engine.Start(tx.AddSeconds(-1));
                        engine.Waiting.Add("DL1ZZA", tx.AddSeconds(-1));
                        engine.OnLine("20251114_160300.111(0) Call to log:DL1ZZA Last logged call1:R9ZZA Last logged call2:R9ZZB lastmsg_is73:True LastMsg:R9ZZA K1ABC RR73", tx.AddMilliseconds(111));
                        engine.OnLine("20251114_160300.111(0)  Transmitting 3.573 MHz + 999Hz  FT8:  DL1ZZA K1ABC RR73", tx.AddMilliseconds(111));

                        DateTime received = tx.AddSeconds(28.916), fin = tx.AddSeconds(29.183);
                        if (hasNextCaller)
                        {
                            var decode = D(tx.AddSeconds(15), "W3ABC", "FN20");
                            decode.Message = "K1ABC W3ABC FN20";
                            engine.OnDecode(decode, received);
                        }
                        working.Decoding = true;
                        engine.OnLine("20251114_160315  -3  0.3 2898 ~ K1ABC DL1ZZA " + final, received);
                        RadioStatus idle = S(false, ""); idle.Frequency = working.Frequency; idle.DeCall = working.DeCall;
                        if (statusBeforeFin) engine.OnStatus(idle, fin);
                        engine.OnLine("20251114_160329.183(0)  Halt Tx triggered at RX: FIN, end of QSO, Autolog ", fin);
                        if (!statusBeforeFin)
                        {
                            engine.Tick(fin, true);
                            Assert(commands.Count == 0, "Started the next QSO while JTDX was still decoding");
                            engine.OnStatus(idle, fin.AddMilliseconds(20));
                        }
                        engine.OnLine(DecodeFinished(fin.AddMilliseconds(20)), fin.AddMilliseconds(20));
                        engine.Tick(tx.AddSeconds(29.3), true);
                        Assert(engine.Armed && engine.ConfirmedCount == 1 && engine.LoggedCount == 0 && engine.Missing73Count == 0,
                            "Native FIN disarmed after a real " + final + " or counted an unlogged QSO as saved");
                        Assert(commands.SequenceEqual(new[] { hasNextCaller ? "K1ABC W3ABC FN20" : "CQ" })
                            && engine.Waiting.Rows.All(row => row.Call != "DL1ZZA") && notices.Any(n => n.Contains("DL1ZZA") && n.Contains("нет записи")),
                            "Confirmed but unlogged contact prevented the next transmission or was silently put back in the queue");
                    }
                });
                Run("An unlogged FIN with no fresh confirmation from the active DX cannot restart TX", () => {
                    foreach (string final in new[] { "73", "RR73" })
                    foreach (bool wrongCall in new[] { false, true })
                    {
                        List<string> commands; AutoCqEngine e = Active(out commands);
                        e.OnLine(Tx(At, "W1AW", "RR73"), At);
                        e.OnLine(Rx(wrongCall ? At.AddSeconds(15) : At.AddSeconds(-30), wrongCall ? "W2ABC" : "W1AW", final), At.AddSeconds(29));
                        e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Autolog ", At.AddSeconds(29.1));
                        e.OnStatus(S(false, ""), At.AddSeconds(29.2));
                        e.Tick(At.AddSeconds(30), true);
                        Assert(!e.Armed && e.ConfirmedCount == 0 && commands.Count == 0, "Unrelated final reply authorized another transmission");
                    }
                });
                Run("Transmitting, decoding and nonempty DX each defer CQ", () => {
                    foreach (int condition in new[] {0,1,2})
                    {
                        List<string> c; AutoCqEngine e = Active(out c); Finish(e, At); RadioStatus s = S(false, condition == 2 ? "W2ABC" : "");
                        s.Transmitting = condition == 0; s.Decoding = condition == 1; e.OnStatus(s, At.AddSeconds(1)); e.Tick(At.AddSeconds(3), true);
                        Assert(c.Count == 0, "CQ while busy"); e.OnStatus(S(false, ""), At.AddSeconds(4)); e.Tick(At.AddSeconds(4), true);
                        Assert(c.SequenceEqual(new[] {"CQ"}), "Did not resume after busy state ended");
                    }
                });
                Run("Watchdog and lost connection stop automation", () => {
                    List<string> c; AutoCqEngine e = Active(out c); RadioStatus s = S(true, "W1AW"); s.Watchdog = true; e.OnStatus(s, At);
                    Assert(!e.Armed && c.Contains("HALT"), "Watchdog ignored");
                    e = Active(out c); e.Tick(At, false); Assert(!e.Armed && c.Contains("HALT"), "Connection loss ignored");
                });
                Run("Frequency, mode and TX-period changes stop automation", () => {
                    foreach (int change in new[] {0,1,2})
                    {
                        List<string> c; AutoCqEngine e = Active(out c); RadioStatus s = S(true, "W1AW");
                        if (change == 0) s.Frequency = 7074000; if (change == 1) s.Mode = "FT4"; if (change == 2) s.TxFirst = false;
                        e.OnStatus(s, At); Assert(!e.Armed && c.Contains("HALT"), "Changed session continued");
                    }
                });
                Run("Missing acknowledgement causes a halt instead of repeated commands", () => {
                    AutoCqEngine e = new AutoCqEngine(); int cq = 0, halt = 0; e.RequestCq = () => cq++; e.RequestHalt = () => halt++;
                    e.OnStatus(S(false,""), At); e.Start(At); e.Tick(At.AddSeconds(1),true); e.Tick(At.AddSeconds(8),true);
                    Assert(!e.Armed && cq == 1 && halt == 1, "Command repeated or no failure halt");
                });
                Run("An arbitrary Enable TX falling edge does not schedule CQ", () => {
                    List<string> c; AutoCqEngine e = Active(out c); e.OnStatus(S(false,""), At); e.Tick(At.AddSeconds(20),true);
                    Assert(c.Count == 0 && !e.Pending, "Restarted after unknown stop");
                });
                Run("FT4 runs through the same completion cycle", () => {
                    AutoCqEngine e = new AutoCqEngine(); RadioStatus s = S(true,"W1AW"); s.Mode="FT4";
                    int cq=0; e.RequestCq=()=>cq++; e.OnStatus(s,At); e.Start(At);
                    e.OnLine(Tx(At,"W1AW","RR73"),At);e.OnLine("QSO logged: W1AW",At);
                    e.OnLine(Rx(At,"W1AW","73"),At);e.OnLine("Halt Tx triggered at RX: S73, none received 14.080 MHz",At);
                    RadioStatus idle=S(false,""); idle.Mode="FT4"; e.OnStatus(idle,At); e.Tick(At.AddSeconds(1),true);
                    Assert(cq==1 && e.Armed,"FT4 failed");
                });
                Run("Real UDP bridge preserves logbook traffic in both directions", () => {
                    using (UdpClient jtdx = new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))
                    using (UdpClient logbook = new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))
                    using (UdpClient other = new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))
                    using (UdpBridge bridge = new UdpBridge(0,"JTDX"))
                    {
                        bridge.Upstream=(IPEndPoint)logbook.Client.LocalEndPoint; byte[] data=StatusPacket(S(true,"W1AW"),"JTDX");
                        Send(jtdx,data,bridge.Port); Pump(bridge,()=>logbook.Available>0); Assert(Receive(logbook).SequenceEqual(data),"Logbook bytes changed");
                        foreach (string timeOn in new[] { "160200", "160700" })
                        {
                            using (var packet = new QtWriter(12, "JTDX", 2))
                            {
                                packet.Text("<call:6>DL1ZZA <mode:3>FT8 <band:3>80m <qso_date:8>20251114 <time_on:6>"
                                    + timeOn + " <rst_sent:3>-04 <rst_rcvd:3>-01 <eor>");
                                byte[] adif = packet.Bytes();
                                Send(jtdx, adif, bridge.Port); Pump(bridge, () => logbook.Available > 0);
                                Assert(Receive(logbook).SequenceEqual(adif), "An ADIF entry for a repeated callsign was not forwarded");
                            }
                        }
                        foreach (uint timeOn in new[] { 57720000u, 58020000u })
                        {
                            using (var packet = new QtWriter(5, "JTDX", 2))
                            {
                                const ulong julianDay = 2460994; // QDataStream date for 14 November 2025
                                packet.U64(julianDay);
                                packet.U32(timeOn + 24000); packet.Byte(1);
                                packet.Text("DL1ZZA"); packet.Text("JO50"); packet.U64(3573999); packet.Text("FT8");
                                foreach (string field in new[] { "-04", "-01", "", "", "" }) packet.Text(field);
                                packet.U64(julianDay);
                                packet.U32(timeOn); packet.Byte(1);
                                foreach (string field in new[] { "K1ABC", "K1ABC", "KO85", "", "" }) packet.Text(field);
                                byte[] qsoLogged = packet.Bytes();
                                Send(jtdx, qsoLogged, bridge.Port); Pump(bridge, () => logbook.Available > 0);
                                Assert(Receive(logbook).SequenceEqual(qsoLogged), "A QSO Logged datagram for a repeated callsign was not forwarded");
                            }
                        }
                        byte[] halt=QtPacket.Halt("JTDX",2); Send(logbook,halt,bridge.Port); Pump(bridge,()=>jtdx.Available>0);
                        Assert(Receive(jtdx).SequenceEqual(halt),"Logbook reply lost");
                        Send(other,data,bridge.Port); for(int i=0;i<10;i++){Thread.Sleep(5);bridge.Poll();}
                        Assert(logbook.Available==0 && bridge.Client.Equals(jtdx.Client.LocalEndPoint),"Other client stole connection");
                    }
                });
                Run("Two successive simulated QSOs trigger two actual UDP CQ requests", () => {
                    using(UdpClient radio=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))
                    using(UdpBridge bridge=new UdpBridge(0,"JTDX"))
                    {
                        AutoCqEngine e=new AutoCqEngine(); bridge.Received=p=>{if(p.Status!=null)e.OnStatus(p.Status,At);};
                        e.RequestCq=()=>bridge.SendToClient(QtPacket.TriggerCq("JTDX",2,"",e.Status.TxFirst,true));
                        Send(radio,StatusPacket(S(true,"W1AW"),"JTDX"),bridge.Port);Pump(bridge,()=>e.Status!=null);e.Start(At);
                        for(int cycle=0;cycle<2;cycle++)
                        {
                            Finish(e,At.AddSeconds(cycle*20)); e.Tick(At.AddSeconds(cycle*20+2),true);
                            Assert(radio.Available>0,"No CQ request"); byte[] bytes=Receive(radio);
                            using(QtReader r=new QtReader(bytes))
                            {Assert(r.U32()==QtPacket.Magic && r.U32()==2 && r.U32()==51 && r.Text()=="JTDX" && r.Text()=="" && r.Bool() && r.Bool(),"Invalid CQ packet or TX period");}
                            Send(radio,StatusPacket(S(true,"W1AW"),"JTDX"),bridge.Port);Pump(bridge,()=>e.Status.TxEnabled);
                        }
                        Assert(e.Resumes==2 && e.LoggedCount==2 && e.ConfirmedCount==2 && e.Armed,"Continuous operation failed");
                    }
                });
                Run("Qt status schema 2 and malformed-packet rejection", () => {
                    RadioStatus original=S(true,"W1AW"); RadioStatus parsed=QtPacket.Parse(StatusPacket(original,"JTDX")).Status;
                    Assert(parsed.Frequency==14074000 && parsed.Mode=="FT8" && parsed.TxFirst && parsed.TxEnabled && parsed.DxCall=="W1AW","Status decoding");
                    bool rejected=false; try{QtPacket.Parse(new byte[]{0xad,0xbc,0xcb,0xda});}catch(EndOfStreamException){rejected=true;}
                    Assert(rejected,"Truncated packet accepted");
                });
                Run("Log tail ignores historical events and handles split writes", () => {
                    string p=Fixture(),file=Path.Combine(p,DateTime.UtcNow.ToString("yyyyMM")+"_ALL.TXT");
                    File.WriteAllText(file,"QSO logged: OLD\r\n"); LogTail tail=new LogTail(p); List<string> lines=new List<string>();
                    tail.Read(lines.Add);Assert(lines.Count==0,"Historical log replayed");
                    File.AppendAllText(file,"QSO logged: W1AW\r\nHalt ");tail.Read(lines.Add);Assert(lines.Count==1,"Partial line mishandled");
                    File.AppendAllText(file,"Tx triggered at RX: FIN, end of QSO, Autolog 14.074 MHz\r\n");tail.Read(lines.Add);
                    Assert(lines.Count==2 && lines[1].StartsWith("Halt Tx"),"Split write lost");
                });
                Run("Settings backup, crash recovery and selective restoration", () => {
                    string p=Fixture(),profile=Path.Combine(p,"profile"),state=Path.Combine(p,"state");Directory.CreateDirectory(profile);
                    string file=Path.Combine(profile,"JTDX.ini");File.WriteAllText(file,"[Configuration]\r\nUDPServer=127.0.0.1\r\nUDPServerPort=2237\r\nAcceptUDPRequests=false\r\nUnrelated=before\r\n[Common]\r\nCallMode=2\r\n");
                    SettingsManager m=new SettingsManager(profile,state);m.Prepare();
                    IniFile ini=new IniFile(file);Assert(ini.Get("Configuration","UDPServerPort","")=="2247","Did not connect");
                    Assert(Directory.GetFiles(state,"JTDX-before-*.ini").Length==1,"No exact backup");
                    ini.Put("Configuration","Unrelated","operator-change");ini.Save(file);
                    m=new SettingsManager(profile,state);m.Restore();ini=new IniFile(file);
                    Assert(ini.Get("Configuration","UDPServerPort","")=="2237" && ini.Get("Configuration","AcceptUDPRequests","")=="false","Connection not restored");
                    Assert(ini.Get("Configuration","Unrelated","")=="operator-change" && ini.Get("Common","CallMode","")=="2","Operator settings changed");
                    Assert(ini.Get("Common","AutoSequence",null)==null,"An originally absent key remained");
                });
                Run("Prepare and Restore preserve either QSO prompt preference", () => {
                    string p=Fixture(),profile=Path.Combine(p,"profile"),state=Path.Combine(p,"state");Directory.CreateDirectory(profile);
                    string file = Path.Combine(profile, "JTDX.ini");
                    foreach (string prompt in new[] { "false", "true" })
                    {
                        string sessionState = Path.Combine(state, prompt);
                        File.WriteAllText(file, "[Configuration]\r\nUDPServer=127.0.0.1\r\nUDPServerPort=2237\r\nPromptToLog=" + prompt + "\r\nAutoQSOLogging=true\r\n");
                        SettingsManager m = new SettingsManager(profile, sessionState);
                        m.Prepare();
                        IniFile ini = new IniFile(file);
                        Assert(ini.Get("Configuration", "PromptToLog", "") == prompt, "Prepare changed the operator's QSO prompt setting");
                        Assert(ini.Get("Configuration", "AutoQSOLogging", "") == "true", "Automatic QSO logging was disabled");
                        m.Restore();
                        ini = new IniFile(file);
                        Assert(ini.Get("Configuration", "PromptToLog", "") == prompt, "Restore changed the operator's QSO prompt setting");
                    }
                });
                Run("Migrating a prepared 0.8 session restores the original QSO prompt", () => {
                    string p = Fixture(), profile = Path.Combine(p, "profile"), state = Path.Combine(p, "state");
                    Directory.CreateDirectory(profile);
                    Directory.CreateDirectory(state);
                    string file = Path.Combine(profile, "JTDX.ini");
                    File.WriteAllText(file, "[Configuration]\r\nUDPServer=127.0.0.1\r\nUDPServerPort=2247\r\nPromptToLog=true\r\nAutoQSOLogging=true\r\n");
                    ConnectionRecord legacy = new ConnectionRecord { Host = "127.0.0.1", Port = 2237 };
                    legacy.Values.Add(new SavedValue { Section = "Configuration", Key = "UDPServerPort", Original = "2237", Applied = "2247", Existed = true });
                    legacy.Values.Add(new SavedValue { Section = "Configuration", Key = "PromptToLog", Original = "false", Applied = "true", Existed = true });
                    using (FileStream saved = File.Create(Path.Combine(state, "connection.xml")))
                        new XmlSerializer(typeof(ConnectionRecord)).Serialize(saved, legacy);
                    SettingsManager m = new SettingsManager(profile, state);
                    m.Prepare();
                    IniFile ini = new IniFile(file);
                    Assert(ini.Get("Configuration", "PromptToLog", "") == "false", "Legacy QSO prompt override remained");
                    Assert(m.Record.Port == 2237 && ini.Get("Configuration", "UDPServerPort", "") == "2247", "LogHX bridge settings were lost in migration");
                    Assert(!m.Record.Values.Any(value => value.Key == "PromptToLog"), "The QSO prompt still belongs to the temporary configuration");
                });
                Run("Preparing rc10 bounds calls with SingleShot off", () => {
                    string p=Fixture(),profile=Path.Combine(p,"profile"),state=Path.Combine(p,"state");Directory.CreateDirectory(profile);
                    string file=Path.Combine(profile,"JTDX.ini");File.WriteAllText(file,"[Configuration]\r\nUDPServerPort=2237\r\nSeqAnswerInCallCounterValue=5\r\n[Common]\r\nSingleShotQSO=false\r\nSkipTx1=true\r\n");
                    SettingsManager m=new SettingsManager(profile,state);m.Prepare();IniFile i=new IniFile(file);
                    Assert(i.Get("Common","SingleShotQSO","")=="false","Preparing unexpectedly enabled SingleShot");
                    Assert(i.Get("Common","SkipTx1","")=="true","Prepare changed the operator's TX1 setting");
                    Assert(i.Get("Configuration","SeqAnswerInCallCount","")=="true" && i.Get("Configuration","SeqAnswerInCallCounterValue","")=="2","Initial calls are unbounded");
                    Assert(i.Get("Configuration","SeqSentRR7373Count","")=="true" && i.Get("Configuration","SeqSentRR7373CounterValue","")=="2","Final transmissions are unbounded");
                    m.Restore();i=new IniFile(file);
                    Assert(i.Get("Common","SingleShotQSO","")=="false" && i.Get("Common","SkipTx1","")=="true" && i.Get("Configuration","SeqAnswerInCallCounterValue","")=="5","Original sequencing preferences not restored");
                });
                Run("A log entry alone does not complete a QSO", () => {
                    List<string> c;AutoCqEngine e=Active(out c);e.OnLine(Tx(At,"W1AW","RR73"),At);e.OnLine("QSO logged: W1AW",At);
                    e.OnLine("Halt Tx triggered at RX: SRR73, none received, end of QSO, Singleshot ",At.AddSeconds(29));
                    e.OnStatus(S(false,""),At.AddSeconds(29));e.Tick(At.AddSeconds(31),true);
                    Assert(e.Armed && e.Awaiting73 && e.LoggedCount==1 && e.ConfirmedCount==0 && c.Count==0,"Logged QSO caused premature CQ");
                });
                Run("Missing 73 waits through TWO peer receive slots in FT8", () => {
                    List<string> c;AutoCqEngine e=Active(out c);e.OnLine(Tx(At,"W1AW","RR73"),At);e.OnLine("QSO logged: W1AW",At);
                    e.OnLine("Halt Tx triggered at RX: SRR73, none received, end of QSO, Singleshot ",At.AddSeconds(29));
                    e.OnStatus(S(false,""),At.AddSeconds(29));e.Tick(At.AddSeconds(30),true);e.Tick(At.AddSeconds(45),true);e.Tick(At.AddSeconds(59.9),true);
                    Assert(c.Count==0,"Skipped the second response opportunity");e.Tick(At.AddSeconds(60.1),true);
                    Assert(c.SequenceEqual(new[]{"CQ"}) && e.Missing73Count==1 && e.ConfirmedCount==0,"Timeout did not return to CQ");
                });
                Run("73 in the second receive slot permits continuation", () => {
                    List<string> c;AutoCqEngine e=Active(out c);e.OnLine(Tx(At,"W1AW","RR73"),At);e.OnLine("QSO logged: W1AW",At);
                    e.OnLine("Halt Tx triggered at RX: SRR73, none received, end of QSO, Singleshot ",At.AddSeconds(29));e.OnStatus(S(false,""),At.AddSeconds(29));
                    e.Tick(At.AddSeconds(45),true);Assert(c.Count==0,"Premature CQ");
                    e.OnLine(Rx(At.AddSeconds(45),"W1AW","73"),At.AddSeconds(59));e.Tick(At.AddSeconds(59),true);
                    Assert(c.SequenceEqual(new[]{"CQ"}) && e.ConfirmedCount==1 && e.Missing73Count==0,"Valid 73 ignored");
                });
                Run("Other calls and stale replays cannot confirm the active QSO", () => {
                    List<string> c;AutoCqEngine e=Active(out c);e.OnLine(Tx(At,"W1AW","RR73"),At);e.OnLine("QSO logged: W1AW",At);
                    foreach (string final in new[] { "73", "RR73" })
                    {
                        e.OnLine(Rx(At.AddSeconds(15), "W2ABC", final), At.AddSeconds(29));
                        e.OnLine(Rx(At.AddSeconds(-30), "W1AW", final), At.AddSeconds(29));
                        e.OnLine(Rx(At.AddSeconds(15), "W1AW", final).Replace("K1ABC", "W2ABC"), At.AddSeconds(29));
                        DecodedMessage replay = D(At.AddSeconds(15), "W1AW", final); replay.IsNew = false;
                        e.OnDecode(replay, At.AddSeconds(29));
                    }
                    Assert(e.Awaiting73 && e.ConfirmedCount == 0, "Unrelated or historical sign-off was accepted");
                });
                Run("I4ZZA regression: after our RR73 either final reply allows the next queued call in the same TX period", () => {
                    foreach (string final in new[] { "73", "RR73" })
                    {
                        DateTime sent = new DateTime(2025, 11, 14, 9, 20, 0, DateTimeKind.Utc);
                        var commands = new List<string>();
                        var engine = new AutoCqEngine { UseNativeSelection = true };
                        engine.Options.PriorityDxcc = new[] { "KL" };
                        engine.RequestCq = () => commands.Add("CQ");
                        engine.RequestHalt = () => commands.Add("HALT");
                        engine.RequestCallerReply = d => commands.Add(d.Message);
                        RadioStatus working = S(true, "I4ZZA");
                        working.Frequency = 21074000; working.DeCall = "K1ABC";
                        engine.OnStatus(working, sent.AddMinutes(-1)); engine.Start(sent.AddMinutes(-1));
                        engine.OnDecode(new DecodedMessage { IsNew = true, Milliseconds = 33525000, Snr = -12,
                            DeltaTime = 0.1, DeltaFrequency = 1508, Mode = "~", Message = "K1ABC TA2WTA KN41" }, sent.AddSeconds(-31));
                        engine.OnLine("20251114_092000.130(0)  Transmitting 21.074 MHz + 1809Hz FT8: I4ZZA K1ABC RR73", sent.AddSeconds(0.131));
                        engine.OnLine("20251114_092000.137(0)  QSO logged: I4ZZA", sent.AddSeconds(0.138));
                        DateTime decoded = sent.AddSeconds(29.222);
                        engine.OnLine("20251114_092015 -22  0.2 1914 ~ K1ABC I4ZZA " + final + "         ^", decoded);
                        var idle = S(false, ""); idle.Frequency = working.Frequency; idle.DeCall = working.DeCall;
                        engine.OnStatus(idle, decoded);
                        engine.OnLine("20251114_092029.212(0)  Halt Tx triggered at RX: FIN, end of QSO, Autolog ", decoded);
                        engine.OnLine(DecodeFinished(decoded), decoded);
                        engine.Tick(sent.AddSeconds(29.3), true);
                        Assert(commands.SequenceEqual(new[] { "K1ABC TA2WTA KN41" }) && engine.Armed
                            && engine.ConfirmedCount == 1 && engine.Missing73Count == 0 && !engine.ListenAfterFinal73,
                            "I4ZZA " + final + " did not confirm the exchange before 09:20:30 UTC");
                    }
                });
                Run("Repeated RR73 does not extend the confirmation timer forever", () => {
                    List<string> c;AutoCqEngine e=Active(out c);e.OnLine(Tx(At,"W1AW","RR73"),At);e.OnLine("QSO logged: W1AW",At);
                    e.OnLine(Tx(At.AddSeconds(30),"W1AW","RR73"),At.AddSeconds(30));
                    e.OnLine("Halt Tx triggered at RX: m_singleshot, counter triggered ",At.AddSeconds(59));e.OnStatus(S(false,""),At.AddSeconds(59));e.Tick(At.AddSeconds(60.1),true);
                    Assert(c.SequenceEqual(new[]{"CQ"}) && e.Missing73Count==1,"RR73 repeats moved the deadline");
                });
                Run("A premature next station or CQ fails closed", () => {
                    foreach(string next in new[]{"W2ABC K1ABC -10","CQ K1ABC FN20"})
                    {
                        List<string> c;AutoCqEngine e=Active(out c);e.OnLine(Tx(At,"W1AW","RR73"),At);e.OnLine("QSO logged: W1AW",At);
                        e.OnLine(At.AddSeconds(30).ToString("yyyyMMdd_HHmmss")+".000(0) Transmitting 14.074 MHz + 1500Hz FT8: "+next,At.AddSeconds(30));
                        Assert(!e.Armed && c.SequenceEqual(new[]{"HALT"}),"Native premature transition was missed");
                    }
                });
                Run("Manual QSO after STOP cannot restart the module", () => {
                    List<string> c;AutoCqEngine e=Active(out c);e.OnLine(Tx(At,"W1AW","RR73"),At);e.OnLine("QSO logged: W1AW",At);e.Stop("Operator STOP",true);
                    e.OnStatus(S(true,"W2ABC"),At.AddSeconds(90));e.OnLine(Tx(At.AddSeconds(90),"W2ABC","RR73"),At.AddSeconds(90));
                    e.OnLine("QSO logged: W2ABC",At.AddSeconds(90));e.OnLine(Rx(At.AddSeconds(105),"W2ABC","73"),At.AddSeconds(119));
                    e.OnStatus(S(false,""),At.AddSeconds(119));e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Singleshot ",At.AddSeconds(119));e.Tick(At.AddSeconds(150),true);
                    Assert(!e.Armed && !e.Pending && c.SequenceEqual(new[]{"HALT"}),"Manual completion restarted automation");
                });
                Run("A fresh caller takes precedence over an older priority queue entry after a native halt", () => {
                    List<string> commands;
                    AutoCqEngine e = Active(out commands);
                    e.UseNativeSelection = true;
                    e.Options.PriorityDxcc = new[] { "K" };
                    e.IsPriorityCall = call => call == "W2ABC";
                    e.RequestCallerReply = d => commands.Add(d.Message);
                    e.OnDecode(D(At.AddSeconds(-15), "W2ABC", "FN20"), At.AddSeconds(-1));
                    e.OnLine(Tx(At, "W1AW", "-10"), At);
                    e.OnDecode(D(At.AddSeconds(15), "W3ABC", "FN31"), At.AddSeconds(29));
                    e.OnLine(DecodeFinished(At.AddSeconds(29.2)), At.AddSeconds(29.2));
                    e.OnLine("Halt Tx triggered at RX: AutoCQ, counter triggered", At.AddSeconds(29.3));
                    e.OnStatus(S(false, ""), At.AddSeconds(29.4));
                    e.Tick(At.AddSeconds(29.4), true);
                    Assert(e.Armed && commands.SequenceEqual(new[] { "K1ABC W3ABC FN31" }),
                        "An older priority entry displaced the station calling us now");
                });
                Run("An unanswered call yields to a fresh lower queue entry only after the full RX decode", () => {
                    foreach (bool ft4 in new[] { false, true })
                    {
                        List<string> commands;
                        AutoCqEngine e = Active(out commands);
                        e.UseNativeSelection = true;
                        double slot = ft4 ? 7.5 : 15;
                        if (ft4)
                        {
                            e.Stop("Change fixture mode", false);
                            RadioStatus start = S(true, "W1AW"); start.Mode = "FT4";
                            e.OnStatus(start, At); e.Start(At);
                        }
                        e.RequestCallerReply = d => commands.Add(d.Message);
                        DecodedMessage old = D(At.AddSeconds(-slot), "W2ABC", "FN20");
                        DecodedMessage caller = D(At.AddSeconds(slot), "W3ABC", "-08");
                        if (ft4) { old.Mode = caller.Mode = ":"; }
                        e.OnDecode(old, At.AddSeconds(-1));
                        e.OnLine(Tx(At, "W1AW", "-10").Replace("FT8:", ft4 ? "FT4:" : "FT8:"), At);
                        RadioStatus receiving = S(true, "W1AW"); receiving.Mode = ft4 ? "FT4" : "FT8";
                        receiving.Decoding = true;
                        e.OnStatus(receiving, At.AddSeconds(slot * 2 - 1));
                        e.OnDecode(caller, At.AddSeconds(slot * 2 - 0.9));
                        Assert(e.HoldNativeSelection, "Native AutoSelect could race the incomplete RX decision");
                        e.Tick(At.AddSeconds(slot * 2 - 0.8), true);
                        Assert(commands.Count == 0, "A fresh caller interrupted an unfinished decode");
                        receiving.Decoding = false;
                        e.OnStatus(receiving, At.AddSeconds(slot * 2 - 0.7));
                        e.OnLine(DecodeFinished(At.AddSeconds(slot * 2 - 0.6)), At.AddSeconds(slot * 2 - 0.6));
                        e.Tick(At.AddSeconds(slot * 2 - 0.5), true);
                        Assert(commands.SequenceEqual(new[] { "HALT" }), "An unanswered call ignored a fresh caller");
                        e.OnLine("Halt Tx triggered at RX: UDP request", At.AddSeconds(slot * 2 - 0.4));
                        RadioStatus stopped = S(false, "W1AW"); stopped.Mode = receiving.Mode;
                        e.OnStatus(stopped, At.AddSeconds(slot * 2 - 0.3));
                        e.Tick(At.AddSeconds(slot * 2 - 0.2), true);
                        Assert(e.Armed && commands.SequenceEqual(new[] { "HALT", "K1ABC W3ABC -08" })
                            && e.Waiting.Find("W2ABC") != null,
                            "The fresh caller was lost or an older queued station was called first");
                    }
                });
                Run("A reply from the selected station preserves its exchange despite another caller", () => {
                    foreach (string answer in new[] { "FN20", "-09", "R-09", "" })
                    {
                        List<string> commands; AutoCqEngine e = Active(out commands);
                        e.OnLine(Tx(At, "W1AW", "-10"), At);
                        e.OnDecode(D(At.AddSeconds(15), "W2ABC", "FN31"), At.AddSeconds(28.9));
                        e.OnDecode(D(At.AddSeconds(15), "W1AW", answer), At.AddSeconds(29));
                        e.OnLine(DecodeFinished(At.AddSeconds(29.2)), At.AddSeconds(29.2));
                        e.Tick(At.AddSeconds(29.3), true);
                        Assert(e.Armed && commands.Count == 0, "A new caller displaced an answering station: " + answer);
                    }
                });
                Run("Old replays and excluded callers cannot displace an unanswered call", () => {
                    foreach (bool excluded in new[] { false, true })
                    {
                        List<string> commands; AutoCqEngine e = Active(out commands);
                        if (excluded) e.CanCall = call => call != "W2ABC";
                        e.OnLine(Tx(At, "W1AW", "-10"), At);
                        DecodedMessage caller = D(At.AddSeconds(excluded ? 15 : -15), "W2ABC", "FN31");
                        e.OnDecode(caller, At.AddSeconds(excluded ? 29 : -1));
                        caller.IsNew = false; e.OnDecode(caller, At.AddSeconds(29));
                        e.OnLine(DecodeFinished(At.AddSeconds(29.2)), At.AddSeconds(29.2));
                        e.Tick(At.AddSeconds(29.3), true);
                        Assert(e.Armed && commands.Count == 0, "An ineligible caller interrupted the selected station");
                    }
                });
                Run("A fresh caller waits through an exchange that has already progressed", () => {
                    foreach (string sent in new[] { "R-10", "RR73", "73" })
                    {
                        List<string> commands; AutoCqEngine e = Active(out commands);
                        e.OnLine(Tx(At, "W1AW", sent), At);
                        e.OnDecode(D(At.AddSeconds(15), "W2ABC", "FN31"), At.AddSeconds(29));
                        e.OnLine(DecodeFinished(At.AddSeconds(29.2)), At.AddSeconds(29.2));
                        e.Tick(At.AddSeconds(29.3), true);
                        Assert(e.Armed && commands.Count == 0, "A fresh caller interrupted the QSO signoff or report acknowledgement");
                    }
                });
                Run("A same-time replay cannot erase the original caller report", () => {
                    CallerQueue q = new CallerQueue(); RadioStatus status = S(true, "W1AW");
                    DecodedMessage original = D(At.AddSeconds(15), "W2ABC", "+02");
                    q.Observe(original, status, "W1AW", At.AddSeconds(29));
                    DecodedMessage replay = original.Copy(); replay.IsNew = false; replay.Message = "K1ABC W2ABC";
                    q.Observe(replay, status, "W1AW", At.AddSeconds(29.1));
                    Assert(q.Rows.Single().Decode.Message == original.Message && q.Rows.Single().Decode.IsNew,
                        "A shorter display replay discarded the received report");
                });
                Run("Fresh callers preserve DXCC priority within the same RX frame", () => {
                    List<string> commands; AutoCqEngine e = Active(out commands);
                    e.Options.PriorityDxcc = new[] { "K" }; e.IsPriorityCall = call => call == "W3ABC";
                    e.RequestCallerReply = d => commands.Add(d.Message);
                    e.OnLine(Tx(At, "W1AW", "-10"), At);
                    e.OnDecode(D(At.AddSeconds(15), "W2ABC", "FN20"), At.AddSeconds(28));
                    e.OnDecode(D(At.AddSeconds(15), "W3ABC", "FN31"), At.AddSeconds(29));
                    e.OnLine(DecodeFinished(At.AddSeconds(29.2)), At.AddSeconds(29.2));
                    e.Tick(At.AddSeconds(29.3), true);
                    e.OnLine("Halt Tx triggered at RX: UDP request", At.AddSeconds(29.4));
                    e.OnStatus(S(false, "W1AW"), At.AddSeconds(29.5)); e.Tick(At.AddSeconds(29.5), true);
                    Assert(commands.SequenceEqual(new[] { "HALT", "K1ABC W3ABC FN31" }), "DXCC priority was lost among fresh callers");
                });
                Run("A fresh caller cannot interrupt an already active transmission", () => {
                    List<string> commands; AutoCqEngine e = Active(out commands);
                    e.OnLine(Tx(At, "W1AW", "-10"), At);
                    e.OnDecode(D(At.AddSeconds(15), "W2ABC", "FN31"), At.AddSeconds(29));
                    e.OnLine(DecodeFinished(At.AddSeconds(29.2)), At.AddSeconds(29.2));
                    RadioStatus transmitting = S(true, "W1AW"); transmitting.Transmitting = true;
                    e.OnStatus(transmitting, At.AddSeconds(30)); e.Tick(At.AddSeconds(30), true);
                    Assert(e.Armed && commands.Count == 0, "A caller cut off an active TX frame");
                });
                Run("Monitoring ignores unrelated callers while a selected target is unanswered", () => {
                    List<string> commands; AutoCqEngine e = MonitorActive(out commands);
                    DecodedMessage target = CqD(At.AddSeconds(15), "JA1AAA"); target.IsNew = false;
                    e.OnDecode(target, At.AddSeconds(29)); e.Tick(At.AddSeconds(29), true);
                    Assert(commands.Count == 1, "The monitoring fixture did not select its visible target");
                    e.OnStatus(S(true, "JA1AAA"), At.AddSeconds(29.1));
                    e.OnLine(Tx(At.AddSeconds(30), "JA1AAA", "-10"), At.AddSeconds(30));
                    e.OnDecode(D(At.AddSeconds(45), "W2ABC", "FN31"), At.AddSeconds(59));
                    e.OnLine(DecodeFinished(At.AddSeconds(59.2)), At.AddSeconds(59.2)); e.Tick(At.AddSeconds(59.3), true);
                    Assert(e.Armed && commands.Count == 1 && e.Status.DxCall == "JA1AAA",
                        "An unrelated caller overrode the monitoring targets");
                });
                Run("After two unanswered calls CQ may clear the completed DX field", () => {
                    List<string> c;AutoCqEngine e=Active(out c);e.OnLine(Tx(At,"W1AW","-10"),At);e.OnLine(Tx(At.AddSeconds(30),"W1AW","-10"),At.AddSeconds(30));
                    e.OnLine("Halt Tx triggered at RX: m_singleshot, counter triggered ",At.AddSeconds(59));e.OnStatus(S(false,"W1AW"),At.AddSeconds(59));e.Tick(At.AddSeconds(60.1),true);
                    Assert(c.SequenceEqual(new[]{"CQ"}) && e.LoggedCount==0,"Failed caller blocked CQ");
                });
                Run("An exhausted call waits for decode and then continues the queue", () => {
                    List<string> commands;
                    AutoCqEngine engine = Active(out commands);
                    engine.RequestCallerReply = d => commands.Add("REPLY " + d.Message.Split(' ')[1]);
                    engine.Waiting.Add("W1AW");
                    engine.OnDecode(D(At.AddSeconds(15), "W2ABC", "FN20"), At.AddSeconds(29));
                    engine.OnLine(Tx(At, "W1AW", "-10"), At);
                    engine.OnLine(Tx(At.AddSeconds(30), "W1AW", "-12"), At.AddSeconds(30));
                    RadioStatus decoding = S(true, "W1AW");
                    decoding.Decoding = decoding.Transmitting = true;
                    engine.OnStatus(decoding, At.AddSeconds(60));
                    engine.OnLine(Tx(At.AddSeconds(60), "W1AW", "-09"), At.AddSeconds(60.3));
                    engine.Tick(At.AddSeconds(60.4), true);
                    Assert(engine.Armed && commands.Count == 0, "An unfinished decode caused a session stop");
                    decoding.Decoding = false;
                    engine.OnStatus(decoding, At.AddSeconds(61));
                    engine.Tick(At.AddSeconds(61), true);
                    Assert(engine.Armed && commands.SequenceEqual(new[] { "HALT" }), "Overrun did not request a TX stop");
                    engine.OnLine("Halt Tx triggered at TX: UDP request ", At.AddSeconds(61.1));
                    engine.OnStatus(S(false, "W1AW"), At.AddSeconds(61.1));
                    engine.Tick(At.AddSeconds(61.1), true);
                    Assert(engine.Armed && commands.SequenceEqual(new[] { "HALT", "REPLY W2ABC" })
                        && engine.Waiting.Rows[0].PauseReason == "Нет ответа", "The failed station blocked the next caller");
                });
                Run("A real report decoded at the TX boundary cancels an apparent call overrun", () => {
                    List<string> commands;
                    AutoCqEngine engine = Active(out commands);
                    engine.OnLine(Tx(At, "W1AW", "-10"), At);
                    engine.OnLine(Tx(At.AddSeconds(30), "W1AW", "-10"), At.AddSeconds(30));
                    RadioStatus busy = S(true, "W1AW");
                    busy.Decoding = busy.Transmitting = true;
                    engine.OnStatus(busy, At.AddSeconds(60));
                    engine.OnLine(Tx(At.AddSeconds(60), "W1AW", "-10"), At.AddSeconds(60.3));
                    engine.Tick(At.AddSeconds(60.3), true);
                    engine.OnDecode(D(At.AddSeconds(45), "W1AW", "R-09"), At.AddSeconds(60.7));
                    engine.OnStatus(S(true, "W1AW"), At.AddSeconds(60.9));
                    engine.Tick(At.AddSeconds(60.9), true);
                    Assert(engine.Armed && commands.Count == 0, "Late report was cut off at the retry limit");
                });
                Run("A late RR73 resumes an earlier report exchange after the active QSO", () => {
                    var engine = new AutoCqEngine { UseNativeSelection = true };
                    var replies = new List<string>();
                    engine.RequestCallerReply = decode => replies.Add(decode.Message);
                    engine.RequestCq = () => replies.Add("CQ");
                    engine.OnStatus(S(true, "DO1OAS"), At);
                    engine.Start(At);
                    engine.OnLine(Tx(At, "DO1OAS", "R-04"), At);
                    engine.OnLine(Tx(At.AddSeconds(30), "DO1OAS", "R-07"), At.AddSeconds(30));
                    engine.OnLine("Halt Tx triggered at RX: AutoCQ, counter triggered ", At.AddSeconds(59));
                    engine.OnStatus(S(false, ""), At.AddSeconds(59));
                    engine.Tick(At.AddSeconds(59), true);
                    engine.OnStatus(S(true, ""), At.AddSeconds(59.1));
                    engine.OnStatus(S(true, "PA2A"), At.AddSeconds(60));
                    engine.OnLine(Tx(At.AddSeconds(60), "PA2A", "RR73"), At.AddSeconds(60));
                    engine.OnLine("QSO logged: PA2A", At.AddSeconds(60));
                    engine.OnDecode(D(At.AddSeconds(75), "W4ABC", "RR73"), At.AddSeconds(90));
                    engine.OnDecode(D(At.AddSeconds(75), "DO1OAS", "RR73"), At.AddSeconds(90));
                    engine.OnDecode(D(At.AddSeconds(105), "DO1OAS", "-12"), At.AddSeconds(119));
                    engine.Tick(At.AddSeconds(90), true);
                    Assert(engine.Awaiting73 && engine.Waiting.Rows.Single().Call == "DO1OAS"
                        && replies.SequenceEqual(new[] { "CQ" }), "Late RR73 displaced the active QSO or admitted an unknown caller");
                    engine.OnLine(Rx(At.AddSeconds(105), "PA2A", "73"), At.AddSeconds(119));
                    engine.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Autolog ", At.AddSeconds(119.1));
                    engine.OnStatus(S(false, ""), At.AddSeconds(119.2));
                    engine.Tick(At.AddSeconds(120), true);
                    Assert(replies.SequenceEqual(new[] { "CQ", "K1ABC DO1OAS RR73" })
                        && engine.LoggedCount == 1, "Late RR73 was lost or logged before the response");
                    engine.OnStatus(S(true, "DO1OAS"), At.AddSeconds(120.1));
                    engine.OnLine("double click on call processed: DO1OAS; EnableTx On, AutoTx On", At.AddSeconds(120.1));
                    engine.OnLine(Tx(At.AddSeconds(120), "DO1OAS", "73"), At.AddSeconds(120.2));
                    engine.OnLine("QSO logged: DO1OAS", At.AddSeconds(120.3));
                    Assert(engine.Armed && engine.LoggedCount == 2 && engine.ConfirmedCount == 2
                        && engine.Waiting.Rows.Count == 0, "Resumed exchange did not finish through the ordinary QSO path");
                });
                Run("An expired session does not reactivate a no-answer row for a late RR73", () => {
                    List<string> commands;
                    AutoCqEngine engine = Active(out commands);
                    engine.Options.RunMinutes = 1;
                    engine.Start(At);
                    engine.Waiting.Add("DO1OAS");
                    engine.OnLine(Tx(At, "DO1OAS", "R-04"), At);
                    engine.Waiting.Pause("DO1OAS", "Нет ответа", At.AddSeconds(29));
                    engine.OnStatus(S(true, "W1AW"), At.AddSeconds(45));
                    engine.Tick(At.AddSeconds(60), true);
                    engine.OnDecode(D(At.AddSeconds(75), "DO1OAS", "RR73"), At.AddSeconds(89));
                    Assert(engine.Finishing && engine.Waiting.Rows.Single().PauseReason == "Нет ответа"
                        && engine.Waiting.Next(engine.Status, true) == null, "A failed row was reactivated while finishing");
                });
                Run("A real reply moves the QSO beyond the initial-call limit", () => {
                    List<string> c;AutoCqEngine e=Active(out c);e.OnLine(Tx(At,"W1AW","-10"),At);
                    e.OnLine(Rx(At.AddSeconds(15),"W1AW","-05"),At.AddSeconds(29));
                    e.OnLine(Tx(At.AddSeconds(30),"W1AW","R-05"),At.AddSeconds(30));e.OnLine(Tx(At.AddSeconds(60),"W1AW","R-05"),At.AddSeconds(60));
                    Assert(e.Armed && c.Count==0,"Progressing exchange was counted as unanswered calls");
                });
                Run("FT4 waits for two peer slots: thirty seconds", () => {
                    AutoCqEngine e=new AutoCqEngine();RadioStatus active=S(true,"W1AW");active.Mode="FT4";e.OnStatus(active,At);e.Start(At);int cq=0;e.RequestCq=()=>cq++;
                    e.OnLine(Tx(At,"W1AW","RR73"),At);e.OnLine("QSO logged: W1AW",At);e.OnLine("Halt Tx triggered at RX: SRR73, none received, end of QSO, Singleshot ",At.AddSeconds(14));
                    RadioStatus idle=S(false,"");idle.Mode="FT4";e.OnStatus(idle,At.AddSeconds(14));e.Tick(At.AddSeconds(29.9),true);Assert(cq==0,"FT4 second reply slot skipped");
                    e.Tick(At.AddSeconds(30.1),true);Assert(cq==1 && e.Missing73Count==1,"FT4 timeout failed");
                });
                Run("Peer RR73 followed by our 73 completes the reverse exchange", () => {
                    List<string> c;AutoCqEngine e=Active(out c);e.OnLine(Rx(At,"W1AW","RR73"),At);
                    e.OnLine(Tx(At.AddSeconds(15),"W1AW","73"),At.AddSeconds(15));e.OnLine("QSO logged: W1AW",At.AddSeconds(15));
                    e.OnLine("Halt Tx triggered at RX: S73, none received ",At.AddSeconds(29));e.OnStatus(S(false,""),At.AddSeconds(29));e.Tick(At.AddSeconds(30),true);
                    Assert(c.SequenceEqual(new[]{"CQ"}) && e.ConfirmedCount==1,"Reverse final acknowledgement blocked");
                });
                Run("G0ZZA regression: the 22:07:45 handoff must wait until 22:08:15", () => {
                    DateTime sent=new DateTime(2025,11,7,22,7,15,DateTimeKind.Utc);RadioStatus s=S(true,"G0ZZA");s.DeCall="K1ABC";
                    AutoCqEngine e=new AutoCqEngine();List<string> c=new List<string>();e.RequestCq=()=>c.Add("CQ");e.OnStatus(s,sent);e.Start(sent);
                    e.OnLine("20251107_220715.095(0) JTDX v2.2.160 Transmitting 7.074 MHz + 2218Hz FT8: G0ZZA K1ABC RR73",sent);
                    e.OnLine("20251107_220715.102(0) QSO logged: G0ZZA",sent);
                    e.OnLine("Halt Tx triggered at RX: SRR73, none received, end of QSO, Singleshot ",sent.AddSeconds(29));
                    RadioStatus idle=S(false,"");idle.DeCall="K1ABC";e.OnStatus(idle,sent.AddSeconds(29));e.Tick(sent.AddSeconds(30),true);Assert(c.Count==0,"Original premature handoff repeated");
                    e.Tick(sent.AddSeconds(59.9),true);Assert(c.Count==0,"Second reply slot cut short");e.Tick(sent.AddSeconds(60.1),true);Assert(c.SequenceEqual(new[]{"CQ"}),"No CQ after complete waiting window");
                });
                Run("Stale RR73 cannot validate a later local 73", () => {
                    List<string> c;AutoCqEngine e=Active(out c);e.OnLine(Tx(At,"W1AW","RR73"),At);
                    e.OnLine(Rx(At.AddSeconds(-30),"W1AW","RR73"),At.AddSeconds(29));e.OnLine(Tx(At.AddSeconds(30),"W1AW","73"),At.AddSeconds(30));
                    Assert(e.Awaiting73 && e.ConfirmedCount==0,"Stale terminal exchange was reused");
                });
                Run("A premature log record for another peer also fails closed", () => {
                    List<string> c;AutoCqEngine e=Active(out c);e.OnLine(Tx(At,"W1AW","RR73"),At);e.OnLine("QSO logged: W1AW",At);e.OnLine("QSO logged: W2ABC",At.AddSeconds(30));
                    Assert(!e.Armed && c.SequenceEqual(new[]{"HALT"}),"Unexpected contact was allowed");
                });
                Run("Restoration respects an operator's new UDP destination", () => {
                    string p=Fixture(),profile=Path.Combine(p,"profile"),state=Path.Combine(p,"state");Directory.CreateDirectory(profile);
                    string file=Path.Combine(profile,"JTDX.ini");File.WriteAllText(file,"[Configuration]\r\nUDPServer=127.0.0.1\r\nUDPServerPort=2237\r\n");
                    SettingsManager m=new SettingsManager(profile,state);m.Prepare();IniFile ini=new IniFile(file);
                    ini.Put("Configuration","UDPServerPort","2299");ini.Save(file);m.Restore();
                    Assert(new IniFile(file).Get("Configuration","UDPServerPort","")=="2299","Operator destination overwritten");
                });
                Run("F1ZZA regression: BG7BVI and TA2EE are answered before CQ", () => {
                    List<string> c;AutoCqEngine e=Active(out c);e.RequestReply=d=>c.Add("REPLY "+d.Message.Split(' ')[1]);
                    e.OnLine(Tx(At,"W1AW","RR73"),At);e.OnLine("QSO logged: W1AW",At);
                    e.OnDecode(D(At.AddSeconds(15),"BG7BVI","OL66"),At.AddSeconds(28));e.OnDecode(D(At.AddSeconds(15),"TA2EE","KM69"),At.AddSeconds(28));
                    e.OnLine(Rx(At.AddSeconds(15),"W1AW","73"),At.AddSeconds(29));e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Singleshot ",At.AddSeconds(29));e.OnStatus(S(false,""),At.AddSeconds(29));e.Tick(At.AddSeconds(30),true);
                    Assert(c.SequenceEqual(new[]{"REPLY BG7BVI"}) && e.Waiting.Rows.Count==2 && e.ReplyCommands==1 && e.Resumes==0,"CQ skipped a waiting caller");
                    e.OnStatus(S(true,"BG7BVI"),At.AddSeconds(30));e.OnLine(Tx(At.AddSeconds(30),"BG7BVI","-11"),At.AddSeconds(30));
                    e.OnLine(Tx(At.AddSeconds(60),"BG7BVI","-11"),At.AddSeconds(60));e.OnLine("Halt Tx triggered at RX: m_singleshot, counter triggered ",At.AddSeconds(89));e.OnStatus(S(false,""),At.AddSeconds(89));e.Tick(At.AddSeconds(90),true);
                    Assert(c.SequenceEqual(new[]{"REPLY BG7BVI","REPLY TA2EE"}) && e.Waiting.Rows[0].PauseReason=="Нет ответа","No-answer caller blocked the next station");
                    e.OnStatus(S(true,"TA2EE"),At.AddSeconds(90));e.OnLine(Tx(At.AddSeconds(120),"TA2EE","RR73"),At.AddSeconds(120));e.OnLine("QSO logged: TA2EE",At.AddSeconds(120));
                    e.OnLine(Rx(At.AddSeconds(135),"TA2EE","73"),At.AddSeconds(149));e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Singleshot ",At.AddSeconds(149));e.OnStatus(S(false,""),At.AddSeconds(149));e.Tick(At.AddSeconds(150),true);
                    Assert(c.Last()=="CQ" && e.Waiting.Rows.Count==1 && e.Waiting.Rows[0].Call=="BG7BVI","Completed station remained or paused station looped");
                });
                Run("Queue handoff waits for the configured number of reply periods", () => {
                    AutoCqEngine e=new AutoCqEngine();e.Options.Wait73Periods=3;e.OnStatus(S(true,"W1AW"),At);e.Start(At);List<string> c=new List<string>();e.RequestReply=d=>c.Add("REPLY");e.RequestCq=()=>c.Add("CQ");
                    e.OnDecode(D(At.AddSeconds(15),"W2ABC","FN20"),At.AddSeconds(29));e.OnLine(Tx(At,"W1AW","RR73"),At);e.OnLine("QSO logged: W1AW",At);
                    e.OnLine("Halt Tx triggered at RX: SRR73, none received, end of QSO, Singleshot ",At.AddSeconds(29));e.OnStatus(S(false,""),At.AddSeconds(29));
                    e.Tick(At.AddSeconds(60),true);e.Tick(At.AddSeconds(89.9),true);Assert(c.Count==0,"Configured third response period was skipped");e.Tick(At.AddSeconds(90.1),true);
                    Assert(c.SequenceEqual(new[]{"REPLY"}) && e.Missing73Count==1,"Did not select queue after the wait");
                });
                Run("One response period and twenty response periods are both supported", () => {
                    foreach(int periods in new[]{1,20})
                    {
                        AutoCqEngine e=new AutoCqEngine();e.Options.Wait73Periods=periods;e.OnStatus(S(true,"W1AW"),At);e.Start(At);int cq=0;e.RequestCq=()=>cq++;
                        e.OnLine(Tx(At,"W1AW","RR73"),At);e.OnLine("QSO logged: W1AW",At);e.OnLine("Halt Tx triggered at RX: SRR73, none received, end of QSO, Singleshot ",At.AddSeconds(29));e.OnStatus(S(false,""),At.AddSeconds(29));
                        e.Tick(At.AddSeconds(periods*30-0.1),true);Assert(cq==0,"Wait cut short");e.Tick(At.AddSeconds(periods*30+0.1),true);Assert(cq==1 && e.Armed,"Configured wait expired incorrectly");
                    }
                });
                Run("A first incoming caller interrupts the ongoing CQ without another CQ command", () => {
                    AutoCqEngine e=new AutoCqEngine();e.OnStatus(S(true,""),At);e.Start(At);int reply=0,cq=0;e.RequestReply=d=>reply++;e.RequestCq=()=>cq++;
                    e.OnDecode(D(At.AddSeconds(15),"W2ABC","FN20"),At.AddSeconds(29));e.Tick(At.AddSeconds(29),true);Assert(reply==1 && cq==0,"CQ took priority over a caller");
                    e.OnStatus(S(true,"W2ABC"),At.AddSeconds(29));e.Tick(At.AddSeconds(40),true);Assert(e.Armed && reply==1,"Reply was not acknowledged by its DX call");
                });
                Run("Queue filters foreign traffic and terminal messages, and preserves first-heard order", () => {
                    List<string> c;AutoCqEngine e=Active(out c);DecodedMessage d=D(At.AddSeconds(15),"W2ABC","FN20");e.OnDecode(d,At.AddSeconds(29));
                    d=D(At.AddSeconds(15),"W3ABC","FN31");e.OnDecode(d,At.AddSeconds(29));d=D(At.AddSeconds(45),"W2ABC","-03");d.Snr=-3;e.OnDecode(d,At.AddSeconds(59));
                    foreach(string payload in new[]{"73","RR73","RRR"})e.OnDecode(D(At.AddSeconds(45),"W4ABC",payload),At.AddSeconds(59));
                    d=D(At.AddSeconds(45),"W5ABC","FN20");d.Message="W9ZZZ W5ABC FN20";e.OnDecode(d,At.AddSeconds(59));
                    e.OnDecode(D(At.AddSeconds(45),"W1AW","FN20"),At.AddSeconds(59));
                    Assert(e.Waiting.Rows.Select(r=>r.Call).SequenceEqual(new[]{"W2ABC","W3ABC"}) && e.Waiting.Rows[0].Decode.Snr==-3,"Queue filtering/order failed");
                });
                Run("Queue editing supports add, rename, reorder, remove and explicit retry", () => {
                    CallerQueue q=new CallerQueue();q.Add("w2abc");q.Add("W3ABC");q.Rename("W2ABC","W4ABC");q.Move("W3ABC",-1);
                    Assert(q.Rows.Select(r=>r.Call).SequenceEqual(new[]{"W3ABC","W4ABC"}),"Editing order failed");q.Pause("W3ABC","Нет ответа",At);q.Retry("W3ABC");Assert(q.Rows[0].PauseReason==null,"Retry failed");q.Remove("W4ABC");
                    Assert(q.Rows.Count==1 && !q.Rows[0].Available,"Manual call fabricated a native decode");
                });
                Run("A logged station is removed and duplicate caller decodes do not re-add it", () => {
                    List<string> c;AutoCqEngine e=Active(out c);e.OnDecode(D(At.AddSeconds(15),"W2ABC","FN20"),At.AddSeconds(29));
                    e.OnLine("QSO logged: W2ABC",At.AddSeconds(30));e.OnDecode(D(At.AddSeconds(45),"W2ABC","FN20"),At.AddSeconds(59));
                    Assert(e.Waiting.Rows.Count==0,"Completed station re-entered the queue");e.Waiting.Add("W2ABC");Assert(e.Waiting.Rows.Count==1,"Operator could not explicitly re-add it");
                });
                Run("No-answer entry stays paused until a fresh subsequent call or operator retry", () => {
                    CallerQueue q=new CallerQueue();RadioStatus s=S(true,"");q.SetContext(s);q.Observe(D(At.AddSeconds(15),"W2ABC","FN20"),s,null,At.AddSeconds(29));q.Pause("W2ABC","Нет ответа",At.AddSeconds(59));
                    q.Observe(D(At.AddSeconds(45),"W2ABC","FN20"),s,null,At.AddSeconds(60));Assert(q.Next(s)==null,"Old decode reactivated a failed call");q.Observe(D(At.AddSeconds(75),"W2ABC","FN20"),s,null,At.AddSeconds(89));Assert(q.Next(s)!=null,"Fresh call did not reactivate the station");
                });
                Run("I4ZZA regression: Reply B4 allows a fresh addressed call after a logged QSO", () => {
                    foreach (bool answerWorked in new[] { false, true })
                    {
                        DateTime workedAt = new DateTime(2025, 11, 14, 9, 20, 0, DateTimeKind.Utc);
                        DateTime cqAt = workedAt.AddMinutes(6.5);
                        RadioStatus idle = S(true, ""); idle.DeCall = "K1ABC"; idle.Frequency = 21074000;
                        var commands = new List<string>();
                        var engine = new AutoCqEngine { UseNativeSelection = true };
                        engine.Options.PriorityDxcc = new[] { "KL" };
                        engine.RequestCq = () => commands.Add("CQ");
                        engine.RequestHalt = () => commands.Add("HALT");
                        engine.RequestCallerReply = d => commands.Add(d.Message);
                        engine.Waiting.AnswerWorkedB4 = answerWorked;
                        engine.OnStatus(idle, workedAt);
                        engine.OnLine("20251114_092000.137(0)  QSO logged: I4ZZA", workedAt);
                        engine.Start(cqAt);
                        engine.OnLine("20251114_092630.052(0)  Transmitting 21.074 MHz + 1809Hz FT8: CQ K1ABC KO85", cqAt);
                        var tx = S(true, ""); tx.DeCall = idle.DeCall; tx.Frequency = idle.Frequency; tx.Transmitting = true;
                        engine.OnStatus(tx, cqAt.AddSeconds(1));
                        engine.OnStatus(idle, cqAt.AddSeconds(14)); engine.Tick(cqAt.AddSeconds(14), true);
                        engine.OnLine("Halt Tx triggered at RX: UDP request ", cqAt.AddSeconds(14.1));
                        var receive = S(false, ""); receive.DeCall = idle.DeCall; receive.Frequency = idle.Frequency;
                        engine.OnStatus(receive, cqAt.AddSeconds(14.1));
                        DateTime ready = cqAt.AddSeconds(29.435);
                        var call = new DecodedMessage { IsNew = false, Milliseconds = 34005000, Snr = -6,
                            DeltaTime = 0.2, DeltaFrequency = 1913, Mode = "~", Message = "K1ABC I4ZZA JN63" };
                        engine.OnDecode(call, ready);
                        Assert(engine.Waiting.Rows.Count == 0, "Replay re-added the completed call");
                        var oldReport = call.Copy(); oldReport.IsNew = true; oldReport.Message = "K1ABC I4ZZA R-18";
                        engine.OnDecode(oldReport, ready);
                        Assert(engine.Waiting.Rows.Count == 0, "An acknowledged report reopened the previous QSO");
                        call.IsNew = true; engine.OnDecode(call, ready);
                        engine.OnLine(DecodeFinished(ready), ready); engine.Tick(ready, true);
                        Assert(commands.SequenceEqual(new[] { "HALT", answerWorked ? call.Message : "CQ" }) && engine.Armed,
                            "The next transmission did not respect Reply B4");
                    }
                });
                Run("Native clear and reconnect invalidate Reply data until the station is heard again", () => {
                    CallerQueue q=new CallerQueue();RadioStatus s=S(true,"");q.SetContext(s);q.Observe(D(At.AddSeconds(15),"W2ABC","FN20"),s,null,At.AddSeconds(29));q.Invalidate();Assert(q.Next(s)==null && q.Rows.Count==1,"Cleared decode was actionable");
                    DecodedMessage d=D(At.AddSeconds(15),"W2ABC","FN20");d.IsNew=false;q.Observe(d,s,null,At.AddSeconds(40));Assert(q.Next(s)!=null,"Known native replay was not recovered");
                    d=D(At.AddSeconds(15),"W3ABC","FN31");d.IsNew=false;q.Observe(d,s,null,At.AddSeconds(40));Assert(q.Rows.Count==1,"Historical replay added a new caller");
                });
                Run("Queue selection respects band, mode, own call and TX period", () => {
                    CallerQueue q=new CallerQueue();RadioStatus s=S(true,"");q.SetContext(s);q.Observe(D(At.AddSeconds(15),"W2ABC","FN20"),s,null,At.AddSeconds(29));
                    RadioStatus other=S(true,"");other.TxFirst=false;Assert(q.Next(other)==null,"Selected wrong TX period");other=S(true,"");other.Frequency=7074000;Assert(q.Next(other)==null,"Selected wrong band");other=S(true,"");other.DeCall="K2ABC";Assert(q.Next(other)==null,"Selected another operator's caller");
                });
                Run("Queue and operator periods persist, native availability does not", () => {
                    string p=Fixture();CallerQueue q=new CallerQueue();RadioStatus s=S(true,"");q.SetContext(s);q.Observe(D(At.AddSeconds(15),"W2ABC","FN20"),s,null,At.AddSeconds(29));q.Add("W3ABC");q.Pause("W3ABC","Нет ответа",At.AddSeconds(30));
                    OperatorStateStore store=new OperatorStateStore(p);store.Save(new AutoCqOptions{Wait73Periods=4,CallAttempts=3},q.Rows);OperatorState saved=store.Load();CallerQueue restored=new CallerQueue();restored.Load(saved.Stations);
                    Assert(saved.Options.Wait73Periods==4 && saved.Options.CallAttempts==3 && restored.Rows.Count==2 && restored.Rows[1].PauseReason=="Нет ответа" && !restored.Rows[0].Available,"State round-trip failed");
                });
                Run("User call counts prepare the native counters and restore every managed option", () => {
                    string p=Fixture(),profile=Path.Combine(p,"profile"),state=Path.Combine(p,"state");Directory.CreateDirectory(profile);string f=Path.Combine(profile,"JTDX.ini");
                    File.WriteAllText(f,"[Configuration]\r\nUDPServerPort=2237\r\nSeqAnswerInCallCounterValue=2\r\n[Common]\r\nCallMode=3\r\nQuickCall=false\r\nAcceptUDPReplyMessages=1\r\n");
                    SettingsManager m=new SettingsManager(profile,state);m.Prepare(new AutoCqOptions{CallAttempts=4,Wait73Periods=20});IniFile ini=new IniFile(f);
                    Assert(ini.Get("Configuration","SeqAnswerInCallCounterValue","")=="4" && ini.Get("Configuration","SeqSentRR7373CounterValue","")=="5" && ini.Get("Common","CallMode","")=="3" && ini.Get("Common","QuickCall","")=="true" && ini.Get("Common","AcceptUDPReplyMessages","")=="3","Prepared the wrong native contract");
                    m.Restore();ini=new IniFile(f);Assert(ini.Get("Common","CallMode","")=="3" && ini.Get("Common","QuickCall","")=="false" && ini.Get("Common","AcceptUDPReplyMessages","")=="1","Native options not restored");
                });
                Run("STOP also blocks queued replies and manual logging still removes the worked entry", () => {
                    List<string> c;AutoCqEngine e=Active(out c);e.RequestReply=d=>c.Add("REPLY");e.OnDecode(D(At.AddSeconds(15),"W2ABC","FN20"),At.AddSeconds(29));e.Stop("Stop",true);e.OnLine("QSO logged: W2ABC",At.AddSeconds(30));e.OnStatus(S(false,""),At.AddSeconds(30));e.Tick(At.AddMinutes(3),true);
                    Assert(c.SequenceEqual(new[]{"HALT"}) && e.Waiting.Rows.Count==0,"Queued reply restarted after STOP");
                });
                Run("Reply acknowledgement requires the requested DX, not only Enable TX", () => {
                    AutoCqEngine e=new AutoCqEngine();e.OnStatus(S(true,""),At);e.Start(At);e.OnDecode(D(At.AddSeconds(15),"W2ABC","FN20"),At.AddSeconds(29));int halt=0;e.RequestHalt=()=>halt++;e.Tick(At.AddSeconds(29),true);e.OnStatus(S(true,""),At.AddSeconds(30));e.Tick(At.AddSeconds(36),true);
                    Assert(!e.Armed && halt==1 && e.Waiting.Rows[0].PauseReason=="Ответ не принят JTDX","CQ Enable TX falsely acknowledged a Reply");
                });
                Run("Native Decode and Reply retain exact message fields and network double encoding", () => {
                    DecodedMessage d=D(At.AddSeconds(15),"BG7BVI","OL66");d.Message="K1ABC BG7BVI OL66       ";d.LowConfidence=true;d.DeltaTime=-0.0;
                    DecodedMessage parsed=QtPacket.Parse(DecodePacket(d)).Decode;byte[] reply=QtPacket.Reply("JTDX",2,parsed);
                    using(QtReader r=new QtReader(reply))
                    {Assert(r.U32()==QtPacket.Magic && r.U32()==2 && r.U32()==4 && r.Text()=="JTDX" && r.U32()==d.Milliseconds && unchecked((int)r.U32())==-11,"Reply header/time/SNR");Assert(BitConverter.DoubleToInt64Bits(r.Double())==BitConverter.DoubleToInt64Bits(d.DeltaTime) && r.U32()==1736 && r.Text()=="~" && r.Text()==d.Message && r.Bool() && r.Byte()==0,"Reply fields changed");}
                });
                Run("Actual local UDP Decode is answered by a Reply packet, without TriggerCQ", () => {
                    using(UdpClient radio=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))using(UdpBridge bridge=new UdpBridge(0,"JTDX"))
                    {
                        AutoCqEngine e=new AutoCqEngine();e.OnStatus(S(true,""),At);e.Start(At);bridge.Received=p=>{if(p.Decode!=null)e.OnDecode(p.Decode,At.AddSeconds(29));};e.RequestReply=d=>bridge.SendToClient(QtPacket.Reply("JTDX",2,d));
                        Send(radio,DecodePacket(D(At.AddSeconds(15),"W2ABC","FN20")),bridge.Port);Pump(bridge,()=>e.Waiting.Rows.Count==1);e.Tick(At.AddSeconds(29),true);Assert(radio.Available>0,"No Reply datagram");using(QtReader r=new QtReader(Receive(radio))){r.U32();r.U32();Assert(r.U32()==4,"Sent CQ instead of Reply");}
                    }
                });
                Run("Retransmitting lines are counted once per TX slot and can begin the 73 wait", () => {
                    List<string> c;AutoCqEngine e=Active(out c);e.OnLine(Tx(At,"W1AW","-10"),At);e.OnLine(Tx(At,"W1AW","-10").Replace("Transmitting","Retransmitting"),At.AddMilliseconds(200));
                    e.OnLine(Tx(At.AddSeconds(30),"W1AW","-10"),At.AddSeconds(30));Assert(e.Armed,"A retransmit inside one slot counted as another call");
                    e.OnLine(Tx(At.AddSeconds(60),"W1AW","RR73").Replace("Transmitting","Retransmitting"),At.AddSeconds(60));Assert(e.Awaiting73 && e.WaitingCall=="W1AW","Retransmitted RR73 was ignored");
                });
                Run("Configurable initial-call guard enforces the selected count", () => {
                    var engine = new AutoCqEngine();
                    int halts = 0;
                    engine.RequestHalt = () => halts++;
                    engine.Options.CallAttempts = 3;
                    engine.OnStatus(S(true, "W1AW"), At);
                    engine.Start(At);
                    for (int i = 0; i < 4; i++)
                    {
                        DateTime time = At.AddSeconds(i * 30);
                        engine.OnLine(Tx(time, "W1AW", "-10"), time);
                        engine.Tick(time, true);
                        Assert(halts == (i == 3 ? 1 : 0), "Stop did not match the configured attempt count");
                    }
                    Assert(engine.Armed, "Retry guard stopped the session instead of the exchange");
                });
                Run("Off-air, stale, future and replayed unknown callers never enter the live queue", () => {
                    CallerQueue q=new CallerQueue();RadioStatus s=S(true,"");q.SetContext(s);DecodedMessage d=D(At.AddSeconds(15),"W2ABC","FN20");d.OffAir=true;q.Observe(d,s,null,At.AddSeconds(29));d.OffAir=false;d.IsNew=false;q.Observe(d,s,null,At.AddSeconds(29));d.IsNew=true;q.Observe(d,s,null,At.AddMinutes(4));q.Observe(D(At.AddMinutes(3),"W3ABC","FN20"),s,null,At);Assert(q.Rows.Count==0,"Untrusted/history decode entered queue");
                });
                Run("Automated Reply and CQ commands cannot reset the chosen session deadline", () => {
                    AutoCqEngine e=new AutoCqEngine();e.Options.RunMinutes=1;e.OnStatus(S(true,"W1AW"),At);e.Start(At);int halt=0;e.RequestHalt=()=>halt++;Finish(e,At.AddSeconds(10));e.Tick(At.AddSeconds(12),true);e.OnStatus(S(true,""),At.AddSeconds(13));e.Tick(At.AddSeconds(60),true);
                    Assert(e.Finishing && e.SessionDeadline==At.AddMinutes(1),"Automated continuation reset the deadline");e.OnLine("Halt Tx triggered at RX: UDP request ",At.AddSeconds(60.1));e.OnStatus(S(false,""),At.AddSeconds(60.1));e.Tick(At.AddSeconds(61),true);Assert(!e.Armed && halt==1,"Idle CQ did not finish after its stop acknowledgement");
                });
                Run("A queued caller cannot override an unexplained native TX stop", () => {
                    AutoCqEngine e=new AutoCqEngine();e.OnStatus(S(true,""),At);e.Start(At);int reply=0;e.RequestReply=d=>reply++;
                    e.OnDecode(D(At.AddSeconds(15),"W2ABC","FN20"),At.AddSeconds(29));RadioStatus busy=S(true,"");busy.Decoding=true;e.OnStatus(busy,At.AddSeconds(29));e.Tick(At.AddSeconds(29),true);
                    e.OnStatus(S(false,""),At.AddSeconds(29.5));e.Tick(At.AddSeconds(30),true);e.Tick(At.AddSeconds(31),true);Assert(!e.Armed && reply==0,"Queue restarted after an unknown native stop");
                });
                Run("Status-before-log ordering still accepts a recognised single-shot end", () => {
                    List<string> c;AutoCqEngine e=Active(out c);e.OnLine(Tx(At,"W1AW","RR73"),At);e.OnLine("QSO logged: W1AW",At);e.OnLine(Rx(At.AddSeconds(15),"W1AW","73"),At.AddSeconds(29));
                    e.OnStatus(S(false,""),At.AddSeconds(29));e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Singleshot ",At.AddSeconds(29.1));e.Tick(At.AddSeconds(30.2),true);Assert(c.SequenceEqual(new[]{"CQ"}) && e.Armed,"Status/log ordering blocked completion");
                });
                Run("Expired runtime keeps an unfinished QSO alive until its final confirmation", () => {
                    AutoCqEngine e=new AutoCqEngine();e.Options.RunMinutes=1;e.OnStatus(S(true,"W1AW"),At);e.Start(At);int halt=0;e.RequestHalt=()=>halt++;e.OnLine(Tx(At.AddSeconds(45),"W1AW","RR73"),At.AddSeconds(45));e.Tick(At.AddSeconds(59.9),true);Assert(e.Armed,"Timer stopped early");e.Tick(At.AddSeconds(60),true);Assert(e.Armed && e.Finishing && halt==0,"Timer cut an unfinished QSO");Finish(e,At.AddSeconds(61));e.Tick(At.AddSeconds(63),true);Assert(!e.Armed && halt==0,"Confirmed QSO did not finish the expired session");
                });
                Run("CQ threshold listens through the LAST reply slot before hunting", () => {
                    List<string> c;AutoCqEngine e=SearchActive(out c);e.OnDecode(CqD(At.AddSeconds(15),"W2ABC"),At.AddSeconds(29));CqListen(e);
                    Assert(c.SequenceEqual(new[]{"HALT"}) && e.Armed,"Expected CQ halt disarmed the engine");e.Tick(At.AddSeconds(29.9),true);Assert(c.Count==1,"Hunt cut short the last CQ response slot");e.Tick(At.AddSeconds(30),true);Assert(c.SequenceEqual(new[]{"HALT","REPLY W2ABC"}) && e.Searching,"Did not hunt after the full response slot");
                });
                Run("A failed hunt returns to own CQ before the next different station", () => {
                    List<string> c;AutoCqEngine e=SearchActive(out c);e.OnDecode(CqD(At.AddSeconds(15),"W2ABC"),At.AddSeconds(29));e.OnDecode(CqD(At.AddSeconds(15),"W3ABC"),At.AddSeconds(29));CqListen(e);e.Tick(At.AddSeconds(30),true);
                    e.OnStatus(S(true,"W2ABC"),At.AddSeconds(30));e.OnLine(Tx(At.AddSeconds(30),"W2ABC","FN20"),At.AddSeconds(30));e.OnLine(Tx(At.AddSeconds(60),"W2ABC","FN20"),At.AddSeconds(60));
                    e.OnLine("Halt Tx triggered at RX: m_singleshot, counter triggered ",At.AddSeconds(89));e.OnStatus(S(false,""),At.AddSeconds(89));e.Tick(At.AddSeconds(90),true);
                    Assert(c.SequenceEqual(new[]{"HALT","REPLY W2ABC","CQ"}),"Search skipped our own CQ after two attempts");
                    e.OnStatus(S(true,""),At.AddSeconds(90));
                    e.OnLine(Tx(At.AddSeconds(90),"CQ","FN20"),At.AddSeconds(90));
                    RadioStatus cqTx=S(true,"");cqTx.Transmitting=true;e.OnStatus(cqTx,At.AddSeconds(91));
                    e.OnStatus(S(true,""),At.AddSeconds(104));e.Tick(At.AddSeconds(104),true);
                    e.OnStatus(S(false,""),At.AddSeconds(104.1));e.OnLine("Halt Tx triggered at RX: UDP request ",At.AddSeconds(104.1));
                    e.OnDecode(CqD(At.AddSeconds(105),"W2ABC"),At.AddSeconds(119));
                    e.OnDecode(CqD(At.AddSeconds(105),"W3ABC"),At.AddSeconds(119));e.Tick(At.AddSeconds(120),true);
                    Assert(c.SequenceEqual(new[]{"HALT","REPLY W2ABC","CQ","HALT","REPLY W3ABC"}),"The second search repeated the failed station");
                    e.OnStatus(S(true,"W3ABC"),At.AddSeconds(120));e.OnLine(Tx(At.AddSeconds(150),"W3ABC","RR73"),At.AddSeconds(150));e.OnLine("QSO logged: W3ABC",At.AddSeconds(150));e.OnLine(Rx(At.AddSeconds(165),"W3ABC","73"),At.AddSeconds(179));e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Singleshot ",At.AddSeconds(179));e.OnStatus(S(false,""),At.AddSeconds(179));e.Tick(At.AddSeconds(180),true);
                    Assert(c.Last()=="CQ" && !e.Searching && e.Cqs.Find("W3ABC").Worked,"Successful hunt did not restore own CQ");
                });
                Run("An incoming caller on the last CQ reply window has priority over hunting", () => {
                    List<string> c;AutoCqEngine e=SearchActive(out c);e.OnDecode(CqD(At.AddSeconds(15),"W3ABC"),At.AddSeconds(29));CqListen(e);e.OnDecode(D(At.AddSeconds(15),"W2ABC","FN20"),At.AddSeconds(29));e.Tick(At.AddSeconds(30),true);
                    Assert(c.SequenceEqual(new[]{"HALT","REPLY W2ABC"}) && !e.Searching,"Hunting took priority over a caller");
                });
                Run("Changing to the selected station period is acknowledged and CQ keeps that same period", () => {
                    List<string> c;AutoCqEngine e=SearchActive(out c);e.OnDecode(CqD(At,"W2ABC"),At.AddSeconds(14));CqListen(e);e.Tick(At.AddSeconds(30),true);
                    RadioStatus before=S(true,"W2ABC");e.OnStatus(before,At.AddSeconds(30));Assert(e.Armed,"Old status during period transition caused a stop");RadioStatus other=S(true,"W2ABC");other.TxFirst=false;e.OnStatus(other,At.AddSeconds(30.2));Assert(e.Armed,"Controlled period change was rejected");
                    e.OnLine(Tx(At.AddSeconds(45),"W2ABC","RR73"),At.AddSeconds(45));e.OnLine("QSO logged: W2ABC",At.AddSeconds(45));e.OnLine(Rx(At.AddSeconds(60),"W2ABC","73"),At.AddSeconds(74));e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Singleshot ",At.AddSeconds(74));RadioStatus idle=S(false,"");idle.TxFirst=false;e.OnStatus(idle,At.AddSeconds(74));e.Tick(At.AddSeconds(75),true);
                    Assert(c.Last()=="CQ" && !e.CqTxFirst,"CQ switched away from the completed QSO period");RadioStatus resumed=S(true,"");resumed.TxFirst=false;e.OnStatus(resumed,At.AddSeconds(75.2));Assert(e.Armed,"Same-period CQ acknowledgement caused a stop");
                });
                Run("Manual CQ selection waits for the current QSO and incoming queue", () => {
                    List<string> c;AutoCqEngine e=Active(out c);e.RequestReply=d=>c.Add("REPLY "+d.Message.Split(' ')[1]);e.OnDecode(CqD(At.AddSeconds(15),"W3ABC"),At.AddSeconds(29));e.OnDecode(D(At.AddSeconds(15),"W2ABC","FN20"),At.AddSeconds(29));e.SelectCq("W3ABC",At.AddSeconds(29));
                    Finish(e,At.AddSeconds(29));e.Tick(At.AddSeconds(31),true);Assert(c.Last()=="REPLY W2ABC","Manual hunt displaced an incoming caller");
                    e.OnStatus(S(true,"W2ABC"),At.AddSeconds(31));e.OnLine(Tx(At.AddSeconds(45),"W2ABC","RR73"),At.AddSeconds(45));e.OnLine("QSO logged: W2ABC",At.AddSeconds(45));e.OnLine(Rx(At.AddSeconds(60),"W2ABC","73"),At.AddSeconds(74));e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Singleshot ",At.AddSeconds(74));e.OnStatus(S(false,""),At.AddSeconds(74));e.Tick(At.AddSeconds(75),true);Assert(c.Last()=="REPLY W3ABC","Manual selected CQ was discarded");
                });
                Run("Manually added callsigns come after addressed callers and before own CQ", () => {
                    List<string> commands;
                    AutoCqEngine engine = Active(out commands);
                    engine.UseNativeSelection = true;
                    engine.Options.SearchEnabled = false;
                    engine.RequestCallerReply = decode => commands.Add("CALLER " + decode.Message);
                    engine.RequestDirectCall = call => commands.Add("DIRECT " + call);
                    engine.AddQueuedCall("w3abc", At.AddSeconds(18));
                    engine.AddQueuedCall("W4ABC", At.AddSeconds(19));
                    engine.OnDecode(D(At.AddSeconds(15), "W2ABC", "FN20"), At.AddSeconds(29));
                    engine.Tick(At.AddSeconds(29.1), true);
                    Assert(commands.Count == 0, "Adding a target interrupted the ongoing QSO");
                    Finish(engine, At.AddSeconds(29.2));
                    engine.Tick(At.AddSeconds(31), true);
                    Assert(commands.SequenceEqual(new[] { "CALLER K1ABC W2ABC FN20" }), "Manual target displaced an addressed caller");

                    engine.OnStatus(S(true, "W2ABC"), At.AddSeconds(31.1));
                    engine.OnLine(Tx(At.AddSeconds(60), "W2ABC", "RR73"), At.AddSeconds(60));
                    engine.OnLine("QSO logged: W2ABC", At.AddSeconds(60));
                    engine.OnLine(Rx(At.AddSeconds(75), "W2ABC", "73"), At.AddSeconds(89.1));
                    engine.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Autolog ", At.AddSeconds(89.2));
                    engine.OnStatus(S(false, ""), At.AddSeconds(89.2));
                    engine.Tick(At.AddSeconds(89.3), true);
                    Assert(commands.SequenceEqual(new[] { "CALLER K1ABC W2ABC FN20", "DIRECT W3ABC" }),
                        "The manual queue did not call a station without any decode");
                    engine.OnStatus(S(true, "W3ABC"), At.AddSeconds(89.4));
                    engine.OnLine("QSO logged: W3ABC", At.AddSeconds(90));
                    Assert(engine.Waiting.Find("W3ABC") == null && engine.Waiting.Find("W4ABC") != null,
                        "The completed call was not removed from the manual list");
                });
                Run("An addressed pair of callsigns in FT8 and FT4 takes priority over the manual queue", () => {
                    const string caller = "PA40XYZAB";
                    foreach (string mode in new[] { "FT8", "FT4" })
                    {
                        var engine = new AutoCqEngine { UseNativeSelection = true };
                        var commands = new List<string>();
                        engine.RequestDirectCall = call => commands.Add("DIRECT " + call);
                        engine.RequestCallerReply = decode => commands.Add("CALLER " + decode.Message);
                        engine.RequestCq = () => commands.Add("CQ");
                        var active = S(true, "W1AW");
                        active.Mode = mode;
                        engine.OnStatus(active, At);
                        engine.Start(At);
                        engine.AddQueuedCall("W3ABC", At);
                        engine.OnLine(Tx(At, "W1AW", "RR73").Replace("FT8:", mode + ":"), At);
                        engine.OnLine("QSO logged: W1AW", At);

                        double slot = mode == "FT4" ? 7.5 : 15;
                        DateTime received = At.AddSeconds(2 * slot - 0.5);
                        var decodeMessage = D(At.AddSeconds(slot), caller, "");
                        decodeMessage.Mode = mode == "FT4" ? ":" : "~";
                        foreach (string unrelated in new[] { "<...> " + caller, "W2ABC " + caller, "K1ABC FN20", "K1ABC 73", "K1ABC RR73" })
                        {
                            decodeMessage.Message = unrelated;
                            engine.OnDecode(QtPacket.Parse(DecodePacket(decodeMessage)).Decode, received);
                        }
                        Assert(engine.Waiting.Rows.Single().Call == "W3ABC", "An unrelated or ambiguous message became a caller");

                        string addressed = (mode == "FT4" ? "<K1ABC> " : "K1ABC ") + caller;
                        decodeMessage.Message = addressed;
                        engine.OnDecode(QtPacket.Parse(DecodePacket(decodeMessage)).Decode, received);
                        engine.Tick(received, true);
                        Assert(commands.Count == 0, "An incoming call interrupted the current exchange");

                        decodeMessage.Message = "K1ABC W1AW 73";
                        engine.OnDecode(QtPacket.Parse(DecodePacket(decodeMessage)).Decode, received);
                        engine.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Autolog ", received.AddMilliseconds(100));
                        RadioStatus idle = S(false, "");
                        idle.Mode = mode;
                        engine.OnStatus(idle, received.AddMilliseconds(200));
                        engine.Tick(received.AddMilliseconds(400), true);
                        Assert(commands.SequenceEqual(new[] { "CALLER " + addressed })
                            && engine.Waiting.Find("W3ABC") != null, "The addressed call was passed over after final 73");
                    }
                });
                Run("A transmission with two callsigns counts towards the configured call limit", () => {
                    const string caller = "PA40XYZAB";
                    var engine = new AutoCqEngine();
                    engine.Options.CallAttempts = 1;
                    RadioStatus active = S(true, caller);
                    engine.OnStatus(active, At);
                    engine.Start(At);
                    engine.OnLine(Tx(At, caller, ""), At);
                    int halts = 0;
                    engine.RequestHalt = () => halts++;
                    var finished = At.AddSeconds(29);
                    engine.Tick(finished, true);
                    Assert(halts == 0, "The reply slot was cut short");
                    engine.OnLine(DecodeFinished(finished), finished);
                    engine.Tick(finished, true);
                    Assert(halts == 1, "The two-callsign transmission did not count as an attempt");
                });
                Run("An unheard manual target added during own CQ is called after the reply slot", () => {
                    List<string> commands;
                    AutoCqEngine engine = SearchActive(out commands);
                    engine.UseNativeSelection = true;
                    engine.Options.SearchEnabled = false;
                    engine.RequestDirectCall = call => commands.Add("DIRECT " + call);
                    RadioStatus transmitting = S(true, "");
                    transmitting.Transmitting = true;
                    engine.OnLine(Tx(At, "CQ", "FN20"), At);
                    engine.OnStatus(transmitting, At.AddSeconds(1));
                    engine.AddQueuedCall("W3ABC", At.AddSeconds(2));
                    engine.OnStatus(S(true, ""), At.AddSeconds(14));
                    engine.Tick(At.AddSeconds(14), true);
                    Assert(commands.Count == 0, "A queued callsign stopped an active CQ");
                    engine.OnLine(DecodeFinished(At.AddSeconds(29.5)), At.AddSeconds(29.5));
                    engine.Tick(At.AddSeconds(29.6), true);
                    Assert(engine.Armed && commands.SequenceEqual(new[] { "DIRECT W3ABC" }),
                        "CQ was repeated despite the manually added station");
                });
                Run("Manual Halt always overrides an internal CQ-listening stop", () => {
                    List<string> c;AutoCqEngine e=SearchActive(out c);e.OnDecode(CqD(At.AddSeconds(15),"W2ABC"),At.AddSeconds(29));CqListen(e);e.OnLine("Halt Tx triggered at RX: Halt Tx button clicked ",At.AddSeconds(29));e.Tick(At.AddSeconds(30),true);Assert(!e.Armed && c.SequenceEqual(new[]{"HALT"}),"Native manual Halt was treated as automatic CQ halt");
                });
                Run("CQ search can be disabled without changing normal incoming-call operation", () => {
                    List<string> c;AutoCqEngine e=SearchActive(out c);e.Options.SearchEnabled=false;e.OnDecode(CqD(At.AddSeconds(15),"W3ABC"),At.AddSeconds(29));e.OnLine(Tx(At,"CQ","FN20"),At);RadioStatus tx=S(true,"");tx.Transmitting=true;e.OnStatus(tx,At.AddSeconds(1));e.OnStatus(S(true,""),At.AddSeconds(14));e.Tick(At.AddSeconds(14),true);Assert(c.Count==0,"Disabled hunting halted CQ");e.OnDecode(D(At.AddSeconds(15),"W2ABC","FN20"),At.AddSeconds(29));e.Tick(At.AddSeconds(29),true);Assert(c.Last()=="REPLY W2ABC","Incoming caller was not answered");
                });
                Run("Only fresh real CQ candidates are eligible, failed and worked stations are skipped", () => {
                    HeardCqList q=new HeardCqList();RadioStatus s=S(true,"");q.SetContext(s);DecodedMessage d=CqD(At.AddSeconds(15),"W2ABC");d.Message="CQ DX W2ABC FN20";q.Observe(d,s,At.AddSeconds(29));q.Observe(CqD(At.AddSeconds(15),"W3ABC"),s,At.AddSeconds(29));q.Attempt("W2ABC",At.AddSeconds(30));Assert(q.Next(At.AddSeconds(30)).Call=="W3ABC","Failed/tried station was selected again");q.Complete("W3ABC");Assert(q.Next(At.AddSeconds(31))==null,"Worked station selected");q.Retry("W2ABC");Assert(q.Next(At.AddSeconds(31)).Call=="W2ABC","Manual retry failed");Assert(q.Next(At.AddMinutes(3))==null,"Stale CQ remained actionable");q.Invalidate();Assert(q.Next(At.AddSeconds(31))==null,"Cleared native data was used");
                });
                Run("Strict native CQ counters cover high-priority outgoing stations and restore correctly", () => {
                    string p=Fixture(),profile=Path.Combine(p,"profile"),state=Path.Combine(p,"state");Directory.CreateDirectory(profile);string f=Path.Combine(profile,"JTDX.ini");File.WriteAllText(f,"[Configuration]\r\nUDPServerPort=2237\r\nStrictDirectionalCQ=false\r\nSeqAnswerCQCounterValue=5\r\n");SettingsManager m=new SettingsManager(profile,state);m.Prepare(new AutoCqOptions{CallAttempts=2});IniFile i=new IniFile(f);Assert(i.Get("Configuration","StrictDirectionalCQ","")=="true" && i.Get("Configuration","SeqAnswerCQCounterValue","")=="2","High-priority CQ counter exception remains enabled");m.Restore();i=new IniFile(f);Assert(i.Get("Configuration","StrictDirectionalCQ","")=="false" && i.Get("Configuration","SeqAnswerCQCounterValue","")=="5","CQ options were not restored");
                });
                Run("Timer and search preferences survive reopening the module", () => {
                    OperatorStateStore store=new OperatorStateStore(Fixture());store.Save(new AutoCqOptions{RunMinutes=15,CqAttemptsBeforeSearch=4,SearchEnabled=false},new WaitingStation[0]);AutoCqOptions o=store.Load().Options;Assert(o.RunMinutes==15 && o.CqAttemptsBeforeSearch==4 && !o.SearchEnabled,"New operator preferences were lost");
                });
                Run("A native left-list selection from idle CQ may change period without interrupting the chosen QSO", () => {
                    AutoCqEngine e=new AutoCqEngine();e.OnStatus(S(true,""),At);e.Start(At);RadioStatus selection=S(true,"W2ABC");selection.TxFirst=false;e.OnStatus(selection,At.AddSeconds(1));Assert(e.Armed,"Explicit idle selection was rejected");
                });
                Run("Native TX status arriving before its CQ log still stops at the selected CQ count", () => {
                    List<string> c;AutoCqEngine e=SearchActive(out c);RadioStatus tx=S(true,"");tx.Transmitting=true;e.OnStatus(tx,At.AddMilliseconds(100));e.OnLine(Tx(At,"CQ","FN20"),At.AddMilliseconds(150));e.OnStatus(S(true,""),At.AddSeconds(14));e.Tick(At.AddSeconds(14),true);Assert(c.SequenceEqual(new[]{"HALT"}) && e.Armed,"Status-before-log lost CQ TX completion");
                });
                Run("Three own CQs count actual slots, not duplicate retransmit lines", () => {
                    List<string> c;AutoCqEngine e=SearchActive(out c);e.Options.CqAttemptsBeforeSearch=3;
                    for(int i=0;i<3;i++)
                    {
                        DateTime t=At.AddSeconds(i*30);e.OnLine(Tx(t,"CQ","FN20"),t);e.OnLine(Tx(t,"CQ","FN20").Replace("Transmitting","Retransmitting"),t.AddMilliseconds(200));RadioStatus tx=S(true,"");tx.Transmitting=true;e.OnStatus(tx,t.AddSeconds(1));e.OnStatus(S(true,""),t.AddSeconds(14));e.Tick(t.AddSeconds(14),true);Assert(e.UnansweredCqs==i+1,"CQ duplicate counted as a new attempt");if(i<2)Assert(c.Count==0,"CQ stopped before configured count");
                    }
                    Assert(c.SequenceEqual(new[]{"HALT"}) && e.Armed,"Did not stop exactly after the third full CQ");
                });
                Run("Stations busy with another QSO are skipped until they send a newer CQ", () => {
                    HeardCqList q=new HeardCqList();RadioStatus s=S(true,"");q.SetContext(s);q.Observe(CqD(At.AddSeconds(15),"W2ABC"),s,At.AddSeconds(29));DecodedMessage busy=D(At.AddSeconds(45),"W2ABC","R-10");busy.Message="W9XYZ W2ABC R-10";q.Observe(busy,s,At.AddSeconds(59));Assert(q.Next(At.AddSeconds(59))==null && q.Find("W2ABC").Busy,"Selected a busy station");DecodedMessage old=CqD(At.AddSeconds(15),"W2ABC");old.IsNew=false;q.Observe(old,s,At.AddSeconds(60));Assert(q.Find("W2ABC").Busy,"Old replay cleared busy state");q.Observe(CqD(At.AddSeconds(75),"W2ABC"),s,At.AddSeconds(89));Assert(q.Next(At.AddSeconds(89))!=null,"Fresh CQ did not clear busy state");
                });
                Run("A fresh native CQ replay can populate the mirrored left-list without adding old callers", () => {
                    HeardCqList q=new HeardCqList();RadioStatus s=S(true,"");q.SetContext(s);DecodedMessage d=CqD(At.AddSeconds(15),"W2ABC");d.IsNew=false;q.Observe(d,s,At.AddSeconds(29));Assert(q.Rows.Count==1 && q.Next(At.AddSeconds(29))!=null,"Fresh CQ replay was lost");d=CqD(At.AddMinutes(-5),"W3ABC");d.IsNew=false;q.Observe(d,s,At.AddSeconds(29));Assert(q.Rows.Count==1,"Stale CQ replay was added");
                });
                Run("FT4 CQ search preserves the half-second boundary of its final reply slot", () => {
                    AutoCqEngine e=new AutoCqEngine();e.Options.CqAttemptsBeforeSearch=1;RadioStatus s=S(true,"");s.Mode="FT4";s.TxFirst=false;e.OnStatus(s,At);e.Start(At);List<string> c=new List<string>();e.RequestHalt=()=>c.Add("HALT");e.RequestReply=d=>c.Add("REPLY");
                    e.OnLine("20251107_120007.500(0) Transmitting 14.080 MHz + 1500Hz FT4: CQ K1ABC FN20",At.AddSeconds(7.5));RadioStatus tx=S(true,"");tx.Mode="FT4";tx.TxFirst=false;tx.Transmitting=true;e.OnStatus(tx,At.AddSeconds(7.6));e.OnStatus(s,At.AddSeconds(13.5));e.Tick(At.AddSeconds(13.5),true);e.OnLine("Halt Tx triggered at RX: UDP request ",At.AddSeconds(13.6));RadioStatus idle=S(false,"");idle.Mode="FT4";idle.TxFirst=false;e.OnStatus(idle,At.AddSeconds(13.6));DecodedMessage decoded=CqD(At.AddSeconds(15),"W2ABC");decoded.Mode=":";e.OnDecode(decoded,At.AddSeconds(21));e.Tick(At.AddSeconds(22.49),true);Assert(c.SequenceEqual(new[]{"HALT"}),"FT4 reply slot was cut short");e.Tick(At.AddSeconds(22.5),true);Assert(c.Last()=="REPLY","FT4 hunt did not start at the correct boundary");
                });
                Run("Manual CQ choice works after long CQ operation with automatic hunting disabled", () => {
                    List<string> c;AutoCqEngine e=SearchActive(out c);e.Options.SearchEnabled=false;
                    for(int i=0;i<3;i++){DateTime t=At.AddSeconds(i*30);e.OnLine(Tx(t,"CQ","FN20"),t);RadioStatus tx=S(true,"");tx.Transmitting=true;e.OnStatus(tx,t.AddSeconds(1));e.OnStatus(S(true,""),t.AddSeconds(14));e.Tick(t.AddSeconds(14),true);}
                    e.OnDecode(CqD(At.AddSeconds(75),"W2ABC"),At.AddSeconds(89));e.SelectCq("W2ABC",At.AddSeconds(89));e.OnLine(Tx(At.AddSeconds(90),"CQ","FN20"),At.AddSeconds(90));Assert(e.Armed,"Manual selection accidentally enforced the disabled hunt threshold");RadioStatus next=S(true,"");next.Transmitting=true;e.OnStatus(next,At.AddSeconds(91));e.OnStatus(S(true,""),At.AddSeconds(104));e.Tick(At.AddSeconds(104),true);e.OnLine("Halt Tx triggered at RX: UDP request ",At.AddSeconds(104.1));e.OnStatus(S(false,""),At.AddSeconds(104.1));e.Tick(At.AddSeconds(120),true);Assert(c.Last()=="REPLY W2ABC","Manual selection did not proceed when auto-hunt was off");
                });
                Run("Expired timer drains available callers, ignores No-answer rows and sends no new CQ", () => {
                    AutoCqEngine e=new AutoCqEngine();e.Options.RunMinutes=1;e.OnStatus(S(true,"W1AW"),At);e.Start(At);List<string> c=new List<string>();e.RequestReply=d=>c.Add(d.Message.Split(' ')[1]);e.RequestCq=()=>c.Add("CQ");e.RequestHalt=()=>c.Add("HALT");
                    e.Waiting.Add("W2ABC");e.Waiting.Pause("W2ABC","Нет ответа",At);e.OnDecode(D(At.AddSeconds(45),"W3ABC","FN20"),At.AddSeconds(59));e.OnLine(Tx(At.AddSeconds(45),"W1AW","RR73"),At.AddSeconds(45));e.OnLine("QSO logged: W1AW",At.AddSeconds(45));e.Tick(At.AddSeconds(60),true);Assert(c.Count==0 && e.Finishing,"Timer interrupted the current exchange");
                    e.OnLine(Rx(At.AddSeconds(60),"W1AW","73"),At.AddSeconds(74));e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Singleshot ",At.AddSeconds(74));e.OnStatus(S(false,""),At.AddSeconds(74));e.Tick(At.AddSeconds(75),true);Assert(c.SequenceEqual(new[]{"W3ABC"}),"Failed row blocked draining or a new CQ was sent");
                    e.OnStatus(S(true,"W3ABC"),At.AddSeconds(75));e.OnLine(Tx(At.AddSeconds(105),"W3ABC","RR73"),At.AddSeconds(105));e.OnLine("QSO logged: W3ABC",At.AddSeconds(105));e.OnLine(Rx(At.AddSeconds(120),"W3ABC","73"),At.AddSeconds(134));e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Singleshot ",At.AddSeconds(134));e.OnStatus(S(false,""),At.AddSeconds(134));e.Tick(At.AddSeconds(135),true);Assert(!e.Armed && c.Count==1 && e.Waiting.Rows.Count==1,"Did not end after the last active caller");
                });
                Run("Expired timer completes the current CQ frame and its last response opportunity", () => {
                    List<string> c;AutoCqEngine e=SearchActive(out c);e.Options.RunMinutes=1;e.Stop("reset",false);e.Start(At);e.Options.SearchEnabled=false;e.OnLine(Tx(At.AddSeconds(45),"CQ","FN20"),At.AddSeconds(45));RadioStatus tx=S(true,"");tx.Transmitting=true;e.OnStatus(tx,At.AddSeconds(46));e.Tick(At.AddSeconds(60),true);Assert(c.Count==0 && e.Finishing,"Timer cut the active CQ frame");e.OnStatus(S(true,""),At.AddSeconds(61));e.Tick(At.AddSeconds(61),true);Assert(c.SequenceEqual(new[]{"HALT"}),"CQ was not stopped between frames");e.OnLine("Halt Tx triggered at RX: UDP request ",At.AddSeconds(61.1));e.OnStatus(S(false,""),At.AddSeconds(61.1));e.Tick(At.AddSeconds(74.9),true);Assert(e.Armed,"Last CQ response slot was cut short");e.Tick(At.AddSeconds(75),true);Assert(!e.Armed && c.SequenceEqual(new[]{"HALT"}),"Expired timer launched another CQ or hunt");
                });
                Run("No-answer marks remain excluded while the expired session is draining", () => {
                    CallerQueue q=new CallerQueue();RadioStatus s=S(true,"");q.SetContext(s);q.Observe(D(At.AddSeconds(15),"W2ABC","FN20"),s,null,At.AddSeconds(29));q.Pause("W2ABC","Нет ответа",At.AddSeconds(30));q.Observe(D(At.AddSeconds(45),"W2ABC","FN20"),s,null,At.AddSeconds(59),false);Assert(q.Next(s,true)==null,"Fresh repeated calling reactivated a No-answer row during shutdown");
                });
                Run("A missing native decode does not discard other waiting callers", () => {
                    AutoCqEngine e=new AutoCqEngine();e.OnStatus(S(false,""),At);e.Start(At);List<string> c=new List<string>();e.RequestReply=d=>c.Add(d.Message.Split(' ')[1]);e.OnDecode(D(At.AddSeconds(15),"SV9AUP","KM25"),At.AddSeconds(29));e.OnDecode(D(At.AddSeconds(15),"GM6URC","-14"),At.AddSeconds(29));e.Tick(At.AddSeconds(30),true);
                    e.OnLine("UDP Reply request ignored, decode not found: 120015 -18 0.2 1004 ~ K1ABC SV9AUP KM25",At.AddSeconds(30.1));e.Tick(At.AddSeconds(31.1),true);Assert(e.Armed && c.SequenceEqual(new[]{"SV9AUP","GM6URC"}) && !e.Waiting.Rows[0].Available,"One stale message stopped or lost the whole queue");
                });
                Run("Deleting a waiting row does not permanently suppress a later fresh call", () => {
                    CallerQueue q=new CallerQueue();RadioStatus s=S(true,"");q.SetContext(s);q.Observe(D(At.AddSeconds(15),"GM6URC","-14"),s,null,At.AddSeconds(29));q.Remove("GM6URC");q.Observe(D(At.AddSeconds(15),"GM6URC","-14"),s,null,At.AddSeconds(30));Assert(q.Rows.Count==0,"Old same-slot decode undid operator deletion");q.Observe(D(At.AddSeconds(45),"GM6URC","-16"),s,null,At.AddSeconds(59));Assert(q.Rows.Count==1,"A fresh repeated caller was suppressed for the whole session");
                });
                Run("Preparing preserves display and AutoSelect priorities and switches SingleShot off", () => {
                    string p = Fixture(), profile = Path.Combine(p, "profile"), state = Path.Combine(p, "state");
                    Directory.CreateDirectory(profile);
                    string f = Path.Combine(profile, "JTDX.ini");
                    File.WriteAllText(f, "[Configuration]\r\nUDPServerPort=2237\r\n[Common]\r\nAutoErase=true\r\nBypassAllTextFilters=false\r\nCallMode=3\r\nCallPriorityCQ=true\r\nSingleShotQSO=true\r\n");
                    SettingsManager manager = new SettingsManager(profile, state);
                    manager.Prepare();
                    IniFile ini = new IniFile(f);
                    Assert(ini.Get("Common", "AutoErase", "") == "true" && ini.Get("Common", "BypassAllTextFilters", "") == "false", "Prepare changed the display options");
                    Assert(ini.Get("Common", "CallMode", "") == "3" && ini.Get("Common", "CallPriorityCQ", "") == "true" && ini.Get("Common", "SingleShotQSO", "") == "false", "Prepare changed the AutoSelect priorities or left SingleShot on");
                    manager.Restore();
                    ini = new IniFile(f);
                    Assert(ini.Get("Common", "SingleShotQSO", "") == "false", "Disconnect re-enabled SingleShot against the operator's preference");
                });
                Run("Actual application packet handler retains a fresh CQ until a real Clear packet", () => {
                    using(MainForm form=new MainForm(true))using(UdpBridge bridge=new UdpBridge(0,"JTDX"))using(UdpClient radio=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))
                    {
                        System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                        typeof(MainForm).GetField("bridge",flags).SetValue(form,bridge);AutoCqEngine e=(AutoCqEngine)typeof(MainForm).GetField("engine",flags).GetValue(form);System.Reflection.MethodInfo handle=typeof(MainForm).GetMethod("HandlePacket",flags);bridge.Received=p=>handle.Invoke(form,new object[]{p});RadioStatus status=S(false,"");status.Frequency=7074000;Send(radio,StatusPacket(status,"JTDX"),bridge.Port);Pump(bridge,()=>e.Status.Frequency==7074000);
                        DateTime now=DateTime.UtcNow;DecodedMessage d=CqD(now.AddSeconds(-15),"W2ABC");Send(radio,DecodePacket(d),bridge.Port);Pump(bridge,()=>e.Cqs.Find("W2ABC")!=null);Assert(e.Cqs.Fresh(e.Cqs.Find("W2ABC"),DateTime.UtcNow),"App invalidated CQ immediately after receiving it");Send(radio,StatusPacket(status,"JTDX"),bridge.Port);for(int i=0;i<5;i++){Thread.Sleep(5);bridge.Poll();}Assert(e.Cqs.Fresh(e.Cqs.Find("W2ABC"),DateTime.UtcNow),"Unrelated status packet invalidated CQ");using(QtWriter w=new QtWriter(3,"JTDX",2)){Send(radio,w.Bytes(),bridge.Port);}Pump(bridge,()=>!e.Cqs.Find("W2ABC").Available);
                    }
                });
                Run("An unavailable queue row stays editable and does not keep the timer armed", () => {
                    AutoCqEngine e=new AutoCqEngine();e.Options.RunMinutes=1;e.OnStatus(S(true,"W1AW"),At);e.Start(At);
                    e.OnDecode(D(At.AddSeconds(15), "W2ABC", "FN20"), At.AddSeconds(29));
                    e.Waiting.Invalidate();
                    e.OnLine("Halt Tx triggered at RX: m_singleshot, counter triggered ",At.AddSeconds(59));
                    e.OnStatus(S(false,""),At.AddSeconds(59));e.Tick(At.AddSeconds(60),true);
                    Assert(!e.Armed && !e.Finishing && !e.Status.TxEnabled && e.Waiting.Rows.Single().Call == "W2ABC", "Unavailable queue entry blocked the timer or was discarded");
                });
                Run("Rejected Reply during an ongoing CQ still continues with the next waiting station", () => {
                    AutoCqEngine e=new AutoCqEngine();e.OnStatus(S(true,""),At);e.Start(At);List<string> c=new List<string>();e.RequestReply=d=>c.Add(d.Message.Split(' ')[1]);e.OnDecode(D(At.AddSeconds(15),"SV9AUP","KM25"),At.AddSeconds(29));e.OnDecode(D(At.AddSeconds(15),"GM6URC","-14"),At.AddSeconds(29));e.Tick(At.AddSeconds(29),true);e.OnLine("UDP Reply request ignored, decode not found: K1ABC SV9AUP KM25",At.AddSeconds(29.1));e.Tick(At.AddSeconds(30.1),true);Assert(e.Armed && c.SequenceEqual(new[]{"SV9AUP","GM6URC"}),"Rejected command while CQ enabled abandoned the next station");
                });
                Run("A recent CQ takes priority over eight older unanswered candidates", () => {
                    List<string> c; AutoCqEngine e=SearchActive(out c);
                    e.OnDecode(CqD(At.AddSeconds(15),"IK4LZH"),At.AddSeconds(29));CqListen(e);e.Tick(At.AddSeconds(30),true);
                    e.OnStatus(S(true,"IK4LZH"),At.AddSeconds(30));
                    e.OnLine(Tx(At.AddSeconds(30),"IK4LZH","-23"),At.AddSeconds(30));
                    e.OnLine(Tx(At.AddSeconds(60),"IK4LZH","-23"),At.AddSeconds(60));
                    foreach(string call in new[]{"MI0NWA","RJ3DC","DF7JS","M1DBW","M9HEB","SP7SZJ","OM3WFH","OH6GAZ"})
                        e.OnDecode(CqD(At,call),At.AddSeconds(89));
                    e.OnDecode(CqD(At.AddSeconds(75),"RQ3P"),At.AddSeconds(89));
                    e.OnLine("Halt Tx triggered at RX: m_singleshot, counter triggered ",At.AddSeconds(89.33));
                    e.OnStatus(S(false,""),At.AddSeconds(89.33));e.Tick(At.AddSeconds(90.28),true);
                    Assert(c.SequenceEqual(new[]{"HALT","REPLY IK4LZH","CQ"}), "Own CQ was skipped after a failed search");
                    e.OnStatus(S(true,""),At.AddSeconds(90.3));
                    var transmitting = S(true, ""); transmitting.Transmitting = true;
                    e.OnStatus(transmitting,At.AddSeconds(90.4));
                    e.OnLine(Tx(At.AddSeconds(90),"CQ","FN20"),At.AddSeconds(90.4));
                    e.OnStatus(S(true,""),At.AddSeconds(104)); e.Tick(At.AddSeconds(104),true);
                    e.OnStatus(S(false,""),At.AddSeconds(104.2));
                    e.OnLine("Halt Tx triggered at RX: UDP request ",At.AddSeconds(104.2));
                    e.Tick(At.AddSeconds(120),true);
                    Assert(c.SequenceEqual(new[]{"HALT","REPLY IK4LZH","CQ","HALT","REPLY RQ3P"}) && e.Armed,"Older CQ requests delayed the fresh station");
                });
                Run("Timeout after RR73 disables repeats during RX and gives the final decode a chance", () => {
                    foreach (string final in new[] { null, "73", "RR73" })
                    {
                        List<string> c; AutoCqEngine e = Active(out c);
                        e.UseNativeSelection = true;
                        e.RequestCallerReply = d => c.Add(d.Message.Split(' ')[1]);
                        e.OnLine(Tx(At,"W1AW","RR73").Replace(".000", ".912"),At.AddSeconds(1));
                        e.OnLine("QSO logged: W1AW",At.AddSeconds(1));
                        e.OnLine(DecodeFinished(At.AddSeconds(29.5)),At.AddSeconds(29.5));
                        e.Tick(At.AddSeconds(29.5),true);
                        Assert(c.Count == 0 && e.Awaiting73 && !e.Pending, "First silent RX stopped before the allowed second RR73");
                        e.OnLine(Tx(At.AddSeconds(30),"W1AW","RR73"),At.AddSeconds(30.1));
                        e.OnDecode(D(At.AddSeconds(15), "W2ABC", "FN20"), At.AddSeconds(29));
                        RadioStatus tx = S(true,"W1AW"); tx.Transmitting = true;
                        e.OnStatus(tx, At.AddSeconds(44)); e.Tick(At.AddSeconds(45),true);
                        Assert(c.Count == 0 && e.Awaiting73, "Cut off the second RR73 frame");
                        e.OnStatus(S(true,"W1AW"),At.AddSeconds(45.1)); e.Tick(At.AddSeconds(45.1),true);
                        Assert(c.SequenceEqual(new[]{"HALT"}) && e.HoldNativeSelection, "Third RR73 was still enabled");
                        e.OnLine("Halt Tx triggered at RX: UDP request ",At.AddSeconds(45.2));
                        var decoding = S(false,"W1AW"); decoding.Decoding = true;
                        e.OnStatus(decoding,At.AddSeconds(45.2)); e.Tick(At.AddSeconds(59),true);
                        Assert(c.Count == 1, "Skipped the final reception opportunity");
                        if (final != null)
                            e.OnDecode(D(At.AddSeconds(45),"W1AW",final),At.AddSeconds(59.3));
                        e.OnLine(DecodeFinished(At.AddSeconds(59.5)),At.AddSeconds(59.5));
                        e.Tick(At.AddSeconds(59.5),true);
                        Assert(c.Count == 1, "Started TX before decoding became idle");
                        e.OnStatus(S(false,"W1AW"),At.AddSeconds(59.6)); e.Tick(At.AddSeconds(59.6),true);
                        Assert(c.SequenceEqual(new[]{"HALT","W2ABC"}) && e.Armed
                            && e.ConfirmedCount == (final != null ? 1 : 0) && e.Missing73Count == (final != null ? 0 : 1),
                            "Queue missed the first TX opportunity after final RX");
                    }
                });
                Run("Repeated addressed RR73 permits another 73 until a quiet reply period", () => {
                    List<string> c; AutoCqEngine e = Active(out c);
                    e.UseNativeSelection = true;
                    e.OnLine(Rx(At,"W1AW","RR73"),At.AddSeconds(14.1));
                    DateTime firstTx = At.AddSeconds(15);
                    e.OnLine(Tx(firstTx,"W1AW","73"),firstTx); e.OnLine("QSO logged: W1AW",firstTx);
                    Assert(e.ListenAfterFinal73 && e.HoldNativeSelection && !e.RepeatFinal73 && e.ConfirmedCount == 1,
                        "Our final 73 did not open a reply window");
                    DateTime repeatRx = At.AddSeconds(30);
                    e.OnDecode(D(repeatRx,"W2ABC","RR73"),At.AddSeconds(43));
                    e.OnDecode(D(At,"W1AW","RR73"),At.AddSeconds(43));
                    Assert(!e.RepeatFinal73,"Unrelated or old RR73 triggered a repeat");
                    e.OnDecode(D(repeatRx,"W1AW","RR73"),At.AddSeconds(43.9));
                    e.OnLine(DecodeFinished(At.AddSeconds(44.5)),At.AddSeconds(44.5)); e.Tick(At.AddSeconds(44.5),true);
                    Assert(e.RepeatFinal73 && e.HoldNativeSelection && c.Count == 0,"Peer RR73 did not unlock the next 73");
                    DateTime secondTx = At.AddSeconds(45);
                    e.OnLine(Tx(secondTx,"W1AW","73"),secondTx);
                    Assert(e.ListenAfterFinal73 && !e.RepeatFinal73 && e.ConfirmedCount == 1,"Repeat left TX enabled for a silent period or logged twice");
                    e.Tick(At.AddSeconds(61),true); Assert(c.Count == 0,"Skipped the second reply window");
                    e.OnLine(DecodeFinished(At.AddSeconds(74.6)),At.AddSeconds(74.6)); e.Tick(At.AddSeconds(74.6),true);
                    Assert(c.SequenceEqual(new[]{"HALT"}),"Did not end the exchange after a full RX without another RR73");
                    e.OnLine("Halt Tx triggered at RX: UDP request ",At.AddSeconds(74.7));
                    e.OnStatus(S(false,"W1AW"),At.AddSeconds(74.7)); e.Tick(At.AddSeconds(74.7),true);
                    Assert(c.SequenceEqual(new[]{"HALT","CQ"}) && e.Armed && !e.ListenAfterFinal73 && e.Missing73Count == 0,
                        "Completed reverse exchange missed its next TX opportunity");
                });
                Run("Sending the final 73 retires the old queued report even without a QSO log entry", () => {
                    foreach (string acknowledgement in new[] { "RRR", "RR73" })
                    {
                        var engine = new AutoCqEngine { UseNativeSelection = true };
                        var commands = new List<string>();
                        engine.RequestCallerReply = decode => commands.Add(decode.Message);
                        engine.RequestHalt = () => commands.Add("HALT");
                        engine.RequestCq = () => commands.Add("CQ");
                        engine.Waiting.AnswerWorkedB4 = true;
                        engine.OnStatus(S(false, ""), At);
                        engine.Start(At);
                        DecodedMessage oldReport = D(At.AddSeconds(15), "W1AW", "-09");
                        engine.OnDecode(oldReport, At.AddSeconds(29));
                        engine.Tick(At.AddSeconds(29), true);
                        engine.OnStatus(S(true, "W1AW"), At.AddSeconds(29.1));
                        engine.OnLine(Tx(At.AddSeconds(30), "W1AW", "R-17"), At.AddSeconds(30));
                        engine.OnDecode(D(At.AddSeconds(45), "W1AW", acknowledgement), At.AddSeconds(59));
                        engine.OnLine(Tx(At.AddSeconds(60), "W1AW", "73"), At.AddSeconds(60));
                        engine.Tick(At.AddSeconds(75), true);
                        Assert(engine.Waiting.Find("W1AW") != null && commands.SequenceEqual(new[] { oldReport.Message }),
                            "The exchange finished before the final RX period");

                        DateTime finished = At.AddSeconds(89.6);
                        engine.OnLine(DecodeFinished(finished), finished);
                        engine.Tick(finished, true);
                        engine.OnLine("Halt Tx triggered at RX: UDP request ", finished.AddMilliseconds(50));
                        engine.OnStatus(S(false, ""), finished.AddMilliseconds(100));
                        engine.Tick(finished.AddMilliseconds(100), true);
                        Assert(engine.Armed && engine.LoggedCount == 0 && engine.ConfirmedCount == 1
                            && engine.Waiting.Find("W1AW") == null && commands.SequenceEqual(new[] { oldReport.Message, "HALT", "CQ" }),
                            "A confirmed exchange with no log entry left the original report in the queue");

                        engine.OnStatus(S(true, ""), At.AddSeconds(90));
                        engine.OnDecode(oldReport, At.AddSeconds(91));
                        Assert(engine.Waiting.Find("W1AW") == null, "A repeated decode requeued the finished exchange");
                        engine.OnDecode(D(At.AddSeconds(105), "W1AW", "FN20"), At.AddSeconds(119));
                        Assert(engine.Waiting.Find("W1AW") != null, "A fresh call was rejected despite Answer B4");
                    }
                });
                Run("Two initial calls stop at the decoded RX boundary without a third frame", () => {
                    List<string> c; AutoCqEngine e = Active(out c);
                    e.OnLine(Tx(At,"W1AW","-10"),At);
                    e.OnLine(Tx(At.AddSeconds(30),"W1AW","-10"),At.AddSeconds(30));
                    e.Tick(At.AddSeconds(45), true); Assert(c.Count == 0, "Did not listen after the second call");
                    DateTime finished = At.AddSeconds(59.6);
                    e.OnLine(DecodeFinished(finished),finished); e.Tick(finished,true);
                    Assert(c.SequenceEqual(new[]{"HALT"}), "Third call was still allowed after the decoder finished");
                    e.OnLine("Halt Tx triggered at RX: UDP request ",finished.AddMilliseconds(20));
                    e.OnStatus(S(false,"W1AW"),finished.AddMilliseconds(20));
                    e.Tick(finished.AddMilliseconds(25),true);
                    Assert(c.SequenceEqual(new[]{"HALT","CQ"}) && e.Armed, "The retry limit ended the session");
                });
                Run("CQ flips after the chosen quiet interval in FT8 and FT4, only after listening", () => {
                    foreach (string mode in new[]{"FT8","FT4"})
                    {
                        var e = new AutoCqEngine { UseNativeSelection = true };
                        var commands = new List<string>();
                        e.Options.SearchEnabled = false; e.Options.SwitchPeriodMinutes = 1;
                        e.RequestHalt = () => commands.Add("HALT");
                        e.RequestCq = () => commands.Add(e.CqTxFirst ? "FIRST" : "SECOND");
                        double slot = mode == "FT4" ? 7.5 : 15;
                        RadioStatus rx = S(true,""); rx.Mode = mode;
                        RadioStatus tx = S(true,""); tx.Mode = mode; tx.Transmitting = true;
                        DateTime sent = At.AddMinutes(1);
                        e.OnStatus(rx, At.AddSeconds(slot)); e.Start(At.AddSeconds(slot));
                        e.OnLine(Tx(sent,"CQ","FN20"),sent); e.OnStatus(tx,sent.AddSeconds(1));
                        e.Tick(sent.AddSeconds(slot),true); Assert(commands.Count == 0, "Phase change cut off an active frame");
                        e.OnStatus(rx,sent.AddSeconds(slot)); e.Tick(sent.AddSeconds(slot),true);
                        Assert(commands.SequenceEqual(new[]{"HALT"}), "No idle-period switch was scheduled");
                        e.OnLine("Halt Tx triggered at RX: UDP request ",sent.AddSeconds(slot + 0.1));
                        e.Tick(sent.AddSeconds(slot + 0.1),true);
                        Assert(commands.Count == 1, "Duplicate halt issued before the status update");
                        var stopped = S(false, ""); stopped.Mode = mode;
                        e.OnStatus(stopped,sent.AddSeconds(slot + 0.2));
                        e.Tick(sent.AddSeconds(2 * slot - 0.1),true); Assert(commands.Count == 1, "Last old-phase RX was skipped");
                        e.Tick(sent.AddSeconds(2 * slot),true);
                        Assert(commands.SequenceEqual(new[]{"HALT","SECOND"}), "CQ did not target the other phase");
                        e.OnStatus(rx,sent.AddSeconds(2 * slot + 0.1));
                        var newPhase = S(true, ""); newPhase.Mode = mode; newPhase.TxFirst = false;
                        e.OnStatus(newPhase,sent.AddSeconds(2 * slot + 0.2));
                        e.Tick(sent.AddSeconds(3 * slot),true);
                        Assert(e.Armed && !e.CqTxFirst && commands.Count == 2, "Phase change failed to acknowledge or repeat timer reset");
                    }
                });
                Run("A caller decoded in the last old-phase RX takes priority over changing period", () => {
                    var e = new AutoCqEngine { UseNativeSelection = true };
                    e.Options.SwitchPeriodMinutes = 1; e.Options.SearchEnabled = true;
                    var c = new List<string>(); e.RequestHalt = () => c.Add("HALT"); e.RequestCq = () => c.Add("CQ");
                    e.RequestCallerReply = d => c.Add(d.Message.Split(' ')[1]);
                    e.OnStatus(S(true,""),At); e.Start(At);
                    DateTime sent = At.AddSeconds(60), heard = sent.AddSeconds(15), ready = sent.AddSeconds(29.5);
                    var tx = S(true, ""); tx.Transmitting = true;
                    e.OnLine(Tx(sent,"CQ","FN20"),sent); e.OnStatus(tx,sent.AddSeconds(1));
                    e.OnStatus(S(true,""),heard); e.Tick(heard,true);
                    e.OnStatus(S(false,""),heard.AddMilliseconds(100));
                    e.OnLine("Halt Tx triggered at RX: UDP request ",heard.AddMilliseconds(100));
                    e.OnDecode(D(heard,"W3ABC","FN20"),ready);
                    e.OnLine(DecodeFinished(ready),ready); e.Tick(ready,true);
                    Assert(c.SequenceEqual(new[]{"HALT","W3ABC"}) && e.CqTxFirst, "Phase switch displaced an incoming call");
                });
                Run("Waiting lifetime removes saved, manual and failed rows and protects the active QSO", () => {
                    var e = new AutoCqEngine(); e.Options.QueueMinutes = 2;
                    e.OnStatus(S(true,"W1AW"),At); e.Start(At);
                    e.Waiting.Add("W1AW",At); e.Waiting.Add("W3ABC",At);
                    e.OnDecode(D(At,"W2ABC","FN20"),At.AddSeconds(1));
                    e.Waiting.Pause("W2ABC","Нет ответа",At.AddSeconds(30));
                    e.OnDecode(D(At.AddSeconds(90),"W2ABC","FN20"),At.AddSeconds(105));
                    e.Tick(At.AddSeconds(119.9),true); Assert(e.Waiting.Rows.Count == 3, "Rows expired too early");
                    e.Tick(At.AddSeconds(121),true); Assert(e.Waiting.Rows.Single().Call == "W1AW", "Queue lifetime cut active QSO or was reset by a repeat");
                    e.Stop("Stop",false); e.OnStatus(S(false,""),At.AddSeconds(122)); e.Tick(At.AddSeconds(122),true);
                    Assert(e.Waiting.Rows.Count == 0, "Expiry depended on arming the automatic session");
                    e.OnDecode(D(At.AddSeconds(90),"W2ABC","FN20"),At.AddSeconds(123));
                    Assert(e.Waiting.Rows.Count == 0, "Old decode revived an expired row");
                    e.OnDecode(D(At.AddSeconds(135),"W2ABC","FN20"),At.AddSeconds(149));
                    Assert(e.Waiting.Rows.Single().Call == "W2ABC", "A new call could not rejoin the queue");
                });
                Run("DXCC exclusions use exact calls and portable prefixes and skip both automatic lists", () => {
                    string file = Path.Combine(Fixture(),"cty.dat");
                    File.WriteAllText(file,"Germany: 14: 28: EU: 0: 0: 0: DL:\n    DA,DB,DL,=K1ABC;\n"
                        + "Spain: 14: 37: EU: 0: 0: 0: EA:\n    EA,EB(14)[37];\n"
                        + "Canary Islands: 33: 36: AF: 0: 0: 0: EA8:\n    EA8,EB8;\n"
                        + "United States: 5: 8: NA: 0: 0: 0: K:\n    K,W,AA;\n");
                    var table = DxccCountries.Load(file);
                    Assert(table.Find("K1ABC").Prefix == "DL" && table.Find("EA8/DL1ABC").Prefix == "EA8"
                        && table.Find("DL1ABC/EA8").Prefix == "EA8" && table.Find("EB7ZZ/P").Prefix == "EA"
                        && table.Find("EA1AA/8").Prefix == "EA8" && table.Find("DL1ABC/MM") == null,
                        "Country lookup ignored a more specific entry or portable indicator");
                    List<string> c; var e = SearchActive(out c);
                    e.Options.ExcludedDxcc = new[]{"DL"}; e.UseNativeSelection = true;
                    e.CanCall = call => table.Find(call).Prefix != "DL";
                    e.RequestCallerReply = d => c.Add("REPLY " + d.Message.Split(' ')[1]);
                    Assert(e.HoldNativeSelection, "Native selection could bypass country exclusions");
                    e.OnDecode(D(At.AddSeconds(15),"DL1ABC","JO62"),At.AddSeconds(29));
                    e.OnDecode(D(At.AddSeconds(15),"EA2ABC","IN93"),At.AddSeconds(29));
                    e.Tick(At.AddSeconds(29),true);
                    Assert(c.SequenceEqual(new[]{"REPLY EA2ABC"}), "An excluded caller bypassed the queue filter");
                    var cq = CqD(At.AddSeconds(15),"DL2ABC"); cq.IsNew = false;
                    e.OnDecode(cq,At.AddSeconds(29)); cq = CqD(At.AddSeconds(15),"EA2ABC"); cq.IsNew = false;
                    e.OnDecode(cq,At.AddSeconds(29));
                    Assert(e.Cqs.Next(At.AddSeconds(30),e.CanCall).Call == "EA2ABC", "An excluded CQ bypassed the search filter");
                    e.Stop("Stop",false); Assert(!e.HoldNativeSelection,"Exclusions were applied after stopping automation");
                });
                Run("DXCC priorities preserve caller order within the group and respect exclusions", () => {
                    List<string> commands;
                    AutoCqEngine e = SearchActive(out commands);
                    e.UseNativeSelection = true;
                    e.Options.PriorityDxcc = new[] { "DL", "EA" };
                    e.IsPriorityCall = call => call.StartsWith("DL") || call.StartsWith("EA");
                    e.CanCall = call => !call.StartsWith("DL");
                    Assert(e.HoldNativeSelection, "Native selection could take a caller before the priority queue");
                    DateTime heard = At.AddSeconds(15);
                    DateTime ready = At.AddSeconds(29);
                    e.OnDecode(D(heard, "W2ABC", "FN20"), ready);
                    e.OnDecode(D(heard, "DL1ABC", "JO62"), ready);
                    e.OnDecode(D(heard, "EA1ABC", "IN93"), ready);
                    e.OnDecode(D(heard, "EA2ABC", "IN93"), ready);
                    var cq = CqD(heard, "EA3ABC"); cq.IsNew = false;
                    e.OnDecode(cq, ready);
                    e.Tick(ready, true);
                    Assert(commands.SequenceEqual(new[] { "REPLY EA1ABC" }), "A CQ or another caller displaced the first eligible priority caller");
                });
                Run("A priority station from the visible CQ list can replace the very first own CQ", () => {
                    var e = new AutoCqEngine { UseNativeSelection = true };
                    var commands = new List<string>();
                    e.RequestCq = () => commands.Add("CQ");
                    e.RequestReply = d => commands.Add("REPLY " + d.Message.Split(' ')[1]);
                    e.Options.PriorityDxcc = new[] { "EA" };
                    e.Options.SearchEnabled = false;
                    e.IsPriorityCall = call => call.StartsWith("EA");
                    e.OnStatus(S(false, ""), At);
                    e.Start(At);
                    e.OnDecode(CqD(At.AddSeconds(-15), "EA1ABC"), At);
                    var visible = CqD(At.AddSeconds(-15), "EA2ABC"); visible.IsNew = false;
                    e.OnDecode(visible, At);
                    e.Tick(At.AddSeconds(1), true);
                    Assert(commands.SequenceEqual(new[] { "REPLY EA2ABC" }), "The priority call did not precede own CQ or bypassed the left-window filter");
                });
                Run("Priority scanning between own CQ frames preserves the ordinary CQ counter", () => {
                    List<string> commands;
                    AutoCqEngine e = SearchActive(out commands);
                    e.UseNativeSelection = true;
                    e.Options.CqAttemptsBeforeSearch = 3;
                    e.Options.PriorityDxcc = new[] { "EA" };
                    e.IsPriorityCall = call => call.StartsWith("EA");
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        DateTime tx = At.AddSeconds(attempt * 30);
                        e.OnStatus(S(true, ""), tx);
                        e.OnLine(Tx(tx, "CQ", "FN20"), tx);
                        RadioStatus active = S(true, ""); active.Transmitting = true;
                        e.OnStatus(active, tx.AddSeconds(1));
                        e.Tick(tx.AddSeconds(6), true);
                        Assert(commands.Count == attempt * 2, "Priority scanning interrupted a CQ frame");
                        DateTime rx = tx.AddSeconds(14);
                        e.OnStatus(S(true, ""), rx);
                        e.Tick(rx, true);
                        e.OnLine("Halt Tx triggered at RX: UDP request ", rx.AddMilliseconds(100));
                        e.OnStatus(S(false, ""), rx.AddMilliseconds(100));
                        e.Tick(rx.AddMilliseconds(101), true);
                        Assert(commands.Count == attempt * 2 + 1, "The response slot was not given time to finish");
                        var other = CqD(tx.AddSeconds(15), "W2ABC"); other.IsNew = false;
                        DateTime ready = tx.AddSeconds(29.5);
                        e.OnDecode(other, ready);
                        e.OnLine(DecodeFinished(ready), ready);
                        e.Tick(ready, true);
                        Assert(commands.Last() == (attempt < 2 ? "CQ" : "REPLY W2ABC"), "The priority check changed when the ordinary search begins");
                    }
                    Assert(commands.SequenceEqual(new[] { "HALT", "CQ", "HALT", "CQ", "HALT", "REPLY W2ABC" }) && e.Armed,
                        "The priority check repeated the halt or reset the CQ count");
                });
                Run("A priority CQ discovered in the first reply slot is called before the next CQ", () => {
                    List<string> commands;
                    AutoCqEngine e = SearchActive(out commands);
                    e.UseNativeSelection = true;
                    e.Options.CqAttemptsBeforeSearch = 3;
                    e.Options.PriorityDxcc = new[] { "EA" };
                    e.IsPriorityCall = call => call.StartsWith("EA");
                    CqListen(e);
                    DateTime heard = At.AddSeconds(15);
                    DateTime ready = At.AddSeconds(29.4);
                    var preferred = CqD(heard, "EA1ABC"); preferred.IsNew = false;
                    e.OnDecode(preferred, ready);
                    e.OnLine(DecodeFinished(ready), ready);
                    e.WaitForVisibleCqsUntil = ready.AddMilliseconds(130);
                    e.Tick(ready, true);
                    Assert(commands.SequenceEqual(new[] { "HALT" }), "Selection did not wait for the left-window replay");
                    e.Tick(ready.AddMilliseconds(150), true);
                    Assert(commands.SequenceEqual(new[] { "HALT", "REPLY EA1ABC" }) && e.Armed, "Priority CQ waited for the general search threshold");
                });
                Run("Selecting and clearing a DX during a priority CQ check still answers the next caller", () => {
                    foreach (bool haltConfirmedBeforeSelection in new[] { true, false })
                    {
                        foreach (bool transmitBeforeClear in new[] { false, true })
                        {
                            List<string> commands;
                            AutoCqEngine engine = SearchActive(out commands);
                            engine.UseNativeSelection = true;
                            engine.Options.CqAttemptsBeforeSearch = 3;
                            engine.Options.PriorityDxcc = new[] { "EA" };
                            engine.OnLine(Tx(At, "CQ", "FN20"), At);
                            var cq = S(true, "");
                            cq.Transmitting = true;
                            engine.OnStatus(cq, At.AddSeconds(1));
                            engine.OnStatus(S(true, ""), At.AddSeconds(14));
                            engine.Tick(At.AddSeconds(14), true);
                            engine.OnStatus(S(false, ""), At.AddSeconds(14.1));
                            const string halt = "Halt Tx triggered at RX: UDP request ";
                            if (haltConfirmedBeforeSelection) engine.OnLine(halt, At.AddSeconds(14.1));
                            engine.OnStatus(S(true, "W2ABC"), At.AddSeconds(16.7));
                            if (!haltConfirmedBeforeSelection) engine.OnLine(halt, At.AddSeconds(16.8));
                            engine.Tick(At.AddSeconds(16.9), true);
                            Assert(commands.SequenceEqual(new[] { "HALT" }), "AutoCQ interfered with the manual selection");

                            int nextCallSlot = 15;
                            if (transmitBeforeClear)
                            {
                                RadioStatus manual = S(true, "W2ABC");
                                manual.Transmitting = true;
                                engine.OnStatus(manual, At.AddSeconds(30));
                                engine.OnLine(Tx(At.AddSeconds(30), "W2ABC", "-12"), At.AddSeconds(30.1));
                                engine.OnStatus(S(true, "W2ABC"), At.AddSeconds(44));
                                nextCallSlot = 45;
                            }
                            engine.OnStatus(S(true, ""), At.AddSeconds(nextCallSlot + 8));
                            DateTime callerSlot = At.AddSeconds(nextCallSlot);
                            DateTime ready = callerSlot.AddSeconds(14.7);
                            engine.OnDecode(D(callerSlot, "W3ABC", "FN20"), ready);
                            engine.OnLine(DecodeFinished(ready), ready);
                            engine.Tick(ready, true);
                            Assert(commands.SequenceEqual(new[] { "HALT", "REPLY W3ABC" }) && engine.Armed,
                                "The abandoned CQ check skipped an addressed caller");

                            engine.OnStatus(S(true, "W3ABC"), ready.AddMilliseconds(100));
                            engine.Tick(At.AddSeconds(76), true);
                            Assert(engine.Armed && !engine.Pending, "The abandoned CQ check still expired");
                        }
                    }
                });
                Run("Priority CQ never interrupts an active QSO or begins after the session timer", () => {
                    List<string> commands;
                    AutoCqEngine e = Active(out commands);
                    e.Options.RunMinutes = 1;
                    e.Options.PriorityDxcc = new[] { "EA" };
                    e.IsPriorityCall = call => call.StartsWith("EA");
                    e.RequestReply = d => commands.Add("REPLY " + d.Message.Split(' ')[1]);
                    e.Start(At);
                    DateTime tx = At.AddSeconds(30);
                    e.OnLine(Tx(tx, "W1AW", "RR73"), tx);
                    e.OnLine("QSO logged: W1AW", tx);
                    var preferred = CqD(At.AddSeconds(45), "EA1ABC"); preferred.IsNew = false;
                    e.OnDecode(preferred, At.AddSeconds(59));
                    e.Tick(At.AddSeconds(59), true);
                    Assert(commands.Count == 0 && e.Awaiting73, "Priority CQ interrupted the final exchange");
                    DateTime ready = At.AddSeconds(60);
                    e.OnLine(Rx(At.AddSeconds(45), "W1AW", "73"), ready);
                    e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Autolog ", ready);
                    e.OnStatus(S(false, ""), ready);
                    e.Tick(ready, true);
                    Assert(commands.Count == 0 && !e.Armed, "A priority CQ began after the session timer");
                });
                Run("Priority scanning respects Enable TX turned off as an own CQ finishes", () => {
                    List<string> commands;
                    AutoCqEngine e = SearchActive(out commands);
                    e.UseNativeSelection = true;
                    e.Options.CqAttemptsBeforeSearch = 3;
                    e.Options.PriorityDxcc = new[] { "EA" };
                    e.IsPriorityCall = call => call.StartsWith("EA");
                    e.OnLine(Tx(At, "CQ", "FN20"), At);
                    RadioStatus active = S(true, ""); active.Transmitting = true;
                    e.OnStatus(active, At.AddSeconds(1));
                    e.OnStatus(S(false, ""), At.AddSeconds(14));
                    e.Tick(At.AddSeconds(14), true);
                    e.Tick(At.AddSeconds(15), true);
                    Assert(!e.Armed && commands.Count == 0, "Priority scanning treated an unexplained stop as its own CQ halt");
                });
                Run("Priority countries persist and older saved options load with an empty priority list", () => {
                    string directory = Fixture();
                    var store = new OperatorStateStore(directory);
                    File.WriteAllText(Path.Combine(directory, "operator.xml"),
                        "<OperatorState><Options><RunMinutes>25</RunMinutes></Options></OperatorState>");
                    var options = store.Load().Options;
                    Assert(options.RunMinutes == 25 && options.PriorityDxcc.Length == 0, "Saved pre-priority options changed");
                    options.ExcludedDxcc = new[] { "EA" };
                    options.PriorityDxcc = new[] { "DL", "EA" };
                    store.Save(options, new WaitingStation[0]);
                    var saved = store.Load().Options;
                    Assert(saved.PriorityDxcc.SequenceEqual(options.PriorityDxcc) && saved.ExcludedDxcc.SequenceEqual(options.ExcludedDxcc),
                        "Priority settings did not round-trip");
                });
                Run("An explicit missing-decode rejection immediately tries the next ready station", () => {
                    List<string> c;AutoCqEngine e=SearchActive(out c);
                    e.OnDecode(CqD(At.AddSeconds(15),"W2ABC"),At.AddSeconds(29));
                    e.OnDecode(CqD(At.AddSeconds(15),"W3ABC"),At.AddSeconds(29));CqListen(e);e.Tick(At.AddSeconds(30),true);
                    e.OnLine("UDP Reply request ignored, decode not found: CQ W2ABC FN20",At.AddSeconds(30.10));
                    e.Tick(At.AddSeconds(30.11),true);
                    Assert(c.SequenceEqual(new[]{"HALT","REPLY W2ABC","REPLY W3ABC"}) && e.Armed,"Known rejection inserted an avoidable retry delay");
                });
                Run("Final 73, RR73 and RRR resume CQ in the next TX slot of FT8 and FT4", () => {
                    foreach(string mode in new[]{"FT8","FT4"})foreach(bool initial in new[]{false,true})
                    foreach (string final in new[] { "73", "RR73", "RRR" })
                    foreach (bool writtenToLog in new[] { false, true })
                    using(UdpClient radio=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))
                    using(UdpBridge bridge=new UdpBridge(0,"JTDX"))
                    {
                        AutoCqEngine e=new AutoCqEngine();RadioStatus start=S(true,"");start.Mode=mode;start.TxFirst=initial;e.OnStatus(start,At);e.Start(At);
                        RadioStatus qso=S(true,"W1AW");qso.Mode=mode;qso.TxFirst=!initial;e.OnStatus(qso,At.AddSeconds(1));
                        Send(radio,StatusPacket(qso,"JTDX"),bridge.Port);Pump(bridge,()=>bridge.Client!=null);
                        e.RequestCq=()=>bridge.SendToClient(QtPacket.TriggerCq("JTDX",2,"",e.CqTxFirst,true));
                        double slot=mode=="FT8"?15:7.5;
                        DateTime tx=At.AddSeconds((qso.TxFirst?2:1)*slot),nextTx=tx.AddSeconds(2*slot),ready=nextTx.AddMilliseconds(-100);
                        e.OnLine(Tx(tx, "W1AW", "RR73").Replace("FT8:", mode + ":"), tx);
                        if (writtenToLog) e.OnLine("QSO logged: W1AW", tx);
                        e.OnLine(Rx(tx.AddSeconds(slot), "W1AW", final).Replace(" ~ ", mode == "FT4" ? " : " : " ~ "), ready);
                        e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Singleshot ",ready);
                        RadioStatus idle=S(false,"");idle.Mode=mode;idle.TxFirst=qso.TxFirst;e.OnStatus(idle,ready);
                        e.Tick(nextTx,true);
                        Assert(radio.Available > 0 && e.Armed && e.ConfirmedCount == 1 && e.LoggedCount == (writtenToLog ? 1 : 0),
                            "Next TX opportunity was skipped or the log count is incorrect: " + mode);
                        using(QtReader r=new QtReader(Receive(radio)))
                        {r.U32();r.U32();Assert(r.U32()==51 && r.Text()=="JTDX" && r.Text()=="" && r.Bool()==qso.TxFirst && r.Bool(),"CQ datagram changed TX phase: "+mode);}
                    }
                });
                Run("Removing the FIN delay still waits for native TX and decoding to become idle", () => {
                    List<string> c;AutoCqEngine e=Active(out c);
                    e.OnLine(Tx(At,"W1AW","RR73"),At);e.OnLine("QSO logged: W1AW",At);
                    e.OnLine(Rx(At.AddSeconds(15),"W1AW","73"),At.AddSeconds(28));
                    e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Singleshot ",At.AddSeconds(28));
                    e.Tick(At.AddSeconds(28),true);Assert(c.Count==0,"CQ ignored the still-enabled native transmitter");
                    RadioStatus busy=S(false,"");busy.Decoding=true;e.OnStatus(busy,At.AddSeconds(28.1));e.Tick(At.AddSeconds(28.1),true);Assert(c.Count==0,"CQ interrupted decoding");
                    e.OnStatus(S(false,""),At.AddSeconds(28.2));e.Tick(At.AddSeconds(28.2),true);
                    Assert(c.SequenceEqual(new[]{"CQ"}) && e.Armed,"Ready native RX waited for an arbitrary timer");
                });
                Run("A cleared display still hands off a saved caller through the queue datagram", () => {
                    using (MainForm form = new MainForm(true))
                    using (UdpBridge bridge = new UdpBridge(0, "JTDX"))
                    using (UdpClient radio = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                    {
                        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                        typeof(MainForm).GetField("bridge", flags).SetValue(form, bridge);
                        AutoCqEngine engine = (AutoCqEngine)typeof(MainForm).GetField("engine", flags).GetValue(form);
                        var handle = typeof(MainForm).GetMethod("HandlePacket", flags);
                        bridge.Received = packet => handle.Invoke(form, new object[] { packet });
                        engine.UseNativeSelection = true;
                        engine.RequestCq = delegate { throw new Exception("CQ displaced the saved caller"); };
                        engine.RequestCallerReply = d => bridge.SendToClient(QtPacket.ReplyToCaller("JTDX", 2, d));
                        Send(radio, StatusPacket(S(true, "W1AW"), "JTDX"), bridge.Port);
                        Pump(bridge, () => engine.Status.DxCall == "W1AW");
                        DateTime now = DateTime.UtcNow;
                        DateTime slot = now.Date.AddSeconds(Math.Floor(now.TimeOfDay.TotalSeconds / 15) * 15 - 30);
                        engine.Start(now);
                        Send(radio, DecodePacket(D(slot, "W2ABC", "FN20")), bridge.Port);
                        Pump(bridge, () => engine.Waiting.Rows.Count == 1);
                        using (QtWriter clear = new QtWriter(3, "JTDX", 2))
                            Send(radio, clear.Bytes(), bridge.Port);
                        Send(radio, StatusPacket(S(true, "W1AW"), "JTDX"), bridge.Port);
                        Pump(bridge, () => bridge.Client != null);
                        Assert(engine.Waiting.Rows[0].Available, "Clear discarded the saved caller");
                        Finish(engine, now);
                        engine.Tick(now.AddSeconds(2), true);
                        Assert(radio.Available > 0, "No cached Reply was sent");
                        byte[] data = Receive(radio);
                        Assert(QtPacket.Parse(data).Type == 52 && data[data.Length - 1] == 0xa8,
                            "Saved caller used the original window-dependent Reply");
                    }
                });
                Run("A visible CQ gets the managed Reply marker and LogHX cannot send it", () => {
                    using (UdpClient radio = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                    using (UdpClient logbook = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                    using (UdpBridge bridge = new UdpBridge(0, "JTDX"))
                    {
                        bridge.Upstream = (IPEndPoint)logbook.Client.LocalEndPoint;
                        Send(radio, StatusPacket(S(false, ""), "JTDX"), bridge.Port);
                        Pump(bridge, () => bridge.Client != null);
                        DecodedMessage cq = CqD(At, "W3ABC");
                        byte[] managed = QtPacket.ReplyToVisibleCq("JTDX", 2, cq);
                        byte[] ordinary = QtPacket.Reply("JTDX", 2, cq);
                        bridge.SendToClient(managed);
                        byte[] reply = Receive(radio);
                        Assert(QtPacket.Parse(reply).Type == 52 && reply[reply.Length - 1] == 0xa9,
                            "Visible CQ does not use the window-checked AutoCQ Reply");
                        Assert(reply.Skip(12).Take(reply.Length - 13).SequenceEqual(ordinary.Skip(12).Take(ordinary.Length - 13)),
                            "Managed Reply changed the original decoded CQ fields");
                        Send(logbook, managed, bridge.Port);
                        Send(logbook, ordinary, bridge.Port);
                        Pump(bridge, () => radio.Available > 0);
                        Assert(Receive(radio).SequenceEqual(ordinary), "The upstream logger could use the AutoCQ Reply marker");
                    }
                });
                Run("CQ search uses the filtered window replay and keeps addressed decodes independently", () => {
                    var engine = new AutoCqEngine { UseNativeSelection = true };
                    engine.OnStatus(S(false, ""), At);
                    DecodedMessage hidden = CqD(At, "W2ABC");
                    DecodedMessage shown = CqD(At, "W3ABC");
                    engine.OnDecode(hidden, At.AddSeconds(14));
                    engine.OnDecode(shown, At.AddSeconds(14));
                    engine.OnDecode(D(At, "SV9AUP", "KM25"), At.AddSeconds(14));
                    shown.IsNew = false;
                    engine.OnDecode(shown, At.AddSeconds(14));
                    Assert(engine.Cqs.Next(At.AddSeconds(14)).Call == "W3ABC" && engine.Cqs.Find("W2ABC") == null,
                        "A hidden general call entered the eligible window list");
                    Assert(engine.Waiting.Rows.Single().Call == "SV9AUP", "Display filtering discarded an addressed caller");
                });
                Run("A priority station's visible 73, RRR or RR73 selects the sender in the next period", () => {
                    foreach (string mode in new[] { "FT8", "FT4" })
                    foreach (string signoff in new[] { "73", "RRR", "RR73" })
                    using (var radio = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                    using (var bridge = new UdpBridge(0, "JTDX"))
                    {
                        radio.Client.ReceiveTimeout = 1000;
                        var engine = new AutoCqEngine { UseNativeSelection = true };
                        engine.Options.SearchEnabled = false;
                        engine.Options.PriorityDxcc = new[] { "UA" };
                        engine.IsPriorityCall = call => call == "R3ZZA";
                        engine.RequestCq = delegate { throw new Exception("A CQ displaced the visible sign-off"); };
                        engine.RequestReply = decode => bridge.SendToClient(QtPacket.ReplyToVisibleCq("JTDX", 2, decode));
                        RadioStatus active = S(true, "W1AW");
                        active.Mode = mode;
                        engine.OnStatus(active, At);
                        engine.Start(At);
                        Send(radio, StatusPacket(active, "JTDX"), bridge.Port);
                        Pump(bridge, () => bridge.Client != null);
                        double slot = mode == "FT8" ? 15 : 7.5;
                        string symbol = mode == "FT8" ? "~" : ":";
                        DateTime ready = At.AddSeconds(2 * slot - 0.3);
                        var heard = D(At.AddSeconds(slot), "R3ZZA", signoff);
                        heard.Message = "R6ZZB R3ZZA " + signoff;
                        heard.Mode = symbol;
                        engine.OnDecode(heard, ready);
                        Assert(engine.Cqs.Find("R3ZZA") == null, "An unseen sign-off was selected from raw UDP");
                        heard.IsNew = false;
                        engine.OnDecode(heard, ready);
                        engine.OnLine(Tx(At, "W1AW", "RR73").Replace("FT8:", mode + ":"), At);
                        engine.OnLine("QSO logged: W1AW", At);
                        engine.Tick(ready, true);
                        Assert(engine.Awaiting73 && radio.Available == 0, "A sign-off interrupted the current QSO");
                        engine.OnLine(Rx(At.AddSeconds(slot), "W1AW", "73").Replace(" ~ ", " " + symbol + " "), ready);
                        engine.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Autolog ", ready);
                        RadioStatus idle = S(false, "");
                        idle.Mode = mode;
                        engine.OnStatus(idle, ready);
                        engine.Tick(ready.AddMilliseconds(40), true);
                        byte[] packet = Receive(radio);
                        using (var reader = new QtReader(packet))
                        {
                            reader.U32(); reader.U32();
                            Assert(reader.U32() == 52 && reader.Text() == "JTDX"
                                && reader.U32() == heard.Milliseconds, "Reply did not keep the original mode and receive period");
                            reader.U32(); reader.Double(); reader.U32();
                            Assert(reader.Text() == symbol && reader.Text() == heard.Message && !reader.Bool() && reader.Byte() == 0xa9,
                                "Sign-off was rewritten or bypassed the native window check");
                        }
                        RadioStatus selected = S(true, "R3ZZA");
                        selected.Mode = mode;
                        engine.OnStatus(selected, ready.AddMilliseconds(100));
                        Assert(engine.Armed && !engine.Pending && engine.ConfirmedCount == 1,
                            "The sender was not the expected callsign");
                    }
                });
                Run("Only an eligible visible sign-off takes priority over CQ", () => {
                    foreach (string ignored in new[] { "hidden", "expired", "busy", "disabled", "excluded", "plain", "own", "portable-own" })
                    {
                        List<string> commands;
                        AutoCqEngine engine = Active(out commands);
                        engine.UseNativeSelection = true;
                        engine.Options.PriorityDxcc = new[] { "UA" };
                        engine.Options.PriorityAfter73 = ignored != "disabled";
                        engine.CanCall = call => ignored != "excluded" || call != "R3ZZA";
                        engine.IsPriorityCall = call => ignored != "plain" && call == "R3ZZA";
                        engine.RequestReply = reply => commands.Add(reply.Message);
                        DateTime slot = ignored == "expired" ? At.AddSeconds(-15) : At.AddSeconds(15);
                        DecodedMessage decode = D(slot, "R3ZZA", "73");
                        string recipient = ignored == "own" ? "K1ABC" : ignored == "portable-own" ? "F/K1ABC" : "R6ZZB";
                        decode.Message = recipient + " R3ZZA 73    ^";
                        decode.IsNew = ignored == "hidden";
                        engine.OnDecode(decode, At.AddSeconds(28));
                        if (ignored == "busy")
                        {
                            var busy = D(At.AddSeconds(30), "R3ZZA", "R-12");
                            busy.Message = "W9XYZ R3ZZA R-12";
                            engine.OnDecode(busy, At.AddSeconds(30));
                        }
                        Finish(engine, At.AddSeconds(30));
                        engine.Tick(At.AddSeconds(31.1), true);
                        Assert(commands.SequenceEqual(new[] { "CQ" }), "Ineligible sign-off was called: " + ignored);
                    }
                });
                Run("Legacy operator entries are called directly in order with no left-window data", () => {
                    string folder = Fixture();
                    File.WriteAllText(Path.Combine(folder, "operator.xml"),
                        "<OperatorState><Stations><WaitingStation><Call>W2ABC</Call>" +
                        "<FirstHeardUtc>2025-11-07T12:00:00Z</FirstHeardUtc>" +
                        "</WaitingStation></Stations></OperatorState>");
                    var store = new OperatorStateStore(folder);
                    List<string> commands;
                    AutoCqEngine engine = Active(out commands);
                    engine.UseNativeSelection = true;
                    engine.Options.SearchEnabled = false;
                    engine.Waiting.Load(store.Load().Stations);
                    engine.RequestDirectCall = call => commands.Add("DIRECT " + call);
                    Assert(engine.Waiting.Rows.Single().AddedByOperator && !engine.Waiting.Rows.Single().Available,
                        "An old manually entered callsign was reclassified or given a decoded message");
                    engine.AddQueuedCall("W3ABC", At);
                    engine.Waiting.Move("W3ABC", -1);
                    Finish(engine, At.AddSeconds(29.2));
                    engine.Tick(At.AddSeconds(31), true);
                    engine.OnStatus(S(true, "W3ABC"), At.AddSeconds(31.1));
                    engine.OnLine("Halt Tx triggered at RX: AutoCQ, counter triggered", At.AddSeconds(89.9));
                    engine.OnStatus(S(false, ""), At.AddSeconds(90));
                    engine.Tick(At.AddSeconds(90.1), true);
                    Assert(commands.SequenceEqual(new[] { "DIRECT W3ABC", "DIRECT W2ABC" }) && engine.Armed,
                        "A failed direct call blocked the next operator entry.");
                });
                Run("Direct call carries the callsign alone in a source-checked extension", () => {
                    using (var packet = new QtReader(QtPacket.CallStation("JTDX", 2, "w3abc")))
                    {
                        Assert(packet.U32() == QtPacket.Magic && packet.U32() == 2 && packet.U32() == 52
                            && packet.Text() == "JTDX" && packet.U32() == 0 && packet.U32() == 0
                            && packet.Double() == 0 && packet.U32() == 0 && packet.Text() == ""
                            && packet.Text() == "W3ABC" && !packet.Bool() && packet.Byte() == 0xaa,
                            "Direct call was encoded as a received line or a regular Reply.");
                    }
                });
                Run("Monitoring accepts only fresh visible target CQ, 73 or RRR", () => {
                    foreach (string allowed in new[] { "CQ", "73", "RRR" })
                    {
                        List<string> commands;
                        AutoCqEngine engine = MonitorActive(out commands);
                        engine.AddQueuedCall("DL3ZZB", At);
                        engine.OnDecode(D(At, "W3ABC", "FN20"), At.AddSeconds(14));
                        foreach (string ignored in new[] { "CQ DL1ZZC JO62", "CQ JA1ZZA PM95", "W7ZZD JA2ZZB RR73", "QRZ JA2ZZB" })
                        {
                            DecodedMessage decode = D(ignored.StartsWith("CQ JA1") ? At.AddMinutes(-1) : At, "JA2ZZB", "73");
                            decode.Message = ignored; decode.IsNew = false;
                            engine.OnDecode(decode, At.AddSeconds(14));
                        }
                        var target = D(At, "JA2ZZB", allowed);
                        target.Message = allowed == "CQ" ? "CQ JA2ZZB PM95" : "W7ZZD JA2ZZB " + allowed;
                        engine.OnDecode(target, At.AddSeconds(14.2));
                        engine.Tick(At.AddSeconds(14.3), true);
                        Assert(engine.Monitoring && engine.HoldNativeSelection && commands.Count == 0,
                            "An unrelated, stale, hidden or unsupported decode triggered transmission.");
                        target.IsNew = false;
                        engine.OnDecode(target, At.AddSeconds(14.4));
                        engine.Tick(At.AddSeconds(14.5), true);
                        Assert(commands.SequenceEqual(new[] { "REPLY " + target.Message }), "The visible target was skipped.");
                    }
                });
                Run("Monitoring returns to RX at the limit and can then select a different target", () => {
                    List<string> commands;
                    AutoCqEngine engine = MonitorActive(out commands);
                    var target = CqD(At.AddSeconds(15), "JA2ZZB");
                    target.IsNew = false;
                    engine.OnDecode(target, At.AddSeconds(29));
                    engine.Tick(At.AddSeconds(29.1), true);
                    engine.OnStatus(S(true, "JA2ZZB"), At.AddSeconds(29.2));
                    engine.OnLine(Tx(At.AddSeconds(30), "JA2ZZB", "-15"), At.AddSeconds(30));
                    engine.OnLine(Tx(At.AddSeconds(60), "JA2ZZB", "-15"), At.AddSeconds(60));
                    engine.OnLine(DecodeFinished(At.AddSeconds(89)), At.AddSeconds(89));
                    engine.Tick(At.AddSeconds(89.1), true);
                    engine.OnLine("Halt Tx triggered at RX: UDP request", At.AddSeconds(89.2));
                    engine.OnStatus(S(false, "JA2ZZB"), At.AddSeconds(89.3));
                    engine.Tick(At.AddSeconds(89.4), true);
                    engine.Tick(At.AddSeconds(89.5), true);
                    Assert(engine.Monitoring && commands.SequenceEqual(new[] { "REPLY CQ JA2ZZB FN20", "HALT" }),
                        "The retry limit started CQ or stopped monitoring.");
                    target = CqD(At.AddSeconds(90), "JA3ZZC");
                    target.IsNew = false;
                    engine.OnDecode(target, At.AddSeconds(104));
                    engine.Tick(At.AddSeconds(104.2), true);
                    Assert(commands.Last() == "REPLY CQ JA3ZZC FN20", "Monitoring did not resume after the failed call.");
                });
                Run("Monitoring settles a logged exchange before stopping at the deadline", () => {
                    List<string> commands;
                    AutoCqEngine engine = MonitorActive(out commands, 1);
                    var target = CqD(At.AddSeconds(15), "JA2ZZB");
                    target.IsNew = false;
                    engine.OnDecode(target, At.AddSeconds(29));
                    engine.Tick(At.AddSeconds(29.1), true);
                    engine.OnStatus(S(true, "JA2ZZB"), At.AddSeconds(29.2));
                    engine.OnLine(Tx(At.AddSeconds(60), "JA2ZZB", "RR73"), At.AddSeconds(60));
                    engine.OnLine("QSO logged: JA2ZZB", At.AddSeconds(60));
                    engine.Tick(At.AddSeconds(61), true);
                    Assert(engine.Monitoring && engine.Finishing && commands.Count == 1, "The deadline interrupted the exchange.");
                    engine.OnLine(Rx(At.AddSeconds(75), "JA2ZZB", "73"), At.AddSeconds(89));
                    engine.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Autolog", At.AddSeconds(89.1));
                    engine.OnStatus(S(false, "JA2ZZB"), At.AddSeconds(89.2));
                    engine.Tick(At.AddSeconds(89.3), true);
                    Assert(!engine.Armed && engine.ConfirmedCount == 1 && commands.Count == 1, "Timer queued a new call after the target completed.");
                });
                Run("Monitoring one per band uses exact, mask and DXCC targets and restores completed targets from ADIF", () => {
                    var options = new AutoCqOptions { MonitorDxcc = new[] { "JA" }, MonitorCalls = new[] { "w1aw" },
                        MonitorMasks = new[] { "k?abc", "EA8*" } };
                    options.Validate();
                    string adif = "<ADIF_VER:5>3.1.6<EOH><COMMENT:12><call:3>X0A <CALL:6>JA1ZZA<BAND:3>20m<EOR>"
                        + "<CALL:5>EA8ZZ<FREQ:6>14.074<EOR><CALL:5>K2ABC<BAND:3>40M<EOR>"
                        + "<CALL:4>W1AW<BAND:3>20m<station_callsign:5>K8ABC<EOR>"
                        + "<CALL:4>W1AW<BAND:3>20m<swl:1>Y<EOR><CALL:4>W1AW<BAND:3>20m";
                    var worked = JtdxAdif.WorkedCallsFromText(adif, JtdxAdif.Band(14074000), "K1ABC");
                    Assert(worked.SetEquals(new[] { "JA1ZZA", "EA8ZZ" }), "ADIF band, station or incomplete record was misread.");
                    var targets = new MonitorTargets(options, call => call.StartsWith("JA") ? "JA" : null, worked);
                    Assert(!targets.CanTry("JA2ZZB") && !targets.CanTry("EA8A") && !targets.CanTry("DL1ZZA")
                        && targets.CanTry("W1AW") && targets.CanTry("K2ABC") && !targets.CanTry("K22ABC"),
                        "Matching or completion of one target per band failed.");
                    targets.Complete("K2ABC");
                    Assert(!targets.CanTry("K3ABC") && targets.CanTry("W1AW"), "A QSO did not satisfy its mask.");
                    options.MonitorOnePerBand = false;
                    Assert(new MonitorTargets(options, call => "JA", worked).CanTry("JA2ZZB"), "The checkbox could not disable ADIF limits.");
                    var store = new OperatorStateStore(Fixture());
                    store.Save(options, new WaitingStation[0]);
                    var saved = store.Load().Options;
                    Assert(!saved.MonitorOnePerBand && saved.MonitorDxcc.SequenceEqual(options.MonitorDxcc)
                        && saved.MonitorCalls.SequenceEqual(new[] { "W1AW" }) && saved.MonitorMasks.SequenceEqual(options.MonitorMasks),
                        "Monitoring targets did not survive a restart.");
                });
                Run("Native AutoSelect is adopted after final 73 without a competing Reply", () => {
                    var engine = new AutoCqEngine { UseNativeSelection = true };
                    var commands = new List<string>();
                    engine.RequestCq = () => commands.Add("CQ");
                    engine.RequestReply = d => commands.Add("REPLY");
                    engine.RequestHalt = () => commands.Add("HALT");
                    engine.OnStatus(S(true, "W1AW"), At);
                    engine.Start(At);
                    engine.OnLine(Tx(At, "W1AW", "RR73"), At);
                    engine.OnLine("QSO logged: W1AW", At);
                    Assert(engine.HoldNativeSelection, "Premature native selection was allowed");
                    engine.OnLine(Rx(At.AddSeconds(15), "W1AW", "73"), At.AddSeconds(29));
                    Assert(!engine.HoldNativeSelection, "Native selection was not released at final 73");
                    engine.OnStatus(S(true, "W3ABC"), At.AddSeconds(29.1));
                    engine.Tick(At.AddSeconds(30), true);
                    Assert(engine.Armed && engine.Status.DxCall == "W3ABC" && commands.Count == 0,
                        "AutoCQ competed with the confirmed native handoff");
                });
                Run("An early native handoff cannot transmit during final-73 wait", () => {
                    var engine = new AutoCqEngine { UseNativeSelection = true };
                    int halts = 0;
                    engine.RequestHalt = () => halts++;
                    engine.OnStatus(S(true, "W1AW"), At);
                    engine.Start(At);
                    engine.OnLine(Tx(At, "W1AW", "RR73"), At);
                    engine.OnLine("QSO logged: W1AW", At);
                    engine.OnStatus(S(true, "W3ABC"), At.AddSeconds(29));
                    Assert(!engine.Armed && halts == 1, "An early native handoff was adopted");
                });
                Run("Native CQ selection gets its RX opportunity before the queue takes over", () => {
                    List<string> commands;
                    AutoCqEngine engine = SearchActive(out commands);
                    engine.Options.SearchEnabled = false;
                    engine.UseNativeSelection = true;
                    engine.OnLine(Tx(At, "CQ", "FN20"), At);
                    engine.OnDecode(D(At.AddSeconds(15), "W2ABC", "FN20"), At.AddSeconds(29));
                    engine.Tick(At.AddSeconds(29.9), true);
                    Assert(commands.Count == 0, "Queue preempted the native RX window");
                    engine.OnStatus(S(true, "W2ABC"), At.AddSeconds(30));
                    engine.Tick(At.AddSeconds(30.1), true);
                    Assert(engine.Armed && commands.Count == 0, "Queue reissued an already selected native call");
                });
                Run("CQ search threshold leaves TX enabled for native AutoSelect in the response window", () => {
                    List<string> commands;
                    AutoCqEngine engine = SearchActive(out commands);
                    engine.UseNativeSelection = true;
                    RadioStatus transmitting = S(true, "");
                    transmitting.Transmitting = true;
                    engine.OnLine(Tx(At, "CQ", "FN20"), At);
                    engine.OnStatus(transmitting, At.AddSeconds(1));
                    engine.OnStatus(S(true, ""), At.AddSeconds(14));
                    engine.Tick(At.AddSeconds(14), true);
                    Assert(commands.Count == 0, "Module halted TX before native AutoSelect could run");
                    engine.OnDecode(D(At.AddSeconds(15), "W3ABC", "FN20"), At.AddSeconds(28));
                    engine.OnStatus(S(true, "W3ABC"), At.AddSeconds(29));
                    engine.Tick(At.AddSeconds(30), true);
                    Assert(engine.Armed && commands.Count == 0, "Native reply was replaced at the search threshold");
                });
                Run("Native CQ response window falls back to the filtered CQ when no caller is selected", () => {
                    List<string> commands;
                    AutoCqEngine engine = SearchActive(out commands);
                    engine.UseNativeSelection = true;
                    RadioStatus transmitting = S(true, "");
                    transmitting.Transmitting = true;
                    engine.OnLine(Tx(At, "CQ", "FN20"), At);
                    engine.OnStatus(transmitting, At.AddSeconds(1));
                    engine.OnStatus(S(true, ""), At.AddSeconds(14));
                    engine.Tick(At.AddSeconds(14), true);
                    DecodedMessage replay = CqD(At.AddSeconds(15), "W3ABC");
                    replay.IsNew = false;
                    engine.OnDecode(replay, At.AddSeconds(29));
                    engine.Tick(At.AddSeconds(30), true);
                    Assert(commands.SequenceEqual(new[] {"REPLY W3ABC"}), "Search threshold failed with TX left enabled for the native RX period");
                });
                Run("Native CQ at the TX boundary leaves time for a late AutoSelect decision", () => {
                    List<string> commands;
                    AutoCqEngine engine = SearchActive(out commands);
                    engine.UseNativeSelection = true;
                    RadioStatus transmitting = S(true, "");
                    transmitting.Transmitting = true;
                    engine.OnStatus(transmitting, At);
                    engine.OnLine(Tx(At, "CQ", "FN20"), At.AddMilliseconds(728));
                    RadioStatus receiving = S(true, "");
                    receiving.Decoding = true;
                    engine.OnStatus(receiving, At.AddSeconds(14));
                    engine.Tick(At.AddSeconds(14), true);
                    transmitting.Decoding = true;
                    engine.OnStatus(transmitting, At.AddSeconds(30));
                    engine.OnLine(Tx(At.AddSeconds(30), "CQ", "FN20"), At.AddSeconds(30.101));
                    engine.Tick(At.AddSeconds(30.238), true);
                    Assert(engine.Armed && commands.Count == 0, "Late native CQ caused a false stop");
                    RadioStatus chosen = S(true, "W3ABC");
                    chosen.Transmitting = true;
                    engine.OnStatus(chosen, At.AddSeconds(31.164));
                    engine.OnLine(Tx(At.AddSeconds(31), "W3ABC", "-10"), At.AddSeconds(31.2));
                    engine.Tick(At.AddSeconds(31.3), true);
                    Assert(engine.Armed && engine.Status.DxCall == "W3ABC" && commands.Count == 0,
                        "Module did not adopt the late native selection");
                });
                Run("A full native CQ that starts before decoding ends still leads to a filtered search", () => {
                    List<string> commands;
                    AutoCqEngine engine = SearchActive(out commands);
                    engine.UseNativeSelection = true;
                    RadioStatus transmitting = S(true, "");
                    transmitting.Transmitting = true;
                    engine.OnStatus(transmitting, At);
                    engine.OnLine(Tx(At, "CQ", "FN20"), At.AddMilliseconds(200));
                    engine.OnStatus(S(true, ""), At.AddSeconds(14));
                    engine.Tick(At.AddSeconds(14), true);
                    engine.OnStatus(transmitting, At.AddSeconds(30));
                    engine.OnLine(Tx(At.AddSeconds(30), "CQ", "FN20"), At.AddSeconds(30.2));
                    var cq = CqD(At.AddSeconds(15), "W2ABC");
                    cq.IsNew = false;
                    engine.OnDecode(cq, At.AddSeconds(31));
                    engine.Tick(At.AddSeconds(31), true);
                    Assert(engine.Armed && commands.Count == 0, "Search interrupted an active native frame");
                    engine.OnStatus(S(true, ""), At.AddSeconds(44));
                    engine.Tick(At.AddSeconds(44), true);
                    Assert(engine.Armed && commands.SequenceEqual(new[] { "REPLY W2ABC" }),
                        "Search was lost after a delayed native CQ");
                });
                Run("An unexpected CQ still cannot override a pending final 73", () => {
                    List<string> commands;
                    AutoCqEngine engine = Active(out commands);
                    engine.UseNativeSelection = true;
                    engine.OnLine(Tx(At, "W1AW", "RR73"), At);
                    engine.OnLine("QSO logged: W1AW", At);
                    engine.OnLine(Tx(At.AddSeconds(30), "CQ", "FN20"), At.AddSeconds(30.1));
                    Assert(!engine.Armed && commands.SequenceEqual(new[] { "HALT" }),
                        "Native CQ bypassed final-73 protection");
                });
                Run("An off-state native transmission is shown without arming the module", () => {
                    using (MainForm form = new MainForm(true))
                    {
                        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                        var engine = (AutoCqEngine)typeof(MainForm).GetField("engine", flags).GetValue(form);
                        var transmitting = S(true, "W3ABC");
                        transmitting.Transmitting = true;
                        engine.OnStatus(transmitting, At);
                        typeof(MainForm).GetMethod("UpdateUi", flags).Invoke(form, null);
                        var radio = (System.Windows.Forms.Label)typeof(MainForm).GetField("radio", flags).GetValue(form);
                        var session = (System.Windows.Forms.Label)typeof(MainForm).GetField("sessionInfo", flags).GetValue(form);
                        Assert(radio.Text.Contains("передача (самостоятельно)") && session.Text == "AutoCQ выключен"
                            && !engine.Armed, "Independent radio TX was hidden or armed the module");
                    }
                });
                report.WriteLine("\nИтог: " + passed + " проверок пройдено. Реальные QSO и трансивер не тестировались.");
                Console.WriteLine("ALL " + passed + " PASSED");return 0;
            }
            catch(Exception e){report.WriteLine("FAIL  "+e);Console.WriteLine(e);return 1;}
        }
    }
}
