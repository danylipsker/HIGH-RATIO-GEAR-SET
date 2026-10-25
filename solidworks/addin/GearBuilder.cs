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
            Say("output ring (" + g.Design.Zo + " teeth)");
            paths["OutputRing"] = BuildRing(g, g.Output, g.ZB0, g.ZB1, outDir, "OutputRing");
            Say("sun (" + g.Design.Zs + " teeth)");
            paths["Sun"] = BuildSun(g, outDir);
            Say("planet (" + g.Design.Zp + " teeth)");
            paths["Planet"] = BuildPlanet(g, outDir);
            Say("thin ring (" + g.Design.Zr1 + " internal / " + g.Design.Zr2 + " external teeth)");
            paths["ThinRing"] = BuildThinRing(g, outDir);
            Say("carrier");
            paths["Carrier"] = BuildCarrier(g, outDir);
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
            if (addMates)
            {
                try { AddMates(asmDoc, g, comps, res); }
                catch (Exception ex) { res.MateReport.Add("mates aborted: " + ex.Message); Log.Add("mates aborted: " + ex); }
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
