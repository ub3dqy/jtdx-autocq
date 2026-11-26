using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace JtdxAutoCq
{
    internal sealed class ExpeditionDialog : OperatorDialog
    {
        readonly CheckBox trips, parks, nature, portableRda, wantedRda, rareDx;
        readonly NumericUpDown rank = new NumericUpDown { Minimum = 1, Maximum = 340, AccessibleName = "Порог редкости DXCC" };
        readonly ComboBox band = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Диапазон личных целей" };
        readonly Label radio, sources, selection;
        readonly Button start, refresh, personal;
        readonly ListView results = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, ShowItemToolTips = true };
        string[] wanted;
        RadioStatus currentRadio;
        string renderedRows;

        internal ExpeditionDialog(ExpeditionOptions options, Func<ExpeditionDialog, string[], string[]> choose,
            Action<ExpeditionOptions> reload, Action<ExpeditionOptions> begin, Action stop, Action help)
            : base("Экспедиции и редкие станции", 1030, 548)
        {
            options = options.Copy();
            wanted = options.WantedDxcc;
            radio = LabelAt("", 20, 53, 990, 23);
            radio.ForeColor = Color.FromArgb(0, 100, 91);
            trips = Option("DX-выезды и экспедиции", 20, 211, options.DxTrips);
            parks = Option("Парки POTA", 238, 130, options.Parks);
            nature = Option("Территории WWFF", 370, 165, options.Nature);
            portableRda = Option("Полевые RDA", 544, 145, options.PortableRda);
            wantedRda = Option("Редкие RDA", 700, 132, options.WantedRda);
            rareDx = Option("Редкие DXCC · топ", 20, 166, options.RareDx, 115);
            rank.Value = options.MaxDxRank;
            Place(rank, 188, 115, 61, 27);
            personal = ButtonAt("", 276, 111, 187, () => {
                string[] selected = choose(this, wanted);
                if (selected != null) wanted = selected;
                UpdateControls();
            });
            LabelAt("Диапазон личных целей", 476, 119, 180, 23);
            band.Items.Add("Все диапазоны");
            band.Items.AddRange(JtdxAdif.BandNames.Cast<object>().ToArray());
            band.SelectedIndex = options.WantedBand.Length == 0 ? 0 : band.Items.IndexOf(options.WantedBand);
            Place(band, 658, 115, 140, 27);
            sources = LabelAt("Ожидаю данные от источников…", 20, 158, 990, 23);
            sources.ForeColor = Color.FromArgb(79, 96, 112);

            results.AccessibleName = "Экспедиции и споты";
            results.Columns.Add("Позывной", 106);
            results.Columns.Add("Причина выбора", 299);
            results.Columns.Add("Диапазон / МГц", 142);
            results.Columns.Add("Режим", 81);
            results.Columns.Add("Активность", 159);
            results.Columns.Add("Источник", 179);
            Place(results, 20, 188, 990, 268);
            selection = LabelAt("", 20, 468, 990, 23);
            results.SelectedIndexChanged += delegate { UpdateSelection(); };
            ButtonAt("Справка", 20, 500, 115, help);
            refresh = ButtonAt("Обновить споты", 147, 500, 151, () => reload(Options));
            start = ButtonAt("Начать поиск", 817, 500, 193, () => {
                if (active) stop(); else begin(Options);
            }, true);
            foreach (CheckBox check in new[] { trips, parks, nature, portableRda, wantedRda, rareDx })
                check.CheckedChanged += delegate { UpdateControls(); };
            UpdateControls();
            UpdateSelection();
        }

        CheckBox Option(string title, int x, int width, bool selected, int y = 81)
        {
            var box = new CheckBox { Text = title, Checked = selected };
            Place(box, x, y, width, 26);
            return box;
        }

        internal ExpeditionOptions Options
        {
            get
            {
                return new ExpeditionOptions { DxTrips = trips.Checked, Parks = parks.Checked, Nature = nature.Checked,
                    PortableRda = portableRda.Checked, WantedRda = wantedRda.Checked, RareDx = rareDx.Checked,
                    MaxDxRank = (int)rank.Value, WantedDxcc = wanted, WantedBand = band.SelectedIndex > 0 ? (string)band.SelectedItem : "" }.Copy();
            }
        }

        bool active, ready;
        void UpdateControls()
        {
            rank.Enabled = rareDx.Checked && !active;
            band.Enabled = wanted.Length > 0 && !active;
            personal.Text = wanted.Length == 0 ? "Мои DXCC…" : "Мои DXCC · " + wanted.Length;
            start.Enabled = active || ready && Options.HasTargets;
            refresh.Enabled = Options.HasTargets;
        }

        internal void Render(ExpeditionCatalog catalog, AutoCqEngine engine, bool canStart, string sourceStatus, DateTime now)
        {
            active = engine.Expeditions != null;
            ready = canStart;
            currentRadio = engine.Status;
            foreach (Control control in new Control[] { trips, parks, nature, portableRda, wantedRda, rareDx, personal })
                control.Enabled = !active;
            start.Text = active ? "Остановить поиск" : "Начать поиск";
            start.BackColor = active ? Color.FromArgb(137, 39, 36) : Color.FromArgb(0, 100, 91);
            radio.Text = (currentRadio == null ? "Ожидаю JTDX" : "JTDX  ·  " + (JtdxAdif.Band(currentRadio.Frequency) ?? "—") + "  ·  " + currentRadio.Mode)
                + (active ? "     Поиск включён" : engine.Armed ? "     Идёт другой сеанс AutoCQ" : "     Поиск выключен");
            sources.Text = sourceStatus;
            UpdateControls();

            var search = engine.Expeditions ?? new ExpeditionSearch(Options, catalog, engine.DxccForCall);
            var rows = new List<ResultRow>();
            foreach (ExpeditionActivity spot in catalog.Activities.Where(item => !item.Announced))
            {
                ExpeditionMatch match = search.Match(spot, now);
                if (match == null) continue;
                rows.Add(new ResultRow(spot.Call, spot.Band, spot.Mode, spot.Frequency, match, SeenText(spot.SpottedUtc, now, "Спот"), spot.SpottedUtc,
                    spot.Source == ExpeditionSource.DxSpots ? "CQDX" : ExpeditionFeeds.Name(spot.Source)));
            }
            if (currentRadio != null)
            {
                string currentBand = JtdxAdif.Band(currentRadio.Frequency);
                double maxAge = currentRadio.Mode == "FT4" ? 15.5 : 30.5;
                foreach (CqStation station in engine.Cqs.Rows.Where(row => engine.Cqs.Fresh(row, now)
                    && (now - row.HeardUtc).TotalSeconds <= maxAge && engine.CanCall(row.Call)))
                {
                    ExpeditionMatch match = search.Match(station.Call, currentBand, currentRadio.Mode, now);
                    if (match == null) continue;
                    rows.Add(new ResultRow(station.Call, currentBand, currentRadio.Mode, currentRadio.Frequency, match,
                        SeenText(station.HeardUtc, now, "JTDX"), station.HeardUtc, "JTDX") { Local = true });
                }
            }
            var spotted = new HashSet<string>(rows.Select(row => row.Call));
            foreach (ExpeditionActivity item in catalog.Activities.Where(item => item.Announced && item.EndUtc > now && !spotted.Contains(item.Call)))
            {
                ExpeditionMatch match = search.MatchActivity(item);
                if (match == null) continue;
                string when = (item.StartUtc <= now ? "По календарю " : "Анонс с ") + item.StartUtc.ToString("dd.MM", CultureInfo.InvariantCulture)
                    + "–" + item.EndUtc.AddDays(-1).ToString("dd.MM", CultureInfo.InvariantCulture);
                rows.Add(new ResultRow(item.Call, null, item.Mode, 0, match, when, item.StartUtc, "NG3K") { Announced = true });
            }
            ResultRow[] visible = rows.GroupBy(row => row.Key).Select(group => group.OrderByDescending(row => row.Local)
                .ThenByDescending(row => row.Match.Priority).ThenByDescending(row => row.Time).First())
                .OrderByDescending(row => row.Local).ThenBy(row => row.Announced).ThenByDescending(row => row.Match.Priority)
                .ThenByDescending(row => row.Time).Take(240).ToArray();
            string signature = String.Join("\n", visible.Select(row => String.Join("|", row.Fields())));
            if (signature == renderedRows) { UpdateSelection(); return; }
            renderedRows = signature;

            var selected = results.SelectedItems.Count == 0 ? null : (ResultRow)results.SelectedItems[0].Tag;
            results.BeginUpdate();
            try
            {
                results.Items.Clear();
                foreach (ResultRow row in visible)
                {
                    var entry = new ListViewItem(row.Fields()) {
                        Tag = row, ToolTipText = row.Match.Reason + " · " + row.SourceText
                    };
                    if (row.Local) entry.BackColor = Color.FromArgb(225, 246, 238);
                    if (!engine.CanCall(row.Call)) entry.ForeColor = SystemColors.GrayText;
                    results.Items.Add(entry);
                    if (selected != null && selected.Key == row.Key) entry.Selected = true;
                }
            }
            finally { results.EndUpdate(); }
            UpdateSelection();
        }

        void UpdateSelection()
        {
            var row = results.SelectedItems.Count == 0 ? null : (ResultRow)results.SelectedItems[0].Tag;
            if (row == null)
                selection.Text = "Для автовызова нужен свежий декод JTDX на выбранном диапазоне. Даты анонсов указаны в UTC.";
            else if (row.Local)
                selection.Text = row.Call + " принята вашим JTDX. Входящие вызовы имеют приоритет.";
            else if (row.Announced)
                selection.Text = "Есть анонс " + row.Call + ". Ожидаю актуальный спот или сообщение в JTDX.";
            else if (currentRadio == null || JtdxAdif.Band(currentRadio.Frequency) != row.Band || currentRadio.Mode != row.Mode)
                selection.Text = row.Call + " замечена на " + row.Band + " в " + row.Mode + (active
                    ? ". После QSO остановите поиск и выберите её диапазон в JTDX." : ". Выберите её диапазон и режим в JTDX.");
            else
                selection.Text = row.Call + " есть в спотах. Ожидаю сообщение на вашем приёмнике.";
        }

        static string SeenText(DateTime time, DateTime now, string label)
        {
            int seconds = Math.Max(0, (int)(now - time).TotalSeconds);
            return seconds < 60 ? label + " · " + seconds + " с назад" : label + " · " + seconds / 60 + " мин назад";
        }

        sealed class ResultRow
        {
            internal readonly string Call, Band, Mode;
            internal readonly ExpeditionMatch Match;
            internal readonly DateTime Time;
            internal readonly string SourceText;
            readonly string activity;
            readonly double frequency;
            internal bool Local, Announced;
            internal string Key { get { return Call + "|" + Band + "|" + Mode; } }
            internal ResultRow(string call, string band, string mode, double frequency, ExpeditionMatch match, string activity, DateTime time, string source)
            {
                Call = call; Band = band; Mode = mode; Match = match; Time = time;
                this.frequency = frequency; this.activity = activity;
                SourceText = match.Source.Contains(source) ? match.Source : source + " · " + match.Source;
            }
            internal string[] Fields()
            {
                string mhz = frequency > 0 ? Band + " · " + (frequency / 1000000).ToString("0.000###", CultureInfo.CurrentCulture) : "—";
                return new[] { Call, Match.Reason, mhz, String.IsNullOrEmpty(Mode) ? "—" : Mode, activity, SourceText };
            }
        }
    }
}
