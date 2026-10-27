// GearSetCli - command line companion of the Gear Set add-in.
//   solve        list the tooth sets for a ratio (no SOLIDWORKS)
//   test-dialog  show the dialog alone (no SOLIDWORKS)
//   build        build the parts + assembly through SOLIDWORKS (running instance, or a hidden new one)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

namespace GearSet.Cli
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try { return Run(args); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR " + ex);
                return 1;
            }
        }

        private static void Usage()
        {
            Console.WriteLine("GearSetCli solve -ratio 500 [-module 1] [-sun 16] [-planet 32] [-nearest] [-aw 31] [-max-od 150]");
            Console.WriteLine("GearSetCli solve -set 16,32,115,123,158,155 -module 1.5          exactly this tooth set (also for build)");
            Console.WriteLine("GearSetCli solve ... -fillet 0.3 (x module, 0 = sharp)   -dump sun|planet|thinint|thinext|fixed|output  prints the outline");
            Console.WriteLine("GearSetCli test-dialog                     the dialog alone, no SOLIDWORKS");
            Console.WriteLine("GearSetCli build -ratio 500 [-module 1] [-pick 1] [-out <folder>] [-sun N] [-planet N] [-backlash 0.05] [-no-mates] [-no-housing]");
            Console.WriteLine("                 [-sw 2020] [-new] [-show]   uses the running SOLIDWORKS, or starts one (-new; hidden unless -show)");
        }

        private static Dictionary<string, string> Parse(string[] args, out string cmd)
        {
            var o = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            cmd = args.Length > 0 ? args[0] : null;
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (!a.StartsWith("-")) continue;
                string key = a.TrimStart('-');
                string val = i + 1 < args.Length && !args[i + 1].StartsWith("-") ? args[++i] : "";
                o[key] = val;
            }
            return o;
        }

        private static string Get(Dictionary<string, string> o, string k) { string v; return o.TryGetValue(k, out v) ? v : null; }

        private static double Dbl(Dictionary<string, string> o, string k, double def)
        {
            string v = Get(o, k);
            double d;
            return v != null && double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : def;
        }

        private static int? IntOpt(Dictionary<string, string> o, string k)
        {
            string v = Get(o, k);
            int i;
            return v != null && int.TryParse(v, out i) ? i : (int?)null;
        }

        private static int Run(string[] args)
        {
            string cmd;
            var o = Parse(args, out cmd);
            if (cmd == null || cmd == "help" || cmd == "-h" || cmd == "--help") { Usage(); return 2; }
            if (cmd == "test-dialog")
            {
                Application.EnableVisualStyles();
                using (var f = new GearSetForm(FindRoot()))
                {
                    if (f.ShowDialog() == DialogResult.OK)
                        Console.WriteLine("chosen: " + f.Result.Name + " -> " + f.OutputFolder + System.Environment.NewLine + f.Result.DimensionsText());
                    else Console.WriteLine("cancelled");
                }
                return 0;
            }
            if (cmd == "solve") return Solve(o, null);
            if (cmd == "build")
            {
                bool started;
                ISldWorks sw = Connect(Get(o, "sw"), Get(o, "new") != null, out started);
                Console.WriteLine("SOLIDWORKS revision " + sw.RevisionNumber());
                if (started) sw.Visible = Get(o, "show") != null;
                try { return Solve(o, sw); }
                finally
                {
                    if (started && Get(o, "show") == null) { try { sw.ExitApp(); } catch { } }
                }
            }
            Usage();
            return 2;
        }

        private static string FindRoot()
        {
            string dir = Path.GetDirectoryName(typeof(Program).Assembly.Location);
            for (int i = 0; i < 6 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir, "gearset.py"))) return dir;
                dir = Path.GetDirectoryName(dir);
            }
            return System.Environment.CurrentDirectory;
        }

        private static int Solve(Dictionary<string, string> o, ISldWorks sw)
        {
            Ratio target;
            double m = Dbl(o, "module", 1.0);
            var lim = new Limits { AwMaxDeg = Dbl(o, "aw", 31.0) };
            int? sun = IntOpt(o, "sun"), planet = IntOpt(o, "planet");
            List<Candidate> list;
            bool exact;
            int[] z;
            if (Design.TryParseTeeth(Get(o, "set") ?? "", out z))
            {
                var des = Design.FromTeeth(z[0], z[1], z[2], z[3], z[4], z[5], lim);
                string why = des.Validate(lim);
                if (why != null) Console.WriteLine("warning: " + why);
                target = Ratio.Normalize(Math.Abs(des.RatioNum), Math.Abs(des.RatioDen));
                list = new List<Candidate> { new Candidate { Design = des } };
                exact = true;
            }
            else
            {
                if (!Ratio.TryParse(Get(o, "ratio") ?? "500", out target)) { Console.Error.WriteLine("bad -ratio"); return 2; }
                list = Solver.Search(target, lim, sun, planet, true, 40);
                exact = list.Count > 0;
                if (!exact && (Get(o, "nearest") != null || sw != null)) list = Solver.Search(target, lim, sun, planet, false, 40);
            }
            double maxOd = Dbl(o, "max-od", 0);
            Console.WriteLine((exact ? "exact" : "nearest") + " solutions for 1:" + target.Value.ToString("0.###", CultureInfo.InvariantCulture) + " at module " + m + ":");
            Console.WriteLine("  #   Zs  Zp  Zr1  Zr2   Zf   Zo   ratio       aw     x    wall   ringOD  length");
            var models = new List<GearModel>();
            foreach (var c in list)
            {
                var g = GearModel.Create(c.Design, m, Dbl(o, "face", 0), 0, Dbl(o, "backlash", 0), 0, 0, 5.0, 6,
                    "ratio-" + target.Value.ToString("0.##", CultureInfo.InvariantCulture) + "_m" + m.ToString("0.###", CultureInfo.InvariantCulture)
                    + "_" + c.Design.Zs + "-" + c.Design.Zp + "-" + c.Design.Zr1 + "-" + c.Design.Zr2 + "-" + c.Design.Zf + "-" + c.Design.Zo,
                    Dbl(o, "fillet", 0.3));
                if (maxOd > 0 && g.RingOD > maxOd) continue;
                if (Get(o, "no-housing") == null) g.AddHousing();
                models.Add(g);
                var d = c.Design;
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,3}  {1,3} {2,3} {3,4} {4,4} {5,4} {6,4}  {7,10:0.###}  {8,5:0.0}  {9,5:+0.00;-0.00}  {10,5:0.0}  {11,7:0.0}  {12,6:0.0}{13}",
                    models.Count, d.Zs, d.Zp, d.Zr1, d.Zr2, d.Zf, d.Zo, d.RatioValue, d.AwDeg, d.XShift, g.ThinRingWall, g.RingOD, g.GearPackLength,
                    g.Notes.Count > 0 ? "  ! " + g.Notes[0] : ""));
            }
            if (models.Count == 0) { Console.WriteLine("  none - free the tooth counts, raise -aw, or use -nearest"); return 3; }
            if (Get(o, "dump") != null)
            {
                // polar outline of one gear (sun, planet, thinint, thinext, fixed, output) for cross-checks against gearset.py
                GearModel gm = models[0];
                string which = Get(o, "dump").ToLowerInvariant();
                GearGeom gg = which == "planet" ? gm.Planet : which == "thinint" ? gm.ThinInt : which == "thinext" ? gm.ThinExt
                            : which == "fixed" ? gm.Fixed : which == "output" ? gm.Output : gm.Sun;
                foreach (double[] pt in gg.Polyline(12, 1.0))
                    Console.WriteLine(pt[0].ToString("R", CultureInfo.InvariantCulture) + " " + pt[1].ToString("R", CultureInfo.InvariantCulture));
                return 0;
            }
            if (sw == null) return 0;

            int pick = (int)Dbl(o, "pick", 1);
            if (pick < 1 || pick > models.Count) pick = 1;
            GearModel model = models[pick - 1];
            string outDir = Get(o, "out") ?? Path.Combine(FindRoot(), "output", model.Name);
            Console.WriteLine();
            Console.WriteLine(model.DimensionsText());
            var builder = new GearBuilder(sw) { Progress = s => Console.WriteLine("  " + s) };
            BuildResult r = builder.Build(model, outDir, true, Get(o, "no-mates") == null);
            foreach (string f in r.Files) Console.WriteLine(f);
            foreach (string line in r.MateReport) Console.WriteLine("  mate: " + line);
            foreach (string line in r.SketchReport) Console.WriteLine("  sketch: " + line);
            Console.WriteLine("  sketches checked in SolidWorks: " + r.SketchesChecked + ", problems: " + r.SketchProblems + (r.MotionStudy != null ? "; " + r.MotionStudy : ""));
            return 0;
        }

        private static ISldWorks Connect(string year, bool newInstance, out bool started)
        {
            started = false;
            object obj = null;
            if (!newInstance)
            {
                try { obj = Marshal.GetActiveObject("SldWorks.Application"); }
                catch (COMException) { obj = null; }
                if (obj != null) Console.WriteLine("SOLIDWORKS: using the running instance");
            }
            if (obj == null)
            {
                string progId = "SldWorks.Application";
                if (!string.IsNullOrEmpty(year)) progId += "." + (int.Parse(year, CultureInfo.InvariantCulture) - 1992);   // 2020 -> 28
                int before = System.Diagnostics.Process.GetProcessesByName("SLDWORKS").Length;
                Console.WriteLine("SOLIDWORKS: starting " + progId + " ...");
                Type t = Type.GetTypeFromProgID(progId, true);
                obj = Activator.CreateInstance(t);
                started = System.Diagnostics.Process.GetProcessesByName("SLDWORKS").Length > before;
                if (!started) Console.WriteLine("SOLIDWORKS: attached to the running instance");
            }
            return (ISldWorks)obj;
        }
    }
}
