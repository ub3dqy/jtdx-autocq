using System;
using System.Collections.Generic;
using System.Linq;
using JtdxAutoCq;

static class PauseTests
{
    static readonly DateTime At = new DateTime(2025, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static RadioStatus Radio(string mode, bool enabled = false, string dx = "", bool transmitting = false)
    {
        return new RadioStatus { Frequency = 14074000, Mode = mode, DxCall = dx,
            DeCall = "K1ABC", TxEnabled = enabled, TxFirst = true, Transmitting = transmitting };
    }

    static AutoCqEngine Engine(List<string> commands)
    {
        var engine = new AutoCqEngine { UseNativeSelection = true };
        engine.RequestCq = () => commands.Add("CQ");
        engine.RequestHalt = () => commands.Add("HALT");
        engine.RequestReply = decode => commands.Add("REPLY " + decode.Message);
        engine.RequestDirectCall = call => commands.Add("DIRECT " + call);
        return engine;
    }

    static DecodedMessage Decode(string mode, DateTime time, string message)
    {
        return new DecodedMessage { Mode = mode == "FT4" ? ":" : "~", IsNew = true,
            Milliseconds = (uint)time.TimeOfDay.TotalMilliseconds, Snr = -10, DeltaFrequency = 1500,
            DeltaTime = 0.1, Message = message };
    }

    static void Transmit(AutoCqEngine engine, string mode, DateTime time, string message)
    {
        engine.OnLine(time.ToString("yyyyMMdd_HHmmss.fff") + "(0) Transmitting 14.074 MHz + 1500Hz "
            + mode + ": " + message, time);
    }

    static void AcknowledgeHalt(AutoCqEngine engine, DateTime time, DateTime? issued = null)
    {
        engine.OnLine((issued ?? time).ToString("yyyyMMdd_HHmmss.fff") + "(0) Halt Tx triggered at RX: UDP request", time);
    }

    public static void RunAll(Action<string, Action> run)
    {
        run("Pause finishes the current FT8 or FT4 QSO and preserves the queue and running time", () => {
            foreach (string mode in new[] { "FT8", "FT4" })
            {
                double slot = mode == "FT4" ? 7.5 : 15;
                var commands = new List<string>();
                var engine = Engine(commands);
                engine.Options.RunMinutes = 2;
                engine.Options.QueueMinutes = 5;
                engine.OnStatus(Radio(mode, true, "W1AW"), At);
                engine.Start(At);
                engine.Waiting.Add("W2ABC", At);
                Transmit(engine, mode, At, "W1AW K1ABC R-10");
                engine.RequestPause(At.AddSeconds(0.5));
                engine.Tick(At.AddSeconds(1), true);
                Assert(engine.PauseRequested && !engine.Paused && commands.Count == 0,
                    "The current report exchange was halted");

                DateTime final = At.AddSeconds(2 * slot);
                Transmit(engine, mode, final, "W1AW K1ABC RR73");
                engine.OnLine("QSO logged: W1AW", final.AddMilliseconds(200));
                engine.Tick(final.AddSeconds(1), true);
                Assert(engine.Awaiting73 && commands.Count == 0, "The pause cut off the final reply window");
                DateTime end = At.AddSeconds(4 * slot);
                engine.OnDecode(Decode(mode, end.AddSeconds(-slot), "K1ABC W1AW 73"), end.AddMilliseconds(-400));
                engine.OnDecode(Decode(mode, end.AddSeconds(-slot), "K1ABC F1ABC JN18"), end.AddMilliseconds(-400));
                engine.Tick(end, true);
                Assert(commands.SequenceEqual(new[] { "HALT" }) && engine.ConfirmedCount == 1 && !engine.Paused,
                    "The last QSO did not finish normally");
                engine.OnStatus(Radio(mode, dx: "W1AW"), end.AddMilliseconds(20));
                engine.Tick(end.AddMilliseconds(30), true);
                Assert(engine.PauseRequested && !engine.Paused, "RX status alone completed the pause");
                AcknowledgeHalt(engine, end.AddMilliseconds(40));
                DateTime paused = end.AddMilliseconds(50);
                engine.Tick(paused, true);
                TimeSpan left = TimeSpan.FromMinutes(2) - (paused - At);
                Assert(engine.Armed && engine.Paused && !engine.PauseRequested && left == engine.RemainingTime(paused),
                    "Pause did not retain the original session balance");

                DateTime resume = paused.AddMinutes(9);
                engine.Tick(resume, true);
                Assert(engine.Paused && engine.RemainingTime(resume) == left && engine.Waiting.Rows.Count == 2,
                    "Paused time expired the session or queue");
                engine.Resume(resume);
                engine.Tick(resume, true);
                Assert(engine.SessionDeadline == resume.Add(left) && engine.Armed && !engine.Paused
                    && !engine.Waiting.Find("F1ABC").Available && commands.SequenceEqual(new[] { "HALT", "DIRECT W2ABC" }),
                    "Resume lost the session or replayed a stale decode");
            }
        });

        run("Pause waits for the current CQ frame and cancels an outstanding CQ safely", () => {
            foreach (string mode in new[] { "FT8", "FT4" })
            foreach (string state in new[] { "transmitting", "log first", "outstanding" })
            foreach (bool statusFirst in new[] { false, true })
            {
                bool outstanding = state == "outstanding";
                var commands = new List<string>();
                var engine = Engine(commands);
                engine.OnStatus(Radio(mode, !outstanding, transmitting: state == "transmitting"), At);
                engine.Start(At);
                if (outstanding) engine.Tick(At.AddSeconds(1), true);
                else Transmit(engine, mode, At, "CQ K1ABC FN20");
                engine.RequestPause(At.AddSeconds(2));
                engine.Tick(At.AddSeconds(2), true);
                if (!outstanding)
                    Assert(commands.Count == 0 && !engine.Paused, "Pause halted CQ mid-frame");
                DateTime halt = outstanding ? At.AddSeconds(2) : At.AddSeconds(mode == "FT4" ? 7.5 : 15);
                if (!outstanding)
                {
                    engine.OnStatus(Radio(mode, true), halt);
                    engine.Tick(halt, true);
                }
                Assert(engine.PauseRequested && !engine.Paused && engine.HoldNativeSelection
                    && commands.SequenceEqual(outstanding ? new[] { "CQ", "HALT" } : new[] { "HALT" }),
                    "An outstanding CQ command was not safely cancelled");

                if (statusFirst)
                {
                    engine.OnStatus(Radio(mode), halt.AddMilliseconds(10));
                    AcknowledgeHalt(engine, halt.AddMilliseconds(20), halt.AddMilliseconds(-100));
                    engine.Tick(halt.AddMilliseconds(30), true);
                    Assert(!engine.Paused, "A stale acknowledgement supported a new pause");
                    AcknowledgeHalt(engine, halt.AddMilliseconds(40));
                }
                else
                {
                    engine.OnStatus(Radio(mode, true), halt.AddMilliseconds(10));
                    AcknowledgeHalt(engine, halt.AddMilliseconds(20));
                    engine.Tick(halt.AddMilliseconds(30), true);
                    Assert(!engine.Paused && engine.Armed, "A Halt log allowed pause before a receive status");
                    engine.OnStatus(Radio(mode), halt.AddMilliseconds(40));
                }
                engine.Tick(halt.AddMilliseconds(50), true);
                Assert(engine.Paused && engine.Armed && commands.Count == (outstanding ? 2 : 1),
                    "The pause did not settle exactly once");
            }
        });

        run("Paused FT8 and FT4 monitoring, awards and expeditions resume their own mode", () => {
            foreach (string mode in new[] { "FT8", "FT4" })
            foreach (string kind in new[] { "targets", "awards", "expeditions" })
            foreach (bool active in new[] { false, true })
            {
                var commands = new List<string>();
                var engine = Engine(commands);
                AwardSearch awards = null;
                ExpeditionSearch expeditions = null;
                if (kind == "targets")
                {
                    engine.Options.MonitorEnabled = true;
                    engine.Options.MonitorCalls = new[] { "JA1AAA" };
                }
                if (kind == "awards")
                {
                    AwardProgram program = HamlogAward.Gunsmiths.Create();
                    program.Selected = true;
                    var catalog = new AwardCatalog { Programs = new List<AwardProgram> { program } };
                    awards = new AwardSearch(catalog, "K1ABC", "20m", mode, call => null);
                }
                if (kind == "expeditions")
                    expeditions = new ExpeditionSearch(new ExpeditionOptions(), new ExpeditionCatalog(), call => null);
                engine.OnStatus(Radio(mode), At);
                engine.Start(At, awards, expeditions);
                DateTime request = At.AddSeconds(1);
                if (active)
                {
                    engine.OnLine(request.ToString("yyyyMMdd_HHmmss.fff")
                        + "(0) double click on call processed: W1AW; EnableTx On, AutoTx On", request);
                    request = request.AddMilliseconds(100);
                    engine.OnStatus(Radio(mode, true, "W1AW"), request);
                }
                engine.RequestPause(request);
                engine.Tick(request, true);
                DateTime paused = request;
                if (active)
                {
                    Assert(!engine.Paused && commands.Count == 0, kind + " cut off a chosen contact");
                    double slot = mode == "FT4" ? 7.5 : 15;
                    Transmit(engine, mode, At.AddSeconds(2 * slot), "W1AW K1ABC RR73");
                    paused = At.AddSeconds(4 * slot);
                    engine.OnDecode(Decode(mode, paused.AddSeconds(-slot), "K1ABC W1AW 73"), paused.AddMilliseconds(-100));
                    engine.Tick(paused, true);
                    AcknowledgeHalt(engine, paused.AddMilliseconds(10));
                    engine.OnStatus(Radio(mode, dx: "W1AW"), paused.AddMilliseconds(20));
                    paused = paused.AddMilliseconds(30);
                    engine.Tick(paused, true);
                }
                int halted = active ? 1 : 0;
                Assert(engine.Paused && engine.Monitoring && commands.Count == halted, kind + " sent TX while entering pause");

                DateTime at = paused.AddSeconds(30);
                engine.OnDecode(Decode(mode, at, "K1ABC F1ABC JN18"), at);
                engine.Tick(at.AddSeconds(1), true);
                Assert(engine.Waiting.Find("F1ABC") != null && commands.Count == halted,
                    "An incoming caller was lost or answered while paused");
                engine.Resume(at.AddSeconds(2));
                engine.Tick(at.AddSeconds(2), true);
                Assert(engine.Monitoring && engine.Awards == awards && engine.Expeditions == expeditions
                    && commands.Count == halted + 1 && commands.Last() == "REPLY K1ABC F1ABC JN18",
                    kind + " did not resume the same monitoring mode");
            }
        });

        run("Pause respects stop, radio changes and connection loss, and rejects stale rearming", () => {
            foreach (string problem in new[] { "stop", "native stop", "frequency", "mode", "TX period", "watchdog", "network", "unexpected TX" })
            {
                var commands = new List<string>();
                var engine = Engine(commands);
                engine.OnStatus(Radio("FT8"), At);
                engine.Start(At);
                engine.RequestPause(At);
                engine.Tick(At, true);
                Assert(engine.Paused && commands.Count == 0, "The test did not reach the paused state");
                DateTime now = At.AddSeconds(5);
                AcknowledgeHalt(engine, now, At.AddSeconds(-1));
                Assert(engine.Armed && engine.Paused && commands.Count == 0, "An old Halt log changed the pause state");
                if (problem == "stop") engine.Stop("test stop", true);
                else if (problem == "native stop") engine.OnLine("Halt Tx triggered at RX: Halt Tx button clicked", now);
                else if (problem == "network") engine.Tick(now, false);
                else
                {
                    RadioStatus next = Radio(problem == "mode" ? "FT4" : "FT8", problem == "unexpected TX");
                    if (problem == "frequency") next.Frequency += 1000;
                    if (problem == "TX period") next.TxFirst = false;
                    if (problem == "watchdog") next.Watchdog = true;
                    engine.OnStatus(next, now);
                }
                engine.Tick(now.AddSeconds(2), true);
                Assert(!engine.Armed && !engine.Paused && !engine.PauseRequested && engine.RemainingTime(now) == TimeSpan.Zero
                    && !commands.Contains("CQ"), "Pause ignored " + problem);
            }
        });

        run("Pause fails closed after a missing Halt acknowledgement or receive status", () => {
            foreach (bool missingStatus in new[] { false, true })
            {
                var commands = new List<string>();
                var engine = Engine(commands);
                engine.OnStatus(Radio("FT8", true), At);
                engine.Start(At);
                engine.RequestPause(At);
                engine.Tick(At, true);
                if (missingStatus) AcknowledgeHalt(engine, At.AddMilliseconds(5));
                else engine.OnStatus(Radio("FT8"), At.AddMilliseconds(5));
                engine.Tick(At.AddSeconds(7), true);
                Assert(!engine.Armed && !engine.Paused && commands.SequenceEqual(new[] { "HALT", "HALT" }),
                    "A pause remained armed after its native confirmation deadline");
            }
        });
    }
}
