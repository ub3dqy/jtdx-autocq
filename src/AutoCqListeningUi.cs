using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Windows.Forms;

namespace JtdxAutoCq
{
    public sealed partial class MainForm
    {
        readonly List<ListeningHit> heardTargets = new List<ListeningHit>();
        ListeningDialog listeningDialog;
        ListeningWatch listeningWatch;
        NotifyIcon listeningNotifier;
        ListeningHit pendingListeningAlert;
        string listeningSourceStatus = "";
        int listeningAlertCount;
        bool listeningPaused, listeningCleanupRegistered, listeningHadConnection;
        DateTime nextListeningSweep, nextListeningAlert;

        bool RadioWorking(DateTime now)
        {
            return engine.Armed || startingWithHistory || ListeningConnected(now) && engine.Status != null
                && (engine.Status.TxEnabled || engine.Status.Transmitting);
        }

        bool ListeningConnected(DateTime now)
        {
            return demo || client != null && nativeStatusSeen && (now - lastSeen).TotalSeconds < 35;
        }

        void ShowListening()
        {
            if (listeningDialog != null && !listeningDialog.IsDisposed)
            { listeningDialog.Activate(); return; }
            var options = (preferences.Listening ?? new ListeningOptions()).Copy();
            listeningDialog = new ListeningDialog(options, chosen => Guard(() => StartListening(chosen)), StopListening,
                () => Guard(() => OpenGuide("listening-watch", "дежурный-приём")));
            listeningDialog.Fill(heardTargets);
            RenderListening(DateTime.UtcNow);
            listeningDialog.Show(this);
            if (!listeningCleanupRegistered)
            {
                Disposed += delegate { StopListening(); };
                listeningCleanupRegistered = true;
            }
        }

