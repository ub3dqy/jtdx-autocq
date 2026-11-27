using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace JtdxAutoCq
{
    internal sealed class ListeningDialog : OperatorDialog
    {
        readonly CheckBox goals, expeditions, desktop, sound;
        readonly TextBox locators;
        readonly Label status, sourceStatus;
        readonly ToolTip sourceTip = new ToolTip();
        readonly ListView activity;
        readonly Button control;
        bool active, ready;

        internal ListeningDialog(ListeningOptions options, Action<ListeningOptions> start, Action stop, Action help)
            : base("Дежурный приём", 892, 492)
        {
            status = LabelAt("Дежурный приём выключен.", 20, 54, 852, 26);
            status.ForeColor = Color.FromArgb(0, 100, 91);
            goals = Choice("Использовать заданные цели мониторинга", 20, 88, 413, options.UseMonitorTargets);
            expeditions = Choice("Искать выбранные экспедиции и их споты", 452, 88, 420, options.UseExpeditions);
            LabelAt("Локаторы, например KO, KO85 или KO85AA", 20, 125, 852, 25);
            locators = new TextBox { Text = String.Join(", ", options.LocatorPrefixes),
                CharacterCasing = CharacterCasing.Upper, AccessibleName = "Локаторы для дежурного приёма" };
            Place(locators, 20, 151, 852, 28);
            desktop = Choice("Уведомления Windows", 20, 191, 228, options.DesktopNotifications);
            sound = Choice("Звук при новой цели", 267, 191, 261, options.Sound);
            activity = new ListView { AccessibleName = "Станции дежурного приёма", View = View.Details,
                FullRowSelect = true, HideSelection = false, MultiSelect = false, ShowItemToolTips = true };
            activity.Columns.Add("Время", 81);
            activity.Columns.Add("Позывной", 116);
            activity.Columns.Add("Диапазон", 83);
            activity.Columns.Add("Режим", 70);
            activity.Columns.Add("Источник", 205);
            activity.Columns.Add("Подробности", 273);
            Place(activity, 20, 225, 852, 174);
            sourceStatus = LabelAt("", 20, 410, 852, 24);
            sourceStatus.AutoEllipsis = true;
            sourceStatus.ForeColor = Color.FromArgb(79, 96, 112);
            Disposed += delegate { sourceTip.Dispose(); };
            ButtonAt("Справка", 20, 443, 112, help);
            ButtonAt("Закрыть", 545, 443, 125, Close);
            control = ButtonAt("Начать приём", 680, 443, 192, () => {
                if (active) stop(); else start(Options());
            }, true);
            goals.CheckedChanged += delegate { UpdateStart(); };
            expeditions.CheckedChanged += delegate { UpdateStart(); };
            locators.TextChanged += delegate { UpdateStart(); };
            UpdateStart();
        }

        CheckBox Choice(string label, int x, int y, int width, bool selected)
        {
            var check = new CheckBox { Text = label, Checked = selected };
            Place(check, x, y, width, 28);
            return check;
        }

        ListeningOptions Options()
        {
            char[] separators = { ' ', ',', ';', '\r', '\n', '\t' };
            return new ListeningOptions {
                UseMonitorTargets = goals.Checked, UseExpeditions = expeditions.Checked, Sound = sound.Checked,
                DesktopNotifications = desktop.Checked,
                LocatorPrefixes = locators.Text.Split(separators, StringSplitOptions.RemoveEmptyEntries)
            };
        }

        internal void Render(bool running, bool canStart, string state)
        {
            if (status.Text != state) status.Text = state;
            if (active == running && ready == canStart) return;
            active = running;
            ready = canStart;
            control.Text = active ? "Остановить приём" : "Начать приём";
            foreach (Control field in new Control[] { goals, expeditions, locators, desktop, sound })
                field.Enabled = !active;
            UpdateStart();
        }

        void UpdateStart()
        {
            control.Enabled = active || ready && (goals.Checked || expeditions.Checked || !String.IsNullOrWhiteSpace(locators.Text));
        }

        internal void Fill(IEnumerable<ListeningHit> hits)
        {
            activity.BeginUpdate();
            try
            {
                activity.Items.Clear();
                foreach (ListeningHit hit in hits) Add(hit, activity.Items.Count);
            }
            finally { activity.EndUpdate(); }
        }

        internal void ShowSourceStatus(string text)
        {
            if (sourceStatus.Text == text) return;
            sourceStatus.Text = text;
            sourceTip.SetToolTip(sourceStatus, text);
        }

        internal void Display(ListeningHit hit, int index)
        {
            Add(hit, index);
            if (activity.Items.Count > 100) activity.Items.RemoveAt(activity.Items.Count - 1);
        }

        void Add(ListeningHit hit, int index)
        {
            var item = new ListViewItem(new[] { hit.TimeUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture),
                hit.Call, hit.Band, hit.Mode, hit.Source, hit.Details }) {
                ToolTipText = hit.Details,
                BackColor = hit.External ? SystemColors.Window : Color.FromArgb(225, 246, 238)
            };
            activity.Items.Insert(index, item);
        }
    }
}
