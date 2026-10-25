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
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace GearSet
{
    public class BuildResult
    {
        public List<string> Files = new List<string>();
        public string AssemblyPath;
        public string Folder;
        public int MatesAdded, MatesWanted;
        public List<string> MateReport = new List<string>();
        public string MotionStudy;              // what the Motion Study got, null = none
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
        private IFace2 _sunFace;                    // the sun's input stub, found by AddMates, the motor's face

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
            Say("calibrating API direction conventions");
            Calibrate();
            Say("calibration: dirFlip=" + _dirFlip + " offFlip=" + _offFlip + " arcCCW=" + _arcCcw);

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
                Say("bearing " + h.SunBearing.Name); paths["BearingSun"] = BuildBearing(g, h.SunBearing, outDir);
                Say("bearing " + h.HubBearing.Name); paths["BearingHub"] = BuildBearing(g, h.HubBearing, outDir);
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
            Say("done: " + res.Files.Count + " files in " + outDir);
            return res;
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
                    Insert(asm, paths["BearingSun"], opt, 0, 0, h.SunBearingFrontZ0 / 1000),
                    Insert(asm, paths["BearingSun"], opt, 0, 0, h.SunBearingRearZ0 / 1000),
                    Insert(asm, paths["BearingHub"], opt, 0, 0, h.HubBearingZ0 / 1000),
                };
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

            AddMates(asmDoc, g, found, res);
            AddMotionStudy(asmDoc, res);
            asmDoc.ClearSelection2(true);
            try { asmDoc.ForceRebuild3(false); } catch { }
            return res;
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
        }

        /// <summary>Close the active sketch and extrude it: depth / offset in mm towards +Z (reverse: towards -Z).</summary>
        private Feature Extrude(IModelDoc2 doc, double depthMm, double offsetMm, bool reverse)
        {
            SketchManager sm = doc.SketchManager;
            Sketch sk = sm.ActiveSketch;
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
            for (int k = 0; k < g.Z; k++)
            {
                foreach (Segment seg in g.ToothSegments(k, NFlank))
                {
                    switch (seg.Kind)
                    {
                        case SegKind.Arc:
                            if (seg.Theta[1] - seg.Theta[0] < 1e-7) break;
                            sm.CreateArc(cxMm / 1000, cyMm / 1000, 0,
                                (cxMm + seg.Rho[0] * Math.Cos(seg.Theta[0])) / 1000, (cyMm + seg.Rho[0] * Math.Sin(seg.Theta[0])) / 1000, 0,
                                (cxMm + seg.Rho[1] * Math.Cos(seg.Theta[1])) / 1000, (cyMm + seg.Rho[1] * Math.Sin(seg.Theta[1])) / 1000, 0,
                                _arcCcw);
                            break;
                        case SegKind.Fillet:
                            {
                                int last = seg.Rho.Length - 1;
                                sm.CreateArc((cxMm + seg.Cx) / 1000, (cyMm + seg.Cy) / 1000, 0,
                                    (cxMm + seg.Rho[0] * Math.Cos(seg.Theta[0])) / 1000, (cyMm + seg.Rho[0] * Math.Sin(seg.Theta[0])) / 1000, 0,
                                    (cxMm + seg.Rho[last] * Math.Cos(seg.Theta[last])) / 1000, (cyMm + seg.Rho[last] * Math.Sin(seg.Theta[last])) / 1000, 0,
                                    (short)(seg.Ccw ? _arcCcw : -_arcCcw));
                            }
                            break;
                        case SegKind.Line:
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
                            SketchSegment s = sm.CreateSpline2(pointData, false);
                            if (s == null) throw new InvalidOperationException("CreateSpline2 failed on " + g.Name);
                            break;
                    }
                }
            }
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
