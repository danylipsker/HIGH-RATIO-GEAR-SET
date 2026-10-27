// Dialog: the table of solutions on top (empty until the user asks), then the three inputs - module,
// gear ratio, Find solutions - then everything that follows the row picked in the table: the
// dimensions, the folder, Build. A ratio no set reaches exactly fills the table with the nearest
// reachable sets, widening the search range on its own (sun to 60, planet from 10, rings up to 6
// teeth apart) and saying so. The search range and the rarely needed inputs (an exact tooth set,
// presets, max ring OD, face width, backlash, root fillet) are under "Advanced", folded, at the bottom.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GearSet
{
    public class GearSetForm : Form
    {
        public const string RegistryKey = @"Software\GearSetAddin";
        /// <summary>A "nearest" row further off the target than this is not a solution: it is not shown.</summary>
        public const double NearestWindow = 0.15;

        public GearModel Result { get; private set; }
        public string OutputFolder { get; private set; }
        public bool AddMates { get { return _mates.Checked; } }

        private readonly string _root, _outputBase;
        // the three inputs
        private readonly TextBox _module = new TextBox { Width = 64, Text = "", Font = new Font("Segoe UI", 11f) };
        private readonly TextBox _ratio = new TextBox { Width = 96, Text = "", Font = new Font("Segoe UI", 11f) };
        private readonly Button _find = new Button { Text = "3.  Find solutions", Width = 150, Height = 32, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        private readonly Label _tableTitle = new Label { Text = "Solutions", AutoSize = true, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), Margin = new Padding(0, 0, 8, 0) };
        private readonly Label _tableHint = new Label { Text = "empty until you type the module and the gear ratio below and press Find solutions; then every tooth set that gives the ratio, best first - pick a row, the rest follows it", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 4, 0, 0), MaximumSize = new Size(760, 0) };
        private readonly Label _status = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(900, 0) };
        // advanced: search range and design rules
        private readonly CheckBox _advanced = new CheckBox { Text = "Advanced: search range, design rules, exact tooth set, presets", AutoSize = true, Appearance = Appearance.Normal, ForeColor = Color.FromArgb(0x1F, 0x4E, 0x9A) };
        private readonly Panel _advancedPanel = new Panel { AutoSize = true, Visible = false, Dock = DockStyle.Top };
        private readonly TextBox _sunMin = new TextBox { Width = 40, Text = "12" }, _sunMax = new TextBox { Width = 40, Text = "30" };
        private readonly TextBox _planetMin = new TextBox { Width = 40, Text = "12" }, _planetMax = new TextBox { Width = 40, Text = "80" };
        private readonly TextBox _sun = new TextBox { Width = 40 }, _planet = new TextBox { Width = 40 };
        private readonly TextBox _deltaMax = new TextBox { Width = 32, Text = "3" };
        private readonly TextBox _awMax = new TextBox { Width = 40, Text = "31" };
        private readonly TextBox _maxOd = new TextBox { Width = 60 };
        private readonly TextBox _set = new TextBox { Width = 170 };
        private readonly ComboBox _preset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        private readonly TextBox _faceWidth = new TextBox { Width = 56 };
        private readonly TextBox _backlash = new TextBox { Width = 56, Text = "0" };
        private readonly TextBox _fillet = new TextBox { Width = 44, Text = "0.3" };
        private readonly CheckBox _nearest = new CheckBox { Text = "when no exact set exists, widen the search (sun to 60, planet from 10, rings up to 6 apart) and list the nearest ratios", AutoSize = true, Checked = true };
        // the table and what follows it
        private readonly ListView _list = new ListView
        {
            View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
            GridLines = true, Font = new Font("Segoe UI", 9f), Dock = DockStyle.Fill,
        };
        private readonly TextBox _dims = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9f), BackColor = SystemColors.Window,
        };
        private readonly CheckBox _mates = new CheckBox { Text = "mates, so the assembly animates (drag the sun) + Motion Study", AutoSize = true, Checked = true };
        private readonly CheckBox _housing = new CheckBox { Text = "housing with bearings (covers, shell, output cup)", AutoSize = true, Checked = true };
        // catalogue bearing parts (BearingLibrary): files or folders of .SLDPRT, ';' separated; blank = plain rings
        private readonly TextBox _bearings = new TextBox { Width = 560 };
        private readonly Button _bearingsBrowse = new Button { Text = "...", Width = 32 };
        private List<Bearing> _catalogue; private string _catalogueFor;
        private readonly TextBox _folder = new TextBox { Width = 420 };
        private readonly Button _browse = new Button { Text = "...", Width = 32 };
        private readonly Button _build = new Button { Text = "Build SOLIDWORKS files", Width = 190, Height = 32, Enabled = false, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        private readonly Button _close = new Button { Text = "Close", Width = 90, Height = 32, DialogResult = DialogResult.Cancel };
        private readonly Label _pickedTitle = new Label { Text = "4.  The picked solution", AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Margin = new Padding(0, 6, 6, 0) };
        private readonly ToolTip _tips = new ToolTip { AutoPopDelay = 12000, InitialDelay = 400 };

        private List<Candidate> _candidates = new List<Candidate>();
        private Ratio _target;
        private bool _givenSet;
        private int _searchGeneration;

        /// <param name="root">folder of gearset.py (default output goes to root\output), may be null</param>
        /// <param name="outputBase">when given, new sets go to outputBase\&lt;set name&gt; (e.g. a parts library)</param>
        public GearSetForm(string root, string outputBase = null)
        {
            _root = root; _outputBase = outputBase;
            Text = "Gear Set - high-ratio single-planet reducer (3K, floating thin ring)";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(980, 720);
            MinimumSize = new Size(860, 620);
            MaximizeBox = true; MinimizeBox = false;
            CancelButton = _close;
            AcceptButton = _find;

            var rootPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
            rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // table heading
            rootPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 56));   // the table
            rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 1 2 3
            rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // status
            rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 4 heading
            rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // options
            rootPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 44));   // dimensions
            rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // folder
            rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // buttons + advanced toggle
            rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // advanced panel

            // ---- the table, first --------------------------------------------------------------------
            var headT = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Top };
            headT.Controls.Add(_tableTitle);
            headT.Controls.Add(_tableHint);
            rootPanel.Controls.Add(headT);
            _list.Columns.Add("#", 34);
            _list.Columns.Add("Sun", 46);
            _list.Columns.Add("Planet", 52);
            _list.Columns.Add("Ring int", 62);
            _list.Columns.Add("Ring ext", 62);
            _list.Columns.Add("Fixed", 52);
            _list.Columns.Add("Output", 56);
            _list.Columns.Add("Ratio", 86);
            _list.Columns.Add("Sense", 60);
            _list.Columns.Add("α_w °", 50);
            _list.Columns.Add("Shift x", 56);
            _list.Columns.Add("Wall mm", 62);
            _list.Columns.Add("Ring OD mm", 82);
            _list.Columns.Add("Overall mm", 82);
            _list.Columns.Add("Note", 260);
            var listHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 8) };
            listHost.Controls.Add(_list);
            rootPanel.Controls.Add(listHost);

            // ---- 1, 2, 3 on one line -------------------------------------------------------------
            var row1 = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Top };
            row1.Controls.Add(Big("1.  Module"));
            _module.Margin = new Padding(0, 2, 4, 0); row1.Controls.Add(_module);
            row1.Controls.Add(Plain("mm", 8, 28));
            row1.Controls.Add(Big("2.  Gear ratio   1 :"));
            _ratio.Margin = new Padding(0, 2, 28, 0); row1.Controls.Add(_ratio);
            _find.Margin = new Padding(0, 0, 0, 0); row1.Controls.Add(_find);
            rootPanel.Controls.Add(row1);
            _status.Margin = new Padding(2, 6, 0, 0);
            rootPanel.Controls.Add(_status);

            // ---- 4 what follows the picked row ---------------------------------------------------
            _pickedTitle.Margin = new Padding(0, 10, 6, 0);
            rootPanel.Controls.Add(_pickedTitle);
            var optRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Top };
            _mates.Margin = new Padding(0, 2, 24, 0); _housing.Margin = new Padding(0, 2, 0, 0);
            optRow.Controls.Add(_mates);
            optRow.Controls.Add(_housing);
            rootPanel.Controls.Add(optRow);
            var dimHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 6) };
            dimHost.Controls.Add(_dims);
            rootPanel.Controls.Add(dimHost);

            var folderRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Top };
            folderRow.Controls.Add(Bold("Folder for the .SLDPRT / .SLDASM files"));
            _folder.Margin = new Padding(0, 3, 3, 0); folderRow.Controls.Add(_folder);
            folderRow.Controls.Add(_browse);
            rootPanel.Controls.Add(folderRow);

            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Top, Margin = new Padding(0, 8, 0, 0) };
            buttons.Controls.Add(_build);
            _close.Margin = new Padding(8, 0, 24, 0);
            buttons.Controls.Add(_close);
            _advanced.Margin = new Padding(0, 8, 0, 0);
            buttons.Controls.Add(_advanced);
            rootPanel.Controls.Add(buttons);

            // ---- advanced (folded, at the bottom) ------------------------------------------------
            var adv = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Top, Margin = new Padding(0, 6, 0, 0), Padding = new Padding(8, 4, 8, 6), BackColor = Color.FromArgb(0xF3, 0xF4, 0xF6) };
            var advRange = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Top };
            advRange.Controls.Add(Bold("Search range:"));
            advRange.Controls.Add(Plain("sun teeth")); advRange.Controls.Add(_sunMin); advRange.Controls.Add(Plain("to")); advRange.Controls.Add(_sunMax);
            advRange.Controls.Add(Plain("     planet teeth")); advRange.Controls.Add(_planetMin); advRange.Controls.Add(Plain("to")); advRange.Controls.Add(_planetMax);
            advRange.Controls.Add(Plain("     or force: sun")); advRange.Controls.Add(_sun); advRange.Controls.Add(Plain("planet")); advRange.Controls.Add(_planet);
            adv.Controls.Add(advRange);
            var advRules = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Top };
            advRules.Controls.Add(Bold("Design rules:"));
            advRules.Controls.Add(Plain("ring tooth difference |Zo - Zf| up to")); advRules.Controls.Add(_deltaMax);
            advRules.Controls.Add(Plain("     max working pressure angle")); advRules.Controls.Add(_awMax); advRules.Controls.Add(Plain("deg"));
            advRules.Controls.Add(Plain("     max ring outer diameter")); advRules.Controls.Add(_maxOd); advRules.Controls.Add(Plain("mm"));
            adv.Controls.Add(advRules);
            var advGeom = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Top };
            advGeom.Controls.Add(Bold("Teeth and faces:"));
            advGeom.Controls.Add(Plain("face width")); advGeom.Controls.Add(_faceWidth); advGeom.Controls.Add(Plain("mm (blank = 10 x module)"));
            advGeom.Controls.Add(Plain("     backlash")); advGeom.Controls.Add(_backlash); advGeom.Controls.Add(Plain("mm"));
            advGeom.Controls.Add(Plain("     root fillet")); advGeom.Controls.Add(_fillet); advGeom.Controls.Add(Plain("x module"));
            adv.Controls.Add(advGeom);
            var advBearings = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Top };
            advBearings.Controls.Add(Bold("Bearings from catalogue parts:"));
            _bearings.Margin = new Padding(0, 3, 3, 0); advBearings.Controls.Add(_bearings);
            advBearings.Controls.Add(_bearingsBrowse);
            advBearings.Controls.Add(Plain(".SLDPRT files or folders of them, ; separated (every configuration is a bearing); blank = plain rings"));
            adv.Controls.Add(advBearings);
            var advSet = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Top };
            advSet.Controls.Add(Bold("A set of your own:"));
            advSet.Controls.Add(Plain("Zs Zp Zr1 Zr2 Zf Zo")); advSet.Controls.Add(_set);
            advSet.Controls.Add(Plain("     or a verified preset")); advSet.Controls.Add(_preset);
            adv.Controls.Add(advSet);
            _nearest.Margin = new Padding(0, 6, 0, 0);
            adv.Controls.Add(_nearest);
            _advancedPanel.Controls.Add(adv);
            rootPanel.Controls.Add(_advancedPanel);
            _advanced.CheckedChanged += (s, e) => { _advancedPanel.Visible = _advanced.Checked; };
            Controls.Add(rootPanel);

            // ---- tips and events -----------------------------------------------------------------
            _tips.SetToolTip(_module, "The module in mm: with the ratio it sets the size of the whole set (every length scales with it)");
            _tips.SetToolTip(_ratio, "Input : output. A number, a decimal or a fraction: 500, 528.69, 1000/3. This mechanism reaches about 1:200 at the smallest and several thousand at the largest.");
            _tips.SetToolTip(_find, "Enumerate every tooth set within the search range that gives the ratio exactly (Enter in the boxes does the same)");
            _tips.SetToolTip(_sunMin, "Smallest sun tooth count searched"); _tips.SetToolTip(_sunMax, "Largest sun tooth count searched: raise it (to 40..60) for ratios under 1:250");
            _tips.SetToolTip(_planetMin, "Smallest planet tooth count searched (under 17 means undercut)"); _tips.SetToolTip(_planetMax, "Largest planet tooth count searched");
            _tips.SetToolTip(_sun, "Force the sun tooth count (blank = free)");
            _tips.SetToolTip(_planet, "Force the planet tooth count (blank = free)");
            _tips.SetToolTip(_deltaMax, "The output ring has this many teeth more or fewer than the fixed ring, at most (1..3 usual; more needs a larger eccentric tooth difference)");
            _tips.SetToolTip(_maxOd, "Hide solutions whose ring outer diameter exceeds this at the given module");
            _tips.SetToolTip(_awMax, "Design rule: working pressure angle of the profile-shifted ring mesh (31 deg default, up to ~34 possible)");
            _tips.SetToolTip(_backlash, "Circumferential backlash per mesh; teeth are thinned by half of it. 0 = theoretical, touching flanks");
            _tips.SetToolTip(_fillet, "Root fillet radius as a multiple of the module (0.3 default, 0 = sharp). 0.38 is the ISO rack value but grazes the ring tips without tip relief.");
            _tips.SetToolTip(_faceWidth, "Face width of each mesh section; blank = 10 x module");
            _tips.SetToolTip(_list, "Pick a row: the dimensions, the folder name and the Build button follow it. Double-click a row to build it.");
            _tips.SetToolTip(_set, "Six tooth counts build exactly this set, whatever the ratio box says (e.g. 16 32 115 123 158 155). Clear it to search by ratio.");
            _tips.SetToolTip(_mates, "Coincident / concentric mates for the positions, gear mates with the exact speed ratios to the sun (one degree of freedom left), and a Motion Study 'Sun drive' with a rotary motor on the sun: 60 rpm for 10 s = 10 sun turns");
            _tips.SetToolTip(_housing, "Front cover with the sun's bearing, shell clamping the fixed ring, output ring extended into a cup with a hub on a large bearing in the rear cover, and the sun's rear bearing inside the hub. Deep-groove ball bearings: the catalogue parts named under Advanced (the real NSK / GOST parts, picked by bore), else plain 60xx rings.");
            _tips.SetToolTip(_bearings, "Catalogue bearing parts: .SLDPRT files, or folders of them, separated by ';'. Every configuration of every part is measured once in SolidWorks (bore, OD, width, axis) and remembered; the housing then takes, for each bore, the real part nearest the 60-series size and inserts it in the assembly. Blank = the plain rings the builder makes itself.");
            _housing.CheckedChanged += (s, e) => { if (_candidates.Count > 0) FillList(); };
            _bearingsBrowse.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog { Description = "A folder of catalogue bearing parts (.SLDPRT); it is added to the list", ShowNewFolderButton = false })
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                        _bearings.Text = string.IsNullOrWhiteSpace(_bearings.Text) ? dlg.SelectedPath : _bearings.Text.TrimEnd(';', ' ') + ";" + dlg.SelectedPath;
            };

            _preset.Items.Add("(choose a known set)");
            foreach (var pr in Presets.All) _preset.Items.Add(pr.Name);
            _preset.SelectedIndex = 0;
            _preset.SelectedIndexChanged += (s, e) =>
            {
                if (_preset.SelectedIndex < 1) return;
                var pr = Presets.All[_preset.SelectedIndex - 1];
                _module.Text = pr.Module.ToString(CultureInfo.InvariantCulture);
                _set.Text = string.Join(" ", Array.ConvertAll(pr.Teeth, t => t.ToString()));
                _sun.Text = ""; _planet.Text = "";
                FindSolutions();
            };
            foreach (var tb in new[] { _set, _ratio, _module, _sunMin, _sunMax, _planetMin, _planetMax, _sun, _planet, _deltaMax, _awMax, _maxOd })
                tb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { FindSolutions(); e.SuppressKeyPress = true; } };
            _find.Click += (s, e) => FindSolutions();
            _list.SelectedIndexChanged += (s, e) => UpdateDimensions();
            _list.ItemActivate += (s, e) => { if (_build.Enabled) _build.PerformClick(); };
            _browse.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog { SelectedPath = _folder.Text, Description = "Folder for the SOLIDWORKS files" })
                    if (dlg.ShowDialog(this) == DialogResult.OK) _folder.Text = dlg.SelectedPath;
            };
            _build.Click += (s, e) => Build();
            _module.TextChanged += (s, e) => { if (_candidates.Count > 0) FillList(); };
            foreach (var tb in new[] { _faceWidth, _backlash, _fillet, _bearings })
                tb.TextChanged += (s, e) => { if (_candidates.Count > 0) UpdateDimensions(); };

            _bearings.Text = BearingLibrary.DefaultPaths();
            LoadLast();
            Shown += (s, e) => { _module.Focus(); Status("Type the module and the gear ratio, then Find solutions.", false); };
        }

        private static Label Big(string t) { return new Label { Text = t, AutoSize = true, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), Margin = new Padding(0, 6, 8, 0) }; }
        private static Label Bold(string t) { return new Label { Text = t, AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Margin = new Padding(0, 6, 6, 0) }; }
        private static Label Plain(string t, int left = 0, int right = 6) { return new Label { Text = t, AutoSize = true, Margin = new Padding(left, 6, right, 0) }; }

        // ------------------------------------------------------------------ parsing
        private static double? Num(TextBox tb)
        {
            double v;
            string t = tb.Text.Trim().Replace(',', '.');
            if (t.Length == 0) return null;
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : (double?)null;
        }

        private static int? Int(TextBox tb)
        {
            int v;
            string t = tb.Text.Trim();
            if (t.Length == 0) return null;
            return int.TryParse(t, out v) && v > 0 ? v : (int?)null;
        }

        private double Module { get { return Num(_module) ?? 1.0; } }

        /// <summary>The search range and design rules as typed under Advanced (defaults when blank).</summary>
        private Limits CurrentLimits()
        {
            var lim = new Limits();
            lim.SunMin = Math.Max(6, Int(_sunMin) ?? lim.SunMin);
            lim.SunMax = Math.Max(lim.SunMin, Math.Min(120, Int(_sunMax) ?? lim.SunMax));
            lim.PlanetMin = Math.Max(6, Int(_planetMin) ?? lim.PlanetMin);
            lim.PlanetMax = Math.Max(lim.PlanetMin, Math.Min(200, Int(_planetMax) ?? lim.PlanetMax));
            lim.DeltaMax = Math.Max(1, Math.Min(12, Int(_deltaMax) ?? lim.DeltaMax));
            double? aw = Num(_awMax);
            if (aw.HasValue) lim.AwMaxDeg = Math.Min(40, Math.Max(15, aw.Value));
            return lim;
        }

        // ------------------------------------------------------------------ solve
        private void FindSolutions()
        {
            if (Num(_module) == null || Module <= 0) { Fail("1. Enter the module in mm."); return; }
            var lim = CurrentLimits();
            int[] z;
            _givenSet = _set.Text.Trim().Length > 0;
            if (_givenSet)
            {
                if (!Design.TryParseTeeth(_set.Text, out z)) { Fail("The tooth set needs six whole numbers: Zs Zp Zr1 Zr2 Zf Zo."); return; }
                var des = Design.FromTeeth(z[0], z[1], z[2], z[3], z[4], z[5], lim);
                string why = des.Validate(lim);
                _target = Ratio.Normalize(Math.Abs(des.RatioNum), Math.Abs(des.RatioDen));
                _ratio.Text = Math.Abs(des.RatioValue).ToString("0.###", CultureInfo.InvariantCulture);
                _candidates = new List<Candidate> { new Candidate { Design = des, Error = 0, Score = 0 } };
                _tableTitle.Text = "Your tooth set";
                FillList();
                if (why != null) Fail("Your set: " + why + " - it is listed anyway, build at your own risk.");
                else Status("Your tooth set, ratio " + des.RatioText + ". Clear the set under Advanced to search by ratio again.", false);
                return;
            }
            if (!Ratio.TryParse(_ratio.Text, out _target)) { Fail("2. Enter the gear ratio as a number (500, 528.69) or a fraction (1000/3)."); return; }
            int? sun = Int(_sun), planet = Int(_planet);
            bool widen = _nearest.Checked;
            Ratio target = _target;
            int gen = ++_searchGeneration;
            Cursor = Cursors.WaitCursor; _find.Enabled = false;
            _tableTitle.Text = "Solutions for 1 : " + target.Value.ToString("0.###", CultureInfo.InvariantCulture);
            Status("Searching the tooth sets for 1:" + target.Value.ToString("0.###", CultureInfo.InvariantCulture) + " ...", false);
            Task.Run(() => StagedSearch(target, lim, sun, planet, widen)).ContinueWith(t =>
            {
                if (gen != _searchGeneration || IsDisposed) return;
                Cursor = Cursors.Default; _find.Enabled = true;
                if (t.IsFaulted) { Fail("Search failed: " + (t.Exception != null ? t.Exception.GetBaseException().Message : "?")); return; }
                ShowSearch(t.Result, lim, sun, planet);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private sealed class SearchResult
        {
            public List<Candidate> Hits = new List<Candidate>();
            public bool Exact, Widened;
            public Limits Used;
            public double ClosestRatio = double.NaN;      // of every reachable set, when nothing is exact
        }

        /// <summary>Exact sets within the range as typed; failing that, exact sets in the widened range;
        /// failing that, the nearest ratios in the widened range (best first). Runs off the UI thread.</summary>
        private static SearchResult StagedSearch(Ratio target, Limits lim, int? sun, int? planet, bool widen)
        {
            var r = new SearchResult { Used = lim };
            r.Hits = Solver.Search(target, lim, sun, planet, true, 40);
            if (r.Hits.Count > 0) { r.Exact = true; return r; }
            if (!widen) return r;
            var wide = new Limits
            {
                SunMin = Math.Min(lim.SunMin, 12), SunMax = Math.Max(lim.SunMax, 60), PlanetMin = Math.Min(lim.PlanetMin, 10), PlanetMax = lim.PlanetMax,
                EccMin = lim.EccMin, EccMax = Math.Max(lim.EccMax, 100), WallMinMod = lim.WallMinMod, WallMaxTeeth = lim.WallMaxTeeth,
                DeltaMax = Math.Max(lim.DeltaMax, 6), AwMinDeg = lim.AwMinDeg, AwMaxDeg = lim.AwMaxDeg, PressureAngleDeg = lim.PressureAngleDeg,
                Addendum = lim.Addendum, Clearance = lim.Clearance,
            };
            r.Used = wide; r.Widened = true;
            r.Hits = Solver.Search(target, wide, sun, planet, true, 40);
            if (r.Hits.Count > 0) { r.Exact = true; return r; }
            r.Hits = Solver.Search(target, wide, sun, planet, false, 40);
            double best = double.NaN, bestErr = double.MaxValue;
            foreach (var c in r.Hits) if (c.Error < bestErr) { bestErr = c.Error; best = Math.Abs(c.Design.RatioValue); }
            r.ClosestRatio = best;
            return r;
        }

        private void ShowSearch(SearchResult r, Limits asked, int? sun, int? planet)
        {
            _candidates = r.Hits;
            FillList();
            string tgt = _target.Value.ToString("0.###", CultureInfo.InvariantCulture);
            string forced = (sun.HasValue || planet.HasValue)
                ? " with the sun / planet forced to " + (sun.HasValue ? sun.Value.ToString() : "free") + " / " + (planet.HasValue ? planet.Value.ToString() : "free") : "";
            string range = " (sun " + r.Used.SunMin + ".." + r.Used.SunMax + ", planet " + r.Used.PlanetMin + ".." + r.Used.PlanetMax + ", rings up to " + r.Used.DeltaMax + " teeth apart" + forced + ")";
            string odNote = _list.Items.Count < _candidates.Count ? ", " + (_candidates.Count - _list.Items.Count) + " more over the ring OD limit" : "";
            if (r.Exact && !r.Widened)
                Status("Exact 1:" + tgt + ": " + _list.Items.Count + " tooth set" + (_list.Items.Count == 1 ? "" : "s") + range + odNote + ". Pick one in the table.", false);
            else if (r.Exact)
                Status("Exact 1:" + tgt + " needs a wider search than the range under Advanced: " + _list.Items.Count + " set" + (_list.Items.Count == 1 ? "" : "s") + range + odNote
                    + ". A sun over 30 or a planet under 12 teeth is unusual: check the Note column.", false);
            else if (_list.Items.Count > 0)
                Status("No tooth set gives exactly 1:" + tgt + ", even" + range + ". The table lists the nearest ratios this mechanism reaches, closest first"
                    + " (the error in the Note column); the closest is 1:" + r.ClosestRatio.ToString("0.#", CultureInfo.InvariantCulture) + "."
                    + (_target.Value < r.ClosestRatio ? " Ratios under about 1:165 are below what a 3K floating-ring set can do: a planetary or compound planetary set is the right choice there." : ""), true);
            else
                Fail("No tooth set gives 1:" + tgt + range + ". Tick 'widen the search' under Advanced, free the forced counts, or raise the max pressure angle.");
        }

        private void FillList()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            double m = Module;
            double? maxOd = Num(_maxOd);
            int n = 0;
            foreach (var c in _candidates)
            {
                var g = QuickModel(c.Design, m);
                if (maxOd.HasValue && g.RingOD > maxOd.Value + 1e-9) continue;
                n++;
                var it = new ListViewItem(n.ToString()) { Tag = c };
                var d = c.Design;
                it.SubItems.Add(d.Zs.ToString()); it.SubItems.Add(d.Zp.ToString()); it.SubItems.Add(d.Zr1.ToString());
                it.SubItems.Add(d.Zr2.ToString()); it.SubItems.Add(d.Zf.ToString()); it.SubItems.Add(d.Zo.ToString());
                it.SubItems.Add(Math.Abs(d.RatioValue).ToString("0.###", CultureInfo.InvariantCulture));
                it.SubItems.Add(d.OutputReversed ? "reversed" : "same way");
                it.SubItems.Add(d.AwDeg.ToString("0.0", CultureInfo.InvariantCulture));
                it.SubItems.Add(d.XShift.ToString("+0.00;-0.00", CultureInfo.InvariantCulture));
                it.SubItems.Add(g.ThinRingWall.ToString("0.0", CultureInfo.InvariantCulture));
                it.SubItems.Add(g.RingOD.ToString("0.0", CultureInfo.InvariantCulture));
                it.SubItems.Add(g.OverallLength.ToString("0.0", CultureInfo.InvariantCulture));
                string note = _givenSet ? "your set" : c.Error > 1e-12 ? "nearest, " + (c.Error * 100).ToString("+0.0;-0.0", CultureInfo.InvariantCulture) + " % off" : "exact";
                if (d.Zs < 17 || d.Zp < 17) note += " - a count under 17 (undercut)";
                if (g.Notes.Count > 0) note += " - " + g.Notes[0];
                it.SubItems.Add(note);
                _list.Items.Add(it);
            }
            _list.EndUpdate();
            if (_list.Items.Count > 0) { _list.Items[0].Selected = true; _list.Items[0].Focused = true; UpdateDimensions(); }
            else { _dims.Text = ""; _build.Enabled = false; _pickedTitle.Text = "4.  The picked solution"; }
            _tableHint.Text = _list.Items.Count > 0
                ? _list.Items.Count + " row" + (_list.Items.Count == 1 ? "" : "s") + ", best first - pick one, the rest of the dialog follows it; double-click builds it"
                : "empty until you type the module and the gear ratio below and press Find solutions; then every tooth set that gives the ratio, best first - pick a row, the rest follows it";
        }

        private GearModel QuickModel(Design d, double m)
        {
            var g = GearModel.Create(d, m, Num(_faceWidth) ?? 0, 0, Num(_backlash) ?? 0, 0, 0, 5.0, 6, SetName(d, m), Num(_fillet) ?? 0.3);
            if (_housing.Checked) g.AddHousing(CatalogueBearings());
            return g;
        }

        /// <summary>The catalogue bearing parts as typed under Advanced (files / folders, ';' separated; "" = plain rings).</summary>
        public string BearingLibraryPaths { get { return _bearings.Text.Trim(); } }

        /// <summary>The catalogue bearings already measured (the cache; no SolidWorks here), so the dimensions shown are
        /// the ones the build will use. A part not measured yet counts once the first build has scanned it.</summary>
        private IList<Bearing> CatalogueBearings()
        {
            string p = BearingLibraryPaths;
            if (p.Length == 0) return null;
            if (_catalogue == null || _catalogueFor != p) { _catalogue = BearingLibrary.FromCache(p).Bearings; _catalogueFor = p; }
            return _catalogue;
        }

        private string SetName(Design d, double m)
        {
            string r = _target.Den == 1 ? _target.Num.ToString(CultureInfo.InvariantCulture) : _target.Value.ToString("0.##", CultureInfo.InvariantCulture);
            return "ratio-" + r + "_m" + m.ToString("0.###", CultureInfo.InvariantCulture) + "_" + d.Zs + "-" + d.Zp + "-" + d.Zr1 + "-" + d.Zr2 + "-" + d.Zf + "-" + d.Zo;
        }

        private Candidate Current
        {
            get { return _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as Candidate : null; }
        }

        private void UpdateDimensions()
        {
            var c = Current;
            if (c == null) { _dims.Text = ""; _build.Enabled = false; _pickedTitle.Text = "4.  The picked solution"; return; }
            var g = QuickModel(c.Design, Module);
            _pickedTitle.Text = "4.  The picked solution: row " + (_list.SelectedItems[0].Index + 1) + ", teeth " + c.Design.TeethText + ", ratio " + c.Design.RatioText;
            _dims.Text = g.DimensionsText().Replace("\n", "\r\n").Replace("\r\r", "\r");
            _build.Enabled = true;
            string baseFolder = _folder.Tag as string ?? DefaultFolder();
            _folder.Text = Path.Combine(baseFolder, g.Name);
        }

        private string DefaultFolder()
        {
            if (!string.IsNullOrEmpty(_outputBase)) return _outputBase;
            string last = null;
            try { using (var k = Registry.CurrentUser.OpenSubKey(RegistryKey)) if (k != null) last = k.GetValue("OutputBase") as string; } catch { }
            if (!string.IsNullOrEmpty(last)) return last;
            return Path.Combine(_root ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "output");
        }

        private void Build()
        {
            var c = Current;
            if (c == null) return;
            if (string.IsNullOrWhiteSpace(_folder.Text)) { Fail("Choose an output folder."); return; }
            var g = QuickModel(c.Design, Module);
            if (g.Notes.Count > 0)
            {
                var r = MessageBox.Show(this, "This solution has warnings:\r\n\r\n" + string.Join("\r\n", g.Notes.ToArray()) + "\r\n\r\nBuild it anyway?",
                    "Gear Set", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
            }
            Result = g;
            OutputFolder = _folder.Text;
            SaveLast();
            DialogResult = DialogResult.OK;
            Close();
        }

        private void Fail(string text) { Status(text, true); }

        private void Status(string text, bool warn)
        {
            _status.Text = text;
            _status.ForeColor = warn ? Color.Firebrick : Color.FromArgb(0x1F, 0x4E, 0x9A);
        }

        // ------------------------------------------------------------------ remembered inputs
        private void LoadLast()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RegistryKey))
                {
                    if (k == null) return;
                    _faceWidth.Text = (k.GetValue("LastFaceWidth") as string) ?? "";
                    _backlash.Text = (k.GetValue("LastBacklash") as string) ?? _backlash.Text;
                    _sunMax.Text = (k.GetValue("LastSunMax") as string) ?? _sunMax.Text;
                }
            }
            catch { /* defaults */ }
        }

        private void SaveLast()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RegistryKey))
                {
                    if (k == null) return;
                    k.SetValue("LastModule", _module.Text);
                    k.SetValue("LastRatio", _ratio.Text);
                    k.SetValue("LastFaceWidth", _faceWidth.Text);
                    k.SetValue("LastBacklash", _backlash.Text);
                    k.SetValue("LastSunMax", _sunMax.Text);
                    k.SetValue(BearingLibrary.RegistryValue, _bearings.Text.Trim());
                    string parent = Path.GetDirectoryName(OutputFolder);
                    if (!string.IsNullOrEmpty(parent) && string.IsNullOrEmpty(_outputBase)) k.SetValue("OutputBase", parent);
                }
            }
            catch { /* not essential */ }
        }
    }
}
