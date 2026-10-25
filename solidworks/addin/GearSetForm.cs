// Dialog, in the order the user works: 1 module, 2 gear ratio, 3 Find solutions -> the table of
// every tooth set that gives the ratio (best first) -> everything below follows the row picked:
// the dimensions, the folder, Build. The search range and the rarely needed inputs (an exact tooth
// set, presets, max ring OD, face width, backlash, root fillet) are under "Advanced", folded.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
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
        private readonly TextBox _module = new TextBox { Width = 64, Text = "1", Font = new Font("Segoe UI", 11f) };
        private readonly TextBox _ratio = new TextBox { Width = 96, Text = "500", Font = new Font("Segoe UI", 11f) };
        private readonly Button _find = new Button { Text = "3.  Find solutions", Width = 150, Height = 32, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
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
        private readonly CheckBox _nearest = new CheckBox { Text = "when no exact set exists, list the nearest ratios (within 15 %)", AutoSize = true, Checked = true };
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
        private readonly TextBox _folder = new TextBox { Width = 420 };
        private readonly Button _browse = new Button { Text = "...", Width = 32 };
        private readonly Button _build = new Button { Text = "Build SOLIDWORKS files", Width = 190, Height = 32, Enabled = false, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        private readonly Button _close = new Button { Text = "Close", Width = 90, Height = 32, DialogResult = DialogResult.Cancel };
        private readonly Label _pickedTitle = new Label { Text = "5.  The picked solution", AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Margin = new Padding(0, 6, 6, 0) };
        private readonly ToolTip _tips = new ToolTip { AutoPopDelay = 12000, InitialDelay = 400 };

        private List<Candidate> _candidates = new List<Candidate>();
        private Ratio _target;
        private bool _givenSet;

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
            rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 1 2 3
            rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // status
            rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // advanced toggle
            rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // advanced panel
            rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 4 heading
            rootPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 58));   // table
            rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 5 heading
            rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // options
            rootPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 42));   // dimensions
            rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // folder
            rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // buttons

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

            // ---- advanced (folded) ----------------------------------------------------------------
            _advanced.Margin = new Padding(0, 6, 0, 0);
            rootPanel.Controls.Add(_advanced);
            var adv = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Top, Margin = new Padding(0), Padding = new Padding(8, 4, 8, 6), BackColor = Color.FromArgb(0xF3, 0xF4, 0xF6) };
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

            // ---- 4 the table ---------------------------------------------------------------------
            var head4 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Top, Margin = new Padding(0, 8, 0, 0) };
            head4.Controls.Add(Bold("4.  Pick a solution"));
            head4.Controls.Add(Plain("every tooth set that gives the ratio, best first; the rows below the table follow the one you pick; double-click builds it"));
            rootPanel.Controls.Add(head4);
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
            var listHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 6) };
            listHost.Controls.Add(_list);
            rootPanel.Controls.Add(listHost);

            // ---- 5 what follows ------------------------------------------------------------------
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

            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Top, Margin = new Padding(0, 8, 0, 0) };
            buttons.Controls.Add(_build);
            _close.Margin = new Padding(8, 0, 0, 0);
            buttons.Controls.Add(_close);
            rootPanel.Controls.Add(buttons);
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
            _tips.SetToolTip(_housing, "Front cover with the sun's bearing, shell clamping the fixed ring, output ring extended into a cup with a hub on a large bearing in the rear cover, and the sun's rear bearing inside the hub. Standard 60xx deep-groove bearings.");
            _housing.CheckedChanged += (s, e) => { if (_candidates.Count > 0) FillList(); };

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
            foreach (var tb in new[] { _faceWidth, _backlash, _fillet })
                tb.TextChanged += (s, e) => { if (_candidates.Count > 0) UpdateDimensions(); };

            LoadLast();
            Shown += (s, e) => { _ratio.Focus(); _ratio.SelectAll(); FindSolutions(); };
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
                FillList();
                if (why != null) Fail("Your set: " + why + " - it is listed anyway, build at your own risk.");
                else Status("Your tooth set, ratio " + des.RatioText + ". Clear the set under Advanced to search by ratio again.", false);
                return;
            }
            if (!Ratio.TryParse(_ratio.Text, out _target)) { Fail("2. Enter the gear ratio as a number (500, 528.69) or a fraction (1000/3)."); return; }
            Cursor = Cursors.WaitCursor;
            try
            {
                int? sun = Int(_sun), planet = Int(_planet);
                _candidates = Solver.Search(_target, lim, sun, planet, true, 40);
                bool exact = _candidates.Count > 0;
                List<Candidate> near = null;
                if (!exact)
                {
                    near = Solver.Search(_target, lim, sun, planet, false, 40);
                    _candidates = _nearest.Checked ? near.FindAll(c => c.Error <= NearestWindow) : new List<Candidate>();
                }
                FillList();
                string tgt = _target.Value.ToString("0.###", CultureInfo.InvariantCulture);
                string range = (sun.HasValue || planet.HasValue)
                    ? " with the sun / planet forced to " + (sun.HasValue ? sun.Value.ToString() : "free") + " / " + (planet.HasValue ? planet.Value.ToString() : "free")
                    : " (sun " + lim.SunMin + ".." + lim.SunMax + ", planet " + lim.PlanetMin + ".." + lim.PlanetMax + ", rings up to " + lim.DeltaMax + " teeth apart)";
                if (exact)
                    Status("Exact 1:" + tgt + ": " + _list.Items.Count + " tooth set" + (_list.Items.Count == 1 ? "" : "s") + range
                        + (_list.Items.Count < _candidates.Count ? ", " + (_candidates.Count - _list.Items.Count) + " more over the ring OD limit" : "") + ". Pick one in the table.", false);
                else if (_list.Items.Count > 0)
                    Status("No set gives exactly 1:" + tgt + range + "; the " + _list.Items.Count + " nearest within 15 % are listed, the error in the Note column. "
                        + "Under Advanced you can widen the sun range or free the forced counts.", true);
                else
                {
                    string reach = "";
                    if (near != null && near.Count > 0)
                    {
                        double lo = double.MaxValue, hi = 0;
                        foreach (var c in near) { double v = Math.Abs(c.Design.RatioValue); if (v < lo) lo = v; if (v > hi) hi = v; }
                        reach = " The closest this mechanism reaches" + range + " is 1:" + (Math.Abs(lo - _target.Value) < Math.Abs(hi - _target.Value) ? lo : hi).ToString("0.#", CultureInfo.InvariantCulture) + ".";
                    }
                    Fail("No tooth set gives 1:" + tgt + " within 15 %" + range + "." + reach
                        + " Ratios under about 1:200 are not this mechanism's: use a planetary or compound planetary set. Otherwise, under Advanced, raise the sun range (to 40..60) or the ring tooth difference, or free the forced counts.");
                }
            }
            finally { Cursor = Cursors.Default; }
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
            else { _dims.Text = ""; _build.Enabled = false; _pickedTitle.Text = "5.  The picked solution"; }
        }

        private GearModel QuickModel(Design d, double m)
        {
            var g = GearModel.Create(d, m, Num(_faceWidth) ?? 0, 0, Num(_backlash) ?? 0, 0, 0, 5.0, 6, SetName(d, m), Num(_fillet) ?? 0.3);
            if (_housing.Checked) g.AddHousing();
            return g;
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
            if (c == null) { _dims.Text = ""; _build.Enabled = false; _pickedTitle.Text = "5.  The picked solution"; return; }
            var g = QuickModel(c.Design, Module);
            _pickedTitle.Text = "5.  The picked solution: row " + (_list.SelectedItems[0].Index + 1) + ", teeth " + c.Design.TeethText + ", ratio " + c.Design.RatioText;
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
                    _module.Text = (k.GetValue("LastModule") as string) ?? _module.Text;
                    _ratio.Text = (k.GetValue("LastRatio") as string) ?? _ratio.Text;
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
                    string parent = Path.GetDirectoryName(OutputFolder);
                    if (!string.IsNullOrEmpty(parent) && string.IsNullOrEmpty(_outputBase)) k.SetValue("OutputBase", parent);
                }
            }
            catch { /* not essential */ }
        }
    }
}
