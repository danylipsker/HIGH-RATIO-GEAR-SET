// Builds the gear set as native SOLIDWORKS parts and one assembly through the API.
// Lengths in the API are metres; GearModel holds millimetres.
//
// Every toothed outline is one closed sketch chain of flank splines, tip/root arcs and radial
// lines (all end points computed from the same numbers, so the chain closes exactly), extruded
// once. The extrude / start-offset / arc direction conventions are measured on a scratch part
// first, so stacking along Z comes out right on any installation.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace GearSet
{
    /// <summary>Catalogue bearing parts. Every .SLDPRT given (a file, or a folder of them; ';' separated) is opened once in
    /// SolidWorks and each of its configurations measured: the bore axis is the short extent of the bounding box (the two
    /// other extents, equal, give the OD), the width is that short extent, the bore is the smallest cylindrical face on
    /// that axis. The result is cached under %LOCALAPPDATA%\GearSetAddin\bearings, keyed by the file's path, size and
    /// time, so without SolidWorks (the dialog, GearSetCli solve) FromCache gives the same bearings a build will use once
    /// the files have been scanned. Known sources: NSK's "600 Series" part (118 configurations named by designation,
    /// 681 .. 639 and MR, bore 1..9 mm, axis Z) and the GOST 8338-75 folder (one file per bearing, 100 = 6000 .. 120 =
    /// 6020 and 16 .. 18, axis X, rings + balls + cage).</summary>
    public class BearingLibrary
    {
        public const string RegistryValue = "BearingLibrary";
        public static readonly string[] KnownFolders =
        {
            @"C:\HARDWARE AND MORE\Bearings\nsk-minature-ball-bearings-600-series-solidworks-2018-1.snapshot.2",
            @"C:\HARDWARE AND MORE\Bearings\1xx-8338-75-radial-ball-bearing-60xx-iso-1.snapshot.2",
        };
        public List<Bearing> Bearings = new List<Bearing>();
        public List<string> Report = new List<string>();

        /// <summary>The remembered library (HKCU\Software\GearSetAddin\BearingLibrary; an empty value means none), else the
        /// known folders that exist on this machine.</summary>
        public static string DefaultPaths()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(GearSetForm.RegistryKey))
                {
                    string v = k == null ? null : k.GetValue(RegistryValue) as string;
                    if (v != null) return v;
                }
            }
            catch { /* defaults */ }
            var found = new List<string>();
            foreach (string f in KnownFolders) if (Directory.Exists(f)) found.Add(f);
            return string.Join(";", found.ToArray());
        }

        /// <summary>The .SLDPRT files named by a ';'-separated list of files and folders (folders: their parts, sorted).</summary>
        public static List<string> Files(string paths)
        {
            var files = new List<string>();
            if (string.IsNullOrWhiteSpace(paths)) return files;
            foreach (string raw in paths.Split(';'))
            {
                string p = raw.Trim().Trim('"');
                if (p.Length == 0) continue;
                if (Directory.Exists(p))
                {
                    var l = new List<string>(Directory.GetFiles(p, "*.SLDPRT"));
                    l.Sort(StringComparer.OrdinalIgnoreCase);
                    files.AddRange(l);
                }
                else if (File.Exists(p)) files.Add(p);
            }
            return files;
        }

        /// <summary>The cached measurements only (no SolidWorks); files not scanned yet are named in Report.</summary>
        public static BearingLibrary FromCache(string paths)
        {
            var lib = new BearingLibrary();
            foreach (string f in Files(paths))
            {
                List<Bearing> l = ReadCache(f);
                if (l == null) lib.Report.Add(Path.GetFileName(f) + ": not measured yet (the next build in SolidWorks does it)");
                else { lib.Bearings.AddRange(l); lib.Report.Add(Path.GetFileName(f) + ": " + l.Count + " bearings (cached)"); }
            }
            return lib;
        }

        /// <summary>The library, measuring in SolidWorks whatever is not cached yet.</summary>
        public static BearingLibrary Scan(ISldWorks sw, string paths, Action<string> progress)
        {
            var lib = new BearingLibrary();
            List<string> files = Files(paths);
            if (files.Count == 0) lib.Report.Add("no .SLDPRT found in: " + paths);
            foreach (string f in files)
            {
                List<Bearing> l = ReadCache(f);
                if (l == null)
                {
                    if (progress != null) progress("measuring the bearings in " + Path.GetFileName(f));
                    l = Measure(sw, f, lib.Report);
                    WriteCache(f, l);
                }
                lib.Bearings.AddRange(l);
                lib.Report.Add(Path.GetFileName(f) + ": " + l.Count + " bearings");
            }
            return lib;
        }

        private static List<Bearing> Measure(ISldWorks sw, string file, List<string> report)
        {
            var list = new List<Bearing>();
            int e = 0, w = 0;
            ModelDoc2 doc = sw.OpenDoc6(file, (int)swDocumentTypes_e.swDocPART,
                (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "", ref e, ref w);
            if (doc == null) { report.Add(Path.GetFileName(file) + ": could not be opened (error " + e + ")"); return list; }
            try
            {
                IPartDoc part = (IPartDoc)doc;
                object[] names = doc.GetConfigurationNames() as object[];
                bool many = names != null && names.Length > 1;
                string active = null;
                try { active = doc.ConfigurationManager.ActiveConfiguration.Name; } catch { }
                var configs = new List<string>();
                if (many) foreach (object o in names) configs.Add((string)o);
                else configs.Add("");
                foreach (string cfg in configs)
                {
                    if (cfg.Length > 0) { doc.ShowConfiguration2(cfg); doc.ForceRebuild3(false); }
                    Bearing b = MeasureShape(part, file, cfg);
                    if (b == null) { report.Add(Path.GetFileName(file) + (cfg.Length > 0 ? " [" + cfg + "]" : "") + ": not a bearing shape (round about one axis, centred, with a bore), skipped"); continue; }
                    list.Add(b);
                }
                if (many && active != null) { try { doc.ShowConfiguration2(active); } catch { } }
            }
            finally { try { sw.CloseDoc(doc.GetTitle()); } catch { } }
            return list;
        }

        private static Bearing MeasureShape(IPartDoc part, string file, string cfg)
        {
            double[] box = part.GetPartBox(true) as double[];
            if (box == null || box.Length < 6) return null;
            double[] ext = { (box[3] - box[0]) * 1000, (box[4] - box[1]) * 1000, (box[5] - box[2]) * 1000 };
            double[] mid = { (box[0] + box[3]) * 500, (box[1] + box[4]) * 500, (box[2] + box[5]) * 500 };
            int ax = 0;
            for (int i = 1; i < 3; i++) if (ext[i] < ext[ax]) ax = i;
            int o1 = (ax + 1) % 3, o2 = (ax + 2) % 3;
            if (Math.Abs(ext[o1] - ext[o2]) > 0.02 * Math.Max(ext[o1], ext[o2]) + 0.05) return null;   // not round about that axis
            if (Math.Abs(mid[o1]) > 0.05 || Math.Abs(mid[o2]) > 0.05) return null;                        // not on the axis
            double od = Math.Max(ext[o1], ext[o2]), width = ext[ax];
            if (width >= od) return null;
            double bore = double.MaxValue;
            object[] bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
            if (bodies == null) return null;
            foreach (object bo in bodies)
            {
                object[] faces = ((IBody2)bo).GetFaces() as object[];
                if (faces == null) continue;
                foreach (object fo in faces)
                {
                    ISurface s = (ISurface)((IFace2)fo).GetSurface();
                    if (s == null || !s.IsCylinder()) continue;
                    double[] p = s.CylinderParams as double[];           // origin x y z, axis x y z, radius (metres)
                    if (p == null || p.Length < 7 || Math.Abs(p[3 + ax]) < 0.999) continue;
                    if (Math.Abs(p[o1]) * 1000 > 0.02 || Math.Abs(p[o2]) * 1000 > 0.02) continue;   // a cage pocket, not the bore
                    bore = Math.Min(bore, 2 * p[6] * 1000);
                }
            }
            if (bore == double.MaxValue || bore >= od - 0.01) return null;
            bore = Math.Round(bore, 3); od = Math.Round(od, 3); width = Math.Round(width, 3);
            string name = Bearing.IsoName(bore, od, width) ?? (cfg.Length > 0 ? cfg : Path.GetFileNameWithoutExtension(file));
            return new Bearing { File = file, Config = cfg, Axis = "XYZ"[ax], AxisOffset = Math.Round(mid[ax], 4), Bore = bore, OD = od, Width = width, Name = name };
        }

        // ---- the cache: one text file per catalogue part, first line = size|time, then one bearing per line
        private static string CacheFile(string file)
        {
            string dir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "GearSetAddin", "bearings");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, Fnv(file.ToLowerInvariant()) + ".txt");
        }

        private static string Fnv(string s)
        {
            ulong h = 14695981039346656037;
            foreach (char c in s) { h ^= c; h *= 1099511628211; }
            return h.ToString("x16");
        }

        private static string Stamp(string file)
        {
            var fi = new FileInfo(file);
            return fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
        }

        private static List<Bearing> ReadCache(string file)
        {
            string cf = CacheFile(file);
            if (!File.Exists(cf)) return null;
            string[] lines = File.ReadAllLines(cf);
            if (lines.Length == 0 || lines[0] != Stamp(file)) return null;
            var list = new List<Bearing>();
            for (int i = 1; i < lines.Length; i++)
            {
                string[] t = lines[i].Split('\t');
                if (t.Length < 7) continue;
                list.Add(new Bearing { File = file, Config = t[0], Axis = t[1][0], AxisOffset = D(t[2]), Bore = D(t[3]), OD = D(t[4]), Width = D(t[5]), Name = t[6] });
            }
            return list;
        }

        private static void WriteCache(string file, List<Bearing> list)
        {
            var lines = new List<string> { Stamp(file) };
            foreach (Bearing b in list)
                lines.Add(b.Config + "\t" + b.Axis + "\t" + S(b.AxisOffset) + "\t" + S(b.Bore) + "\t" + S(b.OD) + "\t" + S(b.Width) + "\t" + b.Name);
            File.WriteAllLines(CacheFile(file), lines.ToArray());
        }

        private static double D(string s) { return double.Parse(s, CultureInfo.InvariantCulture); }
        private static string S(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }
    }

    public class BuildResult
    {
        public List<string> Files = new List<string>();
        public string AssemblyPath;
        public string Folder;
        public int MatesAdded, MatesWanted;
        public List<string> MateReport = new List<string>();
        public string MotionStudy;              // what the Motion Study got, null = none
        public List<string> SketchReport = new List<string>();   // one line per profile sketch, as SolidWorks saw it
        public int SketchesChecked, SketchProblems;
        public List<string> BearingReport = new List<string>();  // which bearing came from which catalogue part, and how it was placed
    }

    public class GearBuilder
    {
        private const int NFlank = 12;
        private readonly ISldWorks _sw;
        private readonly string _partTemplate, _asmTemplate;
        private bool _dirFlip, _offFlip;
        private short _arcCcw = 1;
        public Action<string> Progress = s => { };
        public List<string> Log = new List<string>();
        /// <summary>After the mates: a Motion Study "Sun drive" with a constant-speed rotary motor on the sun's
        /// input stub. 60 rpm for 10 s = 10 sun turns: 7.2 degrees on the output ring of the 1:500 set.</summary>
        public bool AddMotor = true;
        public double MotorRpm = 60, MotorSeconds = 10;
        /// <summary>Catalogue bearing parts (.SLDPRT files, or folders of them, ';' separated - see BearingLibrary): the
        /// housing takes its bearings from these real parts, measured once in SolidWorks and cached, instead of the
        /// plain rings it would otherwise make. Blank = plain rings. Default: the dialog's remembered value, else the
        /// known folders under C:\HARDWARE AND MORE\Bearings.</summary>
        public string BearingLibraryPaths = BearingLibrary.DefaultPaths();
        public BearingLibrary Library;              // scanned on the first build that needs it
        private IFace2 _sunFace;                    // the sun's input stub, found by AddMates, the motor's face
        // what AddOutline put into the sketch being drawn, so Extrude can ask SolidWorks whether it got exactly that
        private int _expOutlines, _expArcs, _expFillets, _expLines, _expSplines, _expTeeth;
        private string _expName = "";
        private readonly List<string> _sketchReport = new List<string>();
        private int _sketchesChecked, _sketchProblems;

        public GearBuilder(ISldWorks sw)
        {
            _sw = sw;
            _partTemplate = sw.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
            _asmTemplate = sw.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly);
            if (string.IsNullOrEmpty(_partTemplate) || string.IsNullOrEmpty(_asmTemplate))
                throw new InvalidOperationException("No default part / assembly template is set in SOLIDWORKS (Tools > Options > Default Templates).");
        }

        private void Say(string s) { Log.Add(s); Progress(s); }

        /// <summary>Builds six parts and the assembly into <paramref name="outDir"/>; returns the file list.</summary>
        public BuildResult Build(GearModel g, string outDir, bool closeParts, bool addMates = true)
        {
            Directory.CreateDirectory(outDir);
            var res = new BuildResult { Folder = outDir };
            _sketchReport.Clear(); _sketchesChecked = 0; _sketchProblems = 0;
            Say("calibrating API direction conventions");
            Calibrate();
            Say("calibration: dirFlip=" + _dirFlip + " offFlip=" + _offFlip + " arcCCW=" + _arcCcw);
            if (g.Housing != null) UseCatalogueBearings(g, res);

            var paths = new Dictionary<string, string>();
            Say("fixed ring (" + g.Design.Zf + " teeth)");
            paths["FixedRing"] = BuildRing(g, g.Fixed, g.ZA0, g.ZA1, outDir, "FixedRing");
            Say("output ring (" + g.Design.Zo + " teeth)" + (g.Housing != null ? " as output cup" : ""));
            paths["OutputRing"] = g.Housing != null ? BuildOutputCup(g, outDir) : BuildRing(g, g.Output, g.ZB0, g.ZB1, outDir, "OutputRing");
            Say("sun (" + g.Design.Zs + " teeth)");
            paths["Sun"] = BuildSun(g, outDir);
            Say("planet (" + g.Design.Zp + " teeth)");
            paths["Planet"] = BuildPlanet(g, outDir);
            Say("thin ring (" + g.Design.Zr1 + " internal / " + g.Design.Zr2 + " external teeth)");
            paths["ThinRing"] = BuildThinRing(g, outDir);
            Say("carrier");
            paths["Carrier"] = BuildCarrier(g, outDir);
            if (g.Housing != null)
            {
                HousingSpec h = g.Housing;
                Say("front cover"); paths["FrontCover"] = BuildFrontCover(g, outDir);
                Say("shell"); paths["Shell"] = BuildShell(g, outDir);
                Say("rear cover"); paths["RearCover"] = BuildRearCover(g, outDir);
                if (!h.SunBearing.FromCatalogue) { Say("bearing " + h.SunBearing.Name); paths["BearingSun"] = BuildBearing(g, h.SunBearing, outDir); }
                if (!h.HubBearing.FromCatalogue) { Say("bearing " + h.HubBearing.Name); paths["BearingHub"] = BuildBearing(g, h.HubBearing, outDir); }
            }
            foreach (var p in paths.Values) res.Files.Add(p);

            Say("assembly" + (addMates ? " + mates" : ""));
            res.AssemblyPath = BuildAssembly(g, paths, outDir, addMates, res);
            res.Files.Add(res.AssemblyPath);
            if (closeParts)
            {
                foreach (var p in paths.Values)
                {
                    try { _sw.CloseDoc(p); } catch { /* keep going */ }
                }
            }
            res.SketchReport.AddRange(_sketchReport); res.SketchesChecked = _sketchesChecked; res.SketchProblems = _sketchProblems;
            Say("done: " + res.Files.Count + " files in " + outDir + "; profile sketches checked in SolidWorks: " + _sketchesChecked + ", problems: " + _sketchProblems);
            return res;
        }

        /// <summary>Lay the housing out again on the catalogue bearings (the library measured in SolidWorks, cached),
        /// when a library is set: the real part of the nearest 60-series size for each bore instead of a plain ring.</summary>
        private void UseCatalogueBearings(GearModel g, BuildResult res)
        {
            if (string.IsNullOrWhiteSpace(BearingLibraryPaths)) { res.BearingReport.Add("no bearing library set: plain rings"); return; }
            try
            {
                if (Library == null) Library = BearingLibrary.Scan(_sw, BearingLibraryPaths, Say);
                foreach (string line in Library.Report) Log.Add("bearing library: " + line);
                if (Library.Bearings.Count == 0) { res.BearingReport.Add("bearing library empty (" + BearingLibraryPaths + "): plain rings"); return; }
                g.AddHousing(Library.Bearings);
                res.BearingReport.Add("sun bearing: " + Describe(g.Housing.SunBearing));
                res.BearingReport.Add("output hub bearing: " + Describe(g.Housing.HubBearing));
            }
            catch (Exception ex) { res.BearingReport.Add("bearing library failed (" + ex.Message + "): plain rings"); Log.Add("bearing library: " + ex); }
        }

        private static string Describe(Bearing b)
        {
            return b.FromCatalogue ? b + " (" + b.File + ")" : b + " as a plain ring (no catalogue part takes this bore)";
        }

        // ------------------------------------------------------------------------------ parts
        private string BuildSun(GearModel g, string outDir)
        {
            IModelDoc2 doc = NewPart();
            StartSketch(doc);
            AddOutline(doc, g.Sun, 0, 0);
            Extrude(doc, g.ZA1 - g.ZA0, g.ZA0, false);
            StartSketch(doc);
            doc.SketchManager.CreateCircleByRadius(0, 0, 0, g.ShaftD / 2000);
            Extrude(doc, g.ZB1 + g.ShaftOut, 0, false);
            StartSketch(doc);
            doc.SketchManager.CreateCircleByRadius(0, 0, 0, g.ShaftD / 2000);
            Extrude(doc, g.ShaftIn, 0, true);
            return Finish(doc, g, "Sun", g.Design.Zs, 0.0, outDir);
        }

        private string BuildPlanet(GearModel g, string outDir)
        {
            IModelDoc2 doc = NewPart();
            StartSketch(doc);
            AddOutline(doc, g.Planet, 0, 0);
            doc.SketchManager.CreateCircleByRadius(0, 0, 0, g.PlanetBoreR / 1000);
            Extrude(doc, g.ZA1 - g.ZA0, g.ZA0, false);
            return Finish(doc, g, "Planet", g.Design.Zp, 0.0, outDir);
        }

        private string BuildThinRing(GearModel g, string outDir)
        {
            // part origin = thin ring centre; external teeth full length, internal teeth in section A,
            // plain bore (internal root radius) in section B for the carrier journal
            IModelDoc2 doc = NewPart();
            double rfInt = g.ThinInt.Rf;
            StartSketch(doc);
            AddOutline(doc, g.ThinExt, 0, 0);
            doc.SketchManager.CreateCircleByRadius(0, 0, 0, rfInt / 1000);
            Extrude(doc, g.ZB1 - g.ZA0, g.ZA0, false);
            StartSketch(doc);
            AddOutline(doc, g.ThinInt, 0, 0);
            doc.SketchManager.CreateCircleByRadius(0, 0, 0, (rfInt + 0.05 * g.M) / 1000);
            Extrude(doc, g.ZA1 - g.ZA0, g.ZA0, false);
            return Finish(doc, g, "ThinRing", g.Design.Zr1, 0.0, outDir);
        }

        private string BuildCarrier(GearModel g, string outDir)
        {
            // part origin = sun axis; eccentric journal in section B, bore for the sun shaft, planet pin into section A
            IModelDoc2 doc = NewPart();
            StartSketch(doc);
            doc.SketchManager.CreateCircleByRadius(-g.E / 1000, 0, 0, g.JournalR / 1000);
            doc.SketchManager.CreateCircleByRadius(0, 0, 0, g.CarrierBoreR / 1000);
            Extrude(doc, g.ZB1 - g.ZB0, g.ZB0, false);
            StartSketch(doc);
            doc.SketchManager.CreateCircleByRadius(g.A1 / 1000, 0, 0, g.PinD / 2000);
            Extrude(doc, g.ZB0 + 0.5 * (g.ZB1 - g.ZB0), 0, false);
            return Finish(doc, g, "Carrier", 0, 0.0, outDir);
        }

        private string BuildRing(GearModel g, GearGeom ring, double z0, double z1, string outDir, string name)
        {
            IModelDoc2 doc = NewPart();
            StartSketch(doc);
            doc.SketchManager.CreateCircleByRadius(0, 0, 0, g.RingOuterR / 1000);
            AddOutline(doc, ring, 0, 0);
            for (int k = 0; k < g.Holes; k++)
            {
                double t = 2 * Math.PI * (k + 0.5) / g.Holes;
                doc.SketchManager.CreateCircleByRadius(g.HoleCircleR * Math.Cos(t) / 1000, g.HoleCircleR * Math.Sin(t) / 1000, 0, g.HoleD / 2000);
            }
            Extrude(doc, z1 - z0, z0, false);
            return Finish(doc, g, name, ring.Z, ring.X, outDir);
        }

        // ------------------------------------------------------------------------------ housing
        /// <summary>Annular (or solid, rInner = 0) extrusion about the part origin between z0 and z1 (mm), optional bolt holes.</summary>
        private void Annulus(IModelDoc2 doc, double rOuter, double rInner, double z0, double z1, HousingSpec bolts)
        {
            StartSketch(doc);
            doc.SketchManager.CreateCircleByRadius(0, 0, 0, rOuter / 1000);
            if (rInner > 1e-9) doc.SketchManager.CreateCircleByRadius(0, 0, 0, rInner / 1000);
            if (bolts != null)
                for (int k = 0; k < bolts.Bolts; k++)
                {
                    double t = 2 * Math.PI * (k + 0.5) / bolts.Bolts;
                    doc.SketchManager.CreateCircleByRadius(bolts.BoltCircleR * Math.Cos(t) / 1000, bolts.BoltCircleR * Math.Sin(t) / 1000, 0, bolts.BoltD / 2000);
                }
            Extrude(doc, z1 - z0, z0, false);
        }

        private string BuildFrontCover(GearModel g, string outDir)
        {
            HousingSpec h = g.Housing;
            IModelDoc2 doc = NewPart();
            // built from the front face backwards: bearing seat, lip for the shaft, recess over the gear faces
            double zRecess = h.FrontZ1 - h.FrontRecessDepth;
            // all three slices start at negative z: extrude backwards from z = 0 in one piece each (reverse = towards -Z)
            Annulus(doc, h.ShellOuterR, h.FrontSeatR, h.FrontSeatZ0, h.FrontSeatZ1, h);
            Annulus(doc, h.ShellOuterR, h.FrontLipR, h.FrontSeatZ1, zRecess, h);
            Annulus(doc, h.ShellOuterR, h.FrontRecessR, zRecess, h.FrontZ1, h);
            return Finish(doc, g, "FrontCover", 0, 0.0, outDir);
        }

        private string BuildShell(GearModel g, string outDir)
        {
            HousingSpec h = g.Housing;
            IModelDoc2 doc = NewPart();
            Annulus(doc, h.ShellOuterR, h.ShellInnerR, h.ShellZ0, h.ShellZ1, h);
            Annulus(doc, h.ShellInnerR + 0.5, h.LipInnerR, h.LipZ0, h.LipZ1, null);     // lip the fixed ring is clamped against
            return Finish(doc, g, "Shell", 0, 0.0, outDir);
        }

        private string BuildRearCover(GearModel g, string outDir)
        {
            HousingSpec h = g.Housing;
            IModelDoc2 doc = NewPart();
            Annulus(doc, h.ShellOuterR, h.RearSeatR, h.RearSeatZ0, h.RearSeatZ1, h);
            Annulus(doc, h.ShellOuterR, h.RearLipR, h.RearSeatZ1, h.RearZ1, h);
            return Finish(doc, g, "RearCover", 0, 0.0, outDir);
        }

        /// <summary>The output ring extended into a cup: teeth, closing web, hub on the rear bearing (with the sun's rear bearing inside), output shaft.</summary>
        private string BuildOutputCup(GearModel g, string outDir)
        {
            HousingSpec h = g.Housing;
            IModelDoc2 doc = NewPart();
            StartSketch(doc);
            doc.SketchManager.CreateCircleByRadius(0, 0, 0, g.RingOuterR / 1000);
            AddOutline(doc, g.Output, 0, 0);
            Extrude(doc, h.WebZ0 - g.ZB0, g.ZB0, false);
            Annulus(doc, g.RingOuterR, h.WebInnerR, h.WebZ0, h.WebZ1, null);
            Annulus(doc, h.HubR, h.HubBoreR, h.HubZ0, h.HubBoreZ1, null);
            Annulus(doc, h.HubR, 0, h.HubBoreZ1, h.HubZ1, null);
            Annulus(doc, h.OutShaftR, 0, h.OutShaftZ0, h.OutShaftZ1, null);
            return Finish(doc, g, "OutputRing", g.Output.Z, g.Output.X, outDir);
        }

        /// <summary>A deep-groove ball bearing as one solid ring, built from z = 0 (placed by the assembly).</summary>
        private string BuildBearing(GearModel g, Bearing b, string outDir)
        {
            IModelDoc2 doc = NewPart();
            double chamfer = Math.Min(0.6, 0.06 * b.Width);
            Annulus(doc, b.OD / 2 - chamfer, b.Bore / 2 + chamfer, 0, chamfer, null);
            Annulus(doc, b.OD / 2, b.Bore / 2, chamfer, b.Width - chamfer, null);
            Annulus(doc, b.OD / 2 - chamfer, b.Bore / 2 + chamfer, b.Width - chamfer, b.Width, null);
            try
            {
                CustomPropertyManager cpm = doc.Extension.get_CustomPropertyManager("");
                Prop(cpm, "Bearing", b.Name + " deep groove ball bearing " + F(b.Bore) + " x " + F(b.OD) + " x " + F(b.Width));
            }
            catch (Exception ex) { Log.Add("bearing properties: " + ex.Message); }
            return Finish(doc, g, "Bearing" + b.Name, 0, 0.0, outDir);
        }

        // ------------------------------------------------------------------------------ assembly
        private string BuildAssembly(GearModel g, Dictionary<string, string> paths, string outDir, bool addMates, BuildResult res)
        {
            IModelDoc2 asmDoc = (IModelDoc2)_sw.NewDocument(_asmTemplate, 0, 0, 0);
            if (asmDoc == null) throw new InvalidOperationException("could not create an assembly from the default template");
            IAssemblyDoc asm = (IAssemblyDoc)asmDoc;
            int opt = (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig;
            // the first component is fixed automatically -> the housing ring
            var comps = new Dictionary<string, Component2>();
            comps["FixedRing"] = Insert(asm, paths["FixedRing"], opt, 0, 0, 0);
            comps["OutputRing"] = Insert(asm, paths["OutputRing"], opt, 0, 0, 0);
            comps["Sun"] = Insert(asm, paths["Sun"], opt, 0, 0, 0);
            comps["Planet"] = Insert(asm, paths["Planet"], opt, g.A1 / 1000, 0, 0);
            comps["ThinRing"] = Insert(asm, paths["ThinRing"], opt, -g.E / 1000, 0, 0);
            comps["Carrier"] = Insert(asm, paths["Carrier"], opt, 0, 0, 0);
            if (g.Housing != null)
            {
                HousingSpec h = g.Housing;
                var fixedComps = new List<Component2>
                {
                    Insert(asm, paths["FrontCover"], opt, 0, 0, 0),
                    Insert(asm, paths["Shell"], opt, 0, 0, 0),
                    Insert(asm, paths["RearCover"], opt, 0, 0, 0),
                };
                fixedComps.Add(InsertBearing(asmDoc, asm, h.SunBearing, paths, "BearingSun", h.SunBearingFrontZ0, "sun front", res));
                fixedComps.Add(InsertBearing(asmDoc, asm, h.SunBearing, paths, "BearingSun", h.SunBearingRearZ0, "sun rear", res));
                fixedComps.Add(InsertBearing(asmDoc, asm, h.HubBearing, paths, "BearingHub", h.HubBearingZ0, "output hub", res));
                foreach (Component2 fc in fixedComps)
                {
                    try
                    {
                        asmDoc.ClearSelection2(true);
                        if (fc.Select4(false, null, false)) asm.FixComponent();
                    }
                    catch (Exception ex) { Log.Add("fix component: " + ex.Message); }
                }
                asmDoc.ClearSelection2(true);
            }
            if (addMates)
            {
                try { AddMates(asmDoc, g, comps, res); }
                catch (Exception ex) { res.MateReport.Add("mates aborted: " + ex.Message); Log.Add("mates aborted: " + ex); }
                AddMotionStudy(asmDoc, res);
            }
            try
            {
                CustomPropertyManager cpm = asmDoc.Extension.get_CustomPropertyManager("");
                Prop(cpm, "GearSet", g.Name);
                Prop(cpm, "Ratio", g.Design.RatioText + (g.Design.OutputReversed ? " (output reversed)" : ""));
                Prop(cpm, "Teeth_Zs_Zp_Zr1_Zr2_Zf_Zo", g.Design.TeethText);
                Prop(cpm, "Module", F(g.M));
                Prop(cpm, "RingOuterDiameter_mm", F(g.RingOD));
                Prop(cpm, "GearPackLength_mm", F(g.GearPackLength));
                Prop(cpm, "OverallLength_mm", F(g.OverallLength));
                Prop(cpm, "Eccentricity_mm", F(g.E));
                Prop(cpm, "CentreDistanceSunPlanet_mm", F(g.A1));
            }
            catch (Exception ex) { Log.Add("assembly properties: " + ex.Message); }
            asmDoc.ForceRebuild3(false);
            asmDoc.ShowNamedView2("*Isometric", 7);
            asmDoc.ViewZoomtofit2();
            string path = Path.Combine(outDir, "GearSet.SLDASM");
            Save(asmDoc, path);
            return path;
        }

        // ------------------------------------------------------------------------------ mates
        // Positions: Front Plane of every part coincident with the fixed ring's (all parts are modelled from
        // z = 0 in absolute Z), one concentric mate per moving part. Motion: SOLIDWORKS gear mates relate the
        // spins of two components in the assembly frame, so a planetary train cannot use tooth counts between
        // meshing pairs; instead every rotating part is geared directly to the sun with its absolute speed
        // ratio from the kinematics (carrier, planet, thin ring, output ring). Result: one degree of freedom,
        // drag the sun or put a rotary motor on it.
        //
        // Direction convention assumed for AddMate5 gear mates: Flip = false -> the two parts turn in opposite
        // directions (external gear behaviour), Flip = true -> same direction. If the built assembly shows the
        // carrier turning against the sun, invert GearMateFlipMeansSameDirection.
        public static bool GearMateFlipMeansSameDirection = true;

        private void AddMates(IModelDoc2 asmDoc, GearModel g, Dictionary<string, Component2> c, BuildResult res)
        {
            Design d = g.Design;
            double tol = Math.Max(0.02, 0.02 * g.M);        // mm, radius match tolerance
            // ---- rotation axis face of every part (cylindrical, on its own axis) --------------------------
            IFace2 fFixed = FindCylinder(c["FixedRing"], g.RingOuterR, g.ZA0, g.ZA1, tol, "fixed ring outer");
            IFace2 fOut = FindCylinder(c["OutputRing"], g.RingOuterR, g.ZB0, g.ZB1, tol, "output ring outer");
            IFace2 fSun = FindCylinder(c["Sun"], g.ShaftD / 2, -g.ShaftIn, 0, tol, "sun shaft (input stub)");
            _sunFace = fSun;
            IFace2 fCarBore = FindCylinder(c["Carrier"], g.CarrierBoreR, g.ZB0, g.ZB1, tol, "carrier bore");
            IFace2 fPin = FindCylinder(c["Carrier"], g.PinD / 2, g.ZA0, g.ZB0, tol, "carrier pin");
            IFace2 fJournal = FindCylinder(c["Carrier"], g.JournalR, g.ZB0, g.ZB1, tol, "carrier journal");
            IFace2 fPlanet = FindCylinder(c["Planet"], g.PlanetBoreR, g.ZA0, g.ZA1, tol, "planet bore");
            IFace2 fThin = FindCylinder(c["ThinRing"], g.ThinInt.Rf, g.ZA1, g.ZB1, tol, "thin ring bore");

            // ---- axial position: Front Plane coincident with the fixed ring's Front Plane ----------------
            Feature pFixed = FirstPlane(c["FixedRing"]);
            foreach (string name in new[] { "OutputRing", "Sun", "Planet", "ThinRing", "Carrier" })
                Mate(asmDoc, "front plane " + name, (int)swMateType_e.swMateCOINCIDENT, FirstPlane(c[name]), pFixed, false, 0, 0, res);

            // ---- axes --------------------------------------------------------------------------------------
            Mate(asmDoc, "concentric output ring / fixed ring", (int)swMateType_e.swMateCONCENTRIC, fOut, fFixed, false, 0, 0, res);
            Mate(asmDoc, "concentric sun / fixed ring", (int)swMateType_e.swMateCONCENTRIC, fSun, fFixed, false, 0, 0, res);
            Mate(asmDoc, "concentric carrier bore / sun shaft", (int)swMateType_e.swMateCONCENTRIC, fCarBore, fSun, false, 0, 0, res);
            Mate(asmDoc, "concentric planet / carrier pin", (int)swMateType_e.swMateCONCENTRIC, fPlanet, fPin, false, 0, 0, res);
            Mate(asmDoc, "concentric thin ring / carrier journal", (int)swMateType_e.swMateCONCENTRIC, fThin, fJournal, false, 0, 0, res);

            // ---- motion: absolute speed ratios relative to the sun -----------------------------------------
            double ks = (double)d.Zs * d.Zr2 / ((double)d.Zf * d.Zr1 + (double)d.Zs * d.Zr2);   // carrier
            double kp = ks * (1.0 + (double)d.Zs / d.Zp) - (double)d.Zs / d.Zp;                  // planet spin
            double kt = ks * (1.0 - (double)d.Zf / d.Zr2);                                        // thin ring spin
            double ko = ks * (double)(d.Zo - d.Zf) / d.Zo;                                        // output ring
            GearMate(asmDoc, "gear sun -> carrier", fSun, fCarBore, ks, res);
            GearMate(asmDoc, "gear sun -> planet", fSun, fPlanet, kp, res);
            GearMate(asmDoc, "gear sun -> thin ring", fSun, fThin, kt, res);
            GearMate(asmDoc, "gear sun -> output ring", fSun, fOut, ko, res);
            res.MateReport.Add(string.Format(CultureInfo.InvariantCulture,
                "speed ratios to the sun: carrier {0:+0.000000;-0.000000}, planet {1:+0.000000;-0.000000}, thin ring {2:+0.000000;-0.000000}, output {3:+0.000000;-0.000000}",
                ks, kp, kt, ko));
        }

        /// <summary>
        /// Mates for an assembly SolidWorks made by importing the engine's STEP of the set (GEARS GENERATOR's
        /// family path: one component per solid, every component fixed, generic names). The six gears are told
        /// apart by their bounding boxes against the model's layout (the housing parts and bearings, when there,
        /// stay fixed), the five moving ones are unfixed, then the same mates as Build adds: Front Planes
        /// coincident with the fixed ring's (every imported part keeps the global frame, so its Front Plane is
        /// z = 0), one concentric mate per moving part, gear mates to the sun with the absolute speed ratios.
        /// </summary>
        public BuildResult MateImportedAssembly(IModelDoc2 asmDoc, GearModel g)
        {
            var res = new BuildResult();
            IAssemblyDoc asm = (IAssemblyDoc)asmDoc;
            object[] all = asm.GetComponents(true) as object[];
            if (all == null || all.Length == 0) { res.MateReport.Add("no components in the assembly"); return res; }

            // the expected boxes (mm, assembly frame): x0 y0 z0 x1 y1 z1
            double R = g.RingOuterR;
            double jr = g.JournalR, pr = g.PinD / 2;
            double outZ1 = g.Housing != null ? g.Housing.OutShaftZ1 : g.ZB1;
            var expected = new Dictionary<string, double[]>
            {
                { "Sun", new[] { -g.Sun.Ra, -g.Sun.Ra, -g.ShaftIn, g.Sun.Ra, g.Sun.Ra, g.ZB1 + g.ShaftOut } },
                { "Planet", new[] { g.A1 - g.Planet.Ra, -g.Planet.Ra, g.ZA0, g.A1 + g.Planet.Ra, g.Planet.Ra, g.ZA1 } },
                { "ThinRing", new[] { -g.E - g.ThinExt.Ra, -g.ThinExt.Ra, g.ZA0, -g.E + g.ThinExt.Ra, g.ThinExt.Ra, g.ZB1 } },
                { "Carrier", new[] { Math.Min(-g.E - jr, g.A1 - pr), -Math.Max(jr, pr), g.ZA0, Math.Max(-g.E + jr, g.A1 + pr), Math.Max(jr, pr), g.ZB1 } },
                { "FixedRing", new[] { -R, -R, g.ZA0, R, R, g.ZA1 } },
                { "OutputRing", new[] { -R, -R, g.ZB0, R, R, outZ1 } },
            };

            // every component with a solid body and its box
            var candidates = new List<KeyValuePair<Component2, double[]>>();
            foreach (object o in all)
            {
                Component2 c = (Component2)o;
                object bodyInfo;
                object[] bodies = null;
                try { bodies = c.GetBodies3((int)swBodyType_e.swSolidBody, out bodyInfo) as object[]; } catch { }
                if (bodies == null || bodies.Length == 0) continue;           // the pitch circle bands, suppressed parts
                double[] b = null;
                try { b = c.GetBox(false, false) as double[]; } catch { }
                if (b == null || b.Length < 6) continue;
                var mm = new[] { Math.Min(b[0], b[3]) * 1000, Math.Min(b[1], b[4]) * 1000, Math.Min(b[2], b[5]) * 1000,
                                 Math.Max(b[0], b[3]) * 1000, Math.Max(b[1], b[4]) * 1000, Math.Max(b[2], b[5]) * 1000 };
                candidates.Add(new KeyValuePair<Component2, double[]>(c, mm));
                Log.Add(string.Format(CultureInfo.InvariantCulture, "component {0}: box x {1:0.#}..{2:0.#} y {3:0.#}..{4:0.#} z {5:0.#}..{6:0.#}",
                    c.Name2, mm[0], mm[3], mm[1], mm[4], mm[2], mm[5]));
            }

            // best pairing, globally: the smallest box error first, each component and each name once
            var found = new Dictionary<string, Component2>();
            var usedComps = new HashSet<string>();
            while (found.Count < expected.Count)
            {
                string bestName = null; Component2 bestComp = null; double bestErr = double.MaxValue; double[] bestExp = null;
                foreach (var kv in expected)
                {
                    if (found.ContainsKey(kv.Key)) continue;
                    foreach (var cand in candidates)
                    {
                        if (usedComps.Contains(cand.Key.Name2)) continue;
                        double err = 0;
                        for (int i = 0; i < 6; i++) err += Math.Abs(cand.Value[i] - kv.Value[i]);
                        if (err < bestErr) { bestErr = err; bestName = kv.Key; bestComp = cand.Key; bestExp = kv.Value; }
                    }
                }
                if (bestName == null) break;
                double size = Math.Max(bestExp[3] - bestExp[0], Math.Max(bestExp[4] - bestExp[1], bestExp[5] - bestExp[2]));
                if (bestErr > 2.0 + 0.1 * size)
                {
                    res.MateReport.Add(bestName + ": no component matches its box (closest " + bestComp.Name2 + ", off by " + bestErr.ToString("0.#", CultureInfo.InvariantCulture) + " mm)");
                    expected.Remove(bestName);
                    continue;
                }
                found[bestName] = bestComp; usedComps.Add(bestComp.Name2);
                res.MateReport.Add(bestName + " = " + bestComp.Name2 + " (box off by " + bestErr.ToString("0.##", CultureInfo.InvariantCulture) + " mm)");
            }
            foreach (string name in new[] { "Sun", "Planet", "ThinRing", "Carrier", "FixedRing", "OutputRing" })
                if (!found.ContainsKey(name)) { res.MateReport.Add("mates skipped: " + name + " not identified"); return res; }

            // the fixed ring stays put; the five moving parts are freed; the housing and bearings stay as imported (fixed)
            try
            {
                asmDoc.ClearSelection2(true);
                if (!found["FixedRing"].IsFixed() && found["FixedRing"].Select4(false, null, false)) asm.FixComponent();
                asmDoc.ClearSelection2(true);
                foreach (string name in new[] { "Sun", "Planet", "ThinRing", "Carrier", "OutputRing" })
                {
                    Component2 c = found[name];
                    if (c.IsFixed() && c.Select4(false, null, false)) asm.UnfixComponent();
                    asmDoc.ClearSelection2(true);
                }
            }
            catch (Exception ex) { res.MateReport.Add("unfix: " + ex.Message); }

            if (g.Housing != null) ReplaceImportedBearings(asmDoc, asm, g, candidates, usedComps, res);
            AddMates(asmDoc, g, found, res);
            AddMotionStudy(asmDoc, res);
            asmDoc.ClearSelection2(true);
            try { asmDoc.ForceRebuild3(false); } catch { }
            return res;
        }

        // ------------------------------------------------------------------------------ catalogue bearings
        /// <summary>One bearing into the assembly, bore on the sun axis, from z0 to z0 + width: the catalogue part in
        /// its configuration, turned so that its bore axis is Z and its mid-width sits at z0 + width/2 - or the plain
        /// ring built from z = 0 when the bearing has no catalogue part.</summary>
        private Component2 InsertBearing(IModelDoc2 asmDoc, IAssemblyDoc asm, Bearing b, Dictionary<string, string> paths, string key, double z0, string what, BuildResult res)
        {
            int opt = (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig;
            if (!b.FromCatalogue) return Insert(asm, paths[key], opt, 0, 0, z0 / 1000);
            return PlaceCatalogueBearing(asmDoc, asm, b, z0, what, res);
        }

        /// <summary>The catalogue part placed with its bore on Z from z0 to z0 + width. The part is inserted in its
        /// configuration, then given a transform: the rotation that takes its bore axis onto Z (row-vector convention,
        /// p' = p R + t) and the translation that puts its mid-width at z0 + width/2. The component's box is read back
        /// and checked against the bearing; if the rotation came out the other way, the transposed one is applied.</summary>
        private Component2 PlaceCatalogueBearing(IModelDoc2 asmDoc, IAssemblyDoc asm, Bearing b, double z0, string what, BuildResult res)
        {
            int opt = (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig;
            string cfg = b.Config ?? "";
            double zc = (z0 + b.Width / 2) / 1000;
            Component2 c = asm.AddComponent5(b.File, opt, "", false, cfg, 0, 0, zc);
            if (c == null)
            {
                int e = 0, w = 0;
                _sw.OpenDoc6(b.File, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, cfg, ref e, ref w);
                c = asm.AddComponent5(b.File, opt, "", false, cfg, 0, 0, zc);
            }
            if (c == null) throw new InvalidOperationException("could not insert the catalogue bearing " + b.Source + " (" + b.File + ")");
            try
            {
                if (cfg.Length > 0 && c.ReferencedConfiguration != cfg) { c.ReferencedConfiguration = cfg; Log.Add(what + " bearing: configuration set to " + cfg); }
            }
            catch (Exception ex) { Log.Add(what + " bearing configuration: " + ex.Message); }

            string placed = null;
            for (int attempt = 0; attempt < 2 && placed == null; attempt++)
            {
                double[] rot = Rotation(b.Axis, attempt == 1);
                // the bearing's mid-width in the part frame, rotated, is what must land on (0, 0, zc)
                double[] off = { 0, 0, 0 };
                off["XYZ".IndexOf(b.Axis)] = b.AxisOffset / 1000;
                double[] offR = { off[0] * rot[0] + off[1] * rot[3] + off[2] * rot[6], off[0] * rot[1] + off[1] * rot[4] + off[2] * rot[7], off[0] * rot[2] + off[1] * rot[5] + off[2] * rot[8] };
                var data = new double[16];
                Array.Copy(rot, data, 9);
                data[9] = -offR[0]; data[10] = -offR[1]; data[11] = zc - offR[2]; data[12] = 1;
                try
                {
                    MathUtility mu = (MathUtility)_sw.GetMathUtility();
                    MathTransform mt = (MathTransform)mu.CreateTransform(data);
                    c.Transform2 = mt;
                    asmDoc.EditRebuild3();
                }
                catch (Exception ex) { Log.Add(what + " bearing transform: " + ex.Message); }
                placed = CheckBearingBox(c, b, z0);
                if (placed == null && attempt == 0) Log.Add(what + " bearing: the box is not where the bearing belongs after the rotation; trying the transposed rotation");
            }
            res.BearingReport.Add(what + " bearing " + b + (placed ?? ": PROBLEM, the placed box does not match the bearing (see the log)"));
            return c;
        }

        /// <summary>Row-vector rotation taking the part's bore axis onto Z (or its transpose).</summary>
        private static double[] Rotation(char axis, bool transpose)
        {
            double[] r;
            switch (char.ToUpperInvariant(axis))
            {
                case 'X': r = new double[] { 0, 0, 1, 0, 1, 0, -1, 0, 0 }; break;
                case 'Y': r = new double[] { 1, 0, 0, 0, 0, 1, 0, -1, 0 }; break;
                default: return new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            }
            if (!transpose) return r;
            return new[] { r[0], r[3], r[6], r[1], r[4], r[7], r[2], r[5], r[8] };
        }

        /// <summary>The component's box against the bearing's (OD x OD x width from z0, about the sun axis): a line
        /// saying where it sits when it matches (0.6 mm + 2 % tolerance), null when it does not.</summary>
        private string CheckBearingBox(Component2 c, Bearing b, double z0)
        {
            double[] box = null;
            try { box = c.GetBox(false, false) as double[]; } catch (Exception ex) { Log.Add("bearing box: " + ex.Message); }
            if (box == null || box.Length < 6) return null;
            double x0 = Math.Min(box[0], box[3]) * 1000, x1 = Math.Max(box[0], box[3]) * 1000;
            double y0 = Math.Min(box[1], box[4]) * 1000, y1 = Math.Max(box[1], box[4]) * 1000;
            double zz0 = Math.Min(box[2], box[5]) * 1000, zz1 = Math.Max(box[2], box[5]) * 1000;
            double tol = 0.6 + 0.02 * b.OD;
            bool ok = Math.Abs(x0 + b.OD / 2) < tol && Math.Abs(x1 - b.OD / 2) < tol && Math.Abs(y0 + b.OD / 2) < tol && Math.Abs(y1 - b.OD / 2) < tol
                      && Math.Abs(zz0 - z0) < tol && Math.Abs(zz1 - (z0 + b.Width)) < tol;
            Log.Add(string.Format(CultureInfo.InvariantCulture, "bearing {0}: placed box x {1:0.##}..{2:0.##} y {3:0.##}..{4:0.##} z {5:0.##}..{6:0.##} (wanted OD {7} from z {8:0.##} to {9:0.##}) {10}",
                b.Source, x0, x1, y0, y1, zz0, zz1, b.OD, z0, z0 + b.Width, ok ? "ok" : "MISMATCH"));
            return ok ? string.Format(CultureInfo.InvariantCulture, ": placed on the sun axis, z {0:0.##}..{1:0.##}, box as expected", z0, z0 + b.Width) : null;
        }

        /// <summary>The family path's bearings arrive as plain solids in the STEP: each one whose box matches a
        /// catalogue bearing of exactly the same bore, OD and width is deleted and the catalogue part put in its
        /// place (fixed, like the imported one was). Anything without an exact catalogue match stays as imported.</summary>
        private void ReplaceImportedBearings(IModelDoc2 asmDoc, IAssemblyDoc asm, GearModel g, List<KeyValuePair<Component2, double[]>> candidates, HashSet<string> usedComps, BuildResult res)
        {
            if (string.IsNullOrWhiteSpace(BearingLibraryPaths)) { res.BearingReport.Add("no bearing library set: the imported bearings stay"); return; }
            try
            {
                if (Library == null) Library = BearingLibrary.Scan(_sw, BearingLibraryPaths, Say);
                foreach (string line in Library.Report) Log.Add("bearing library: " + line);
            }
            catch (Exception ex) { res.BearingReport.Add("bearing library failed (" + ex.Message + "): the imported bearings stay"); Log.Add("bearing library: " + ex); return; }
            HousingSpec h = g.Housing;
            var wanted = new[]
            {
                new { B = h.SunBearing, Z0 = h.SunBearingFrontZ0, What = "sun front" },
                new { B = h.SunBearing, Z0 = h.SunBearingRearZ0, What = "sun rear" },
                new { B = h.HubBearing, Z0 = h.HubBearingZ0, What = "output hub" },
            };
            foreach (var wnt in wanted)
            {
                Bearing cat = HousingSpec.ForDims(wnt.B.Bore, wnt.B.OD, wnt.B.Width, Library.Bearings);
                if (cat == null) { res.BearingReport.Add(wnt.What + " bearing " + wnt.B + ": no catalogue part with exactly these dimensions, the imported solid stays"); continue; }
                double[] exp = { -wnt.B.OD / 2, -wnt.B.OD / 2, wnt.Z0, wnt.B.OD / 2, wnt.B.OD / 2, wnt.Z0 + wnt.B.Width };
                Component2 best = null; double bestErr = double.MaxValue;
                foreach (var cand in candidates)
                {
                    if (usedComps.Contains(cand.Key.Name2)) continue;
                    double err = 0;
                    for (int i = 0; i < 6; i++) err += Math.Abs(cand.Value[i] - exp[i]);
                    if (err < bestErr) { bestErr = err; best = cand.Key; }
                }
                if (best == null || bestErr > 1.0 + 0.05 * wnt.B.OD)
                {
                    res.BearingReport.Add(wnt.What + " bearing " + wnt.B + ": no imported component has its box" + (best != null ? " (closest " + best.Name2 + ", off by " + bestErr.ToString("0.#", CultureInfo.InvariantCulture) + " mm)" : "") + ", nothing replaced");
                    continue;
                }
                string oldName = best.Name2;
                try
                {
                    usedComps.Add(oldName);
                    asmDoc.ClearSelection2(true);
                    if (!best.Select4(false, null, false)) { res.BearingReport.Add(wnt.What + " bearing: could not select " + oldName + " to replace it"); continue; }
                    asmDoc.EditDelete();
                    asmDoc.ClearSelection2(true);
                    Component2 c = PlaceCatalogueBearing(asmDoc, asm, cat, wnt.Z0, wnt.What, res);
                    asmDoc.ClearSelection2(true);
                    if (c.Select4(false, null, false)) asm.FixComponent();
                    asmDoc.ClearSelection2(true);
                    Log.Add(wnt.What + " bearing: imported " + oldName + " replaced by " + cat.Source);
                }
                catch (Exception ex) { res.BearingReport.Add(wnt.What + " bearing: replacing " + oldName + " failed: " + ex.Message); Log.Add("replace bearing: " + ex); }
            }
        }

        // ------------------------------------------------------------------------------ motion study
        // The swmotionstudy interop is driven late-bound (IDispatch through dynamic), so the add-in needs no
        // reference to SolidWorks.Interop.swmotionstudy.dll: IModelDocExtension.GetMotionStudyManager ->
        // IMotionStudyManager.CreateMotionStudy -> IMotionStudy.Activate / SetDuration / CreateMotor(motorType,
        // motionType, speed) on the pre-selected face (the sun's input stub). The enum values are read from the
        // installed interop assembly when it can be found, else the documented ones (rotary 1, constant speed 1).
        private void AddMotionStudy(IModelDoc2 asmDoc, BuildResult res)
        {
            if (!AddMotor) return;
            if (_sunFace == null) { res.MateReport.Add("motion study: skipped (sun shaft face not found)"); return; }
            try
            {
                dynamic mgr = asmDoc.Extension.GetMotionStudyManager();
                if (mgr == null) { res.MateReport.Add("motion study: no MotionStudyManager (SOLIDWORKS Motion not loaded?)"); return; }
                dynamic study = mgr.CreateMotionStudy();
                if (study == null) { res.MateReport.Add("motion study: CreateMotionStudy returned nothing"); return; }
                try { study.Name = "Sun drive"; } catch (Exception ex) { Log.Add("motion study name: " + ex.Message); }
                try { study.Activate(); } catch (Exception ex) { Log.Add("motion study activate: " + ex.Message); }
                try { study.SetDuration(MotorSeconds); } catch (Exception ex) { Log.Add("motion study duration: " + ex.Message); }

                asmDoc.ClearSelection2(true);
                SelectData data = (SelectData)((ISelectionMgr)asmDoc.SelectionManager).CreateSelectData();
                data.Mark = 0;
                if (!((Entity)_sunFace).Select4(false, data)) { res.MateReport.Add("motion study: could not select the sun shaft face"); return; }
                int rotary = MotionEnum("swMotorType_e", "swRotaryMotor", 1);
                int constant = MotionEnum("swMotorMotionType_e", "swMotorConstantSpeed", 1);
                object motor = null;
                try { motor = study.CreateMotor(rotary, constant, (double)MotorRpm); }
                catch (Exception ex)
                {
                    Log.Add("CreateMotor(double): " + ex.Message);
                    try { motor = study.CreateMotor(rotary, constant, MotorRpm.ToString(CultureInfo.InvariantCulture)); }
                    catch (Exception ex2) { Log.Add("CreateMotor(string): " + ex2.Message); }
                }
                asmDoc.ClearSelection2(true);
                if (motor == null) { res.MateReport.Add("motion study 'Sun drive' created, but the motor was not (see the log)"); return; }
                double turns = MotorRpm * MotorSeconds / 60.0;
                res.MotionStudy = string.Format(CultureInfo.InvariantCulture,
                    "Motion Study 'Sun drive': rotary motor on the sun, {0:0.#} rpm for {1:0.#} s = {2:0.#} sun turns (Calculate, then play)",
                    MotorRpm, MotorSeconds, turns);
                res.MateReport.Add(res.MotionStudy);
            }
            catch (Exception ex) { res.MateReport.Add("motion study: " + ex.Message); Log.Add("motion study: " + ex); }
        }

        /// <summary>A value of the swmotionstudy interop's enums, from the installed assembly (next to
        /// SLDWORKS.exe, in its api\redist, or in the GAC); the fallback when it cannot be loaded.</summary>
        private int MotionEnum(string enumName, string member, int fallback)
        {
            try
            {
                Assembly asm = null;
                string exeDir = null;
                try { exeDir = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName); } catch { }
                if (exeDir != null)
                    foreach (string candidate in new[] { Path.Combine(exeDir, "SolidWorks.Interop.swmotionstudy.dll"), Path.Combine(exeDir, "api", "redist", "SolidWorks.Interop.swmotionstudy.dll") })
                        if (asm == null && File.Exists(candidate)) { try { asm = Assembly.LoadFrom(candidate); } catch { } }
#pragma warning disable 618
                if (asm == null) { try { asm = Assembly.LoadWithPartialName("SolidWorks.Interop.swmotionstudy"); } catch { } }
#pragma warning restore 618
                if (asm == null) { Log.Add("motion study: interop enums not found, using " + member + " = " + fallback); return fallback; }
                Type t = asm.GetType("SolidWorks.Interop.swmotionstudy." + enumName, false);
                if (t == null) return fallback;
                return Convert.ToInt32(Enum.Parse(t, member), CultureInfo.InvariantCulture);
            }
            catch { return fallback; }
        }

        /// <summary>Gear mate sun -> part with |ratio| = spin of the part per spin of the sun; sign = direction.</summary>
        private void GearMate(IModelDoc2 asmDoc, string what, IFace2 sun, IFace2 part, double ratio, BuildResult res)
        {
            bool sameDirection = ratio > 0;
            bool flip = sameDirection == GearMateFlipMeansSameDirection;
            // AddMate5: numerator belongs to the first selection (sun), denominator to the second:
            // part turns numerator/denominator per sun turn.
            Mate(asmDoc, what, (int)swMateType_e.swMateGEAR, sun, part, flip, Math.Abs(ratio), 1.0, res);
        }

        private void Mate(IModelDoc2 asmDoc, string what, int type, object a, object b, bool flip, double num, double den, BuildResult res)
        {
            res.MatesWanted++;
            if (a == null || b == null) { res.MateReport.Add(what + ": skipped (entity not found)"); return; }
            int err = 0;
            asmDoc.ClearSelection2(true);
            if (!SelectForMate(asmDoc, a, false) || !SelectForMate(asmDoc, b, true))
            {
                asmDoc.ClearSelection2(true);
                res.MateReport.Add(what + ": selection failed");
                return;
            }
            IAssemblyDoc asm = (IAssemblyDoc)asmDoc;
            object mate = asm.AddMate5(type, (int)swMateAlign_e.swMateAlignCLOSEST, flip, 0, 0, 0, num, den, 0, 0, 0, false, false, 0, out err);
            asmDoc.ClearSelection2(true);
            if (mate == null) { res.MateReport.Add(what + ": AddMate5 failed (error " + err + ")"); return; }
            res.MatesAdded++;
            res.MateReport.Add(what + ": ok");
        }

        private static bool SelectForMate(IModelDoc2 asmDoc, object entity, bool append)
        {
            Feature feat = entity as Feature;
            if (feat != null) return feat.Select2(append, 1);
            SelectData data = (SelectData)((ISelectionMgr)asmDoc.SelectionManager).CreateSelectData();
            data.Mark = 1;
            return ((Entity)entity).Select4(append, data);
        }

        /// <summary>First reference plane feature of a component (its Front Plane in any language).</summary>
        private static Feature FirstPlane(Component2 comp)
        {
            Feature f = (Feature)comp.FirstFeature();
            while (f != null && f.GetTypeName2() != "RefPlane") f = (Feature)f.GetNextFeature();
            return f;
        }

        /// <summary>
        /// Cylindrical face of the component with this radius (mm), axis along Z, lying within the z range (mm,
        /// part coordinates = assembly Z since nothing is rotated or moved in Z). null if none.
        /// </summary>
        private IFace2 FindCylinder(Component2 comp, double radiusMm, double zMinMm, double zMaxMm, double tolMm, string what)
        {
            object bodyInfo;
            object[] bodies = comp.GetBodies3((int)swBodyType_e.swSolidBody, out bodyInfo) as object[];
            if (bodies == null || bodies.Length == 0) { Log.Add(what + ": component has no body"); return null; }
            IFace2 best = null;
            double bestErr = double.MaxValue;
            foreach (object bo in bodies)
            {
                IBody2 body = (IBody2)bo;
                object[] faces = body.GetFaces() as object[];
                if (faces == null) continue;
                foreach (object fo in faces)
                {
                    IFace2 f = (IFace2)fo;
                    ISurface s = (ISurface)f.GetSurface();
                    if (!s.IsCylinder()) continue;
                    double[] p = (double[])s.CylinderParams;          // origin xyz, axis xyz, radius (metres)
                    if (Math.Abs(p[5]) < 0.999) continue;               // axis not along Z
                    double rErr = Math.Abs(p[6] * 1000 - radiusMm);
                    if (rErr > tolMm) continue;
                    double[] box = (double[])f.GetBox();
                    double z0 = Math.Min(box[2], box[5]) * 1000, z1 = Math.Max(box[2], box[5]) * 1000;
                    if (z1 < zMinMm - tolMm || z0 > zMaxMm + tolMm) continue;   // outside the wanted z range
                    double err = rErr + 1e-3 * (Math.Abs(z0 - zMinMm) + Math.Abs(z1 - zMaxMm));
                    if (err < bestErr) { bestErr = err; best = f; }
                }
            }
            if (best == null) Log.Add(what + ": no cylindrical face r=" + radiusMm.ToString("0.###", CultureInfo.InvariantCulture) + " in z " + zMinMm + ".." + zMaxMm);
            return best;
        }

        private Component2 Insert(IAssemblyDoc asm, string path, int opt, double x, double y, double z)
        {
            Component2 c = asm.AddComponent5(path, opt, "", false, "", x, y, z);
            if (c == null)
            {
                // AddComponent5 needs the part loaded; load it silently and retry once
                int e = 0, w = 0;
                _sw.OpenDoc6(path, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref e, ref w);
                c = asm.AddComponent5(path, opt, "", false, "", x, y, z);
            }
            if (c == null) throw new InvalidOperationException("could not insert " + Path.GetFileName(path) + " into the assembly");
            return c;
        }

        // ------------------------------------------------------------------------------ sketch helpers
        private IModelDoc2 NewPart()
        {
            IModelDoc2 doc = (IModelDoc2)_sw.NewDocument(_partTemplate, 0, 0, 0);
            if (doc == null) throw new InvalidOperationException("could not create a part from the default template " + _partTemplate);
            return doc;
        }

        /// <summary>Select the first reference plane (Front Plane in any language) and open a sketch on it.</summary>
        private void StartSketch(IModelDoc2 doc)
        {
            Feature f = (Feature)doc.FirstFeature();
            while (f != null && f.GetTypeName2() != "RefPlane") f = (Feature)f.GetNextFeature();
            if (f == null) throw new InvalidOperationException("no reference plane found in the part template");
            doc.ClearSelection2(true);
            f.Select2(false, 0);
            SketchManager sm = doc.SketchManager;
            sm.InsertSketch(true);
            sm.AddToDB = true;
            sm.DisplayWhenAdded = false;
            _expOutlines = 0; _expArcs = 0; _expFillets = 0; _expLines = 0; _expSplines = 0; _expTeeth = 0; _expName = "";
        }

        /// <summary>Close the active sketch and extrude it: depth / offset in mm towards +Z (reverse: towards -Z).</summary>
        private Feature Extrude(IModelDoc2 doc, double depthMm, double offsetMm, bool reverse)
        {
            SketchManager sm = doc.SketchManager;
            Sketch sk = sm.ActiveSketch;
            VerifySketch(doc, sk);
            FixSketch(doc, sk);
            sm.AddToDB = false;
            sm.DisplayWhenAdded = true;
            sm.InsertSketch(true);
            Feature skFeat = (Feature)sk;
            doc.ClearSelection2(true);
            skFeat.Select2(false, 0);
            bool dir = reverse ? !_dirFlip : _dirFlip;
            bool off = reverse ? !_offFlip : _offFlip;
            int t0 = offsetMm > 1e-7 ? (int)swStartConditions_e.swStartOffset : (int)swStartConditions_e.swStartSketchPlane;
            Feature f = (Feature)doc.FeatureManager.FeatureExtrusion3(true, false, dir,
                (int)swEndConditions_e.swEndCondBlind, (int)swEndConditions_e.swEndCondBlind, depthMm / 1000, 0,
                false, false, false, false, 0, 0, false, false, false, false, true, true, true,
                t0, offsetMm / 1000, off);
            if (f == null) throw new InvalidOperationException("extrusion failed (open sketch contour?) in " + doc.GetTitle());
            return f;
        }

        /// <summary>All teeth of one outline as a closed chain (centre cx, cy in mm).</summary>
        private void AddOutline(IModelDoc2 doc, GearGeom g, double cxMm, double cyMm)
        {
            SketchManager sm = doc.SketchManager;
            _expOutlines++; _expTeeth += g.Z; _expName = g.Name;
            for (int k = 0; k < g.Z; k++)
            {
                foreach (Segment seg in g.ToothSegments(k, NFlank))
                {
                    switch (seg.Kind)
                    {
                        case SegKind.Arc:
                            if (seg.Theta[1] - seg.Theta[0] < 1e-7) break;
                            _expArcs++;
                            sm.CreateArc(cxMm / 1000, cyMm / 1000, 0,
                                (cxMm + seg.Rho[0] * Math.Cos(seg.Theta[0])) / 1000, (cyMm + seg.Rho[0] * Math.Sin(seg.Theta[0])) / 1000, 0,
                                (cxMm + seg.Rho[1] * Math.Cos(seg.Theta[1])) / 1000, (cyMm + seg.Rho[1] * Math.Sin(seg.Theta[1])) / 1000, 0,
                                _arcCcw);
                            break;
                        case SegKind.Fillet:
                            {
                                int last = seg.Rho.Length - 1;
                                _expFillets++;
                                sm.CreateArc((cxMm + seg.Cx) / 1000, (cyMm + seg.Cy) / 1000, 0,
                                    (cxMm + seg.Rho[0] * Math.Cos(seg.Theta[0])) / 1000, (cyMm + seg.Rho[0] * Math.Sin(seg.Theta[0])) / 1000, 0,
                                    (cxMm + seg.Rho[last] * Math.Cos(seg.Theta[last])) / 1000, (cyMm + seg.Rho[last] * Math.Sin(seg.Theta[last])) / 1000, 0,
                                    (short)(seg.Ccw ? _arcCcw : -_arcCcw));
                            }
                            break;
                        case SegKind.Line:
                            _expLines++;
                            sm.CreateLine(
                                (cxMm + seg.Rho[0] * Math.Cos(seg.Theta[0])) / 1000, (cyMm + seg.Rho[0] * Math.Sin(seg.Theta[0])) / 1000, 0,
                                (cxMm + seg.Rho[1] * Math.Cos(seg.Theta[1])) / 1000, (cyMm + seg.Rho[1] * Math.Sin(seg.Theta[1])) / 1000, 0);
                            break;
                        case SegKind.Spline:
                            int n = seg.Rho.Length;
                            var pts = new double[3 * n];
                            for (int i = 0; i < n; i++)
                            {
                                pts[3 * i] = (cxMm + seg.Rho[i] * Math.Cos(seg.Theta[i])) / 1000;
                                pts[3 * i + 1] = (cyMm + seg.Rho[i] * Math.Sin(seg.Theta[i])) / 1000;
                                pts[3 * i + 2] = 0;
                            }
                            object pointData = pts;
                            _expSplines++;
                            SketchSegment s = sm.CreateSpline2(pointData, false);
                            if (s == null) throw new InvalidOperationException("CreateSpline2 failed on " + g.Name);
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Ask SolidWorks what the sketch just drawn contains and compare it with what AddOutline put in: the
        /// entity counts by type (lines, arcs, splines) and the contours. A tooth profile must come back as ONE
        /// closed contour - a continuous chain - plus one contour per circle (bore, rim, holes), with no straight
        /// line beyond the radial ones of a small pinion, no extra or missing entity. Every sketch of the build is
        /// checked this way; the report goes to BuildResult.SketchReport and the log.
        /// </summary>
        private void VerifySketch(IModelDoc2 doc, Sketch sk)
        {
            if (sk == null) return;
            try
            {
                object[] segs = sk.GetSketchSegments() as object[];
                int lines = 0, arcs = 0, splines = 0, other = 0;
                if (segs != null)
                    foreach (object o in segs)
                    {
                        int t = ((ISketchSegment)o).GetType();
                        if (t == (int)swSketchSegments_e.swSketchLINE) lines++;
                        else if (t == (int)swSketchSegments_e.swSketchARC) arcs++;
                        else if (t == (int)swSketchSegments_e.swSketchSPLINE) splines++;
                        else other++;
                    }
                object[] contours = sk.GetSketchContours() as object[];
                int closed = 0, open = 0;
                if (contours != null)
                    foreach (object o in contours)
                    {
                        if (((ISketchContour)o).IsClosed()) closed++; else open++;
                    }
                int circles = arcs - _expArcs - _expFillets;                    // whole circles the caller added (bores, rims, holes)
                int expectedContours = _expOutlines + circles;
                var problems = new List<string>();
                if (_expOutlines > 0)
                {
                    if (lines != _expLines) problems.Add("straight lines " + lines + " (expected " + _expLines + (_expLines == 0 ? ", none belong to this profile" : ", the radial stretches below the base circle") + ")");
                    if (splines != _expSplines) problems.Add("flank splines " + splines + " (expected " + _expSplines + ")");
                    if (circles < 0) problems.Add("arcs " + arcs + " (expected at least " + (_expArcs + _expFillets) + ")");
                    if (other > 0) problems.Add(other + " entities of another type");
                }
                if (open > 0) problems.Add(open + " open contour" + (open == 1 ? "" : "s") + " - the chain is broken somewhere");
                if (closed != expectedContours) problems.Add("closed contours " + closed + " (expected " + expectedContours + ": " + (_expOutlines > 0 ? "the tooth profile as one chain" : "") + (circles > 0 ? (_expOutlines > 0 ? " + " : "") + circles + " circle" + (circles == 1 ? "" : "s") : "") + ")");
                string what = _expOutlines > 0
                    ? _expName + " profile (" + _expTeeth + " teeth): " + splines + " flank splines, " + _expFillets + " fillet arcs, " + _expArcs + " root/tip arcs, " + lines + " straight lines" + (circles > 0 ? ", " + circles + " circle" + (circles == 1 ? "" : "s") : "")
                    : "plain sketch: " + circles + " circle" + (circles == 1 ? "" : "s") + (lines > 0 ? ", " + lines + " lines" : "");
                _sketchesChecked++;
                if (problems.Count == 0)
                    _sketchReport.Add(what + " -> " + closed + " closed contour" + (closed == 1 ? "" : "s") + ", chain continuous, nothing stray");
                else
                {
                    _sketchProblems++;
                    _sketchReport.Add(what + " -> PROBLEM: " + string.Join("; ", problems.ToArray()));
                }
                Log.Add("sketch check: " + _sketchReport[_sketchReport.Count - 1]);
            }
            catch (Exception ex) { _sketchReport.Add("sketch check failed to run: " + ex.Message); Log.Add("sketch check: " + ex); }
        }

        /// <summary>
        /// Every entity of the sketch gets a Fixed relation, so nothing in it can move or be dragged later: the
        /// profile is positioned by its coordinates, not by dimensions (a dimension scheme for a thousand involute
        /// splines would be unreadable). SolidWorks then reports the sketch fully defined; that status is appended
        /// to the sketch's report line.
        /// </summary>
        private void FixSketch(IModelDoc2 doc, Sketch sk)
        {
            if (sk == null) return;
            try
            {
                object[] segs = sk.GetSketchSegments() as object[];
                if (segs == null || segs.Length == 0) return;
                doc.ClearSelection2(true);
                int selected = 0;
                foreach (object o in segs)
                    if (((ISketchSegment)o).Select4(selected > 0, null)) selected++;
                if (selected > 0) doc.SketchAddConstraints("sgFIXED");
                doc.ClearSelection2(true);
                int status = sk.GetConstrainedStatus();
                bool full = status == (int)swConstrainedStatus_e.swFullyConstrained;
                string note = "fixed " + selected + " of " + segs.Length + " entities, " + (full ? "fully defined" : "NOT fully defined (status " + status + ")");
                if (!full) _sketchProblems++;
                if (_sketchReport.Count > 0) _sketchReport[_sketchReport.Count - 1] += "; " + note;
                else _sketchReport.Add(note);
                Log.Add("sketch fix: " + note);
            }
            catch (Exception ex) { _sketchReport.Add("fixing the sketch failed: " + ex.Message); _sketchProblems++; Log.Add("sketch fix: " + ex); }
        }

        private string Finish(IModelDoc2 doc, GearModel g, string name, int teeth, double x, string outDir)
        {
            try
            {
                CustomPropertyManager cpm = doc.Extension.get_CustomPropertyManager("");
                Prop(cpm, "GearSet", g.Name);
                Prop(cpm, "Module", F(g.M));
                if (teeth > 0) Prop(cpm, "Teeth", teeth.ToString(CultureInfo.InvariantCulture));
                Prop(cpm, "PressureAngle", F(g.Design.AlphaDeg));
                Prop(cpm, "ProfileShift", F(x));
                Prop(cpm, "Ratio", g.Design.RatioText);
                EquationMgr eq = (EquationMgr)doc.GetEquationMgr();
                eq.Add2(-1, "\"Module\" = " + F(g.M), true);
                if (teeth > 0) eq.Add2(-1, "\"Teeth\" = " + teeth.ToString(CultureInfo.InvariantCulture), true);
                eq.Add2(-1, "\"FaceWidth\" = " + F(g.FaceWidth), true);
            }
            catch (Exception ex) { Log.Add(name + " properties: " + ex.Message); }
            doc.ForceRebuild3(false);
            doc.ShowNamedView2("*Isometric", 7);
            doc.ViewZoomtofit2();
            string path = Path.Combine(outDir, name + ".SLDPRT");
            Save(doc, path);
            return path;
        }

        private void Save(IModelDoc2 doc, string path)
        {
            int e = 0, w = 0;
            bool ok = doc.Extension.SaveAs3(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref e, ref w);
            if (!ok) throw new InvalidOperationException("SaveAs failed for " + path + " (error " + e + ", warning " + w + ")");
        }

        private static void Prop(CustomPropertyManager cpm, string name, string value)
        {
            cpm.Add3(name, (int)swCustomInfoType_e.swCustomInfoText, value, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
        }

        private static string F(double v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }

        // ------------------------------------------------------------------------------ calibration
        /// <summary>
        /// On a scratch part: which flag values make blind extrusions grow towards +Z, start offsets move
        /// towards +Z and CreateArc run counter-clockwise.
        /// </summary>
        private void Calibrate()
        {
            _dirFlip = false; _offFlip = false; _arcCcw = 1;
            IModelDoc2 doc = NewPart();
            StartSketch(doc);
            SketchSegment seg = doc.SketchManager.CreateArc(0, 0, 0, 0.01, 0, 0, 0, 0.01, 0, 1);
            SketchArc arc = (SketchArc)seg;
            double[] nv = (double[])arc.GetNormalVector();
            if (nv[2] < 0) _arcCcw = -1;
            doc.SketchManager.AddToDB = false;
            doc.SketchManager.InsertSketch(true);

            StartSketch(doc);
            doc.SketchManager.CreateCircleByRadius(0, 0, 0, 0.005);
            Extrude(doc, 10, 0, false);
            double[] box = BodyBox(doc);
            if (box[5] < 0.001) _dirFlip = true;

            StartSketch(doc);
            doc.SketchManager.CreateCircleByRadius(0, 0, 0, 0.003);
            Extrude(doc, 2, 20, false);
            box = BodyBox(doc);
            if (box[2] < -0.005) _offFlip = true;
            _sw.CloseDoc(doc.GetTitle());
        }

        private static double[] BodyBox(IModelDoc2 doc)
        {
            var res = new[] { 1e99, 1e99, 1e99, -1e99, -1e99, -1e99 };
            IPartDoc part = (IPartDoc)doc;
            object[] bodies = (object[])part.GetBodies2((int)swBodyType_e.swSolidBody, true);
            if (bodies == null) return res;
            foreach (object b in bodies)
            {
                Body2 body = (Body2)b;
                double[] bb = (double[])body.GetBodyBox();
                for (int i = 0; i < 3; i++) { res[i] = Math.Min(res[i], bb[i]); res[i + 3] = Math.Max(res[i + 3], bb[i + 3]); }
            }
            return res;
        }
    }
}
