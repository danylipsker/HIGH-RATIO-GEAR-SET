// Dialog: module + gear ratio in, list of tooth-count solutions to pick from, overall dimensions
// of the picked one, output folder, Build.
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

        public GearModel Result { get; private set; }
        public string OutputFolder { get; private set; }

        private readonly string _root;
        private readonly TextBox _module = new TextBox { Width = 60, Text = "1" };
        private readonly TextBox _ratio = new TextBox { Width = 90, Text = "500" };
        private readonly TextBox _sun = new TextBox { Width = 45 };
        private readonly TextBox _planet = new TextBox { Width = 45 };
        private readonly TextBox _maxOd = new TextBox { Width = 60 };
        private readonly TextBox _faceWidth = new TextBox { Width = 60 };
        private readonly TextBox _backlash = new TextBox { Width = 60, Text = "0" };
        private readonly TextBox _awMax = new TextBox { Width = 45, Text = "31" };
        private readonly CheckBox _nearest = new CheckBox { Text = "if no exact set exists, show the nearest ratios", AutoSize = true, Checked = true };
        private readonly Button _find = new Button { Text = "Find solutions", Width = 120, Height = 28 };
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
        private readonly TextBox _folder = new TextBox { Width = 420 };
        private readonly Button _browse = new Button { Text = "...", Width = 32 };
        private readonly Button _build = new Button { Text = "Build SOLIDWORKS files", Width = 170, Height = 30, Enabled = false };
        private readonly Button _close = new Button { Text = "Close", Width = 90, Height = 30, DialogResult = DialogResult.Cancel };
        private readonly Label _status = new Label { AutoSize = true, ForeColor = Color.DimGray };
        private readonly ToolTip _tips = new ToolTip { AutoPopDelay = 10000, InitialDelay = 400 };

        private List<Candidate> _candidates = new List<Candidate>();
        private Ratio _target;

        public GearSetForm(string root)
        {
            _root = root;
            Text = "Gear Set - high ratio single-planet reducer";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(900, 680);
            MinimumSize = new Size(820, 600);
            MaximizeBox = true; MinimizeBox = false;
            CancelButton = _close;

            var rootPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
            rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // inputs
            rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // options
            rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // heading
            rootPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 55));   // list
            rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // heading
            rootPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 45));   // dimensions
            rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // folder
            rootPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // buttons

            var row1 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Top };
            row1.Controls.Add(Bold("1. Module"));
            row1.Controls.Add(_module);
            row1.Controls.Add(Plain("mm      "));
            row1.Controls.Add(Bold("2. Gear ratio  1 :"));
            row1.Controls.Add(_ratio);
            row1.Controls.Add(Plain("      sun teeth"));
            row1.Controls.Add(_sun);
            row1.Controls.Add(Plain("planet teeth"));
            row1.Controls.Add(_planet);
            row1.Controls.Add(Plain("(blank = free)    "));
            row1.Controls.Add(_find);
            rootPanel.Controls.Add(row1);

            var row2 = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Top, Margin = new Padding(0, 4, 0, 0) };
            row2.Controls.Add(Plain("Max ring outer diameter"));
            row2.Controls.Add(_maxOd);
            row2.Controls.Add(Plain("mm     face width"));
            row2.Controls.Add(_faceWidth);
            row2.Controls.Add(Plain("mm (blank = 10 x module)     backlash"));
            row2.Controls.Add(_backlash);
            row2.Controls.Add(Plain("mm     max α_w"));
            row2.Controls.Add(_awMax);
            row2.Controls.Add(Plain("deg   "));
            row2.Controls.Add(_nearest);
            rootPanel.Controls.Add(row2);

            rootPanel.Controls.Add(Bold("3. Pick a solution (all tooth sets that give the ratio; best first)"));
            _list.Columns.Add("#", 32);
            _list.Columns.Add("Sun", 45);
            _list.Columns.Add("Planet", 50);
            _list.Columns.Add("Ring int", 60);
            _list.Columns.Add("Ring ext", 60);
            _list.Columns.Add("Fixed", 50);
            _list.Columns.Add("Output", 55);
            _list.Columns.Add("Ratio", 90);
            _list.Columns.Add("α_w °", 50);
            _list.Columns.Add("shift x", 55);
            _list.Columns.Add("Wall mm", 60);
            _list.Columns.Add("Ring OD mm", 80);
            _list.Columns.Add("Pack length mm", 95);
            _list.Columns.Add("Note", 120);
            var listHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 6) };
            listHost.Controls.Add(_list);
            rootPanel.Controls.Add(listHost);

            rootPanel.Controls.Add(Bold("4. Overall dimensions of the picked solution"));
            var dimHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 6) };
            dimHost.Controls.Add(_dims);
            rootPanel.Controls.Add(dimHost);

            var folderRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Top };
            folderRow.Controls.Add(Bold("5. Folder for the .SLDPRT / .SLDASM files"));
            folderRow.Controls.Add(_folder);
            folderRow.Controls.Add(_browse);
            rootPanel.Controls.Add(folderRow);

            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Top, Margin = new Padding(0, 8, 0, 0) };
            buttons.Controls.Add(_build);
            buttons.Controls.Add(_close);
            _status.Margin = new Padding(12, 8, 0, 0);
            buttons.Controls.Add(_status);
            rootPanel.Controls.Add(buttons);
            Controls.Add(rootPanel);

            _tips.SetToolTip(_ratio, "Integer, decimal or fraction: 500, 528.69, 1000/3");
            _tips.SetToolTip(_sun, "Force the sun tooth count (optional)");
            _tips.SetToolTip(_planet, "Force the planet tooth count (optional)");
            _tips.SetToolTip(_maxOd, "Hide solutions whose ring outer diameter exceeds this at the given module");
            _tips.SetToolTip(_awMax, "Design rule: working pressure angle of the profile-shifted ring mesh (31 deg default, up to ~34 possible)");
            _tips.SetToolTip(_backlash, "Circumferential backlash per mesh; teeth are thinned by half of it. 0 = theoretical, touching flanks");
            _tips.SetToolTip(_list, "Double-click a row to build it");

            _find.Click += (s, e) => FindSolutions();
            _list.SelectedIndexChanged += (s, e) => UpdateDimensions();
            _list.ItemActivate += (s, e) => { if (_build.Enabled) _build.PerformClick(); };
            _browse.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog { SelectedPath = _folder.Text, Description = "Folder for the SOLIDWORKS files" })
                    if (dlg.ShowDialog(this) == DialogResult.OK) _folder.Text = dlg.SelectedPath;
            };
            _build.Click += (s, e) => Build();
            _ratio.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { FindSolutions(); e.SuppressKeyPress = true; } };
            _module.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { FindSolutions(); e.SuppressKeyPress = true; } };
            _module.TextChanged += (s, e) => { if (_candidates.Count > 0) FillList(); };

            LoadLast();
            Shown += (s, e) => { _ratio.Focus(); FindSolutions(); };
        }

        private static Label Bold(string t) { return new Label { Text = t, AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Margin = new Padding(0, 6, 6, 0) }; }
        private static Label Plain(string t) { return new Label { Text = t, AutoSize = true, Margin = new Padding(0, 6, 6, 0) }; }

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

        // ------------------------------------------------------------------ solve
        private void FindSolutions()
        {
            if (!Ratio.TryParse(_ratio.Text, out _target)) { Fail("Enter the gear ratio as a number (500, 528.69) or a fraction (1000/3)."); return; }
            if (Num(_module) == null || Module <= 0) { Fail("Enter the module in mm."); return; }
            var lim = new Limits();
            double? aw = Num(_awMax);
            if (aw.HasValue) lim.AwMaxDeg = Math.Min(40, Math.Max(15, aw.Value));
            Cursor = Cursors.WaitCursor;
            try
            {
                _candidates = Solver.Search(_target, lim, Int(_sun), Int(_planet), true, 40);
                bool exact = _candidates.Count > 0;
                if (!exact && _nearest.Checked) _candidates = Solver.Search(_target, lim, Int(_sun), Int(_planet), false, 40);
                FillList();
                if (_candidates.Count == 0)
                    Fail("No tooth set gives 1:" + _target.Value.ToString("0.##", CultureInfo.InvariantCulture) + " within the design rules. Free the sun / planet counts, raise the max pressure angle, or tick 'nearest'.");
                else
                    Status((exact ? "exact solutions: " : "no exact set - nearest ratios shown: ") + _list.Items.Count + (_list.Items.Count < _candidates.Count ? " shown of " + _candidates.Count + " (ring OD limit)" : ""), false);
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
                it.SubItems.Add(d.RatioValue.ToString("0.###", CultureInfo.InvariantCulture));
                it.SubItems.Add(d.AwDeg.ToString("0.0", CultureInfo.InvariantCulture));
                it.SubItems.Add(d.XShift.ToString("+0.00;-0.00", CultureInfo.InvariantCulture));
                it.SubItems.Add(g.ThinRingWall.ToString("0.0", CultureInfo.InvariantCulture));
                it.SubItems.Add(g.RingOD.ToString("0.0", CultureInfo.InvariantCulture));
                it.SubItems.Add(g.GearPackLength.ToString("0.0", CultureInfo.InvariantCulture));
                string note = c.Error > 1e-12 ? "nearest, " + (c.Error * 100).ToString("0.0", CultureInfo.InvariantCulture) + " % off" : "exact";
                if (g.Notes.Count > 0) note += " - " + g.Notes[0];
                it.SubItems.Add(note);
                _list.Items.Add(it);
            }
            _list.EndUpdate();
            if (_list.Items.Count > 0) { _list.Items[0].Selected = true; _list.Items[0].Focused = true; }
            else { _dims.Text = ""; _build.Enabled = false; }
        }

        private GearModel QuickModel(Design d, double m)
        {
            return GearModel.Create(d, m, Num(_faceWidth) ?? 0, 0, Num(_backlash) ?? 0, 0, 0, 5.0, 6, SetName(d, m));
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
            if (c == null) { _dims.Text = ""; _build.Enabled = false; return; }
            var g = QuickModel(c.Design, Module);
            _dims.Text = g.DimensionsText().Replace("\n", "\r\n").Replace("\r\r", "\r");
            _build.Enabled = true;
            string baseFolder = _folder.Tag as string ?? DefaultFolder();
            _folder.Text = Path.Combine(baseFolder, g.Name);
        }

        private string DefaultFolder()
        {
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
            _status.ForeColor = warn ? Color.Firebrick : Color.DimGray;
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
                    string parent = Path.GetDirectoryName(OutputFolder);
                    if (!string.IsNullOrEmpty(parent)) k.SetValue("OutputBase", parent);
                }
            }
            catch { /* not essential */ }
        }
    }
}