        void StartListening(ListeningOptions options)
        {
            DateTime now = DateTime.UtcNow;
            if (RadioWorking(now)) throw new InvalidOperationException("Завершите работу на передачу, чтобы включить дежурный приём.");
            if (options.UseMonitorTargets && engine.Options.MonitorDxcc.Length > 0 || engine.Options.ExcludedDxcc.Length > 0)
                CountryTable();
            else if (options.UseExpeditions)
            {
                try { CountryTable(); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            var watch = new ListeningWatch(options, engine.Options, preferences.ExpeditionSearch, expeditionCatalog,
                engine.DxccForCall, engine.CanCall);
            ListeningOptions previous = preferences.Listening;
            preferences.Listening = watch.Options.Copy();
            try { preferenceStore.Save(preferences); }
            catch { preferences.Listening = previous; throw; }
            listeningWatch = watch;
            listeningPaused = false;
            listeningHadConnection = ListeningConnected(now);
            listeningSourceStatus = "";
            nextListeningSweep = nextListeningAlert = DateTime.MinValue;
            heardTargets.Clear();
            if (listeningDialog != null && !listeningDialog.IsDisposed) listeningDialog.Fill(heardTargets);
            UpdateListening(now);
        }

        void StopListening()
        {
            listeningWatch = null;
            pendingListeningAlert = null;
            listeningAlertCount = 0;
            if (listeningNotifier != null)
            {
                listeningNotifier.Dispose();
                listeningNotifier = null;
            }
            if (!IsDisposed && !Disposing) RenderListening(DateTime.UtcNow);
        }

        void ObserveListeningDecode(DecodedMessage decode, DateTime now)
        {
            if (listeningWatch == null || RadioWorking(now) || !ListeningConnected(now)) return;
            ListeningHit hit = listeningWatch.Observe(decode, engine.Status, now);
            if (hit != null) AddListeningHit(hit);
        }

        void UpdateListening(DateTime now)
        {
            if (listeningWatch != null && RadioWorking(now))
            {
                if (!listeningPaused)
                {
                    listeningPaused = true;
                    listeningWatch.ResetDecodes();
                    pendingListeningAlert = null;
                    listeningAlertCount = 0;
                }
            }
            else if (listeningWatch != null)
            {
                listeningPaused = false;
                bool connected = ListeningConnected(now);
                if (listeningHadConnection && !connected) listeningWatch.ResetDecodes();
                listeningHadConnection = connected;
                if (!demo && now >= nextListeningSweep && listeningWatch.Sources.Any())
                {
                    nextListeningSweep = now.AddSeconds(1);
                    var sources = listeningWatch.Sources.ToArray();
                    foreach (ExpeditionSource source in sources) UpdateExpeditionFeed(source, now, false);
                    listeningSourceStatus = ExpeditionSourceStatus(sources, now);
                    string own = engine.Status == null ? "" : engine.Status.DeCall;
                    foreach (ListeningHit hit in listeningWatch.ObserveSpots(own, now)) AddListeningHit(hit);
                }
                if (!demo && pendingListeningAlert != null && now >= nextListeningAlert)
                    ShowListeningAlert(now);
            }
            RenderListening(now);
        }

        void AddListeningHit(ListeningHit hit)
        {
            int index = heardTargets.FindIndex(item => item.TimeUtc <= hit.TimeUtc);
            if (index < 0) index = heardTargets.Count;
            if (index < 100)
            {
                heardTargets.Insert(index, hit);
                if (heardTargets.Count > 100) heardTargets.RemoveAt(heardTargets.Count - 1);
                if (listeningDialog != null && !listeningDialog.IsDisposed) listeningDialog.Display(hit, index);
            }
            if (listeningWatch.Options.DesktopNotifications || listeningWatch.Options.Sound)
            {
                if (pendingListeningAlert == null || pendingListeningAlert.External && !hit.External
                    || pendingListeningAlert.External == hit.External && pendingListeningAlert.TimeUtc <= hit.TimeUtc)
                    pendingListeningAlert = hit;
                listeningAlertCount++;
            }
        }

        void ShowListeningAlert(DateTime now)
        {
            ListeningHit hit = pendingListeningAlert;
            int count = listeningAlertCount;
            pendingListeningAlert = null;
            listeningAlertCount = 0;
            nextListeningAlert = now.AddSeconds(12);
            if (listeningWatch.Options.DesktopNotifications)
            {
                try
                {
                    if (listeningNotifier == null)
                    {
                        listeningNotifier = new NotifyIcon { Icon = Icon ?? SystemIcons.Application,
                            Text = "AutoCQ · Дежурный приём", Visible = true };
                        listeningNotifier.BalloonTipClicked += delegate {
                            if (!Visible) { closeWhenDone = false; Show(); }
                            Guard(ShowListening);
                        };
                    }
                    string text = hit.Call + " · " + hit.Band + " · " + hit.Mode + " · " + hit.Source;
                    if (count > 1) text += Environment.NewLine + "Есть и другие цели. Откройте список.";
                    listeningNotifier.ShowBalloonTip(7000, "AutoCQ · Дежурный приём", text, ToolTipIcon.Info);
                }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }
            if (listeningWatch.Options.Sound)
            {
                try { SystemSounds.Asterisk.Play(); }
                catch (InvalidOperationException) { }
            }
        }

        void RenderListening(DateTime now)
        {
            if (listeningDialog == null || listeningDialog.IsDisposed) return;
            bool running = listeningWatch != null;
            bool connected = ListeningConnected(now);
            bool busy = RadioWorking(now);
            string state = !running ? (busy ? "Дежурный приём можно начать после окончания работы на передачу." : "Дежурный приём выключен.")
                : busy ? "Пауза на время передачи или сеанса AutoCQ."
                : connected ? "Приём включён. Ищу цели в декодах JTDX."
                : listeningWatch.Sources.Any() ? "Ожидаю JTDX. Внешние споты обновляются."
                : "Приём включён. Ожидаю подключения JTDX.";
            listeningDialog.Render(running, !busy, state);
            listeningDialog.ShowSourceStatus(running && !busy ? listeningSourceStatus : "");
        }

        Form ListeningDemoForm()
        {
            var now = DateTime.UtcNow;
            var form = new ListeningDialog(new ListeningOptions { UseExpeditions = true, DesktopNotifications = true,
                LocatorPrefixes = new[] { "KO85" } }, options => { }, () => { }, () => { });
            form.Render(true, true, "Демонстрация дежурного приёма.");
            form.ShowSourceStatus("WWFF через Spothole · Обновлено " + now.ToLocalTime().ToString("HH:mm:ss"));
            form.Fill(new[] {
                new ListeningHit { Call = "R3AAA", Band = "20m", Mode = "FT8", Source = "Декод JTDX",
                    TimeUtc = now.AddSeconds(-5), Details = "CQ R3AAA KO85 · Локатор KO85" },
                new ListeningHit { Call = "DL1AAA/P", Band = "30m", Mode = "FT8", Source = "Спот · WWFF через Spothole",
                    TimeUtc = now.AddMinutes(-1), Details = "Природная территория DLFF-0123", External = true },
                new ListeningHit { Call = "JA1AAA", Band = "20m", Mode = "FT4", Source = "Декод JTDX",
                    TimeUtc = now.AddMinutes(-2), Details = "CQ JA1AAA PM95 · Цель мониторинга" }
            });
            return form;
        }
    }
}
