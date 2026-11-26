using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Serialization;
using JtdxAutoCq;

sealed class TestWorkspace : IDisposable
{
    static readonly string Parent = Path.GetFullPath(Directory.GetCurrentDirectory());
    static readonly string Root = Path.Combine(Parent, "test-fixtures-" + Guid.NewGuid().ToString("N"));
    public static string Create()
    { string path = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    public void Dispose()
    {
        if (Path.GetDirectoryName(Path.GetFullPath(Root)) != Parent) throw new IOException("Invalid test workspace.");
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}

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
    static string ManualSelection(DateTime time, string call)
    { return time.ToString("yyyyMMdd_HHmmss.fff") + "(0)  double click on call processed: " + call + "; EnableTx On, AutoTx On"; }
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
    static AutoCqEngine TerminalHaltRace(out List<string> commands, bool monitorMode, string mode, bool logged,
        AwardSearch awards = null, ExpeditionSearch expeditions = null)
    {
        AutoCqEngine e = monitorMode ? MonitorActive(out commands) : Active(out commands);
        e.Stop("fixture", false);
        RadioStatus initial = S(!monitorMode, monitorMode ? "" : "W1AW"); initial.Mode = mode;
        e.OnStatus(initial, At); e.Start(At, awards, expeditions);
        if (monitorMode)
        {
            e.OnLine(ManualSelection(At.AddSeconds(1), "W1AW"), At.AddSeconds(1));
            RadioStatus selected = S(true, "W1AW"); selected.Mode = mode; e.OnStatus(selected, At.AddSeconds(1.1));
        }
        double slot = mode == "FT4" ? 7.5 : 15;
        e.OnLine(Tx(At.AddSeconds(2 * slot), "W1AW", "RR73").Replace("FT8:", mode + ":"), At.AddSeconds(2 * slot));
        if (logged) e.OnLine("QSO logged: W1AW", At.AddSeconds(2 * slot + 0.1));
        var final = D(At.AddSeconds(3 * slot), "W1AW", "73"); final.Mode = mode == "FT4" ? ":" : "~";
        e.OnDecode(final, At.AddSeconds(4 * slot - 0.75));
        e.OnLine(DecodeFinished(At.AddSeconds(4 * slot + 0.711)), At.AddSeconds(4 * slot + 0.711));
        e.Tick(At.AddSeconds(4 * slot + 0.812), true);
        Assert(commands.SequenceEqual(new[] { "HALT" }), "The race fixture did not request its terminal Halt TX");
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
    { return TestWorkspace.Create(); }

    static ExpeditionDialog ExpeditionWindow(ExpeditionOptions options)
    {
        var form = new ExpeditionDialog(options, (dialog, values) => values, data => { }, data => { }, () => { }, () => { });
        typeof(Control).GetMethod("CreateControl", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(bool) }, null).Invoke(form, new object[] { true });
        form.PerformLayout();
        return form;
    }

    static ExpeditionActivity ParkSpot(string call, double frequency, DateTime spotted)
    {
        return new ExpeditionActivity { Source = ExpeditionSource.Pota, Call = call,
            Place = "US-1234", Mode = "FT8", Frequency = frequency, SpottedUtc = spotted };
    }

    [STAThread]
    static int Main(string[] args)
    {
        using (var workspace = new TestWorkspace())
        using (report = new StreamWriter(args[0], false, new UTF8Encoding(true)))
        {
            report.AutoFlush = true;
            report.WriteLine("JTDX AutoCQ — офлайн-проверка " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm zzz"));
            report.WriteLine("Рабочий профиль JTDX и трансивер в этих тестах не используются.\n");
            try
            {
                UpdateTests.RunAll(Run, args.Length > 1 ? args[1] : null);
                BugReportTests.RunAll(Run);
                OperatorToolsTests.RunAll(Run);
                Run("Expedition ages, new rows and expired rows preserve the native list viewport", () => {
                    var catalog = new ExpeditionCatalog();
                    var engine = new AutoCqEngine();
                    ExpeditionActivity[] spots = Enumerable.Range(0, 48).Select(index => ParkSpot(
                        "W1T" + (char)('A' + index / 26) + (char)('A' + index % 26) + "/P",
                        14074000, At.AddMilliseconds(-index * 100))).ToArray();
                    catalog.Replace(ExpeditionSource.Pota, new ExpeditionFeedData { Activities = spots });
                    using (var form = ExpeditionWindow(new ExpeditionOptions()))
                    {
                        var view = (ListView)typeof(ExpeditionDialog).GetField("results", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                        form.Render(catalog, engine, false, "", At);
                        ListViewItem top = view.Items[12];
                        string selected = view.Items[14].Text, next = view.Items[13].Text, age = top.SubItems[4].Text;
                        view.TopItem = top;
                        view.Items[14].Selected = true;
                        Assert(Object.ReferenceEquals(view.TopItem, top) && view.SelectedItems.Count == 1, "Could not set the test viewport");
                        form.Render(catalog, engine, false, "", At.AddSeconds(2));
                        Assert(Object.ReferenceEquals(view.TopItem, top) && view.TopItem.SubItems[4].Text != age
                            && view.SelectedItems[0].Text == selected, "A timestamp change rebuilt or scrolled the list");

                        var added = ParkSpot("W2ABC/P", 14074000, At.AddSeconds(1));
                        catalog.Replace(ExpeditionSource.Pota, new ExpeditionFeedData { Activities = spots.Concat(new[] { added }).ToArray() });
                        form.Render(catalog, engine, false, "", At.AddSeconds(3));
                        Assert(view.Items[0].Text == added.Call && view.TopItem.Text == top.Text
                            && view.SelectedItems[0].Text == selected, "An arrival above the viewport moved the visible station");

                        catalog.Replace(ExpeditionSource.Pota, new ExpeditionFeedData { Activities = spots.Where(spot => spot.Call != top.Text).ToArray() });
                        form.Render(catalog, engine, false, "", At.AddSeconds(4));
                        Assert(view.TopItem.Text == next && view.SelectedItems[0].Text == selected, "An expired anchor did not retain its nearest station");
                    }
                });
                Run("The expedition list band filter works without personal targets and during a search", () => {
                    var catalog = new ExpeditionCatalog();
                    var engine = new AutoCqEngine();
                    engine.OnStatus(S(false, ""), At);
                    catalog.Replace(ExpeditionSource.Pota, new ExpeditionFeedData { Activities = new[] {
                        ParkSpot("W1AAA", 7074000, At), ParkSpot("W2ABC", 14074000, At),
                    } });
                    catalog.Replace(ExpeditionSource.Ng3k, new ExpeditionFeedData { Activities = new[] {
                        new ExpeditionActivity { Source = ExpeditionSource.Ng3k, Call = "T88AAA", Place = "Fixture",
                            Bands = "80m 40m", Mode = "FT8", StartUtc = At.AddDays(1), EndUtc = At.AddDays(5) },
                        new ExpeditionActivity { Source = ExpeditionSource.Ng3k, Call = "T88BBB", Place = "Fixture",
                            Mode = "FT8", StartUtc = At.AddDays(1), EndUtc = At.AddDays(5) },
                    } });
                    using (var form = ExpeditionWindow(new ExpeditionOptions { ListBand = "40m" }))
                    {
                        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                        var picker = (ComboBox)typeof(ExpeditionDialog).GetField("listBand", flags).GetValue(form);
                        var view = (ListView)typeof(ExpeditionDialog).GetField("results", flags).GetValue(form);
                        form.Render(catalog, engine, true, "", At);
                        Assert(picker.Enabled && (string)picker.SelectedItem == "40m" && picker.Items.Contains("20m")
                            && view.Items.Cast<ListViewItem>().Select(item => item.Text).SequenceEqual(new[] { "W1AAA", "T88AAA" }),
                            "The band picker depended on personal targets or included unrelated results");
                        engine.Start(At, null, new ExpeditionSearch(form.Options, catalog, call => null));
                        Assert(engine.ExpeditionTarget("W2ABC", At) != null, "Browsing another band changed search eligibility on JTDX's band");
                        picker.SelectedItem = "20m";
                        form.Render(catalog, engine, false, "", At);
                        Assert(picker.Enabled && view.Items.Count == 1 && view.Items[0].Text == "W2ABC"
                            && engine.Status.Frequency == 14074000, "Band browsing was locked or changed the radio during a search");
                        picker.SelectedIndex = 0;
                        form.Render(catalog, engine, false, "", At);
                        Assert(view.Items.Count == 4 && view.Items.Cast<ListViewItem>().Any(item => item.Text == "T88BBB"),
                            "All bands did not restore both spots and the announcement without a band");
                    }
                });
                Run("Additional spot sources keep the advertised frequencies, references and calendar dates", () => {
                    const string news = @"[{""from"":""2025-11-07"",""to"":""2025-11-09"",""cal"":""V6AAA, V6BBB: Chuuk (OC-011)""},
                        {""from"":""2025-11-07"",""to"":""2025-11-09"",""cal"":""W2ABC: special callsign""}]";
                    var announcements = ExpeditionFeeds.Parse(ExpeditionSource.DxNews, news, At).Activities;
                    Assert(announcements.Select(item => item.Call).SequenceEqual(new[] { "V6AAA", "V6BBB" })
                        && announcements[0].Announced && announcements[0].EndUtc == At.Date.AddDays(3), "425 DX News dates or calls changed");
                    const string dx = @"[{""dx_call"":""V6AAA"",""frequency"":14080,""time"":""2025-11-07T11:58:00"",
                        ""info"":""FT4 IOTA OC-011""},{""dx_call"":""W2ABC"",""frequency"":14074.0,""time"":""2025-11-07T11:59:00"",
                        ""info"":""CQ""}]";
                    var spot = ExpeditionFeeds.Parse(ExpeditionSource.DxSummit, dx, At).Activities.Single();
                    Assert(spot.Call == "V6AAA" && spot.Mode == "FT4" && spot.Place == "OC-011" && spot.ReferenceProgram == "IOTA"
                        && spot.Frequency == 14080000 && spot.SpottedUtc == At.AddMinutes(-2), "DX Summit inferred a mode or lost the island reference");
                    const string sota = @"{""activations"":[{""callsign"":""HB0AAA/P"",""type"":""sota"",""reference"":""HB0/LI-004"",
                        ""frequency"":""14.074"",""mode"":""FT8"",""spotTime"":""2025-11-07T11:59:00Z""}]}";
                    var summit = ExpeditionFeeds.Parse(ExpeditionSource.Sota, sota, At).Activities.Single();
                    Assert(summit.Frequency == 14074000 && summit.Place == "HB0/LI-004", "SOTA frequency units or reference changed");
                    const string bunkers = @"[{""call"":""ON1AAA/P"",""type"":""Live"",""freq"":7.0475,""mode"":""FT4"",
                        ""time"":""2025-11-07T11:59:30Z"",""references"":[{""reference"":""B/ON-0051"",""dxcc"":209}]},
                        {""call"":""ON2AAA/P"",""type"":""QRT"",""freq"":7.0475,""mode"":""FT4"",
                        ""time"":""2025-11-07T11:59:40Z"",""references"":[{""reference"":""B/ON-0002""}]}]";
                    var bunker = ExpeditionFeeds.Parse(ExpeditionSource.Wwbota, bunkers, At).Activities.Single();
                    Assert(bunker.Frequency == 7047500 && bunker.Place == "B/ON-0051" && bunker.Dxcc == 209,
                        "WWBOTA lost the activation or treated a QRT as live");
                });
                Run("LogHX cluster batches keep recent spots, expire them, and respect the source switch", () => {
                    const int recordBytes = 267;
                    byte[] packet = new byte[recordBytes * 2];
                    Encoding.ASCII.GetBytes("R0AAA/P").CopyTo(packet, 0);
                    Encoding.ASCII.GetBytes("FT8 RDA CK-04").CopyTo(packet, 44);
                    Encoding.ASCII.GetBytes("DIGI").CopyTo(packet, 197);
                    BitConverter.GetBytes(At.AddMinutes(-1).ToOADate()).CopyTo(packet, 230);
                    BitConverter.GetBytes((uint)14074000).CopyTo(packet, 246);
                    Array.Copy(packet, 0, packet, recordBytes, recordBytes);
                    BitConverter.GetBytes(At.AddMinutes(-16).ToOADate()).CopyTo(packet, recordBytes + 230);
                    string folder = TestWorkspace.Create();
                    string path = Path.Combine(folder, "SpotsDataList.hx");
                    File.WriteAllBytes(path, packet);
                    var batch = LogHxSpotFeed.ReadBatch(path, At, true);
                    ExpeditionActivity spot = batch.Activities.Single();
                    Assert(spot.Call == "R0AAA/P" && spot.Band == "20m" && spot.Mode == "FT8" && spot.Place == "CK-04",
                        "Packed LogHX record, frequency or time was misread");

                    var catalog = new ExpeditionCatalog();
                    catalog.MergeLogHx(batch, At);
                    catalog.MergeLogHx(LogHxSpotFeed.Parse(packet, At), At.AddSeconds(2));
                    catalog.MergeLogHx(new ExpeditionFeedData(), At.AddSeconds(3));
                    catalog.Replace(ExpeditionSource.WantedRda, new ExpeditionFeedData { RdaRanks = new Dictionary<string, int> { { "CK-04", 1 } } });
                    var enabled = new ExpeditionSearch(new ExpeditionOptions { LogHxSpots = true }, catalog, call => null);
                    var disabled = new ExpeditionSearch(new ExpeditionOptions { LogHxSpots = false }, catalog, call => null);
                    Assert(catalog.ForCall("R0AAA/P").Count() == 1 && enabled.Match("R0AAA/P", "20m", "FT8", At).Reason.Contains("Редкий район")
                        && disabled.Match("R0AAA/P", "20m", "FT8", At) == null, "LogHX batching or its source switch did not apply");
                    File.Delete(path);
                    var gap = LogHxSpotFeed.ReadBatch(path, At, true);
                    Assert(gap.Activities.Length == 0, "A batch gap was treated as new spots");
                    catalog.MergeLogHx(gap, At);
                    Assert(catalog.ForCall("R0AAA/P").Count() == 1, "A gap discarded a fresh cached spot");
                    File.WriteAllBytes(path, packet);
                    Assert(LogHxSpotFeed.ReadBatch(path, At, true).Activities.Length == 1, "The next LogHX batch was missed");
                    File.Delete(path);
                    bool missing = false;
                    try { LogHxSpotFeed.ReadBatch(path, At, false); }
                    catch (FileNotFoundException) { missing = true; }
                    Assert(missing, "An absent LogHX server and batch looked like a live feed");
                    catalog.MergeLogHx(gap, At.AddMinutes(15));
                    Assert(!catalog.ForCall("R0AAA/P").Any(), "An old LogHX spot was retained");
                });
                Run("Expedition source failures distinguish a network timeout from a missing LogHX file", () => {
                    Assert(MainForm.DescribeFeedFailure(new WebException("Timeout", WebExceptionStatus.Timeout)) == "тайм-аут"
                        && MainForm.DescribeFeedFailure(new FileNotFoundException()) == "файл спотов не найден"
                        && MainForm.DescribeFeedFailure(new InvalidDataException()) == "ошибка формата данных",
                        "The source failed without an actionable reason");
                });
                Run("DX activity announcements require an exact advertised call and valid UTC dates", () => {
                    const string html = "<h1>Announced DX Operations</h1>"
                        + "<p>Nov 7-9, 2025<br>DXCC: Micronesia<br>Callsign: <b>V6</b><br>Info: By JA1AAA as V6/JA1AAA; 40-10m; CW FT8</p>"
                        + "<p>Dec 29, 2025-Jan 3, 2026<br>DXCC: Palau<br>Callsign: T88AAA<br>Info: 20-10m; FT4</p>"
                        + "<p>Nov 7-8, 2025<br>DXCC: Palau<br>Callsign: T88ABC<br>Info: HF; SSB only</p>";
                    var data = ExpeditionFeeds.Parse(ExpeditionSource.Ng3k, html, At);
                    var catalog = new ExpeditionCatalog();
                    catalog.Replace(ExpeditionSource.Ng3k, data);
                    var search = new ExpeditionSearch(new ExpeditionOptions(), catalog, call => null);
                    Assert(data.Activities.Select(row => row.Call).SequenceEqual(new[] { "V6/JA1AAA", "T88AAA" }),
                        "A prefix, home call or an SSB-only announcement became a digital target");
                    Assert(search.Match("V6/JA1AAA", "20m", "FT8", At) != null
                        && search.Match("V6/JA1AAA", "20m", "FT4", At) == null
                        && search.Match("V6/JA1AAA", "80m", "FT8", At) == null
                        && search.Match("V6/JA1AAA", "20m", "FT8", At.AddDays(3)) == null
                        && search.Match("T88AAA", "20m", "FT4", At) == null
                        && search.Match("T88AAA", "20m", "FT4", new DateTime(2026, 1, 3, 23, 59, 0, DateTimeKind.Utc)) != null,
                        "Announcement dates, modes or ranges were ignored");
                });
                Run("POTA, WWFF and CQDX preserve spot timestamps, frequency units and exact calls", () => {
                    const string parks = @"[null,{""activator"":""W2ABC/P"",""frequency"":""14074.0"",""reference"":""US-1234"",
                        ""mode"":""FT8"",""spotTime"":""2025-11-07T11:58:00""},
                        {""activator"":""W3ABC"",""frequency"":""7074"",""reference"":""US-2345"",
                        ""mode"":""FT8"",""spotTime"":""2025-11-07T11:20:00""}]";
                    long epoch = (long)(At - new DateTime(1970, 1, 1)).TotalSeconds;
                    string nature = @"[{""activator"":""DL1AAA/P"",""frequency_khz"":10136.0,""reference"":""DLFF-0123"",
                        ""mode"":""FT8"",""spot_time"":" + (epoch - 180) + "}]";
                    string cluster = @"{""groups"":[{""dxCall"":""V6AAA"",""frequency"":14080425,""mode"":""FT4"",
                        ""dxDxcc"":173,""lastSeen"":" + (epoch - 98) + "}]}";
                    ExpeditionActivity park = ExpeditionFeeds.Parse(ExpeditionSource.Pota, parks, At).Activities.Single();
                    ExpeditionActivity area = ExpeditionFeeds.Parse(ExpeditionSource.Wwff, nature, At).Activities.Single();
                    ExpeditionActivity dx = ExpeditionFeeds.Parse(ExpeditionSource.DxSpots, cluster, At).Activities.Single();
                    Assert(park.Call == "W2ABC/P" && park.Band == "20m" && park.SpottedUtc == At.AddMinutes(-2)
                        && area.Place == "DLFF-0123" && area.Frequency == 10136000 && area.SpottedUtc == At.AddMinutes(-3)
                        && dx.Mode == "FT4" && dx.Frequency == 14080425 && dx.Dxcc == 173,
                        "One of the feeds changed the band, time, callsign, or program reference");
                });
                Run("RDA matches the activator, applies the Wanted lists, and expires the previous location", () => {
                    const string html = "<h1>RDA CLUSTER</h1><div class=\"activator_0\"><A class=\"rdalnkc\">RØAAA/P</A></div><table>"
                        + "<tr><td></td><td>23:58</td><td></td><td>$</td><td>R2AAA</td><td>14074.0</td><td>CK-04</td><td>DIGI</td><td>FT8</td></tr>"
                        + "<tr><td></td><td>00:02</td><td></td><td>$</td><td>R2AAA</td><td>7074.0</td><td>KK-10</td><td>DIGI</td><td>FT8</td></tr></table>";
                    DateTime now = At.Date.AddMinutes(3);
                    var data = ExpeditionFeeds.Parse(ExpeditionSource.Rda, html, now);
                    Assert(data.Activities.Length == 2 && data.Activities.All(row => row.Call == "R0AAA/P")
                        && data.Activities[1].SpottedUtc == now.AddMinutes(-5), "RDA spotter or wrong date used for the activator");
                    var catalog = new ExpeditionCatalog();
                    catalog.Replace(ExpeditionSource.Rda, data);
                    string wantedPage = "<table>" + String.Join("", Enumerable.Range(1, 100).Select(rank =>
                        "<tr><td>" + rank + "</td><td>" + (rank == 1 ? "CK-04" : "AA-" + (rank - 1).ToString("00"))
                        + "</td><td>District</td><td>0</td><td></td><td></td></tr>")) + "</table>";
                    catalog.Replace(ExpeditionSource.WantedRda, ExpeditionFeeds.Parse(ExpeditionSource.WantedRda, wantedPage, now));
                    var search = new ExpeditionSearch(new ExpeditionOptions(), catalog, call => null);
                    Assert(search.Match("R0AAA/P", "20m", "FT8", now) == null
                        && search.Match("R0AAA/P", "40m", "FT8", now).Reason.Contains("KK-10")
                        && search.Match("R0AAA", "40m", "FT8", now) == null
                        && search.Match("R0AAA/P", "40m", "FT8", now.AddMinutes(15)) == null,
                        "Old location, bare call, or expired spot was eligible");
                    catalog.Replace(ExpeditionSource.Rda, new ExpeditionFeedData { Activities = new[] { data.Activities[1] } });
                    Assert(search.Match("R0AAA/P", "20m", "FT8", now).Reason.Contains("Редкий район"), "Wanted RDA could not be selected");
                });
                Run("Rare DXCC and personal band matching work with local calls and a refreshed ranking", () => {
                    var catalog = new ExpeditionCatalog();
                    string ranks = @"{""entities"":[{""adifNumber"":247,""prefix"":""1S"",""entityName"":""Spratly"",
                        ""rarityRank"":12,""isDeleted"":false},{""adifNumber"":400,""prefix"":""W"",""rarityRank"":1,""isDeleted"":true}]}";
                    catalog.Replace(ExpeditionSource.DxRanks, ExpeditionFeeds.Parse(ExpeditionSource.DxRanks, ranks, At));
                    var options = new ExpeditionOptions { MaxDxRank = 15, WantedDxcc = new[] { "JA" }, WantedBand = "40m" };
                    var search = new ExpeditionSearch(options, catalog, call => call.StartsWith("JA") ? "JA" : call == "9M0AAA" ? "1S" : "W");
                    Assert(search.Match("9M0AAA", "20m", "FT8", At).Reason.Contains("#12")
                        && search.Match("W2ABC", "20m", "FT8", At) == null
                        && search.Match("JA1AAA", "20m", "FT8", At) == null
                        && search.Match("JA1AAA", "40m", "FT8", At).Reason.Contains("Ваша цель"), "Ranking or band constraint was lost");
                    options.MaxDxRank = 1;
                    Assert(search.Match("9M0AAA", "20m", "FT8", At) != null, "The configuration of a running search changed");
                    catalog.Replace(ExpeditionSource.DxRanks, new ExpeditionFeedData());
                    Assert(search.Match("9M0AAA", "20m", "FT8", At) == null, "An updated source left old eligibility active");
                });
                Run("Expedition monitoring uses fresh FT8 or FT4 decodes and gives incoming calls priority", () => {
                    foreach (string mode in new[] { "FT8", "FT4" })
                    foreach (bool incoming in new[] { false, true })
                    {
                        var commands = new List<string>();
                        var engine = new AutoCqEngine { UseNativeSelection = true, CanCall = call => call != "T88ABC" };
                        engine.RequestReply = message => commands.Add("REPLY " + message.Message);
                        engine.RequestCallerReply = message => commands.Add("CALLER " + message.Message);
                        engine.RequestCq = () => commands.Add("CQ");
                        var status = S(false, ""); status.Mode = mode;
                        engine.OnStatus(status, At);
                        var catalog = new ExpeditionCatalog();
                        catalog.Replace(ExpeditionSource.Pota, new ExpeditionFeedData { Activities = new[] {
                            new ExpeditionActivity { Source = ExpeditionSource.Pota, Call = "W2ABC/P", Place = "US-1234", Mode = mode,
                                Frequency = 7074000, SpottedUtc = At.AddMinutes(-1) },
                            new ExpeditionActivity { Source = ExpeditionSource.Pota, Call = "W3ABC/P", Place = "US-2345", Mode = mode,
                                Frequency = 14074000, SpottedUtc = At.AddMinutes(-20) },
                        } });
                        catalog.Replace(ExpeditionSource.Ng3k, new ExpeditionFeedData { Activities = new[] { "V6AAA", "T88ABC" }
                            .Select(call => new ExpeditionActivity { Source = ExpeditionSource.Ng3k, Call = call, Place = "Fixture",
                                StartUtc = At.Date, EndUtc = At.AddDays(1), Mode = mode }).ToArray() });
                        engine.Start(At, null, new ExpeditionSearch(new ExpeditionOptions(), catalog, call => null));
                        double slot = mode == "FT8" ? 15 : 7.5;
                        DateTime heard = At.AddSeconds(slot), decoded = At.AddSeconds(2 * slot - 0.4);
                        engine.Tick(At.AddSeconds(2), true);
                        foreach (string call in new[] { "W2ABC/P", "W3ABC/P", "T88ABC" })
                        {
                            var cq = CqD(heard, call); cq.Mode = mode == "FT8" ? "~" : ":"; cq.IsNew = false;
                            engine.OnDecode(cq, decoded);
                        }
                        engine.Tick(decoded, true);
                        Assert(engine.Armed && engine.Monitoring && engine.HoldNativeSelection && commands.Count == 0,
                            "A spot, wrong band, stale classification or excluded call caused TX");
                        var target = CqD(heard, "V6AAA"); target.Mode = mode == "FT8" ? "~" : ":";
                        engine.OnDecode(target, decoded);
                        engine.Tick(decoded, true);
                        Assert(commands.Count == 0, "An expedition decode outside JTDX's visible replay caused a call");
                        target.IsNew = false;
                        engine.OnDecode(target, decoded);
                        if (incoming)
                        {
                            var caller = D(heard, "K2ABC", "FN20"); caller.Mode = target.Mode;
                            engine.OnDecode(caller, decoded);
                        }
                        engine.Tick(decoded, true);
                        Assert(commands.SequenceEqual(new[] { incoming ? "CALLER K1ABC K2ABC FN20" : "REPLY CQ V6AAA FN20" }),
                            mode + " did not choose the expected " + (incoming ? "incoming caller" : "expedition")
                                + ". Commands: " + String.Join(", ", commands));
                    }
                });
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
                Run("Radio changes stop automation and monitoring with the exact reason", () => {
                    var changes = new[] {
                        new { Frequency = 14074040UL, Mode = "FT8", TxFirst = true, Watchdog = false,
                            Reason = "Частота изменилась с 14,074000 до 14,074040 МГц (+40 Гц)." },
                        new { Frequency = 14073960UL, Mode = "FT8", TxFirst = true, Watchdog = false,
                            Reason = "Частота изменилась с 14,074000 до 14,073960 МГц (-40 Гц)." },
                        new { Frequency = 7074000UL, Mode = "FT8", TxFirst = true, Watchdog = false,
                            Reason = "Частота изменилась с 14,074000 до 7,074000 МГц (-7000000 Гц)." },
                        new { Frequency = 14074000UL, Mode = "FT4", TxFirst = true, Watchdog = false,
                            Reason = "Режим изменился с FT8 на FT4." },
                        new { Frequency = 14074000UL, Mode = "FT8", TxFirst = false, Watchdog = false,
                            Reason = "Период TX изменился с 00/30 на 15/45." },
                        new { Frequency = 14074000UL, Mode = "FT8", TxFirst = true, Watchdog = true,
                            Reason = "Сработал сторожевой таймер JTDX." },
                        new { Frequency = 14074040UL, Mode = "FT4", TxFirst = false, Watchdog = true,
                            Reason = "Частота изменилась с 14,074000 до 14,074040 МГц (+40 Гц). Режим изменился с FT8 на FT4. "
                                + "Период TX изменился с 00/30 на 7,5/22,5/37,5/52,5. Сработал сторожевой таймер JTDX." },
                    };
                    foreach (bool monitoring in new[] { false, true })
                    foreach (var change in changes)
                    {
                        List<string> commands;
                        AutoCqEngine engine = monitoring ? MonitorActive(out commands) : Active(out commands);
                        var notices = new List<string>();
                        OperatingSession finished = null;
                        engine.Notice = notices.Add;
                        engine.SessionFinished = session => finished = session;
                        RadioStatus changed = S(!monitoring, monitoring ? "" : "W1AW");
                        changed.Frequency = change.Frequency;
                        changed.Mode = change.Mode;
                        changed.TxFirst = change.TxFirst;
                        changed.Watchdog = change.Watchdog;
                        engine.OnStatus(changed, At.AddSeconds(1));
                        engine.OnStatus(changed, At.AddSeconds(2));
                        string expected = (monitoring ? "Мониторинг остановлен. " : "Автомат остановлен. ") + change.Reason;
                        Assert(!engine.Armed && commands.SequenceEqual(new[] { "HALT" }), "Radio change did not send a single halt");
                        Assert(notices.SequenceEqual(new[] { expected }), "Wrong notice: " + String.Join(" | ", notices));
                        Assert(finished != null && finished.StopReason == expected, "Session summary lost the exact stop reason");
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
                Run("A busy radio log is retried and the reader releases it before processing", () => {
                    string folder = Fixture();
                    string path = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMM") + "_ALL.TXT");
                    File.WriteAllText(path, "");
                    var tail = new LogTail(folder);
                    var lines = new List<string>();
                    using (var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
                    using (var text = new StreamWriter(writer))
                    {
                        text.WriteLine("QSO logged: W1AW");
                        text.Flush();
                        tail.Read(lines.Add);
                        Assert(lines.Count == 0, "Read from an exclusively held radio log");
                    }
                    tail.Read(line => {
                        using (var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
                            lines.Add(line);
                    });
                    Assert(lines.SequenceEqual(new[] { "QSO logged: W1AW" }), "Lost or repeated the delayed line");
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
                Run("An unworked caller precedes two historical repeats in the same RX frame", () => {
                    foreach (bool prioritizeRepeat in new[] { false, true })
                    {
                        var commands = new List<string>();
                        var e = new AutoCqEngine { UseNativeSelection = true };
                        int reads = 0;
                        e.WorkedCallsOnBandMode = (band, mode, own) => {
                            reads++;
                            Assert(band == "20m" && mode == "FT8" && own == "K1ABC", "History used the wrong QSO context");
                            return new[] { "W2ABC", "W3ABC" };
                        };
                        if (prioritizeRepeat) { e.Options.PriorityDxcc = new[] { "K" }; e.IsPriorityCall = call => call == "W2ABC"; }
                        e.RequestCallerReply = d => commands.Add(d.Message);
                        e.OnStatus(S(true, ""), At); e.Start(At);
                        Assert(e.HoldNativeSelection, "Native AutoSelect could choose a repeat before all callers were decoded");
                        e.OnLine(Tx(At, "CQ", "FN20"), At);
                        RadioStatus decoding = S(true, ""); decoding.Decoding = true;
                        e.OnStatus(decoding, At.AddSeconds(28));
                        e.OnDecode(D(At.AddSeconds(15), "W2ABC", "FN20"), At.AddSeconds(28));
                        e.OnDecode(D(At.AddSeconds(15), "W3ABC", "FN31"), At.AddSeconds(28.2));
                        e.Tick(At.AddSeconds(28.3), true);
                        Assert(commands.Count == 0, "A repeat was selected before the RX decode completed");
                        e.OnDecode(D(At.AddSeconds(15), "DL1ABC", "JO62"), At.AddSeconds(29));
                        e.OnLine(DecodeFinished(At.AddSeconds(29.2)), At.AddSeconds(29.2));
                        e.OnStatus(S(true, ""), At.AddSeconds(29.3)); e.Tick(At.AddSeconds(29.3), true);
                        Assert(commands.SequenceEqual(new[] { "K1ABC DL1ABC JO62" }) && e.Waiting.Rows.Count == 3 && reads == 1,
                            "An older QSO, queue position or DXCC priority displaced the new caller");
                    }
                });
                Run("Repeat callers remain available when every fresh caller is already worked", () => {
                    var commands = new List<string>(); var e = new AutoCqEngine { UseNativeSelection = true };
                    e.WorkedCallsOnBandMode = (band, mode, own) => new[] { "W2ABC", "W3ABC" };
                    e.Options.PriorityDxcc = new[] { "K" }; e.IsPriorityCall = call => call == "W3ABC";
                    e.RequestCallerReply = d => commands.Add(d.Message);
                    e.OnStatus(S(true, ""), At); e.Start(At); e.OnLine(Tx(At, "CQ", "FN20"), At);
                    e.OnDecode(D(At.AddSeconds(15), "W2ABC", "FN20"), At.AddSeconds(29));
                    e.OnDecode(D(At.AddSeconds(15), "W3ABC", "FN31"), At.AddSeconds(29.1));
                    e.OnLine(DecodeFinished(At.AddSeconds(29.2)), At.AddSeconds(29.2)); e.Tick(At.AddSeconds(29.3), true);
                    Assert(commands.SequenceEqual(new[] { "K1ABC W3ABC FN31" }) && e.Waiting.Rows.Count == 2,
                        "Repeat callers were dropped or lost their DXCC priority");
                });
                Run("A newly logged QSO immediately lowers a repeat caller's priority", () => {
                    List<string> commands; AutoCqEngine e = Active(out commands);
                    e.Options.PriorityDxcc = new[] { "K" }; e.IsPriorityCall = call => call == "W1AW";
                    e.RequestCallerReply = d => commands.Add(d.Message);
                    Finish(e, At); e.Tick(At.AddSeconds(2), true);
                    e.OnStatus(S(true, ""), At.AddSeconds(2.1)); e.OnLine(Tx(At.AddSeconds(30), "CQ", "FN20"), At.AddSeconds(30));
                    e.OnDecode(D(At.AddSeconds(45), "W1AW", "FN20"), At.AddSeconds(59));
                    e.OnDecode(D(At.AddSeconds(45), "W2ABC", "FN31"), At.AddSeconds(59.1));
                    e.OnLine(DecodeFinished(At.AddSeconds(59.2)), At.AddSeconds(59.2)); e.Tick(At.AddSeconds(59.3), true);
                    Assert(e.LoggedCount == 1 && commands.Last() == "K1ABC W2ABC FN31", "A QSO logged in this session remained a new station");
                });
                Run("A confirmed QSO without a log entry still lowers a repeat caller's priority", () => {
                    List<string> commands; AutoCqEngine e = Active(out commands);
                    e.Options.PriorityDxcc = new[] { "K" }; e.IsPriorityCall = call => call == "W1AW";
                    e.RequestCallerReply = d => commands.Add(d.Message);
                    e.OnLine(Tx(At, "W1AW", "RR73"), At);
                    e.OnLine(Rx(At.AddSeconds(15), "W1AW", "73"), At.AddSeconds(29));
                    e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Autolog", At.AddSeconds(29.1));
                    e.OnStatus(S(false, ""), At.AddSeconds(29.2)); e.Tick(At.AddSeconds(29.3), true);
                    e.OnStatus(S(true, ""), At.AddSeconds(29.4)); e.OnLine(Tx(At.AddSeconds(30), "CQ", "FN20"), At.AddSeconds(30));
                    e.OnDecode(D(At.AddSeconds(45), "W1AW", "FN20"), At.AddSeconds(59));
                    e.OnDecode(D(At.AddSeconds(45), "W2ABC", "FN31"), At.AddSeconds(59.1));
                    e.OnLine(DecodeFinished(At.AddSeconds(59.2)), At.AddSeconds(59.2)); e.Tick(At.AddSeconds(59.3), true);
                    Assert(e.LoggedCount == 0 && e.ConfirmedCount == 1 && commands.Last() == "K1ABC W2ABC FN31",
                        "An unlogged completed QSO was forgotten when ranking callers");
                });
                Run("Caller history is reset when starting on another band or mode", () => {
                    var commands = new List<string>(); var e = new AutoCqEngine();
                    e.WorkedCallsOnBandMode = (band, mode, own) => band == "20m" && mode == "FT8" ? new[] { "W2ABC" } : new string[0];
                    e.RequestCallerReply = d => commands.Add(d.Message);
                    e.OnStatus(S(true, ""), At); e.Start(At); e.Stop("Change band and mode", false);
                    RadioStatus other = S(true, ""); other.Mode = "FT4"; other.Frequency = 7074000;
                    e.OnStatus(other, At.AddSeconds(30)); e.Start(At.AddSeconds(30));
                    e.OnLine(Tx(At.AddSeconds(30), "CQ", "FN20").Replace("FT8:", "FT4:"), At.AddSeconds(30));
                    foreach (string call in new[] { "W2ABC", "W3ABC" })
                    {
                        DecodedMessage d = D(At.AddSeconds(37.5), call, "FN20"); d.Mode = ":";
                        e.OnDecode(d, At.AddSeconds(44));
                    }
                    e.OnLine(DecodeFinished(At.AddSeconds(44.2)), At.AddSeconds(44.2)); e.Tick(At.AddSeconds(44.3), true);
                    Assert(commands.SequenceEqual(new[] { "K1ABC W2ABC FN20" }), "A QSO in another band or mode still lowered caller priority");
                });
                Run("A new caller from an excluded DXCC cannot displace an allowed repeat", () => {
                    var commands = new List<string>(); var e = new AutoCqEngine();
                    e.WorkedCallsOnBandMode = (band, mode, own) => new[] { "W2ABC" };
                    e.CanCall = call => call != "DL1ABC";
                    e.RequestCallerReply = d => commands.Add(d.Message);
                    e.OnStatus(S(true, ""), At); e.Start(At); e.OnLine(Tx(At, "CQ", "FN20"), At);
                    e.OnDecode(D(At.AddSeconds(15), "W2ABC", "FN20"), At.AddSeconds(29));
                    e.OnDecode(D(At.AddSeconds(15), "DL1ABC", "JO62"), At.AddSeconds(29.1));
                    e.OnLine(DecodeFinished(At.AddSeconds(29.2)), At.AddSeconds(29.2)); e.Tick(At.AddSeconds(29.3), true);
                    Assert(commands.SequenceEqual(new[] { "K1ABC W2ABC FN20" }), "New-caller priority bypassed the DXCC exclusion");
                });
                Run("ADIF caller history distinguishes band and mode and recognizes MFSK FT4 submode", () => {
                    string text = "<CALL:5>W2ABC<BAND:3>20m<MODE:3>FT8<EOR>"
                        + "<CALL:5>W3ABC<FREQ:6>14.080<MODE:4>MFSK<SUBMODE:3>FT4<EOR>"
                        + "<CALL:5>W4ABC<BAND:3>40m<MODE:3>FT8<EOR>"
                        + "<CALL:5>W5ABC<BAND:3>20m<MODE:3>FT8<STATION_CALLSIGN:5>K8ABC<EOR>"
                        + "<CALL:5>W6ABC<BAND:3>20m<MODE:3>FT8<SWL:1>Y<EOR>"
                        + "<CALL:5>W7ABC<BAND:3>20m<EOR>";
                    Assert(JtdxAdif.WorkedCallsFromText(text, "20m", "K1ABC", "FT8").SetEquals(new[] { "W2ABC" }), "FT8 history included another context");
                    Assert(JtdxAdif.WorkedCallsFromText(text, "20m", "K1ABC", "FT4").SetEquals(new[] { "W3ABC" }), "ADIF FT4 submode was lost");
                    Assert(JtdxAdif.WorkedCallsFromText(text, "20m", "K1ABC").SetEquals(new[] { "W2ABC", "W3ABC", "W7ABC" }), "Band-only monitoring history became mode-specific");
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
                Run("Monitoring yields an unanswered target to a station calling us outside the filter", () => {
                    List<string> commands; AutoCqEngine e = MonitorActive(out commands);
                    e.RequestCallerReply = d => commands.Add("CALLER " + d.Message);
                    DecodedMessage target = CqD(At.AddSeconds(15), "JA1AAA"); target.IsNew = false;
                    e.OnDecode(target, At.AddSeconds(29)); e.Tick(At.AddSeconds(29), true);
                    Assert(commands.Count == 1, "The monitoring fixture did not select its visible target");
                    e.OnStatus(S(true, "JA1AAA"), At.AddSeconds(29.1));
                    e.OnLine(Tx(At.AddSeconds(30), "JA1AAA", "-10"), At.AddSeconds(30));
                    e.OnDecode(D(At.AddSeconds(45), "W2ABC", "FN31"), At.AddSeconds(59));
                    e.OnLine(DecodeFinished(At.AddSeconds(59.2)), At.AddSeconds(59.2)); e.Tick(At.AddSeconds(59.3), true);
                    Assert(e.Monitoring && commands.SequenceEqual(new[] { "REPLY CQ JA1AAA FN20", "HALT" }),
                        "The unanswered target kept repeating instead of yielding to our caller");
                    e.OnLine("Halt Tx triggered at RX: UDP request", At.AddSeconds(59.4));
                    e.OnStatus(S(false, "JA1AAA"), At.AddSeconds(59.5)); e.Tick(At.AddSeconds(59.6), true);
                    Assert(commands.Last() == "CALLER K1ABC W2ABC FN31" && e.IncomingMonitorCall == "W2ABC",
                        "The incoming station was still filtered after the unanswered call stopped");
                });
                Run("After two unanswered calls CQ may clear the completed DX field", () => {
                    List<string> c;AutoCqEngine e=Active(out c);e.OnLine(Tx(At,"W1AW","-10"),At);e.OnLine(Tx(At.AddSeconds(30),"W1AW","-10"),At.AddSeconds(30));
                    e.OnLine("Halt Tx triggered at RX: m_singleshot, counter triggered ",At.AddSeconds(59));e.OnStatus(S(false,"W1AW"),At.AddSeconds(59));e.Tick(At.AddSeconds(60.1),true);
                    Assert(c.SequenceEqual(new[]{"CQ"}) && e.LoggedCount==0,"Failed caller blocked CQ");
                });
                Run("A late native report limit resumes CQ with a fresh TX confirmation", () => {
                    foreach (string mode in new[] { "FT4", "FT8" })
                    foreach (string outcome in new[] { "status first", "halt first", "already receiving", "incoming", "manual stop", "timeout", "monitoring" })
                    {
                        var commands = new List<string>();
                        var engine = new AutoCqEngine { UseNativeSelection = true };
                        bool monitoring = outcome == "monitoring";
                        engine.Options.MonitorEnabled = monitoring;
                        engine.Options.MonitorCalls = new[] { "W1AW" };
                        engine.RequestCallerReply = row => commands.Add(row.Message);
                        engine.RequestCq = () => commands.Add("CQ");
                        engine.RequestHalt = () => commands.Add("HALT");
                        RadioStatus rx = S(false, "");
                        RadioStatus dx = S(true, "W1AW");
                        RadioStatus cleared = S(true, "");
                        foreach (RadioStatus status in new[] { rx, dx, cleared })
                        {
                            status.Mode = mode;
                            status.TxFirst = false;
                        }
                        double slot = mode == "FT4" ? 7.5 : 15;
                        string marker = mode == "FT4" ? ":" : "~";
                        DateTime firstTx = At.AddSeconds(slot), secondTx = firstTx.AddSeconds(2 * slot);
                        engine.OnStatus(rx, At); engine.Start(At);
                        var caller = D(At, "W1AW", "-20"); caller.Mode = marker;
                        engine.OnDecode(caller, firstTx.AddMilliseconds(-800));
                        engine.Tick(firstTx.AddMilliseconds(-600), true);
                        Assert(commands.SequenceEqual(new[] { caller.Message }), "The test did not select its first caller");
                        engine.OnStatus(dx, firstTx.AddMilliseconds(-400));
                        engine.OnLine(Tx(firstTx, "W1AW", "R-03").Replace("FT8:", mode + ":"), firstTx);
                        engine.OnLine(Tx(secondTx, "W1AW", "R-01").Replace("FT8:", mode + ":"), secondTx);

                        // The scheduled TX boundary passes before JTDX finishes the decode and clears DX.
                        DateTime afterDecode = secondTx.AddSeconds(2 * slot + 3.6);
                        var incoming = D(secondTx.AddSeconds(slot), "W2ABC", "FN31"); incoming.Mode = marker;
                        if (outcome == "incoming") engine.OnDecode(incoming, afterDecode.AddMilliseconds(-200));
                        bool receiving = outcome == "already receiving" || monitoring;
                        if (receiving || outcome == "status first")
                            engine.OnStatus(receiving ? rx : cleared, afterDecode.AddMilliseconds(-100));
                        engine.OnLine("DX Call W1AW cleared, RREPORT/SRREPORT count reached", afterDecode);
                        if (!receiving && outcome != "status first")
                            engine.OnStatus(cleared, afterDecode.AddMilliseconds(50));
                        engine.OnLine(DecodeFinished(afterDecode.AddMilliseconds(70)), afterDecode.AddMilliseconds(70));
                        engine.Tick(afterDecode.AddMilliseconds(150), true);
                        Assert(engine.Armed && engine.LoggedCount == 0, "The native retry limit ended the session or logged a QSO");
                        if (monitoring)
                        {
                            engine.Tick(afterDecode.AddSeconds(7), true);
                            Assert(engine.Monitoring && commands.SequenceEqual(new[] { caller.Message }),
                                "The native retry limit ended monitoring or started CQ");
                            continue;
                        }
                        if (outcome == "incoming")
                        {
                            Assert(commands.SequenceEqual(new[] { caller.Message, incoming.Message }),
                                "A new caller was delayed or passed over for CQ");
                            continue;
                        }
                        if (!receiving)
                        {
                            Assert(commands.SequenceEqual(new[] { caller.Message, "HALT" }),
                                "CQ was sent while TX was still enabled and could not produce a new acknowledgement");
                            if (outcome == "timeout")
                            {
                                engine.Tick(afterDecode.AddSeconds(7), true);
                                Assert(!engine.Armed && commands.Count == 2, "Missing receive confirmation rearmed TX");
                                continue;
                            }
                            if (outcome == "manual stop")
                            {
                                engine.OnLine("Halt Tx triggered at RX: Halt Tx button clicked", afterDecode.AddMilliseconds(200));
                                engine.OnStatus(rx, afterDecode.AddMilliseconds(250));
                                engine.Tick(afterDecode.AddSeconds(8), true);
                                Assert(!engine.Armed && commands.Count == 2, "A manual stop restarted CQ");
                                continue;
                            }
                            if (outcome == "status first") engine.OnStatus(rx, afterDecode.AddMilliseconds(200));
                            else engine.OnLine("Halt Tx triggered at RX: UDP request", afterDecode.AddMilliseconds(200));
                            engine.Tick(afterDecode.AddMilliseconds(250), true);
                            Assert(commands.Count == 2, "CQ resumed before both confirmations of receive");
                            if (outcome == "status first") engine.OnLine("Halt Tx triggered at RX: UDP request", afterDecode.AddMilliseconds(300));
                            else engine.OnStatus(rx, afterDecode.AddMilliseconds(300));
                            engine.Tick(afterDecode.AddMilliseconds(350), true);
                        }
                        Assert(commands.SequenceEqual(receiving ? new[] { caller.Message, "CQ" } : new[] { caller.Message, "HALT", "CQ" })
                            && !engine.CqTxFirst, "The next CQ was missed, repeated, or used the wrong TX phase");
                        engine.OnStatus(cleared, afterDecode.AddMilliseconds(600));
                        engine.Tick(afterDecode.AddSeconds(7), true);
                        Assert(engine.Armed, "The session stopped before the next available TX slot");
                    }
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
                    List<string> commands; AutoCqEngine e = Active(out commands);
                    DecodedMessage call = D(At.AddSeconds(15), "W2ABC", "FN20");
                    e.OnDecode(call, At.AddSeconds(29));
                    e.OnLine("QSO logged: W2ABC", At.AddSeconds(30));
                    e.OnDecode(call, At.AddSeconds(59));
                    Assert(e.Waiting.Rows.Count == 0, "An old decode of a worked station re-entered the queue");
                    e.Waiting.Add("W2ABC");
                    Assert(e.Waiting.Rows.Count == 1, "Operator could not explicitly re-add the station");
                });
                Run("No-answer entry stays paused until a fresh subsequent call or operator retry", () => {
                    CallerQueue q=new CallerQueue();RadioStatus s=S(true,"");q.SetContext(s);q.Observe(D(At.AddSeconds(15),"W2ABC","FN20"),s,null,At.AddSeconds(29));q.Pause("W2ABC","Нет ответа",At.AddSeconds(59));
                    q.Observe(D(At.AddSeconds(45),"W2ABC","FN20"),s,null,At.AddSeconds(60));Assert(q.Next(s)==null,"Old decode reactivated a failed call");q.Observe(D(At.AddSeconds(75),"W2ABC","FN20"),s,null,At.AddSeconds(89));Assert(q.Next(s)!=null,"Fresh call did not reactivate the station");
                });
                Run("I4ZZA regression: a fresh repeat call keeps priority over CQ", () => {
                    DateTime workedAt = new DateTime(2025, 11, 14, 9, 20, 0, DateTimeKind.Utc);
                    DateTime cqAt = workedAt.AddMinutes(6.5);
                    RadioStatus idle = S(true, ""); idle.DeCall = "K1ABC"; idle.Frequency = 21074000;
                    var commands = new List<string>();
                    var engine = new AutoCqEngine { UseNativeSelection = true };
                    engine.Options.PriorityDxcc = new[] { "KL" };
                    engine.RequestCq = () => commands.Add("CQ");
                    engine.RequestHalt = () => commands.Add("HALT");
                    engine.RequestCallerReply = d => commands.Add(d.Message);
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
                    Assert(commands.SequenceEqual(new[] { "HALT", call.Message }) && engine.Armed,
                        "A fresh repeat call was passed over for CQ");
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
                Run("Two silent RR73 retries leave the final receive opportunity open", () => {
                    foreach (string final in new[] { null, "73", "RR73" })
                    {
                        List<string> c; AutoCqEngine e = Active(out c);
                        e.UseNativeSelection = true;
                        e.RequestCallerReply = d => c.Add(d.Message.Split(' ')[1]);
                        e.OnLine(Tx(At,"W1AW","RR73").Replace(".000", ".912"),At.AddSeconds(1));
                        e.OnLine("QSO logged: W1AW",At.AddSeconds(1));
                        for (int repeat = 1; repeat <= 2; repeat++)
                        {
                            DateTime next = At.AddSeconds(30 * repeat);
                            DateTime decoded = next.AddMilliseconds(-500);
                            e.OnLine(DecodeFinished(decoded), decoded);
                            e.Tick(decoded, true);
                            Assert(c.Count == 0 && e.Awaiting73 && e.RepeatFinal73, "Stopped before a configured silent retry");
                            e.OnLine(Tx(next, "W1AW", "RR73"), next);
                            Assert(!e.RepeatFinal73 && e.SilentTerminalRepeats == repeat, "Repeat permission outlived the transmitted frame");
                        }
                        var decoding = S(true,"W1AW"); decoding.Decoding = true;
                        e.OnStatus(decoding, At.AddSeconds(75.2)); e.Tick(At.AddSeconds(89), true);
                        Assert(c.Count == 0 && e.ListenAfterFinal73 && !e.RepeatFinal73, "The final RX window was cut short or an excess repeat was enabled");
                        e.OnDecode(D(At.AddSeconds(75), "W2ABC", "FN20"), At.AddSeconds(89.2));
                        if (final != null)
                            e.OnDecode(D(At.AddSeconds(75),"W1AW",final),At.AddSeconds(89.3));
                        e.OnLine(DecodeFinished(At.AddSeconds(89.5)),At.AddSeconds(89.5));
                        e.Tick(At.AddSeconds(89.5),true);
                        Assert(c.Count == 0, "Finished before decoding became idle");
                        e.OnStatus(S(true,"W1AW"),At.AddSeconds(89.6)); e.Tick(At.AddSeconds(89.6),true);
                        Assert(c.SequenceEqual(new[]{"HALT"}) && e.HoldNativeSelection && !e.RepeatFinal73, "The sequence did not stop at its limit");
                        e.OnLine("Halt Tx triggered at RX: UDP request ",At.AddSeconds(89.7));
                        e.OnStatus(S(false,"W1AW"),At.AddSeconds(89.7)); e.Tick(At.AddSeconds(89.7),true);
                        Assert(c.SequenceEqual(new[]{"HALT","W2ABC"}) && e.Armed
                            && e.ConfirmedCount == (final != null ? 1 : 0) && e.Missing73Count == (final != null ? 0 : 1),
                            "Queue missed the first TX opportunity after final RX");
                    }
                });
                Run("Repeated reports get another sign-off and restart the selected number of silent retries", () => {
                    foreach (string mode in new[] { "FT8", "FT4" })
                    foreach (int limit in new[] { 1, 2, 4, 20 })
                    {
                        const string peer = "W1AW";
                        var commands = new List<string>();
                        var engine = new AutoCqEngine { UseNativeSelection = true };
                        engine.Options.Wait73Periods = limit;
                        engine.CanCall = call => call != "W2ABC";
                        engine.RequestHalt = () => commands.Add("HALT");
                        engine.RequestCq = () => commands.Add("CQ");
                        RadioStatus radio = S(true, peer); radio.Mode = mode;
                        engine.OnStatus(radio, At); engine.Start(At);
                        double slot = mode == "FT4" ? 7.5 : 15;
                        string signoff = mode == "FT4" ? "RRR" : "RR73";
                        string receiveMode = mode == "FT4" ? ":" : "~";
                        DateTime tx = At;
                        engine.OnLine(Tx(tx, peer, signoff).Replace("FT8:", mode + ":"), tx);
                        Action<string, string> receive = (call, payload) => {
                            DateTime ready = tx.AddSeconds(2 * slot - 0.6);
                            if (payload != null)
                            {
                                DecodedMessage decode = D(tx.AddSeconds(slot), call, payload); decode.Mode = receiveMode;
                                engine.OnDecode(decode, ready.AddMilliseconds(-50));
                                engine.OnDecode(decode, ready.AddMilliseconds(-25));
                            }
                            engine.OnLine(DecodeFinished(ready), ready); engine.Tick(ready, true);
                        };
                        Action sendRepeat = () => {
                            Assert(engine.Awaiting73 && engine.RepeatFinal73 && commands.Count == 0, "A valid sign-off retry was blocked");
                            tx = tx.AddSeconds(2 * slot);
                            string line = Tx(tx, peer, signoff).Replace("FT8:", mode + ":");
                            engine.OnLine(line, tx); engine.OnLine(line.Replace("Transmitting", "Retransmitting"), tx.AddMilliseconds(100));
                        };
                        for (int reports = 0; reports < 3; reports++)
                        {
                            receive(peer, "R+00"); sendRepeat();
                            Assert(engine.SilentTerminalRepeats == 0, "A reply to a report used a retry");
                        }
                        for (int pass = 0; pass < 2; pass++)
                        {
                            for (int repeat = 1; repeat <= limit; repeat++)
                            {
                                receive("W2ABC", "R-08"); sendRepeat();
                                Assert(engine.SilentTerminalRepeats == repeat, "An unrelated report or duplicate TX changed the count");
                            }
                            if (pass == 0) { receive(peer, "R+00"); sendRepeat(); }
                        }
                        receive(peer, null);
                        Assert(commands.SequenceEqual(new[] { "HALT" }) && !engine.RepeatFinal73,
                            "The final silent period did not stop at the configured count");
                        DateTime readyToContinue = tx.AddSeconds(2 * slot - 0.4);
                        engine.OnLine("Halt Tx triggered at RX: UDP request", readyToContinue);
                        RadioStatus idle = S(false, peer); idle.Mode = mode;
                        engine.OnStatus(idle, readyToContinue); engine.Tick(readyToContinue, true);
                        Assert(commands.SequenceEqual(new[] { "HALT", "CQ" }) && engine.Missing73Count == 1,
                            "AutoCQ missed the next TX period after the unanswered retries");
                    }
                });
                Run("The repeat request preserves the terminal message for the selected DX at the next transmit slot", () => {
                    DateTime firstTx = At.AddSeconds(30), endOfReply = At.AddSeconds(59.4);
                    List<string> actions;
                    AutoCqEngine engine = Active(out actions);
                    engine.UseNativeSelection = true;
                    engine.OnLine(Tx(firstTx, "W1AW", "RR73"), firstTx);
                    engine.OnLine("QSO logged: W1AW", firstTx.AddMilliseconds(50));
                    Assert(engine.TerminalRepeatMessage == 0, "The first RR73 already enabled a repeat");
                    engine.OnLine(DecodeFinished(endOfReply), endOfReply);
                    engine.Tick(endOfReply, true);
                    Assert(engine.Armed && engine.Awaiting73 && engine.TerminalRepeatMessage == 4 && actions.Count == 0,
                        "AutoCQ did not request the standard Tx4 message after the silent receive period");
                    engine.OnLine(Tx(firstTx.AddSeconds(30), "W1AW", "RR73"), firstTx.AddSeconds(30));
                    Assert(engine.Armed && engine.SilentTerminalRepeats == 1 && engine.TerminalRepeatMessage == 0,
                        "The retry was not counted or the authorization outlived the frame");
                });
                Run("A substituted report leaves time for a final reply and the session continues", () => {
                    foreach (int replaced in new[] { 1, 2 })
                    foreach (bool acknowledged in new[] { false, true })
                    {
                        List<string> commands;
                        AutoCqEngine engine = Active(out commands);
                        engine.UseNativeSelection = true;
                        engine.RequestCallerReply = d => commands.Add("REPLY " + d.Message.Split(' ')[1]);
                        engine.OnLine(Tx(At, "W1AW", "RR73"), At);
                        engine.OnLine("QSO logged: W1AW", At.AddMilliseconds(50));
                        for (int attempt = 1; attempt <= 2; attempt++)
                        {
                            DateTime tx = At.AddSeconds(30 * attempt);
                            DateTime replyEnd = tx.AddMilliseconds(-600);
                            DecodedMessage other = D(tx.AddSeconds(-15), "W1AW", "R-16");
                            other.Message = "W3ABC W1AW R-16";
                            engine.OnDecode(other, replyEnd.AddMilliseconds(-50));
                            engine.OnLine(DecodeFinished(replyEnd), replyEnd);
                            engine.Tick(replyEnd, true);
                            Assert(engine.TerminalRepeatMessage == 4, "A report to another station removed the RR73 request");

                            // Exercise recovery even if JTDX selects the wrong message.
                            engine.OnLine(Tx(tx, "W1AW", attempt == replaced ? "-10" : "RR73"), tx);
                            engine.Tick(tx.AddSeconds(15.1), true);
                            Assert(engine.Armed && engine.Awaiting73 && !engine.RepeatFinal73 && commands.Count == 0,
                                "The changed TX stopped the session or interrupted reception");
                        }
                        Assert(engine.SilentTerminalRepeats == 1, "A report was counted as an RR73 transmission");
                        DateTime finalReplyEnd = At.AddSeconds(89.4);
                        engine.OnDecode(D(At.AddSeconds(75), "W2ABC", "FN20"), finalReplyEnd.AddMilliseconds(-100));
                        if (acknowledged)
                            engine.OnLine(Rx(At.AddSeconds(75), "W1AW", "73"), finalReplyEnd.AddMilliseconds(-50));
                        engine.OnLine(DecodeFinished(finalReplyEnd), finalReplyEnd);
                        engine.Tick(finalReplyEnd, true);
                        Assert(commands.SequenceEqual(new[] { "HALT" }), "The silent attempt limit did not end this exchange");
                        engine.OnLine("Halt Tx triggered at RX: UDP request", finalReplyEnd.AddMilliseconds(100));
                        engine.OnStatus(S(false, ""), finalReplyEnd.AddMilliseconds(100));
                        engine.Tick(finalReplyEnd.AddMilliseconds(100), true);
                        Assert(engine.Armed && commands.SequenceEqual(new[] { "HALT", "REPLY W2ABC" })
                            && engine.ConfirmedCount == (acknowledged ? 1 : 0) && engine.Missing73Count == (acknowledged ? 0 : 1),
                            "The station queue stopped or missed the late reply");
                    }
                });
                Run("An untransmitted sign-off repeat does not stop CQ or monitoring", () => {
                    foreach (string operation in new[] { "CQ", "monitoring", "awards", "expeditions" })
                    foreach (string mode in new[] { "FT4", "FT8" })
                    foreach (string signoff in new[] { "RR73", "73" })
                    foreach (string outcome in new[] { "repeat", "final", "missing", "manual stop" })
                    {
                        const string peer = "R3PAA";
                        bool monitoring = operation != "CQ", reverse = signoff == "73";
                        var commands = new List<string>();
                        var engine = new AutoCqEngine { UseNativeSelection = true, DxccForCall = call => "UA" };
                        engine.Options.Wait73Periods = 2;
                        engine.Options.MonitorEnabled = operation == "monitoring";
                        engine.Options.MonitorDxcc = new[] { "UA" };
                        engine.RequestCq = () => commands.Add("CQ");
                        engine.RequestHalt = () => commands.Add("HALT");
                        engine.RequestCallerReply = decode => commands.Add(decode.Message);
                        RadioStatus initial = S(!monitoring, monitoring ? "" : peer);
                        initial.Mode = mode; initial.TxFirst = false;
                        engine.OnStatus(initial, At);
                        AwardSearch awards = null;
                        ExpeditionSearch expeditions = null;
                        if (operation == "awards")
                        {
                            var program = HamlogAward.Gunsmiths.Create();
                            program.Selected = true;
                            awards = new AwardSearch(new AwardCatalog { Programs = new List<AwardProgram> { program } },
                                "K1ABC", "20m", mode, engine.DxccForCall);
                        }
                        if (operation == "expeditions")
                            expeditions = new ExpeditionSearch(new ExpeditionOptions(), new ExpeditionCatalog(), engine.DxccForCall);
                        engine.Start(At, awards, expeditions);
                        if (monitoring)
                        {
                            engine.OnLine(ManualSelection(At.AddSeconds(1), peer), At.AddSeconds(1));
                            RadioStatus active = S(true, peer); active.Mode = mode; active.TxFirst = false;
                            engine.OnStatus(active, At.AddSeconds(1.1));
                        }

                        double slot = mode == "FT4" ? 7.5 : 15;
                        string marker = mode == "FT4" ? ":" : "~";
                        DateTime start = At.AddSeconds(slot), missed = start.AddSeconds(2 * slot);
                        if (reverse)
                        {
                            var acknowledgement = D(At, peer, "RR73"); acknowledgement.Mode = marker;
                            engine.OnDecode(acknowledgement, start.AddMilliseconds(-800));
                        }
                        engine.OnLine(Tx(start, peer, signoff).Replace("FT8:", mode + ":"), start);
                        engine.OnLine("QSO logged: " + peer, start.AddMilliseconds(50));
                        if (reverse)
                        {
                            var acknowledgement = D(missed.AddSeconds(-slot), peer, "RR73"); acknowledgement.Mode = marker;
                            engine.OnDecode(acknowledgement, missed.AddMilliseconds(-800));
                        }
                        // JTDX skips the TX slot. It is still connected and the same DX is selected.
                        if (outcome != "final")
                        {
                            engine.Tick(missed.AddMilliseconds(150), true);
                            Assert(engine.TerminalRepeatMessage == (reverse ? 5 : 4), "The first repeat was not authorized");
                        }
                        engine.Tick(missed.AddSeconds(slot).AddMilliseconds(400), true);
                        Assert(engine.Armed && !engine.RepeatFinal73 && commands.Count == 0 && engine.SilentTerminalRepeats == 0,
                            operation + " stopped, left TX authorized, or counted an unsent " + signoff + " in " + mode);

                        DateTime boundary = missed.AddSeconds(2 * slot);
                        if (outcome == "manual stop")
                        {
                            DateTime stopped = missed.AddSeconds(slot).AddMilliseconds(500);
                            engine.OnLine("Halt Tx triggered at RX: Halt Tx button clicked", stopped);
                            engine.Tick(boundary, true);
                            Assert(!engine.Armed && commands.Count == 0, "An explicit Stop was treated as a missed repeat");
                            continue;
                        }
                        if (outcome != "final")
                        {
                            DateTime decoded = boundary.AddMilliseconds(-600);
                            engine.OnLine(DecodeFinished(decoded), decoded);
                            engine.Tick(decoded, true);
                            Assert(engine.TerminalRepeatMessage == (reverse ? 5 : 4) && commands.Count == 0,
                                "The next TX slot was not available to the repeat");
                            if (outcome == "repeat")
                            {
                                engine.OnLine(Tx(boundary, peer, signoff).Replace("FT8:", mode + ":"), boundary);
                                boundary = boundary.AddSeconds(2 * slot);
                                if (!reverse)
                                {
                                    decoded = boundary.AddMilliseconds(-600);
                                    engine.OnLine(DecodeFinished(decoded), decoded); engine.Tick(decoded, true);
                                    Assert(engine.TerminalRepeatMessage == 4 && engine.SilentTerminalRepeats == 1,
                                        "A skipped slot used up a configured RR73 repeat");
                                    engine.OnLine(Tx(boundary, peer, signoff).Replace("FT8:", mode + ":"), boundary);
                                    boundary = boundary.AddSeconds(2 * slot);
                                }
                            }
                            else
                            {
                                engine.Tick(boundary.AddSeconds(slot).AddMilliseconds(400), true);
                                Assert(engine.Armed && !engine.RepeatFinal73 && commands.Count == 0,
                                    "Two missed transmissions did not leave time for the final receive window");
                                boundary = boundary.AddSeconds(2 * slot);
                            }
                        }

                        DateTime receive = boundary.AddSeconds(-slot);
                        var caller = D(receive, "DL2ZZB", "FN31"); caller.Mode = marker;
                        engine.OnDecode(caller, boundary.AddMilliseconds(-900));
                        if (outcome != "missing")
                        {
                            var final = D(receive, peer, "73"); final.Mode = marker;
                            engine.OnDecode(final, boundary.AddMilliseconds(-800));
                        }
                        DateTime decodeEnd = boundary.AddMilliseconds(-600);
                        engine.OnLine(DecodeFinished(decodeEnd), decodeEnd);
                        engine.Tick(decodeEnd, true);
                        Assert(engine.Armed && commands.SequenceEqual(new[] { "HALT" }) && !engine.RepeatFinal73,
                            "The exchange did not finish after the final receive window");
                        DateTime halted = boundary.AddMilliseconds(-350);
                        engine.OnLine("Halt Tx triggered at RX: UDP request", halted);
                        RadioStatus idle = S(false, ""); idle.Mode = mode; idle.TxFirst = false;
                        engine.OnStatus(idle, halted); engine.Tick(halted, true);
                        Assert(engine.Armed && commands.SequenceEqual(new[] { "HALT", caller.Message })
                            && engine.LoggedCount == 1 && engine.Missing73Count == (!reverse && outcome == "missing" ? 1 : 0),
                            "The session dropped the next caller, or assigned the wrong QSO outcome");
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
                    Assert(e.RepeatFinal73 && e.TerminalRepeatMessage == 5 && e.HoldNativeSelection && c.Count == 0,"Peer RR73 did not unlock the next 73");
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
                Run("A plain 73 after our acknowledged report closes the exchange without silent repeats", () => {
                    List<string> commands;
                    AutoCqEngine engine = Active(out commands);
                    engine.UseNativeSelection = true;
                    engine.OnLine(Tx(At, "W1AW", "R+00"), At);
                    engine.OnDecode(D(At.AddSeconds(15), "W1AW", "73"), At.AddSeconds(29));
                    engine.OnLine(Tx(At.AddSeconds(30), "W1AW", "73"), At.AddSeconds(30));
                    engine.OnLine("QSO logged: W1AW", At.AddSeconds(30));
                    Assert(engine.ConfirmedCount == 1 && !engine.Awaiting73 && !engine.RepeatFinal73,
                        "The earlier directed 73 did not complete the exchange");
                    DateTime next = At.AddSeconds(59.6);
                    engine.OnLine(DecodeFinished(next), next);
                    engine.Tick(next, true);
                    Assert(commands.SequenceEqual(new[] { "HALT" }) && engine.SilentTerminalRepeats == 0,
                        "AutoCQ tried to retransmit 73 after the quiet period");
                    engine.OnLine("Halt Tx triggered at RX: UDP request", next);
                    engine.OnStatus(S(false, "W1AW"), next);
                    engine.Tick(next, true);
                    Assert(engine.Armed && engine.Missing73Count == 0 && commands.SequenceEqual(new[] { "HALT", "CQ" }),
                        "The completed exchange did not resume CQ");

                    foreach (string sentReport in new[] { "-17", "R-17" })
                    {
                        engine = Active(out commands);
                        engine.OnLine(Tx(At, "W1AW", sentReport), At);
                        string peer = sentReport[0] == 'R' ? "W2ABC" : "W1AW";
                        engine.OnDecode(D(At.AddSeconds(15), peer, "73"), At.AddSeconds(29));
                        engine.OnLine(Tx(At.AddSeconds(30), "W1AW", "73"), At.AddSeconds(30));
                        Assert(engine.Awaiting73 && engine.ConfirmedCount == 0,
                            "An unrelated 73 or a 73 before an acknowledged report completed the exchange");
                    }
                });
                Run("Sending the final 73 retires the old queued report even without a QSO log entry", () => {
                    foreach (string acknowledgement in new[] { "RRR", "RR73", "73" })
                    {
                        var engine = new AutoCqEngine { UseNativeSelection = true };
                        var commands = new List<string>();
                        engine.RequestCallerReply = decode => commands.Add(decode.Message);
                        engine.RequestHalt = () => commands.Add("HALT");
                        engine.RequestCq = () => commands.Add("CQ");
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
                        Assert(engine.Waiting.Find("W1AW") != null, "A fresh call was rejected after the previous contact finished");
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
                Run("Incoming monitor calls outrank target and manual searches in both modes and TX periods", () => {
                    foreach (string mode in new[] { "FT8", "FT4" })
                    foreach (bool period in new[] { false, true })
                    {
                        List<string> commands; AutoCqEngine e = MonitorActive(out commands);
                        e.Stop("fixture", false); RadioStatus idle = S(false, ""); idle.Mode = mode; idle.TxFirst = !period;
                        e.OnStatus(idle, At); e.Start(At); DateTime deadline = e.SessionDeadline;
                        e.RequestCallerReply = d => commands.Add("CALLER " + d.Message);
                        double slot = mode == "FT4" ? 7.5 : 15;
                        DateTime rx = At.AddSeconds(period ? slot : 0), now = rx.AddSeconds(slot - 1);
                        var target = CqD(rx, "JA2ZZB"); target.IsNew = false; target.Mode = mode == "FT4" ? ":" : "~";
                        e.OnDecode(target, now); e.SelectCq("JA2ZZB", now);
                        var caller = D(rx, "DL3ZZB", "JO62"); caller.Mode = target.Mode;
                        e.OnDecode(caller, now); e.WaitForVisibleCqsUntil = now.AddSeconds(1);
                        e.Tick(now, true);
                        Assert(e.Monitoring && e.IncomingMonitorCall == "DL3ZZB" && e.ManualMonitorCall == ""
                            && commands.SequenceEqual(new[] { "CALLER K1ABC DL3ZZB JO62" }),
                            "An incoming call was filtered, waited for left-pane replay or lost its reply period");
                        RadioStatus replying = S(true, "DL3ZZB"); replying.Mode = mode; replying.TxFirst = period;
                        e.OnStatus(replying, now.AddSeconds(0.1)); e.Tick(now.AddSeconds(0.2), true);
                        Assert(e.Monitoring && e.SessionDeadline == deadline && commands.Count == 1 && e.CqTxFirst == period
                            && e.SelectedCqCall == "JA2ZZB" && e.Options.MonitorDxcc.SequenceEqual(new[] { "JA" }),
                            "Answering a caller changed the timer, targets or saved manual selection");
                    }
                });
                Run("A caller in the final receive period is answered before monitoring resumes with or without logging", () => {
                    foreach (string mode in new[] { "FT8", "FT4" })
                    foreach (string final in new[] { "73", "RR73", "RRR" })
                    foreach (bool logged in new[] { true, false })
                    foreach (bool callerFirst in new[] { true, false })
                    {
                        List<string> commands; AutoCqEngine e = MonitorActive(out commands);
                        e.Stop("fixture", false); RadioStatus idle = S(false, ""); idle.Mode = mode;
                        e.OnStatus(idle, At); e.Start(At); DateTime deadline = e.SessionDeadline;
                        e.RequestCallerReply = d => commands.Add("CALLER " + d.Message);
                        double slot = mode == "FT4" ? 7.5 : 15;
                        var target = CqD(At.AddSeconds(slot), "JA2ZZB"); target.IsNew = false; target.Mode = mode == "FT4" ? ":" : "~";
                        e.OnDecode(target, At.AddSeconds(2 * slot - 1)); e.Tick(At.AddSeconds(2 * slot - 1), true);
                        RadioStatus selected = S(true, "JA2ZZB"); selected.Mode = mode;
                        e.OnStatus(selected, At.AddSeconds(2 * slot - 0.9));
                        e.OnLine(Tx(At.AddSeconds(2 * slot), "JA2ZZB", "RR73").Replace("FT8:", mode + ":"), At.AddSeconds(2 * slot));
                        if (logged) e.OnLine("QSO logged: JA2ZZB", At.AddSeconds(2 * slot + 0.1));
                        var incoming = D(At.AddSeconds(3 * slot), "DL3ZZB", "JO62"); incoming.Mode = target.Mode;
                        var farewell = D(At.AddSeconds(3 * slot), "JA2ZZB", final); farewell.Mode = target.Mode;
                        if (callerFirst) e.OnDecode(incoming, At.AddSeconds(4 * slot - 1));
                        e.OnDecode(farewell, At.AddSeconds(4 * slot - 0.9));
                        if (!callerFirst) e.OnDecode(incoming, At.AddSeconds(4 * slot - 0.8));
                        e.OnLine(DecodeFinished(At.AddSeconds(4 * slot - 0.5)), At.AddSeconds(4 * slot - 0.5));
                        e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Autolog", At.AddSeconds(4 * slot - 0.4));
                        selected.TxEnabled = false; e.OnStatus(selected, At.AddSeconds(4 * slot - 0.3)); e.Tick(At.AddSeconds(4 * slot - 0.2), true);
                        Assert(e.Monitoring && e.ConfirmedCount == 1 && e.IncomingMonitorCall == "DL3ZZB"
                            && e.Waiting.Find("JA2ZZB") == null && commands.Last() == "CALLER K1ABC DL3ZZB JO62"
                            && e.SessionDeadline == deadline, "The final RX-period caller was skipped: " + mode + "/" + final + "/" + logged + "/" + callerFirst);
                        RadioStatus replying = S(true, "DL3ZZB"); replying.Mode = mode; e.OnStatus(replying, At.AddSeconds(4 * slot - 0.1));
                        e.OnLine(Tx(At.AddSeconds(6 * slot), "DL3ZZB", "RR73").Replace("FT8:", mode + ":"), At.AddSeconds(6 * slot));
                        var done = D(At.AddSeconds(7 * slot), "DL3ZZB", "73"); done.Mode = target.Mode;
                        e.OnDecode(done, At.AddSeconds(8 * slot - 1));
                        e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Autolog", At.AddSeconds(8 * slot - 0.8));
                        replying.TxEnabled = false; e.OnStatus(replying, At.AddSeconds(8 * slot - 0.7)); e.Tick(At.AddSeconds(8 * slot - 0.6), true);
                        Assert(e.Monitoring && e.IncomingMonitorCall == "" && e.SessionDeadline == deadline
                            && e.Waiting.Find("DL3ZZB") == null && commands.Count == 2 && !e.Status.TxEnabled,
                            "The incoming QSO stopped monitoring, left a queue entry or started CQ");
                    }
                });
                Run("Monitoring drains several addressed callers using their saved decode after the left pane clears", () => {
                    List<string> commands; AutoCqEngine e = MonitorActive(out commands); DateTime deadline = e.SessionDeadline;
                    var replies = new List<DecodedMessage>();
                    e.RequestCallerReply = d => { replies.Add(d); commands.Add("CALLER " + d.Message); };
                    foreach (string call in new[] { "DL3ZZB", "W2ABC", "F1ABC" }) e.OnDecode(D(At.AddSeconds(15), call, "-11"), At.AddSeconds(29));
                    e.Cqs.Invalidate(); e.Tick(At.AddSeconds(29.1), true);
                    e.OnStatus(S(true, "DL3ZZB"), At.AddSeconds(29.2));
                    e.OnLine(Tx(At.AddSeconds(30), "DL3ZZB", "RR73"), At.AddSeconds(30));
                    e.OnLine(Rx(At.AddSeconds(45), "DL3ZZB", "73"), At.AddSeconds(59));
                    e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Autolog", At.AddSeconds(59.1));
                    e.OnStatus(S(false, "DL3ZZB"), At.AddSeconds(59.2)); e.Tick(At.AddSeconds(59.3), true);
                    Assert(e.Monitoring && e.IncomingMonitorCall == "W2ABC" && e.Waiting.Find("F1ABC") != null
                        && replies.Count == 2 && replies[1].Message == "K1ABC W2ABC -11" && replies[1].DeltaFrequency == 1736
                        && replies[1].Snr == -11 && replies[1].Milliseconds == (uint)At.AddSeconds(15).TimeOfDay.TotalMilliseconds
                        && e.SessionDeadline == deadline, "The queued caller needed a new CQ, visible row or fabricated decode");
                });
                Run("Incoming calls bypass completed monitoring targets and unanswered-target cooldowns", () => {
                    List<string> commands; AutoCqEngine e = MonitorActive(out commands);
                    e.Stop("fixture", false); e.WorkedCallsOnBand = (band, own) => new[] { "JA1AAA" }; e.Start(At);
                    e.RequestCallerReply = d => commands.Add("CALLER " + d.Message);
                    e.Cqs.Observe(CqD(At.AddSeconds(15), "JA2ZZB"), e.Status, At.AddSeconds(29));
                    e.Cqs.Failed("JA2ZZB", At.AddSeconds(28), true);
                    e.OnDecode(D(At.AddSeconds(15), "JA2ZZB", "PM95"), At.AddSeconds(29)); e.Tick(At.AddSeconds(29.1), true);
                    Assert(e.IncomingMonitorCall == "JA2ZZB" && commands.SequenceEqual(new[] { "CALLER K1ABC JA2ZZB PM95" }),
                        "The one-per-target limit or search cooldown suppressed a station calling us");
                });
                Run("Monitoring prefers new callers over previously worked callers including a priority DXCC", () => {
                    List<string> commands; AutoCqEngine e = MonitorActive(out commands);
                    e.Stop("fixture", false); e.WorkedCallsOnBandMode = (band, mode, own) => new[] { "JA1AAA", "W2ABC" };
                    e.Options.PriorityDxcc = new[] { "JA" }; e.IsPriorityCall = call => call.StartsWith("JA"); e.Start(At);
                    e.RequestCallerReply = d => commands.Add("CALLER " + d.Message);
                    foreach (string call in new[] { "JA1AAA", "W2ABC", "DL3ZZB" }) e.OnDecode(D(At.AddSeconds(15), call, "-12"), At.AddSeconds(29));
                    e.Tick(At.AddSeconds(29.1), true);
                    Assert(e.IncomingMonitorCall == "DL3ZZB" && commands.Single() == "CALLER K1ABC DL3ZZB -12",
                        "Monitoring did not apply the existing new-contact priority to incoming callers");
                });
                Run("Monitoring preserves an answered QSO, an active TX frame and the final confirmation wait", () => {
                    foreach (string state in new[] { "answered", "transmitting", "waiting73" })
                    {
                        List<string> commands; AutoCqEngine e = MonitorActive(out commands);
                        var target = CqD(At.AddSeconds(15), "JA2ZZB"); target.IsNew = false;
                        e.OnDecode(target, At.AddSeconds(29)); e.Tick(At.AddSeconds(29), true);
                        e.OnStatus(S(true, "JA2ZZB"), At.AddSeconds(29.1));
                        e.OnLine(Tx(At.AddSeconds(30), "JA2ZZB", state == "waiting73" ? "RR73" : "-11"), At.AddSeconds(30));
                        if (state == "answered") e.OnDecode(D(At.AddSeconds(45), "JA2ZZB", "-10"), At.AddSeconds(59));
                        e.OnDecode(D(At.AddSeconds(45), "DL3ZZB", "JO62"), At.AddSeconds(59));
                        e.OnLine(DecodeFinished(At.AddSeconds(59.1)), At.AddSeconds(59.1));
                        if (state == "transmitting") { var tx = S(true, "JA2ZZB"); tx.Transmitting = true; e.OnStatus(tx, At.AddSeconds(59.2)); }
                        e.Tick(At.AddSeconds(59.3), true);
                        Assert(e.Monitoring && commands.Count == 1 && e.Waiting.Find("DL3ZZB") != null,
                            "An incoming caller interrupted " + state);
                    }
                });
                Run("Monitoring ignores history, off-air, stale, future and third-party messages as incoming calls", () => {
                    foreach (string kind in new[] { "history", "offair", "stale", "future", "thirdparty", "mode", "own", "73" })
                    {
                        List<string> commands; AutoCqEngine e = MonitorActive(out commands);
                        var d = D(At.AddSeconds(15), "DL3ZZB", "JO62"); DateTime now = At.AddSeconds(29);
                        if (kind == "history") d.IsNew = false;
                        if (kind == "offair") d.OffAir = true;
                        if (kind == "stale") now = At.AddSeconds(136);
                        if (kind == "future") d.Milliseconds = (uint)At.AddSeconds(45).TimeOfDay.TotalMilliseconds;
                        if (kind == "thirdparty") d.Message = "W7ZZD DL3ZZB JO62";
                        if (kind == "mode") d.Mode = ":";
                        if (kind == "own") d.Message = "K1ABC K1ABC FN20";
                        if (kind == "73") d.Message = "K1ABC DL3ZZB 73";
                        e.OnDecode(d, now); e.Tick(now.AddSeconds(0.1), true);
                        Assert(e.Monitoring && e.IncomingMonitorCall == "" && commands.Count == 0, "Invalid incoming call was answered: " + kind);
                    }
                });
                Run("Incoming monitoring replies keep DXCC exclusions and continue past a rejected native reply", () => {
                    List<string> commands; AutoCqEngine e = MonitorActive(out commands);
                    e.CanCall = call => call != "DL3ZZB";
                    e.RequestCallerReply = d => commands.Add("CALLER " + d.Message);
                    foreach (string call in new[] { "DL3ZZB", "W2ABC", "F1ABC" }) e.OnDecode(D(At.AddSeconds(15), call, "-12"), At.AddSeconds(29));
                    e.Tick(At.AddSeconds(29.1), true);
                    Assert(e.IncomingMonitorCall == "W2ABC", "An excluded DXCC was answered");
                    e.OnLine("UDP Reply request ignored, decode not found: K1ABC W2ABC -12", At.AddSeconds(29.2)); e.Tick(At.AddSeconds(29.3), true);
                    Assert(e.Monitoring && e.IncomingMonitorCall == "F1ABC" && commands.Count == 2,
                        "One rejected caller disabled monitoring or blocked the next caller");
                });
                Run("An unanswered incoming call returns monitoring to receive at its configured limit", () => {
                    List<string> commands; AutoCqEngine e = MonitorActive(out commands); DateTime deadline = e.SessionDeadline;
                    e.RequestCallerReply = d => commands.Add("CALLER " + d.Message);
                    e.OnDecode(D(At.AddSeconds(15), "DL3ZZB", "JO62"), At.AddSeconds(29)); e.Tick(At.AddSeconds(29.1), true);
                    e.OnStatus(S(true, "DL3ZZB"), At.AddSeconds(29.2));
                    e.OnLine(Tx(At.AddSeconds(30), "DL3ZZB", "-11"), At.AddSeconds(30));
                    e.OnLine(Tx(At.AddSeconds(60), "DL3ZZB", "-11"), At.AddSeconds(60));
                    e.OnLine(DecodeFinished(At.AddSeconds(89)), At.AddSeconds(89)); e.Tick(At.AddSeconds(89.1), true);
                    e.OnLine("Halt Tx triggered at RX: UDP request", At.AddSeconds(89.2));
                    e.OnStatus(S(false, "DL3ZZB"), At.AddSeconds(89.3)); e.Tick(At.AddSeconds(89.4), true);
                    Assert(e.Monitoring && !e.Status.TxEnabled && e.IncomingMonitorCall == "" && e.SessionDeadline == deadline
                        && commands.SequenceEqual(new[] { "CALLER K1ABC DL3ZZB JO62", "HALT" })
                        && e.Waiting.Find("DL3ZZB").PauseReason == CallerQueue.NoAnswer, "The caller retried forever, started CQ or stopped monitoring");
                    var target = CqD(At.AddSeconds(90), "JA2ZZB"); target.IsNew = false;
                    e.OnDecode(target, At.AddSeconds(104)); e.Tick(At.AddSeconds(104.1), true);
                    Assert(commands.Last() == "REPLY CQ JA2ZZB FN20", "Monitoring did not resume its configured targets");
                });
                Run("At the monitor deadline only incoming calls received before expiry are completed", () => {
                    List<string> commands; AutoCqEngine e = MonitorActive(out commands, 1); DateTime deadline = e.SessionDeadline;
                    e.RequestCallerReply = d => commands.Add("CALLER " + d.Message);
                    e.OnDecode(D(At.AddSeconds(45), "DL3ZZB", "JO62"), At.AddSeconds(59)); e.Tick(At.AddSeconds(60), true);
                    Assert(e.Monitoring && e.Finishing && e.IncomingMonitorCall == "DL3ZZB", "The timer dropped an accepted incoming caller");
                    e.OnStatus(S(true, "DL3ZZB"), At.AddSeconds(60.1));
                    e.OnLine(Tx(At.AddSeconds(90), "DL3ZZB", "RR73"), At.AddSeconds(90));
                    e.OnDecode(D(At.AddSeconds(105), "W2ABC", "FN20"), At.AddSeconds(119));
                    e.OnDecode(D(At.AddSeconds(105), "DL3ZZB", "73"), At.AddSeconds(119.1));
                    e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Autolog", At.AddSeconds(119.2));
                    e.OnStatus(S(false, "DL3ZZB"), At.AddSeconds(119.3)); e.Tick(At.AddSeconds(119.4), true);
                    Assert(!e.Armed && e.SessionDeadline == deadline && commands.Count == 1 && e.ConfirmedCount == 1,
                        "A post-deadline caller restarted TX or the timer cut off the accepted QSO");
                });
                Run("A caller is answered after a manually chosen monitor QSO ends", () => {
                    List<string> commands; AutoCqEngine e = MonitorActive(out commands); DateTime deadline = e.SessionDeadline;
                    e.RequestCallerReply = d => commands.Add("CALLER " + d.Message);
                    e.OnLine(ManualSelection(At.AddSeconds(2), "DL3ZZB"), At.AddSeconds(2));
                    e.OnStatus(S(true, "DL3ZZB"), At.AddSeconds(2.1));
                    e.OnLine(Tx(At.AddSeconds(30), "DL3ZZB", "RR73"), At.AddSeconds(30));
                    e.OnLine("QSO logged: DL3ZZB", At.AddSeconds(30.1));
                    e.OnDecode(D(At.AddSeconds(45), "F1ABC", "JN07"), At.AddSeconds(59));
                    e.OnDecode(D(At.AddSeconds(45), "DL3ZZB", "73"), At.AddSeconds(59.1));
                    e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Autolog", At.AddSeconds(59.2));
                    e.OnStatus(S(false, "DL3ZZB"), At.AddSeconds(59.3)); e.Tick(At.AddSeconds(59.4), true);
                    Assert(e.Monitoring && e.ManualMonitorCall == "" && e.IncomingMonitorCall == "F1ABC"
                        && e.SessionDeadline == deadline && commands.Single() == "CALLER K1ABC F1ABC JN07",
                        "Completing a manual QSO hid the next incoming caller");
                });
                Run("Award monitoring answers a non-target caller in the same slot as a manual QSO's final 73", () => {
                    foreach (bool farewellFirst in new[] { false, true })
                    {
                        var commands = new List<string>();
                        var e = new AutoCqEngine { UseNativeSelection = true, DxccForCall = call => call.StartsWith("R") ? "UA" : null };
                        e.RequestHalt = () => commands.Add("HALT");
                        e.RequestCallerReply = d => commands.Add("CALLER " + d.Message);
                        e.RequestReply = d => commands.Add("TARGET " + d.Message);
                        var tula = HamlogAward.Gunsmiths.Create();
                        tula.Selected = true;
                        var catalog = new AwardCatalog { Programs = new List<AwardProgram> { tula } };
                        var search = new AwardSearch(catalog, "K1ABC", "20m", "FT8", e.DxccForCall);
                        e.OnStatus(S(false, ""), At);
                        e.Start(At, search);
                        e.OnLine(ManualSelection(At.AddSeconds(2), "W3AAA"), At.AddSeconds(2));
                        e.OnStatus(S(true, "W3AAA"), At.AddSeconds(2.1));
                        e.OnLine(Tx(At.AddSeconds(30), "W3AAA", "RR73"), At.AddSeconds(30));
                        e.OnLine("QSO logged: W3AAA", At.AddSeconds(30.1));
                        var caller = D(At.AddSeconds(45), "DO1ZZZ", "JO53");
                        var farewell = D(At.AddSeconds(45), "W3AAA", "73");
                        e.OnDecode(farewellFirst ? farewell : caller, At.AddSeconds(58.9));
                        e.OnDecode(farewellFirst ? caller : farewell, At.AddSeconds(59));
                        var awardTarget = CqD(At.AddSeconds(45), "R3PAA");
                        awardTarget.IsNew = false; // Replay of the left pane from JTDX.
                        e.OnDecode(awardTarget, At.AddSeconds(59.1));
                        e.OnLine(DecodeFinished(At.AddSeconds(59.2)), At.AddSeconds(59.2));
                        e.Tick(At.AddSeconds(59.3), true);
                        e.OnLine("Halt Tx triggered at RX: UDP request", At.AddSeconds(59.4));
                        e.OnStatus(S(false, "W3AAA"), At.AddSeconds(59.5));
                        e.Tick(At.AddSeconds(59.6), true);
                        Assert(search.MatchCount("DO1ZZZ", At) == 0 && search.MatchCount("R3PAA", At) == 1,
                            "The fixture failed to distinguish the caller from an award target");
                        Assert(e.Monitoring && e.ManualMonitorCall == "" && e.IncomingMonitorCall == "DO1ZZZ"
                            && commands.SequenceEqual(new[] { "HALT", "CALLER K1ABC DO1ZZZ JO53" }),
                            "An award target took precedence over the incoming call after a manual QSO");
                    }
                });
                Run("Monitoring answers another fresh call from a station it worked during the session", () => {
                    List<string> commands; AutoCqEngine e = MonitorActive(out commands);
                    e.RequestCallerReply = d => commands.Add("CALLER " + d.Message);
                    e.OnDecode(D(At.AddSeconds(15), "DL3ZZB", "JO62"), At.AddSeconds(29)); e.Tick(At.AddSeconds(29.1), true);
                    e.OnStatus(S(true, "DL3ZZB"), At.AddSeconds(29.2));
                    e.OnLine(Tx(At.AddSeconds(30), "DL3ZZB", "RR73"), At.AddSeconds(30));
                    e.OnDecode(D(At.AddSeconds(45), "DL3ZZB", "73"), At.AddSeconds(59));
                    e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Autolog", At.AddSeconds(59.1));
                    e.OnStatus(S(false, "DL3ZZB"), At.AddSeconds(59.2)); e.Tick(At.AddSeconds(59.3), true);
                    e.OnDecode(D(At.AddSeconds(75), "DL3ZZB", "JO62"), At.AddSeconds(89)); e.Tick(At.AddSeconds(89.1), true);
                    Assert(e.Monitoring && commands.SequenceEqual(Enumerable.Repeat("CALLER K1ABC DL3ZZB JO62", 2)),
                        "Monitoring skipped a new call from the completed contact");
                });
                Run("After our final 73 every active mode answers an allowed repeat before sending another call", () => {
                    foreach (string operation in new[] { "cq", "monitor", "awards" })
                    {
                        var commands = new List<string>();
                        var e = new AutoCqEngine { UseNativeSelection = true, CanCall = call => call != "JA1AAA",
                            DxccForCall = call => call.StartsWith("R") ? "UA" : null };
                        e.Options.ExcludedDxcc = new[] { "JA" };
                        e.Options.MonitorEnabled = operation == "monitor";
                        e.Options.MonitorDxcc = new[] { "UA" };
                        e.RequestCq = () => commands.Add("CQ");
                        e.RequestHalt = () => commands.Add("HALT");
                        e.RequestReply = d => commands.Add("TARGET " + d.Message);
                        e.RequestCallerReply = d => commands.Add("CALLER " + d.Message);
                        e.OnStatus(operation == "cq" ? S(true, "R3PAA") : S(false, ""), At);
                        e.OnLine("QSO logged: DL3ZZB", At);
                        AwardSearch search = null;
                        if (operation == "awards")
                        {
                            var program = HamlogAward.Gunsmiths.Create();
                            program.Selected = true;
                            var catalog = new AwardCatalog { Programs = new List<AwardProgram> { program } };
                            search = new AwardSearch(catalog, "K1ABC", "20m", "FT8", e.DxccForCall);
                        }
                        e.Start(At, search);
                        if (operation != "cq")
                        {
                            var target = CqD(At.AddSeconds(15), "R3PAA"); target.IsNew = false;
                            e.OnDecode(target, At.AddSeconds(29)); e.Tick(At.AddSeconds(29), true);
                            Assert(commands.Count == 1 && commands[0].StartsWith("TARGET "), "The fixture did not start a monitored QSO");
                            e.OnStatus(S(true, "R3PAA"), At.AddSeconds(29.1));
                            commands.Clear();
                        }
                        e.OnLine(Tx(At.AddSeconds(30), "R3PAA", "R-09"), At.AddSeconds(30));
                        e.OnDecode(D(At.AddSeconds(45), "R3PAA", "RR73"), At.AddSeconds(59));
                        e.OnLine(Tx(At.AddSeconds(60), "R3PAA", "73"), At.AddSeconds(60));
                        e.OnDecode(D(At.AddSeconds(75), "JA1AAA", "PM95"), At.AddSeconds(89));
                        e.OnDecode(D(At.AddSeconds(75), "DL3ZZB", "JO62"), At.AddSeconds(89.1));
                        var next = CqD(At.AddSeconds(75), "R3PBB"); next.IsNew = false;
                        e.OnDecode(next, At.AddSeconds(89.2));
                        e.OnLine(DecodeFinished(At.AddSeconds(89.3)), At.AddSeconds(89.3)); e.Tick(At.AddSeconds(89.4), true);
                        e.OnLine("Halt Tx triggered at RX: UDP request", At.AddSeconds(89.5));
                        e.OnStatus(S(false, "R3PAA"), At.AddSeconds(89.6)); e.Tick(At.AddSeconds(89.7), true);
                        Assert(e.Armed && e.ConfirmedCount == 1 && commands.SequenceEqual(new[] { "HALT", "CALLER K1ABC DL3ZZB JO62" }),
                            "An excluded station or new outgoing call displaced an eligible repeat: " + operation);
                    }
                });
                Run("A queued caller remains eligible when it repeats after the monitor timer expires", () => {
                    List<string> commands; AutoCqEngine e = MonitorActive(out commands, 1);
                    e.RequestCallerReply = d => commands.Add("CALLER " + d.Message);
                    e.OnDecode(D(At.AddSeconds(45), "DL3ZZB", "JO62"), At.AddSeconds(59));
                    e.OnDecode(D(At.AddSeconds(45), "W2ABC", "FN20"), At.AddSeconds(59.1)); e.Tick(At.AddSeconds(60), true);
                    e.OnStatus(S(true, "DL3ZZB"), At.AddSeconds(60.1));
                    e.OnLine(Tx(At.AddSeconds(90), "DL3ZZB", "RR73"), At.AddSeconds(90));
                    e.OnDecode(D(At.AddSeconds(105), "W2ABC", "FN20"), At.AddSeconds(119));
                    e.OnDecode(D(At.AddSeconds(105), "DL3ZZB", "73"), At.AddSeconds(119.1));
                    e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Autolog", At.AddSeconds(119.2));
                    e.OnStatus(S(false, "DL3ZZB"), At.AddSeconds(119.3)); e.Tick(At.AddSeconds(119.4), true);
                    Assert(e.Monitoring && e.Finishing && e.IncomingMonitorCall == "W2ABC" && commands.Count == 2,
                        "Refreshing an accepted queue entry made the timer discard it");
                });
                Run("A late TX log cannot erase the pending acknowledgement of our own terminal Halt", () => {
                    foreach (bool monitoring in new[] { false, true })
                    foreach (string mode in new[] { "FT8", "FT4" })
                    foreach (bool logged in new[] { false, true })
                    foreach (bool statusFirst in new[] { false, true })
                    foreach (string payload in new[] { "-08", "RR73" })
                    {
                        List<string> commands; AutoCqEngine e = TerminalHaltRace(out commands, monitoring, mode, logged);
                        double slot = mode == "FT4" ? 7.5 : 15; DateTime boundary = At.AddSeconds(4 * slot);
                        RadioStatus off = S(false, "W1AW"); off.Mode = mode;
                        e.OnLine(Tx(boundary, "W1AW", payload).Replace("FT8:", mode + ":"), boundary.AddSeconds(0.977));
                        if (statusFirst) e.OnStatus(off, boundary.AddSeconds(0.98));
                        e.OnLine("Halt Tx triggered at TX: UDP request 14.074 MHz FT8: W1AW K1ABC " + payload, boundary.AddSeconds(1));
                        Assert(e.Armed, "Our own Halt was misclassified as a manual stop after the TX log");
                        if (!statusFirst) e.OnStatus(off, boundary.AddSeconds(1.01));
                        e.Tick(boundary.AddSeconds(1.02), true);
                        Assert(e.Armed && e.ConfirmedCount == 1 && e.Missing73Count == 0
                            && commands.SequenceEqual(monitoring ? new[] { "HALT" } : new[] { "HALT", "CQ" }),
                            "The acknowledged stop disabled automation or lost the completed QSO");
                        if (monitoring)
                        {
                            var target = CqD(boundary, "JA2ZZB"); target.Mode = mode == "FT4" ? ":" : "~"; target.IsNew = false;
                            e.OnDecode(target, boundary.AddSeconds(1.1)); e.Tick(boundary.AddSeconds(1.2), true);
                            Assert(e.Monitoring && commands.Last() == "REPLY CQ JA2ZZB FN20",
                                "Monitoring did not continue after consuming its own Halt acknowledgement");
                        }
                    }
                });
                Run("Late status for the previous DX cannot cancel a new RR73 exchange", () => {
                    foreach (string mode in new[] { "FT4", "FT8" })
                    foreach (string operation in new[] { "CQ", "monitoring", "awards", "expeditions" })
                    foreach (string outcome in new[] { "confirm", "manual stop", "timeout", "wrong DX", "retune", "later change" })
                    {
                        AwardSearch awards = null;
                        ExpeditionSearch expeditions = null;
                        if (operation == "awards")
                        {
                            var program = HamlogAward.Gunsmiths.Create();
                            program.Selected = true;
                            var catalog = new AwardCatalog { Programs = new List<AwardProgram> { program } };
                            awards = new AwardSearch(catalog, "K1ABC", "20m", mode, call => null);
                        }
                        if (operation == "expeditions")
                            expeditions = new ExpeditionSearch(new ExpeditionOptions(), new ExpeditionCatalog(), call => null);
                        List<string> commands;
                        AutoCqEngine engine = TerminalHaltRace(out commands, operation != "CQ", mode, true, awards, expeditions);
                        var diagnostics = new List<string>();
                        engine.Diagnostic = diagnostics.Add;
                        engine.RequestCallerReply = decode => commands.Add(decode.Message);
                        double slot = mode == "FT4" ? 7.5 : 15;
                        DateTime tx = At.AddSeconds(4 * slot);
                        var caller = D(tx.AddSeconds(-slot), "W2ABC", "R-06");
                        caller.Mode = mode == "FT4" ? ":" : "~";
                        engine.OnDecode(caller, tx.AddSeconds(0.85));
                        engine.OnLine("Halt Tx triggered at RX: UDP request", tx.AddSeconds(0.9));
                        RadioStatus idle = S(false, ""); idle.Mode = mode;
                        engine.OnStatus(idle, tx.AddSeconds(0.94));
                        engine.Tick(tx.AddSeconds(1), true);
                        Assert(commands.SequenceEqual(new[] { "HALT", caller.Message }), "The next caller was not selected");

                        // The log of the new TX is consumed while the prior DX
                        // status is still queued and the new status is in flight.
                        engine.OnLine(Tx(tx, "W2ABC", "RR73").Replace("FT8:", mode + ":"), tx.AddSeconds(1.1));
                        RadioStatus previous = S(true, "W1AW"); previous.Mode = mode;
                        engine.OnStatus(previous, tx.AddSeconds(1.12));
                        Assert(engine.Armed && engine.WaitingCall == "W2ABC" && engine.Status == idle,
                            "A queued status for the previous DX stopped or replaced the new exchange");
                        Assert(diagnostics.Count == 1 && diagnostics[0].Contains("W1AW") && diagnostics[0].Contains("W2ABC"),
                            "The handoff diagnostic did not identify both callsigns");
                        if (outcome == "manual stop")
                            engine.OnLine("Halt Tx triggered at RX: Halt Tx button clicked", tx.AddSeconds(1.2));
                        if (outcome == "wrong DX")
                        {
                            RadioStatus wrong = S(true, "W3ABC"); wrong.Mode = mode;
                            engine.OnStatus(wrong, tx.AddSeconds(1.2));
                        }
                        if (outcome == "retune")
                        {
                            previous.Frequency += 1000;
                            engine.OnStatus(previous, tx.AddSeconds(1.2));
                        }
                        if (outcome == "confirm" || outcome == "later change")
                        {
                            RadioStatus selected = S(true, "W2ABC"); selected.Mode = mode;
                            engine.OnStatus(selected, tx.AddSeconds(1.2));
                            Assert(engine.Armed && engine.Status == selected, "The real acknowledgement was not accepted");
                            if (outcome == "later change") engine.OnStatus(previous, tx.AddSeconds(1.3));
                        }
                        engine.Tick(tx.AddSeconds(7.5), true);
                        Assert(engine.Armed == (outcome == "confirm"),
                            operation + " mishandled " + outcome + " during the handoff in " + mode);
                    }
                });
                Run("A receipt from JTDX allows a late reply confirmation once per command", () => {
                    foreach (string mode in new[] { "FT4", "FT8" })
                    foreach (string route in new[] { "band list", "incoming", "monitoring target", "awards", "expeditions" })
                    foreach (string outcome in new[] { "confirmed", "no receipt", "wrong message", "old receipt", "duplicate",
                        "manual stop", "connection lost", "rejected" })
                    {
                        AwardSearch awards = null;
                        ExpeditionSearch expeditions = null;
                        if (route == "awards")
                        {
                            var program = HamlogAward.Gunsmiths.Create();
                            program.Selected = true;
                            var catalog = new AwardCatalog { Programs = new List<AwardProgram> { program } };
                            awards = new AwardSearch(catalog, "K1ABC", "20m", mode, call => null);
                        }
                        if (route == "expeditions")
                            expeditions = new ExpeditionSearch(new ExpeditionOptions(), new ExpeditionCatalog(), call => null);
                        bool monitoring = route == "monitoring target" || awards != null || expeditions != null;
                        List<string> commands;
                        AutoCqEngine engine = TerminalHaltRace(out commands, monitoring, mode, true, awards, expeditions);
                        engine.RequestReply = engine.RequestCallerReply = d => commands.Add(d.Message);
                        var diagnostics = new List<string>();
                        engine.Diagnostic = diagnostics.Add;
                        if (route == "band list")
                        {
                            engine.Options.PriorityDxcc = new[] { "K" };
                            engine.Options.PriorityAfter73 = true;
                            engine.IsPriorityCall = call => call == "W2ABC";
                        }
                        string peer = route == "monitoring target" ? "JA2ZZB" : "W2ABC";
                        double slot = mode == "FT4" ? 7.5 : 15;
                        DateTime boundary = At.AddSeconds(4 * slot);
                        var decode = D(boundary.AddSeconds(-slot), peer, "FN20");
                        decode.Mode = mode == "FT4" ? ":" : "~";
                        if (route == "band list" || route == "monitoring target")
                        {
                            decode.IsNew = false;
                            decode.Message = "W3ABC " + peer + " RR73";
                        }
                        engine.OnDecode(decode, boundary.AddSeconds(0.85));
                        engine.OnLine("Halt Tx triggered at RX: UDP request", boundary.AddSeconds(0.9));
                        RadioStatus idle = S(false, ""); idle.Mode = mode;
                        engine.OnStatus(idle, boundary.AddSeconds(0.94));
                        DateTime sent = boundary.AddSeconds(1).AddTicks(5000);
                        engine.Tick(sent, true);
                        Assert(commands.SequenceEqual(new[] { "HALT", decode.Message }), "The test did not send the requested reply");

                        string echo = outcome == "wrong message" ? decode.Message + " OTHER" : decode.Message;
                        DateTime received = outcome == "old receipt" ? sent.AddSeconds(-2) : sent;
                        DateTime afterOrdinaryTimeout = sent.AddSeconds(7.54);
                        DateTime readAt = route == "band list" && outcome == "confirmed" ? afterOrdinaryTimeout : sent.AddMilliseconds(80);
                        if (outcome != "no receipt")
                            engine.OnLine(received.ToString("yyyyMMdd_HHmmss.fff") + "(0)  UDP Reply request received: " + echo + "    ",
                                readAt);
                        if (outcome == "duplicate")
                            engine.OnLine(sent.AddSeconds(5).ToString("yyyyMMdd_HHmmss.fff") + "(0)  UDP Reply request received: " + echo,
                                sent.AddSeconds(5.1));
                        engine.Tick(afterOrdinaryTimeout, true);
                        if (outcome == "no receipt" || outcome == "wrong message" || outcome == "old receipt")
                        {
                            Assert(!engine.Armed && commands.Last() == "HALT" && diagnostics.Count == 0,
                                "A missing or unrelated receipt extended the command timeout");
                            continue;
                        }
                        Assert(engine.Armed && commands.Count == 2 && diagnostics.Count == 1 && diagnostics[0].Contains(peer),
                            route + " stopped or repeated the command despite its receipt in " + mode);
                        DateTime late = sent.AddSeconds(7.577);
                        if (outcome == "confirmed")
                        {
                            RadioStatus selected = S(true, peer); selected.Mode = mode;
                            engine.OnStatus(selected, late);
                            DateTime nextTx = boundary.AddSeconds(2 * slot);
                            engine.OnLine(Tx(nextTx, peer, "-12").Replace("FT8:", mode + ":"), nextTx);
                        }
                        if (outcome == "manual stop")
                            engine.OnLine("Halt Tx triggered at RX: Halt Tx button clicked", late);
                        if (outcome == "rejected")
                            engine.OnLine("UDP Reply request ignored, decode not found: " + decode.Message, late);
                        if (outcome == "connection lost") engine.Tick(late, false);
                        engine.Tick(sent.AddSeconds(2 * slot + 6.2), true);
                        Assert(engine.Armed == (outcome == "confirmed" || outcome == "rejected"),
                            route + " mishandled " + outcome + " after a reply receipt in " + mode);
                    }
                });
                Run("Terminal Halt acknowledgement handling still honours manual and external stops", () => {
                    foreach (string reason in new[] { "Halt Tx button clicked", "TX halted via Escape button" })
                    {
                        List<string> commands; AutoCqEngine e = TerminalHaltRace(out commands, true, "FT8", true);
                        e.OnLine(Tx(At.AddSeconds(60), "W1AW", "-08"), At.AddSeconds(60.9));
                        e.OnLine("Halt Tx triggered at TX: " + reason, At.AddSeconds(61));
                        Assert(!e.Armed, "An operator stop was swallowed as our own Halt acknowledgement");
                    }
                    List<string> external; AutoCqEngine idle = MonitorActive(out external);
                    idle.OnLine("Halt Tx triggered at RX: UDP request", At.AddSeconds(1));
                    Assert(!idle.Armed && external.Count == 0, "An unrequested external UDP stop was ignored");
                });
                Run("A missing acknowledgement after the late TX race still times out without resuming", () => {
                    List<string> commands; AutoCqEngine e = TerminalHaltRace(out commands, true, "FT8", true);
                    e.OnLine(Tx(At.AddSeconds(60), "W1AW", "-08"), At.AddSeconds(60.977));
                    e.Tick(At.AddSeconds(106), true);
                    Assert(!e.Armed && commands.SequenceEqual(new[] { "HALT" }), "A lost Halt acknowledgement resumed TX or left monitoring stuck");
                });
                Run("Manual JTDX selection outside monitoring targets survives status and log arrival in either order", () => {
                    foreach (string mode in new[] { "FT8", "FT4" })
                    foreach (bool newPeriod in new[] { true, false })
                    foreach (bool statusFirst in new[] { true, false })
                    {
                        List<string> commands; AutoCqEngine e = MonitorActive(out commands);
                        e.Stop("fixture", false); RadioStatus idle = S(false, ""); idle.Mode = mode;
                        e.OnStatus(idle, At); e.Start(At); DateTime deadline = e.SessionDeadline;
                        RadioStatus selected = S(true, "DL3ZZB"); selected.Mode = mode; selected.TxFirst = newPeriod;
                        if (statusFirst)
                        {
                            e.OnStatus(selected, At.AddSeconds(2.12)); e.Tick(At.AddSeconds(2.15), true);
                            Assert(e.Monitoring && commands.Count == 0, "Status cancelled the user's transmission before its log entry");
                        }
                        e.OnLine(ManualSelection(At.AddSeconds(2.1), "DL3ZZB"), At.AddSeconds(2.16));
                        if (!statusFirst) e.OnStatus(selected, At.AddSeconds(2.2));
                        e.OnLine(Tx(At.AddSeconds(2.222), "DL3ZZB", "-17").Replace("FT8:", mode + ":"), At.AddSeconds(2.222));
                        e.Tick(At.AddSeconds(3.3), true);
                        Assert(e.Monitoring && e.ManualMonitorCall == "DL3ZZB" && e.HoldNativeSelection
                            && e.SessionDeadline == deadline && e.CqTxFirst == newPeriod && commands.Count == 0,
                            "The manual call was halted, duplicated, changed the timer or lost its TX period");
                        Assert(e.Options.MonitorDxcc.SequenceEqual(new[] { "JA" }) && e.Options.MonitorCalls.Length == 0,
                            "The manual exception changed the configured targets");
                    }
                });
                Run("Ordinary CQ accepts a manual station on the other TX period and continues after QSO", () => {
                    foreach (bool statusFirst in new[] { true, false })
                    {
                        var commands = new List<string>();
                        var e = new AutoCqEngine { UseNativeSelection = true };
                        e.RequestCq = () => commands.Add("CQ");
                        e.RequestHalt = () => commands.Add("HALT");
                        e.OnStatus(S(false, ""), At);
                        e.Start(At);
                        DateTime deadline = e.SessionDeadline;
                        e.Tick(At.AddSeconds(1), true);
                        Assert(commands.SequenceEqual(new[] { "CQ" }), "The fixture did not issue the first CQ");

                        const string selected = "DL3ZZB";
                        RadioStatus manual = S(true, selected); manual.TxFirst = false;
                        if (statusFirst) e.OnStatus(manual, At.AddSeconds(2.12));
                        e.OnLine(ManualSelection(At.AddSeconds(2.1), selected), At.AddSeconds(2.15));
                        if (!statusFirst) e.OnStatus(manual, At.AddSeconds(2.2));
                        Assert(e.Armed && !e.Monitoring && !e.CqTxFirst && e.SessionDeadline == deadline
                            && commands.SequenceEqual(new[] { "CQ" }), "Manual takeover halted or duplicated the selected exchange");

                        e.OnLine(Tx(At.AddSeconds(15), selected, "RR73"), At.AddSeconds(15));
                        e.OnLine("QSO logged: " + selected, At.AddSeconds(15.1));
                        e.OnLine(Rx(At.AddSeconds(30), selected, "73"), At.AddSeconds(44.2));
                        e.OnLine(DecodeFinished(At.AddSeconds(44.3)), At.AddSeconds(44.3));
                        e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Autolog", At.AddSeconds(44.4));
                        RadioStatus receive = S(false, ""); receive.TxFirst = false;
                        e.OnStatus(receive, At.AddSeconds(44.5));
                        e.Tick(At.AddSeconds(45), true);
                        Assert(e.Armed && e.ConfirmedCount == 1 && commands.SequenceEqual(new[] { "CQ", "CQ" }),
                            "The CQ program did not continue in the selected period after the manual QSO");
                    }
                });
                Run("An old automatic status does not interrupt a confirmed manual monitoring call", () => {
                    List<string> commands; AutoCqEngine e = MonitorActive(out commands);
                    DateTime deadline = e.SessionDeadline;
                    var target = CqD(At.AddSeconds(15), "JA2ZZB"); target.IsNew = false;
                    e.OnDecode(target, At.AddSeconds(29)); e.Tick(At.AddSeconds(29.1), true);
                    e.OnStatus(S(true, "JA2ZZB"), At.AddSeconds(29.2));
                    e.OnLine(Tx(At.AddSeconds(30), "JA2ZZB", "-17"), At.AddSeconds(30));

                    e.OnLine(ManualSelection(At.AddSeconds(46.9), "DL3ZZB"), At.AddSeconds(47));
                    e.OnStatus(S(true, "JA2ZZB"), At.AddSeconds(47.02));
                    RadioStatus manual = S(true, "DL3ZZB"); manual.TxFirst = false;
                    e.OnStatus(manual, At.AddSeconds(47.15));
                    e.Tick(At.AddSeconds(48.2), true);
                    Assert(e.Monitoring && e.ManualMonitorCall == "DL3ZZB" && !e.CqTxFirst
                        && e.SessionDeadline == deadline && commands.SequenceEqual(new[] { "REPLY CQ JA2ZZB FN20" }),
                        "A superseded status halted the accepted manual call");
                });
                Run("A manually selected QSO returns to the original monitoring targets with or without a log record", () => {
                    foreach (bool logged in new[] { true, false })
                    {
                        List<string> commands; AutoCqEngine e = MonitorActive(out commands); DateTime deadline = e.SessionDeadline;
                        e.OnStatus(S(true, "DL3ZZB"), At.AddSeconds(2.12));
                        e.OnLine(ManualSelection(At.AddSeconds(2.1), "DL3ZZB"), At.AddSeconds(2.16));
                        e.OnLine(Tx(At.AddSeconds(30), "DL3ZZB", "RR73"), At.AddSeconds(30));
                        if (logged) e.OnLine("QSO logged: DL3ZZB", At.AddSeconds(30.1));
                        e.OnLine(Rx(At.AddSeconds(45), "DL3ZZB", "73"), At.AddSeconds(59));
                        e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Autolog", At.AddSeconds(59.1));
                        e.OnStatus(S(false, "DL3ZZB"), At.AddSeconds(59.2)); e.Tick(At.AddSeconds(59.3), true);
                        Assert(e.Monitoring && e.ManualMonitorCall == "" && e.ConfirmedCount == 1
                            && e.SessionDeadline == deadline && commands.Count == 0, "The one-off QSO ended monitoring or triggered CQ");
                        var other = CqD(At.AddSeconds(60), "DL4ZZC"); other.IsNew = false;
                        e.OnDecode(other, At.AddSeconds(74)); e.Tick(At.AddSeconds(74.1), true);
                        Assert(commands.Count == 0, "A manual exception admitted another non-target station");
                        other = CqD(At.AddSeconds(75), "JA2ZZB"); other.IsNew = false;
                        e.OnDecode(other, At.AddSeconds(89)); e.Tick(At.AddSeconds(89.1), true);
                        Assert(commands.SequenceEqual(new[] { "REPLY CQ JA2ZZB FN20" }), "The original target list was not resumed");
                    }
                });
                Run("A manual monitoring call uses the attempt limit and returns to receive", () => {
                    List<string> commands; AutoCqEngine e = MonitorActive(out commands); DateTime deadline = e.SessionDeadline;
                    e.OnLine(ManualSelection(At.AddSeconds(2), "DL3ZZB"), At.AddSeconds(2));
                    e.OnStatus(S(true, "DL3ZZB"), At.AddSeconds(2.1));
                    e.OnLine(Tx(At.AddSeconds(30), "DL3ZZB", "-15"), At.AddSeconds(30));
                    e.OnLine(Tx(At.AddSeconds(60), "DL3ZZB", "-15"), At.AddSeconds(60));
                    e.OnLine(DecodeFinished(At.AddSeconds(89)), At.AddSeconds(89)); e.Tick(At.AddSeconds(89.1), true);
                    Assert(commands.SequenceEqual(new[] { "HALT" }), "A manual call ignored the configured attempt limit");
                    e.OnLine("Halt Tx triggered at RX: UDP request", At.AddSeconds(89.2));
                    e.OnStatus(S(false, "DL3ZZB"), At.AddSeconds(89.3)); e.Tick(At.AddSeconds(89.4), true);
                    e.Tick(At.AddSeconds(90), true);
                    Assert(e.Monitoring && e.ManualMonitorCall == "" && e.SessionDeadline == deadline
                        && !e.Status.TxEnabled && commands.Count == 1, "The failed manual call stopped monitoring or called CQ");
                });
                Run("The monitoring call button allows a one-off selection outside filters, worked limits and retry pauses", () => {
                    foreach (string message in new[] { "CQ DL3ZZB JO62", "W7ZZD DL3ZZB 73", "W7ZZD DL3ZZB RRR", "W7ZZD DL3ZZB RR73" })
                    {
                        List<string> commands; AutoCqEngine e = MonitorActive(out commands); DateTime deadline = e.SessionDeadline;
                        var d = CqD(At.AddSeconds(15), "DL3ZZB"); d.IsNew = false; d.Message = message;
                        e.OnDecode(d, At.AddSeconds(29)); e.Cqs.Failed("DL3ZZB", At.AddSeconds(28), true); e.Cqs.Complete("DL3ZZB");
                        e.SelectCq("DL3ZZB", At.AddSeconds(29.1)); e.Tick(At.AddSeconds(29.2), true);
                        Assert(e.Monitoring && e.ManualMonitorCall == "DL3ZZB" && e.SessionDeadline == deadline
                            && commands.SequenceEqual(new[] { "REPLY " + message }), "The explicit selection was filtered, duplicated or stopped the monitor");
                    }
                });
                Run("A manual module selection waits for the current monitored QSO", () => {
                    List<string> commands; AutoCqEngine e = MonitorActive(out commands);
                    var first = CqD(At.AddSeconds(15), "JA2ZZB"); first.IsNew = false;
                    e.OnDecode(first, At.AddSeconds(29)); e.Tick(At.AddSeconds(29.1), true);
                    e.OnStatus(S(true, "JA2ZZB"), At.AddSeconds(29.2));
                    var second = CqD(At.AddSeconds(30), "DL3ZZB"); second.IsNew = false;
                    e.OnDecode(second, At.AddSeconds(44)); e.SelectCq("DL3ZZB", At.AddSeconds(44.1)); e.Tick(At.AddSeconds(44.2), true);
                    Assert(commands.Count == 1, "Manual selection interrupted the current QSO");
                    e.OnLine(Tx(At.AddSeconds(60), "JA2ZZB", "RR73"), At.AddSeconds(60));
                    e.OnLine(Rx(At.AddSeconds(75), "JA2ZZB", "73"), At.AddSeconds(89));
                    e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Autolog", At.AddSeconds(89.1));
                    e.OnStatus(S(false, "JA2ZZB"), At.AddSeconds(89.2)); e.Tick(At.AddSeconds(89.3), true); e.Tick(At.AddSeconds(89.4), true);
                    Assert(e.Monitoring && e.ManualMonitorCall == "DL3ZZB" && commands.Last() == "REPLY CQ DL3ZZB FN20", "The saved manual selection was lost after the QSO");
                });
                Run("Manual selection cannot bypass DXCC exclusions or an expired monitor timer", () => {
                    foreach (bool expired in new[] { true, false })
                    {
                        List<string> commands; AutoCqEngine e = MonitorActive(out commands, 1);
                        if (!expired) e.CanCall = call => call != "DL3ZZB";
                        DateTime now = At.AddSeconds(expired ? 60 : 2);
                        var d = CqD(now, "DL3ZZB"); d.IsNew = false; e.OnDecode(d, now);
                        bool rejected = false; try { e.SelectCq("DL3ZZB", now); } catch (InvalidOperationException) { rejected = true; }
                        Assert(rejected, "The call button bypassed an exclusion or expired timer");
                        e.OnStatus(S(true, "DL3ZZB"), now);
                        e.OnLine(ManualSelection(now, "DL3ZZB"), now); e.Tick(now.AddSeconds(1.1), true);
                        Assert(!e.Armed && commands.SequenceEqual(new[] { "HALT" }), "A native manual call bypassed an exclusion or expired timer");
                    }
                });
                Run("Unconfirmed, stale and mismatched manual selections do not authorize out-of-target TX", () => {
                    foreach (string bad in new[] { "missing", "stale", "wrongCall", "previousSession", "noTimestamp" })
                    {
                        List<string> commands; AutoCqEngine e = MonitorActive(out commands);
                        e.OnStatus(S(true, "DL3ZZB"), At.AddSeconds(10));
                        DateTime when = At.AddSeconds(bad == "stale" ? 5 : bad == "previousSession" ? -1 : 10);
                        if (bad != "missing") e.OnLine(bad == "noTimestamp" ? "double click on call processed: DL3ZZB; EnableTx On, AutoTx On"
                            : ManualSelection(when, bad == "wrongCall" ? "DL4ZZC" : "DL3ZZB"), At.AddSeconds(10.1));
                        e.OnStatus(S(true, "DL3ZZB"), At.AddSeconds(10.5)); e.Tick(At.AddSeconds(11.1), true);
                        Assert(!e.Armed && commands.SequenceEqual(new[] { "HALT" }), "Unverified TX was accepted: " + bad);
                    }
                });
                Run("Manual monitoring still respects stop, connection loss and context changes", () => {
                    foreach (string reason in new[] { "stop", "connection", "band", "mode", "watchdog", "CQ" })
                    {
                        List<string> commands; AutoCqEngine e = MonitorActive(out commands);
                        e.OnLine(ManualSelection(At.AddSeconds(2), "DL3ZZB"), At.AddSeconds(2));
                        e.OnStatus(S(true, "DL3ZZB"), At.AddSeconds(2.1));
                        if (reason == "stop") e.OnLine("Halt Tx triggered at RX: Halt Tx button clicked", At.AddSeconds(3));
                        else if (reason == "connection") e.Tick(At.AddSeconds(3), false);
                        else if (reason == "CQ") e.OnLine(Tx(At.AddSeconds(3), "CQ", "FN20"), At.AddSeconds(3));
                        else
                        {
                            RadioStatus s = S(true, "DL3ZZB");
                            if (reason == "band") s.Frequency = 7074000;
                            if (reason == "mode") s.Mode = "FT4";
                            if (reason == "watchdog") s.Watchdog = true;
                            e.OnStatus(s, At.AddSeconds(3));
                        }
                        Assert(!e.Armed && e.ManualMonitorCall == "" && !e.HoldNativeSelection, "Manual selection suppressed " + reason);
                    }
                });
                Run("The monitoring timer finishes a manually selected QSO without resetting the session", () => {
                    List<string> commands; AutoCqEngine e = MonitorActive(out commands, 1);
                    e.OnLine(ManualSelection(At.AddSeconds(2), "DL3ZZB"), At.AddSeconds(2)); e.OnStatus(S(true, "DL3ZZB"), At.AddSeconds(2.1));
                    e.OnLine(Tx(At.AddSeconds(60), "DL3ZZB", "RR73"), At.AddSeconds(60));
                    e.OnLine("QSO logged: DL3ZZB", At.AddSeconds(60)); e.Tick(At.AddSeconds(61), true);
                    Assert(e.Monitoring && e.Finishing && commands.Count == 0, "The timer cut off the manually selected QSO");
                    e.OnLine(Rx(At.AddSeconds(75), "DL3ZZB", "RR73"), At.AddSeconds(89));
                    e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Autolog", At.AddSeconds(89.1));
                    e.OnStatus(S(false, "DL3ZZB"), At.AddSeconds(89.2)); e.Tick(At.AddSeconds(89.3), true);
                    Assert(!e.Armed && e.ConfirmedCount == 1 && commands.Count == 0, "The timer restarted monitoring after the manual QSO");
                });
                Run("A confirmed monitored QSO completes its target even when the log record is missing", () => {
                    List<string> commands; AutoCqEngine e = MonitorActive(out commands);
                    var first = CqD(At.AddSeconds(15), "JA2ZZB"); first.IsNew = false;
                    e.OnDecode(first, At.AddSeconds(29)); e.Tick(At.AddSeconds(29.1), true);
                    e.OnStatus(S(true, "JA2ZZB"), At.AddSeconds(29.2));
                    e.OnLine(Tx(At.AddSeconds(30), "JA2ZZB", "RR73"), At.AddSeconds(30));
                    e.OnLine(Rx(At.AddSeconds(45), "JA2ZZB", "73"), At.AddSeconds(59));
                    e.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Autolog", At.AddSeconds(59.1));
                    e.OnStatus(S(false, "JA2ZZB"), At.AddSeconds(59.2)); e.Tick(At.AddSeconds(59.3), true);
                    var second = CqD(At.AddSeconds(60), "JA3ZZC"); second.IsNew = false;
                    e.OnDecode(second, At.AddSeconds(74)); e.Tick(At.AddSeconds(74.1), true);
                    Assert(e.Monitoring && e.Cqs.Find("JA2ZZB").Worked && e.LoggedCount == 0
                        && commands.Count == 1, "One-per-target was not updated after a confirmed exchange without a record");
                    e.SelectCq("JA3ZZC", At.AddSeconds(74.2)); e.Tick(At.AddSeconds(74.3), true);
                    Assert(e.ManualMonitorCall == "JA3ZZC" && commands.Last() == "REPLY CQ JA3ZZC FN20",
                        "Explicit manual choice could not override a completed monitoring target");
                });
                Run("An unavailable manual selection returns to monitoring without calling a different non-target", () => {
                    List<string> commands; AutoCqEngine e = MonitorActive(out commands);
                    var d = CqD(At.AddSeconds(15), "DL3ZZB"); d.IsNew = false;
                    e.OnDecode(d, At.AddSeconds(29)); e.SelectCq("DL3ZZB", At.AddSeconds(29.1)); e.Tick(At.AddSeconds(29.2), true);
                    e.OnLine("UDP Reply request ignored, decode not found: CQ DL3ZZB FN20", At.AddSeconds(29.3));
                    e.Tick(At.AddSeconds(29.4), true);
                    Assert(e.Monitoring && e.ManualMonitorCall == "" && commands.Count == 1, "A rejected manual reply disabled monitoring");
                    d = CqD(At.AddSeconds(30), "DL4ZZC"); d.IsNew = false; e.OnDecode(d, At.AddSeconds(44)); e.Tick(At.AddSeconds(44.1), true);
                    Assert(commands.Count == 1, "The unavailable selection widened monitoring targets");
                });
                Run("Proactive monitoring accepts only fresh visible target CQ, 73, RRR or RR73", () => {
                    foreach (string allowed in new[] { "CQ", "73", "RRR", "RR73" })
                    {
                        List<string> commands;
                        AutoCqEngine engine = MonitorActive(out commands);
                        engine.AddQueuedCall("DL3ZZB", At);
                        var unrelated = D(At, "W3ABC", "FN20"); unrelated.Message = "W7ZZD W3ABC FN20";
                        engine.OnDecode(unrelated, At.AddSeconds(14));
                        foreach (string ignored in new[] { "CQ DL1ZZC JO62", "CQ JA1ZZA PM95", "W7ZZD JA2ZZB -10", "QRZ JA2ZZB" })
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
                Run("Monitoring selects the RR73 sender by DXCC, callsign and mask in FT8 and FT4", () => {
                    foreach (string mode in new[] { "FT8", "FT4" })
                    foreach (string kind in new[] { "dxcc", "call", "mask" })
                    {
                        List<string> commands; AutoCqEngine e = MonitorActive(out commands);
                        e.Stop("Configure monitor fixture", false);
                        e.Options.MonitorDxcc = kind == "dxcc" ? new[] { "JA" } : new string[0];
                        e.Options.MonitorCalls = kind == "call" ? new[] { "JA2ZZB" } : new string[0];
                        e.Options.MonitorMasks = kind == "mask" ? new[] { "JA2Z*" } : new string[0];
                        RadioStatus receiving = S(false, ""); receiving.Mode = mode;
                        e.OnStatus(receiving, At); e.Start(At);
                        double slot = mode == "FT4" ? 7.5 : 15;
                        DateTime now = At.AddSeconds(2 * slot - 1);
                        DecodedMessage d = D(At.AddSeconds(slot), "JA2ZZB", "RR73");
                        d.Mode = mode == "FT4" ? ":" : "~"; d.IsNew = false;
                        d.Message = "W7ZZD JA2ZZB RR73";
                        e.OnDecode(d, now); e.Tick(now, true);
                        Assert(commands.SequenceEqual(new[] { "REPLY W7ZZD JA2ZZB RR73" })
                            && e.LoggedCount == 0 && e.ConfirmedCount == 0 && !e.Awaiting73,
                            "RR73 selected its recipient, completed a nonexistent QSO, or missed a matching target");
                    }
                });
                Run("RR73 monitoring preserves exclusions, completed targets and fresh third-party messages", () => {
                    foreach (string blocked in new[] { "excluded", "completed", "stale", "addressed" })
                    {
                        List<string> commands; AutoCqEngine e = MonitorActive(out commands);
                        e.Stop("Configure monitor fixture", false);
                        if (blocked == "excluded") e.CanCall = call => call != "JA2ZZB";
                        if (blocked == "completed") e.WorkedCallsOnBand = (band, own) => new[] { "JA1ZZA" };
                        e.Start(At);
                        DecodedMessage d = D(At.AddSeconds(blocked == "stale" ? -45 : 15), "JA2ZZB", "RR73");
                        d.IsNew = false; d.Message = (blocked == "addressed" ? "K1ABC" : "W7ZZD") + " JA2ZZB RR73";
                        e.OnDecode(d, At.AddSeconds(29)); e.Tick(At.AddSeconds(29.1), true);
                        Assert(e.Monitoring && commands.Count == 0, "An ineligible RR73 started a call: " + blocked);
                    }
                });
                Run("A monitored RR73 target returns to RX after unanswered calls and can select another RR73", () => {
                    List<string> commands; AutoCqEngine e = MonitorActive(out commands);
                    DecodedMessage d = D(At.AddSeconds(15), "JA2ZZB", "RR73");
                    d.IsNew = false; d.Message = "W7ZZD JA2ZZB RR73";
                    e.OnDecode(d, At.AddSeconds(29)); e.Tick(At.AddSeconds(29.1), true);
                    e.OnStatus(S(true, "JA2ZZB"), At.AddSeconds(29.2));
                    e.OnLine(Tx(At.AddSeconds(30), "JA2ZZB", "-15"), At.AddSeconds(30));
                    e.OnLine(Tx(At.AddSeconds(60), "JA2ZZB", "-15"), At.AddSeconds(60));
                    e.OnLine(DecodeFinished(At.AddSeconds(89)), At.AddSeconds(89)); e.Tick(At.AddSeconds(89.1), true);
                    e.OnLine("Halt Tx triggered at RX: UDP request", At.AddSeconds(89.2));
                    e.OnStatus(S(false, "JA2ZZB"), At.AddSeconds(89.3));
                    e.Tick(At.AddSeconds(89.4), true); e.Tick(At.AddSeconds(89.5), true);
                    Assert(e.Monitoring && !e.Status.TxEnabled && commands.SequenceEqual(new[] { "REPLY W7ZZD JA2ZZB RR73", "HALT" }),
                        "A target selected by RR73 started CQ or exhausted the monitor session");
                    d = D(At.AddSeconds(90), "JA3ZZC", "RR73"); d.IsNew = false; d.Message = "W7ZZD JA3ZZC RR73";
                    e.OnDecode(d, At.AddSeconds(104)); e.Tick(At.AddSeconds(104.2), true);
                    Assert(commands.Last() == "REPLY W7ZZD JA3ZZC RR73", "Monitoring did not resume for the next RR73 target");
                });
                Run("Early terminal messages do not bypass the call limit before a report", () => {
                    foreach (string response in new[] { "RR73", "RRR", "73", "R-08" })
                    {
                        List<string> commands; AutoCqEngine e = MonitorActive(out commands);
                        var target = D(At.AddSeconds(15), "JA2ZZB", "RR73");
                        target.IsNew = false; target.Message = "W7ZZD JA2ZZB RR73";
                        e.OnDecode(target, At.AddSeconds(29)); e.Tick(At.AddSeconds(29.1), true);
                        e.OnStatus(S(true, "JA2ZZB"), At.AddSeconds(29.2));
                        e.OnLine(Tx(At.AddSeconds(30), "JA2ZZB", "-19"), At.AddSeconds(30));
                        e.OnLine(Tx(At.AddSeconds(60), "JA2ZZB", "-18"), At.AddSeconds(60));
                        e.OnLine(Rx(At.AddSeconds(75), "JA2ZZB", response), At.AddSeconds(89));
                        e.OnLine(DecodeFinished(At.AddSeconds(89.1)), At.AddSeconds(89.1));
                        e.Tick(At.AddSeconds(89.2), true);
                        Assert(e.Monitoring && e.LoggedCount == 0 && e.ConfirmedCount == 0
                            && commands.Count == (response == "R-08" ? 1 : 2)
                            && (response == "R-08" || commands.Last() == "HALT"),
                            "An early terminal message bypassed the limit, or a genuine report was interrupted: " + response);
                    }
                });
                Run("A late TX status after an automatic retry does not stop a receiving monitor", () => {
                    foreach (bool duplicateCounter in new[] { false, true })
                    {
                        List<string> commands; AutoCqEngine e = MonitorActive(out commands);
                        var notices = new List<string>(); e.Notice = notices.Add;
                        var target = CqD(At.AddSeconds(15), "JA2ZZB"); target.IsNew = false;
                        e.OnDecode(target, At.AddSeconds(29)); e.Tick(At.AddSeconds(29.1), true);
                        e.OnStatus(S(true, "JA2ZZB"), At.AddSeconds(29.2));
                        e.OnLine(Tx(At.AddSeconds(30), "JA2ZZB", "-17"), At.AddSeconds(30));
                        e.OnStatus(S(false, "JA2ZZB"), At.AddSeconds(59.1));
                        e.OnLine("Halt Tx triggered at RX: Called station not decoded", At.AddSeconds(59.2));
                        e.Tick(At.AddSeconds(59.3), true);
                        DateTime retry = e.Cqs.Find("JA2ZZB").MonitorRetryUntilUtc;

                        e.OnStatus(S(true, "JA2ZZB"), At.AddSeconds(88.9));
                        if (duplicateCounter) e.OnLine("Halt Tx triggered at RX: AutoCQ, counter triggered", At.AddSeconds(89.1));
                        e.OnStatus(S(false, duplicateCounter ? "JA2ZZB" : ""), At.AddSeconds(89.2));
                        e.Tick(At.AddSeconds(90.1), true);
                        Assert(e.Monitoring && !e.Status.TxEnabled && commands.Count == 1 && retry > At
                            && e.Cqs.Find("JA2ZZB").MonitorRetryUntilUtc == retry
                            && notices.Contains("JTDX не декодировал станцию JA2ZZB. Возвращаюсь на приём."),
                            "The old status stopped monitoring or extended the failed target's cooldown");

                        target = CqD(At.AddSeconds(105), "JA3ZZC"); target.IsNew = false;
                        e.OnDecode(target, At.AddSeconds(119)); e.Tick(At.AddSeconds(119.1), true);
                        Assert(commands.Last() == "REPLY CQ JA3ZZC FN20", "Monitoring failed to select a new target after an old status");
                    }
                });
                Run("A native counter stop reports the attempt limit only when the calls reached it", () => {
                    foreach (int attempts in new[] { 1, 2 })
                    {
                        List<string> commands; AutoCqEngine e = MonitorActive(out commands);
                        var notices = new List<string>(); e.Notice = notices.Add;
                        var target = CqD(At.AddSeconds(15), "JA2ZZB"); target.IsNew = false;
                        e.OnDecode(target, At.AddSeconds(29)); e.Tick(At.AddSeconds(29.1), true);
                        e.OnStatus(S(true, "JA2ZZB"), At.AddSeconds(29.2));
                        e.OnLine(Tx(At.AddSeconds(30), "JA2ZZB", "-17"), At.AddSeconds(30));
                        if (attempts == 2) e.OnLine(Tx(At.AddSeconds(60), "JA2ZZB", "-18"), At.AddSeconds(60));
                        var other = D(At.AddSeconds(15 + attempts * 30), "JA2ZZB", "-10");
                        other.Message = "OE1AAA JA2ZZB -10";
                        DateTime end = At.AddSeconds(29 + attempts * 30);
                        e.OnDecode(other, end); e.OnStatus(S(false, ""), end.AddSeconds(0.1));
                        e.OnLine("Halt Tx triggered at RX: AutoCQ, counter triggered", end.AddSeconds(0.2));
                        e.Tick(end.AddSeconds(0.3), true);
                        string reason = attempts == 2 ? "Лимит попыток JA2ZZB исчерпан." : "JTDX завершил вызов JA2ZZB.";
                        Assert(e.Monitoring && notices.Contains(reason + " Возвращаюсь на приём.") && commands.Count == 1,
                            "The stop reason disagrees with the number of initial calls");
                    }
                });
                Run("JTDX stopping one busy target keeps monitoring and its original timer", () => {
                    foreach (bool statusFirst in new[] { false, true })
                    foreach (bool nextCaller in new[] { false, true })
                    {
                        List<string> commands;
                        AutoCqEngine e = MonitorActive(out commands);
                        DateTime deadline = e.SessionDeadline;
                        var target = CqD(At.AddSeconds(15), "JA2ZZB"); target.IsNew = false;
                        e.OnDecode(target, At.AddSeconds(29)); e.Tick(At.AddSeconds(29.1), true);
                        e.OnStatus(S(true, "JA2ZZB"), At.AddSeconds(29.2));
                        e.OnLine(Tx(At.AddSeconds(30), "JA2ZZB", "-11"), At.AddSeconds(30));
                        e.OnLine(Tx(At.AddSeconds(60), "JA2ZZB", "-11"), At.AddSeconds(60));
                        var other = D(At.AddSeconds(75), "JA2ZZB", "-16"); other.Message = "OE1AAA JA2ZZB -16";
                        e.OnDecode(other, At.AddSeconds(88.9));
                        if (nextCaller) e.OnDecode(D(At.AddSeconds(75), "W2ABC", "FN20"), At.AddSeconds(89));
                        if (statusFirst) e.OnStatus(S(false, "JA2ZZB"), At.AddSeconds(89.1));
                        e.OnLine("Halt Tx triggered at RX: readFromStdout, not owner of the frequency or reply to other ", At.AddSeconds(89.2));
                        if (!statusFirst) e.OnStatus(S(false, ""), At.AddSeconds(89.3));
                        e.Tick(At.AddSeconds(89.4), true);
                        Assert(e.Monitoring && e.SessionDeadline == deadline && !e.Status.TxEnabled
                            && e.LoggedCount == 0 && e.Cqs.MonitorTimeouts(At.AddSeconds(89.4)).Single().Call == "JA2ZZB",
                            "A native busy-target stop ended monitoring, lost the retry pause or changed the timer");
                        if (nextCaller)
                            Assert(commands.Last() == "REPLY K1ABC W2ABC FN20", "The caller was skipped after the busy station");
                        else
                        {
                            Assert(commands.Count == 1, "The busy station or CQ was called again");
                            var fresh = CqD(At.AddSeconds(90), "JA3ZZC"); fresh.IsNew = false;
                            e.OnDecode(fresh, At.AddSeconds(104)); e.Tick(At.AddSeconds(104.1), true);
                            Assert(commands.Last() == "REPLY CQ JA3ZZC FN20", "Monitoring did not select the next eligible target");
                        }
                    }
                });
                Run("A busy-target stop during a terminal exchange still waits for the last receive slot", () => {
                    List<string> commands;
                    AutoCqEngine e = MonitorActive(out commands);
                    var target = CqD(At.AddSeconds(15), "JA2ZZB"); target.IsNew = false;
                    e.OnDecode(target, At.AddSeconds(29)); e.Tick(At.AddSeconds(29.1), true);
                    e.OnStatus(S(true, "JA2ZZB"), At.AddSeconds(29.2));
                    e.OnLine(Tx(At.AddSeconds(30), "JA2ZZB", "RR73"), At.AddSeconds(30));
                    e.OnLine("QSO logged: JA2ZZB", At.AddSeconds(30.1));
                    e.OnLine("Halt Tx triggered at RX: readFromStdout, not owner of the frequency or reply to other", At.AddSeconds(59));
                    e.OnStatus(S(false, "JA2ZZB"), At.AddSeconds(59.1));
                    e.OnDecode(D(At.AddSeconds(45), "W2ABC", "FN31"), At.AddSeconds(59.2)); e.Tick(At.AddSeconds(59.3), true);
                    Assert(e.Monitoring && e.Awaiting73 && commands.Count == 1, "A busy-target stop cut off the final exchange");
                    e.OnDecode(D(At.AddSeconds(75), "JA2ZZB", "73"), At.AddSeconds(89));
                    e.Tick(At.AddSeconds(89.2), true);
                    Assert(e.Monitoring && e.ConfirmedCount == 1 && commands.Last() == "REPLY K1ABC W2ABC FN31",
                        "The terminal exchange did not resume monitoring after final 73");
                });
                Run("A selected busy target can be released before the enabled status arrives", () => {
                    List<string> commands;
                    AutoCqEngine e = MonitorActive(out commands);
                    var target = CqD(At.AddSeconds(15), "JA2ZZB"); target.IsNew = false;
                    e.OnDecode(target, At.AddSeconds(29)); e.Tick(At.AddSeconds(29.1), true);
                    e.OnLine("Halt Tx triggered at RX: readFromStdout, not owner of the frequency or reply to other", At.AddSeconds(29.2));
                    e.OnStatus(S(false, ""), At.AddSeconds(29.3));
                    e.Tick(At.AddSeconds(29.4), true);
                    Assert(e.Monitoring && e.Cqs.MonitorTimeouts(At.AddSeconds(29.4)).Single().Call == "JA2ZZB" && commands.Count == 1,
                        "A target rejected during selection ended monitoring or left the failed call active");
                });
                Run("An interrupted RR73 exchange continues CQ, monitoring and awards in FT8 and FT4", () => {
                    foreach (string operation in new[] { "CQ", "monitoring", "awards" })
                    foreach (string mode in new[] { "FT8", "FT4" })
                    foreach (bool statusFirst in new[] { false, true })
                    foreach (string outcome in new[] { "caller", "quiet", "final" })
                    {
                        const string peer = "R3PAA";
                        var commands = new List<string>();
                        var engine = new AutoCqEngine {
                            UseNativeSelection = true,
                            DxccForCall = call => call.StartsWith("R") ? "UA" : null
                        };
                        engine.Options.MonitorEnabled = operation == "monitoring";
                        engine.Options.MonitorDxcc = new[] { "UA" };
                        engine.RequestCq = () => commands.Add("CQ");
                        engine.RequestHalt = () => commands.Add("HALT");
                        engine.RequestCallerReply = decode => commands.Add("CALLER " + decode.Message);
                        RadioStatus initial = S(operation == "CQ", operation == "CQ" ? peer : "");
                        initial.Mode = mode;
                        engine.OnStatus(initial, At);
                        AwardSearch search = null;
                        if (operation == "awards")
                        {
                            var program = HamlogAward.Gunsmiths.Create();
                            program.Selected = true;
                            var catalog = new AwardCatalog { Programs = new List<AwardProgram> { program } };
                            search = new AwardSearch(catalog, "K1ABC", "20m", mode, engine.DxccForCall);
                        }
                        engine.Start(At, search);
                        DateTime deadline = engine.SessionDeadline;
                        if (operation != "CQ")
                        {
                            engine.OnLine(ManualSelection(At.AddSeconds(1), peer), At.AddSeconds(1));
                            RadioStatus selected = S(true, peer); selected.Mode = mode;
                            engine.OnStatus(selected, At.AddSeconds(1.1));
                        }

                        double slot = mode == "FT4" ? 7.5 : 15;
                        string marker = mode == "FT4" ? ":" : "~";
                        DateTime first = At.AddSeconds(2 * slot);
                        DateTime repeat = first.AddSeconds(2 * slot);
                        engine.OnLine(Tx(first, peer, "RR73").Replace("FT8:", mode + ":"), first);
                        engine.OnLine("QSO logged: " + peer, first.AddMilliseconds(50));
                        DecodedMessage peerReport = D(first.AddSeconds(slot), peer, "R-20"); peerReport.Mode = marker;
                        engine.OnDecode(peerReport, repeat.AddMilliseconds(-800));
                        engine.OnLine(DecodeFinished(repeat.AddMilliseconds(-600)), repeat.AddMilliseconds(-600));
                        engine.Tick(repeat.AddMilliseconds(-500), true);
                        Assert(engine.TerminalRepeatMessage == 4, "The addressed report did not authorize RR73");
                        engine.OnLine(Tx(repeat, peer, "RR73").Replace("FT8:", mode + ":"), repeat);

                        DateTime rx = repeat.AddSeconds(slot), boundary = repeat.AddSeconds(2 * slot);
                        DecodedMessage elsewhere = D(rx, peer, "-13");
                        elsewhere.Mode = marker; elsewhere.Message = "W3ABC " + peer + " -13";
                        engine.OnDecode(elsewhere, boundary.AddMilliseconds(-850));
                        var receiving = S(false, peer); receiving.Mode = mode; receiving.Decoding = true;
                        if (statusFirst) engine.OnStatus(receiving, boundary.AddMilliseconds(-800));
                        engine.OnLine("Halt Tx triggered at RX: readFromStdout, not owner of the frequency or reply to other",
                            boundary.AddMilliseconds(-750));
                        if (!statusFirst) engine.OnStatus(receiving, boundary.AddMilliseconds(-700));
                        engine.Tick(boundary.AddMilliseconds(-600), true);
                        Assert(engine.Armed && engine.Awaiting73 && commands.Count == 0,
                            operation + " stopped or started another TX before RX completed");

                        if (outcome != "quiet")
                        {
                            DecodedMessage response = outcome == "final" ? D(rx, peer, "73") : D(rx, "W2ABC", "FN20");
                            response.Mode = marker;
                            engine.OnDecode(response, boundary.AddMilliseconds(-450));
                        }
                        engine.OnLine(DecodeFinished(boundary.AddMilliseconds(-330)), boundary.AddMilliseconds(-330));
                        RadioStatus idle = S(false, ""); idle.Mode = mode;
                        engine.OnStatus(idle, boundary.AddMilliseconds(-250));
                        engine.Tick(boundary.AddMilliseconds(-200), true);
                        string[] expected = outcome == "caller" ? new[] { "CALLER K1ABC W2ABC FN20" }
                            : operation == "CQ" ? new[] { "CQ" } : new string[0];
                        Assert(engine.Armed && engine.Monitoring == (operation != "CQ") && engine.SessionDeadline == deadline
                            && engine.LoggedCount == 1 && engine.ConfirmedCount == (outcome == "final" ? 1 : 0)
                            && engine.Missing73Count == (outcome == "final" ? 0 : 1) && commands.SequenceEqual(expected),
                            operation + " did not preserve the QSO result or continue the session in " + mode);
                    }
                });
                Run("A busy call selected by JTDX can end before the first transmission in CQ mode", () => {
                    List<string> commands;
                    AutoCqEngine engine = Active(out commands);
                    engine.UseNativeSelection = true;
                    DateTime end = At.AddSeconds(14);
                    const string busy = "Halt Tx triggered at RX: readFromStdout, not owner of the frequency or reply to other";
                    engine.OnStatus(S(false, "W1AW"), end);
                    engine.OnLine(busy, end.AddMilliseconds(100));
                    engine.OnStatus(S(false, ""), end.AddMilliseconds(200));
                    engine.OnLine(busy, end.AddMilliseconds(250));
                    engine.Tick(end.AddMilliseconds(300), true);
                    Assert(engine.Armed && commands.SequenceEqual(new[] { "CQ" }),
                        "The busy target or its duplicate stop ended the CQ session");
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
                Run("Monitoring exposes only unanswered calls until their five-minute retry expires", () => {
                    List<string> commands;AutoCqEngine e=MonitorActive(out commands);
                    var target=CqD(At.AddSeconds(15),"JA2ZZB");target.IsNew=false;
                    e.OnDecode(target,At.AddSeconds(29));e.Tick(At.AddSeconds(29.1),true);
                    Assert(!e.Cqs.MonitorTimeouts(At.AddSeconds(29.1)).Any(),"A station still being called appeared as unanswered");
                    e.OnStatus(S(true,"JA2ZZB"),At.AddSeconds(29.2));
                    e.OnLine(Tx(At.AddSeconds(30),"JA2ZZB","-15"),At.AddSeconds(30));
                    e.OnLine(Tx(At.AddSeconds(60),"JA2ZZB","-15"),At.AddSeconds(60));
                    e.OnLine(DecodeFinished(At.AddSeconds(89)),At.AddSeconds(89));e.Tick(At.AddSeconds(89.1),true);
                    e.OnLine("Halt Tx triggered at RX: UDP request",At.AddSeconds(89.2));
                    e.OnStatus(S(false,"JA2ZZB"),At.AddSeconds(89.3));e.Tick(At.AddSeconds(89.4),true);e.Tick(At.AddSeconds(89.5),true);
                    CqStation paused=e.Cqs.MonitorTimeouts(At.AddSeconds(90)).Single();
                    Assert(paused.Call=="JA2ZZB" && (paused.MonitorRetryUntilUtc-At.AddSeconds(90)).TotalSeconds>299,"The retry pause was not recorded at the failed call");
                    DateTime end=paused.MonitorRetryUntilUtc;
                    target=CqD(end.AddSeconds(-15),"JA2ZZB");target.IsNew=false;e.OnDecode(target,end.AddSeconds(-1));
                    Assert(e.Cqs.MonitorTimeouts(end.AddSeconds(-1)).Any() && !e.Cqs.CanTry(paused,end.AddSeconds(-1),true),"A fresh CQ bypassed the pause");
                    Assert(!e.Cqs.MonitorTimeouts(end).Any() && e.Cqs.CanTry(paused,end,true),"The station did not leave the list exactly when its pause expired");
                });
                Run("A visible rc10 replay missing the final token matches the complete decode for long calls", () => {
                    foreach(string final in new[]{"73","RRR","RR73"})
                    {
                        List<string> commands;AutoCqEngine e=MonitorActive(out commands);
                        e.Options.MonitorDxcc=new string[0];e.Options.MonitorCalls=new[]{"JA2ZZB"};
                        e.Stop("configure",false);e.Start(At);
                        var raw=D(At.AddSeconds(15),"JA2ZZB",final);raw.Message="W7ZZDD JA2ZZB "+final;
                        e.OnDecode(raw,At.AddSeconds(28));e.Tick(At.AddSeconds(28.1),true);
                        Assert(commands.Count==0 && e.Cqs.Find("JA2ZZB")==null,"A hidden raw decode bypassed the JTDX filter");
                        var visible=raw.Copy();visible.IsNew=false;visible.Message="W7ZZDD JA2ZZB";
                        e.OnDecode(visible,At.AddSeconds(29));e.Tick(At.AddSeconds(29.1),true);
                        Assert(commands.SequenceEqual(new[]{"REPLY W7ZZDD JA2ZZB "+final}) && e.Cqs.Find("JA2ZZB").Signoff==final,"The replay lost the sender's final token: "+final);
                    }
                });
                Run("Truncated replay recovery never matches another period, signal, callsign or a non-final report", () => {
                    foreach(string mismatch in new[]{"time","snr","frequency","mode","callsign","report"})
                    {
                        var rows=new HeardCqList();RadioStatus status=S(false,"");rows.SetContext(status);
                        var raw=D(At,"JA2ZZB","73");raw.Message="W7ZZDD JA2ZZB "+(mismatch=="report"?"-12":"73");
                        rows.Observe(raw,status,At,false);
                        var visible=raw.Copy();visible.IsNew=false;visible.Message=mismatch=="callsign"?"W7ZZDE JA2ZZB":"W7ZZDD JA2ZZB";
                        if(mismatch=="time")visible.Milliseconds+=15000;
                        if(mismatch=="snr")visible.Snr++;
                        if(mismatch=="frequency")visible.DeltaFrequency++;
                        if(mismatch=="mode")visible.Mode=":";
                        rows.Observe(visible,status,At.AddSeconds(15),true);
                        Assert(rows.Find("JA2ZZB")==null,"An unrelated decode supplied a final sign-off: "+mismatch);
                    }
                });
                Run("Stopping monitoring clears its failed-call pauses without clearing ordinary search history", () => {
                    List<string> commands;AutoCqEngine e=MonitorActive(out commands);
                    foreach(string call in new[]{"JA2ZZB","JA3ZZC"})e.Cqs.Observe(CqD(At,call),e.Status,At);
                    e.Cqs.Failed("JA2ZZB",At,true);e.Cqs.Failed("JA3ZZC",At,false);
                    e.Stop("operator stop",false);
                    Assert(!e.Cqs.MonitorTimeouts(At).Any() && e.Cqs.Find("JA2ZZB").NextTryUtc==DateTime.MinValue,"Stopping left a hidden monitoring timeout");
                    Assert(e.Cqs.Find("JA3ZZC").NextTryUtc==At.AddMinutes(5),"Stopping changed an unrelated ordinary-search pause");
                    e.OnStatus(S(false,""),At.AddSeconds(1));e.Start(At.AddSeconds(1));
                    Assert(!e.Cqs.MonitorTimeouts(At.AddSeconds(1)).Any() && e.Cqs.CanTry(e.Cqs.Find("JA2ZZB"),At.AddSeconds(1),true),"A new monitor session inherited the old pause");
                });
                Run("Manual retry and a completed QSO remove a station from the monitoring pause list", () => {
                    var rows=new HeardCqList();rows.SetContext(S(false,""));
                    foreach(string call in new[]{"JA2ZZB","JA3ZZC"}){rows.Observe(CqD(At,call),S(false,""),At);rows.Failed(call,At,true);}
                    rows.Retry("JA2ZZB");rows.Complete("JA3ZZC");
                    Assert(!rows.MonitorTimeouts(At).Any() && rows.Find("JA2ZZB").NextTryUtc==DateTime.MinValue,"A retry or completion left a stale timeout row");
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
                Run("The first QSO hands off normally while worked-call ranking takes ownership after logging", () => {
                    foreach (bool logged in new[] { false, true })
                    {
                        var engine = new AutoCqEngine { UseNativeSelection = true };
                        var commands = new List<string>();
                        engine.RequestCq = () => commands.Add("CQ");
                        engine.RequestReply = d => commands.Add("REPLY");
                        engine.RequestHalt = () => commands.Add("HALT");
                        engine.OnStatus(S(true, "W1AW"), At); engine.Start(At);
                        engine.OnLine(Tx(At, "W1AW", "RR73"), At);
                        if (logged) engine.OnLine("QSO logged: W1AW", At);
                        Assert(engine.HoldNativeSelection, "Premature native selection was allowed");
                        engine.OnLine(Rx(At.AddSeconds(15), "W1AW", "73"), At.AddSeconds(29));
                        Assert(engine.HoldNativeSelection == logged, "Native selection ignored whether caller history was available");
                        if (logged)
                        {
                            engine.OnDecode(D(At.AddSeconds(15), "W3ABC", "FN20"), At.AddSeconds(29.1));
                            engine.OnLine("Halt Tx triggered at RX: FIN, end of QSO, Autolog", At.AddSeconds(29.2));
                            engine.OnStatus(S(false, ""), At.AddSeconds(29.3)); engine.Tick(At.AddSeconds(29.4), true);
                        }
                        engine.OnStatus(S(true, "W3ABC"), At.AddSeconds(29.5)); engine.Tick(At.AddSeconds(30), true);
                        Assert(engine.Armed && engine.Status.DxCall == "W3ABC" && commands.SequenceEqual(logged ? new[] { "REPLY" } : new string[0]),
                            "The next contact was lost or selected twice");
                    }
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
