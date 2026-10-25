// Gear set mathematics - C# port of gearset.py (solver, involute geometry, layout, checks).
// No SOLIDWORKS dependency: shared by the add-in, the dialog and the command line tool.
//
// Mechanism: 3K ("Wolfrom") compound planetary with ONE planet and a floating thin ring.
//   sun Zs (input) -> planet Zp on an idle eccentric carrier -> thin ring internal teeth Zr1
//   thin ring external teeth Zr2 -> fixed ring Zf (housing) and output ring Zo (= Zf +/- 1..3, profile shifted)
//   i = Zo (Zf Zr1 + Zs Zr2) / ( Zs Zr2 (Zo - Zf) )          (exact rational; sign = output direction)
//   e = m/2 (Zr1 - Zs - 2 Zp)  eccentricity; the ring with Zr2 + (Zr1 - Zs - 2Zp) teeth is standard, the other shifted.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace GearSet
{
    /// <summary>Target ratio as an exact fraction (text "500", "528.69" or "1000/3").</summary>
    public struct Ratio
    {
        public long Num, Den;
        public double Value { get { return (double)Num / Den; } }

        public static bool TryParse(string text, out Ratio r)
        {
            r = new Ratio { Num = 0, Den = 1 };
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim().Replace(',', '.').Replace(":", "/").Replace(" ", "");
            if (text.StartsWith("1/") && text.Length > 2 && text.IndexOf('/', 2) < 0)
            {
                // "1/500" means 1:500 -> 500
                text = text.Substring(2);
            }
            int slash = text.IndexOf('/');
            if (slash > 0)
            {
                Ratio a, b;
                if (!TryParse(text.Substring(0, slash), out a) || !TryParse(text.Substring(slash + 1), out b) || b.Num == 0) return false;
                r = Normalize(a.Num * b.Den, a.Den * b.Num);
                return true;
            }
            decimal d;
            if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out d) || d <= 0) return false;
            long den = 1;
            while (d != Math.Floor(d) && den < 1000000) { d *= 10; den *= 10; }
            r = Normalize((long)Math.Round(d), den);
            return r.Num > 0;
        }

        public static Ratio Normalize(long num, long den)
        {
            if (den < 0) { num = -num; den = -den; }
            long g = Gcd(Math.Abs(num), den);
            if (g > 1) { num /= g; den /= g; }
            return new Ratio { Num = num, Den = den };
        }

        public static long Gcd(long a, long b) { while (b != 0) { long t = a % b; a = b; b = t; } return a == 0 ? 1 : a; }

        public override string ToString()
        {
            if (Den == 1) return Num.ToString(CultureInfo.InvariantCulture);
            return Num.ToString(CultureInfo.InvariantCulture) + "/" + Den.ToString(CultureInfo.InvariantCulture)
                 + " = " + Value.ToString("0.000000", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Search ranges and design rules (tooth differences in teeth, lengths in modules).</summary>
    public class Limits
    {
        public int SunMin = 12, SunMax = 30;
        public int PlanetMin = 12, PlanetMax = 80;
        public int EccMin = 10, EccMax = 70;          // Zr1 - Zs - 2Zp  (= tooth difference of the eccentric meshes)
        public double WallMinMod = 1.5;               // thin ring wall between the two tooth roots, in modules
        public int WallMaxTeeth = 30;                 // Zr2 - Zr1 upper bound
        public int DeltaMax = 3;                      // |Zo - Zf|
        public double AwMinDeg = 14.0, AwMaxDeg = 31.0;
        public double PressureAngleDeg = 20.0;
        public double Addendum = 1.0, Clearance = 0.25;

        public int WallMinTeeth
        {
            get { return (int)Math.Ceiling(2 * (WallMinMod + 2 * (Addendum + Clearance)) - 1e-9); }
        }
    }

    public class Design
    {
        public int Zs, Zp, Zr1, Zr2, Zf, Zo;
        public bool OutputShifted;                    // true: output ring is the profile-shifted one, else the fixed ring
        public double AlphaDeg = 20.0, Addendum = 1.0, Clearance = 0.25;

        public int D { get { return Zr1 - Zs - 2 * Zp; } }
        public double Alpha { get { return AlphaDeg * Math.PI / 180; } }
        public int Zshifted { get { return OutputShifted ? Zo : Zf; } }
        public int Zstd { get { return OutputShifted ? Zf : Zo; } }
        public int Delta { get { return Math.Abs(Zo - Zf); } }

        public long RatioNum { get { return (long)Zo * ((long)Zf * Zr1 + (long)Zs * Zr2); } }
        public long RatioDen { get { return (long)Zs * Zr2 * (Zo - Zf); } }
        public double RatioValue { get { return (double)RatioNum / RatioDen; } }
        public Ratio RatioFraction { get { return Ratio.Normalize(RatioNum, RatioDen); } }
        public bool OutputReversed { get { return RatioValue < 0; } }

        public static double Inv(double a) { return Math.Tan(a) - a; }

        /// <summary>Working pressure angle of the shifted ring / thin ring external mesh.</summary>
        public double Aw
        {
            get { return Math.Acos(Math.Cos(Alpha) * (Zshifted - Zr2) / D); }
        }
        public double AwDeg { get { return Aw * 180 / Math.PI; } }

        /// <summary>Profile shift coefficient of the shifted ring (thin ring external teeth unshifted).</summary>
        public double XShift
        {
            get { return (Inv(Aw) - Inv(Alpha)) * (Zshifted - Zr2) / (2 * Math.Tan(Alpha)); }
        }
        public double XFixed { get { return OutputShifted ? 0.0 : XShift; } }
        public double XOutput { get { return OutputShifted ? XShift : 0.0; } }

        public double WallModules { get { return (Zr2 - Zr1) / 2.0 - 2 * (Addendum + Clearance); } }

        public string RatioText
        {
            get
            {
                Ratio f = RatioFraction;
                if (f.Den == 1) return f.Num.ToString(CultureInfo.InvariantCulture);
                return f.Value.ToString("0.00", CultureInfo.InvariantCulture) + " (" + f.Num + "/" + f.Den + ")";
            }
        }

        public string Validate(Limits lim)
        {
            if (D < 1) return "thin ring internal gear too small for sun + planet (Zr1 <= Zs + 2Zp)";
            if (Zstd != Zr2 + D) return "standard ring must have Zr2 + (Zr1 - Zs - 2Zp) teeth";
            double c = Math.Cos(Alpha) * (Zshifted - Zr2) / D;
            if (!(c > -1 && c < 1)) return "shifted ring cannot reach the thin ring centre distance";
            double awd = Math.Acos(c) * 180 / Math.PI;
            if (awd < lim.AwMinDeg || awd > lim.AwMaxDeg) return "working pressure angle " + awd.ToString("0.0") + " deg outside the allowed range";
            if (WallModules < lim.WallMinMod) return "thin ring wall " + WallModules.ToString("0.00") + " m below the minimum";
            if (Zo == Zf) return "fixed and output ring must differ";
            return null;
        }

        public string TeethText
        {
            get { return Zs + " / " + Zp + " / " + Zr1 + " / " + Zr2 + " / " + Zf + " / " + Zo; }
        }

        /// <summary>Design from six tooth counts; the ring at standard centre distance decides which one is shifted.</summary>
        public static Design FromTeeth(int zs, int zp, int zr1, int zr2, int zf, int zo, Limits lim)
        {
            int D = zr1 - zs - 2 * zp;
            return new Design
            {
                Zs = zs, Zp = zp, Zr1 = zr1, Zr2 = zr2, Zf = zf, Zo = zo,
                OutputShifted = zf == zr2 + D,
                AlphaDeg = lim.PressureAngleDeg, Addendum = lim.Addendum, Clearance = lim.Clearance,
            };
        }

        /// <summary>Parse "16 32 115 123 158 155" (spaces, commas or slashes).</summary>
        public static bool TryParseTeeth(string text, out int[] z)
        {
            z = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Split(new[] { ' ', ',', '/', ';', '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 6) return false;
            z = new int[6];
            for (int i = 0; i < 6; i++) if (!int.TryParse(parts[i], out z[i]) || z[i] < 1) return false;
            return true;
        }
    }

    /// <summary>Sets that are known to work; shown as presets in the dialog.</summary>
    public static class Presets
    {
        public class Preset { public string Name; public double Module; public int[] Teeth; }
        public static readonly Preset[] All =
        {
            new Preset { Name = "1:500  default  24/20/102/114/152/150  m1", Module = 1.0, Teeth = new[] { 24, 20, 102, 114, 152, 150 } },
            new Preset { Name = "1:500  sun 16  16/18/96/123/167/164  m1", Module = 1.0, Teeth = new[] { 16, 18, 96, 123, 167, 164 } },
            new Preset { Name = "1:500  planet 32  20/32/135/153/204/200  m1", Module = 1.0, Teeth = new[] { 20, 32, 135, 153, 204, 200 } },
            new Preset { Name = "1:528.7  thin ring 16/32/115/123/158/155  m1", Module = 1.0, Teeth = new[] { 16, 32, 115, 123, 158, 155 } },
            new Preset { Name = "1:528.7  thin ring 16/32/115/123/158/155  m1.25", Module = 1.25, Teeth = new[] { 16, 32, 115, 123, 158, 155 } },
            new Preset { Name = "1:528.7  thin ring 16/32/115/123/158/155  m1.5", Module = 1.5, Teeth = new[] { 16, 32, 115, 123, 158, 155 } },
            new Preset { Name = "1:528.7  thin ring 16/32/115/123/158/155  m2", Module = 2.0, Teeth = new[] { 16, 32, 115, 123, 158, 155 } },
        };
    }

    public class Candidate
    {
        public Design Design;
        public double Error;       // relative ratio error, 0 = exact
        public double Score;
    }

    public static class Solver
    {
        /// <summary>
        /// Enumerate tooth sets. exactOnly: only sets whose ratio equals the target exactly. Otherwise the
        /// closest sets (within 5 %, or the best 300 whatever their error when none is that close).
        /// </summary>
        public static List<Candidate> Search(Ratio target, Limits lim, int? sun, int? planet, bool exactOnly, int maxResults)
        {
            double alpha = lim.PressureAngleDeg * Math.PI / 180;
            double ca = Math.Cos(alpha);
            double cmin = Math.Cos(lim.AwMaxDeg * Math.PI / 180), cmax = Math.Cos(lim.AwMinDeg * Math.PI / 180);
            long tn = Math.Abs(target.Num), td = target.Den;
            var hits = new List<Candidate>();
            double window = 0.05;
            for (int pass = 0; pass < 2; pass++)
            {
                hits.Clear();
                int sMin = sun ?? lim.SunMin, sMax = sun ?? lim.SunMax;
                int pMin = planet ?? lim.PlanetMin, pMax = planet ?? lim.PlanetMax;
                for (int Zs = sMin; Zs <= sMax; Zs++)
                    for (int Zp = pMin; Zp <= pMax; Zp++)
                    {
                        int baseZ = Zs + 2 * Zp;
                        for (int D = lim.EccMin; D <= lim.EccMax; D++)
                        {
                            int Zr1 = baseZ + D;
                            var dd = new List<int>();
                            for (int d = -lim.DeltaMax; d <= lim.DeltaMax; d++)
                            {
                                if (d == 0) continue;
                                double c = ca * (D + d) / D;
                                if (c >= cmin && c <= cmax) dd.Add(d);
                            }
                            if (dd.Count == 0) continue;
                            for (int W = lim.WallMinTeeth; W <= lim.WallMaxTeeth; W++)
                            {
                                int Zr2 = Zr1 + W;
                                int Zstd = Zr2 + D;
                                foreach (int d in dd)
                                {
                                    int Zsh = Zstd + d;
                                    for (int which = 0; which < 2; which++)
                                    {
                                        bool outShifted = which == 0;
                                        int Zf = outShifted ? Zstd : Zsh, Zo = outShifted ? Zsh : Zstd;
                                        long num = Math.Abs((long)Zo * ((long)Zf * Zr1 + (long)Zs * Zr2));
                                        long den = Math.Abs((long)Zs * Zr2 * (Zo - Zf));
                                        double err;
                                        if (exactOnly)
                                        {
                                            if (num * td != tn * den) continue;
                                            err = 0;
                                        }
                                        else
                                        {
                                            err = Math.Abs((double)(num * td - tn * den)) / ((double)tn * den);
                                            if (err > window) continue;
                                        }
                                        hits.Add(new Candidate
                                        {
                                            Error = err,
                                            Design = new Design
                                            {
                                                Zs = Zs, Zp = Zp, Zr1 = Zr1, Zr2 = Zr2, Zf = Zf, Zo = Zo, OutputShifted = outShifted,
                                                AlphaDeg = lim.PressureAngleDeg, Addendum = lim.Addendum, Clearance = lim.Clearance,
                                            },
                                        });
                                    }
                                }
                            }
                        }
                    }
                if (hits.Count > 0 || exactOnly) break;
                window = 10.0;      // nothing within 5 %: keep whatever is closest
            }
            foreach (var h in hits) h.Score = Score(h.Design, h.Error);
            hits.Sort((a, b) => a.Score.CompareTo(b.Score));
            if (hits.Count > maxResults) hits.RemoveRange(maxResults, hits.Count - maxResults);
            return hits;
        }

        /// <summary>Lower is better: ratio error, working pressure angle, ring tooth difference, undercut, size.</summary>
        public static double Score(Design d, double err)
        {
            double s = err * 1e4;
            s += Math.Max(0.0, d.AwDeg - 27.0) * 2.0;
            s += Math.Max(0.0, 20.0 - d.AwDeg) * 1.0;
            s += (d.Delta - 1) * 1.0;
            s += Math.Max(0, 17 - d.Zs) * 1.0;
            s += Math.Max(0, 17 - d.Zp) * 1.0;
            s += Math.Abs(d.Zs - 16) * 0.6;
            s += Math.Abs(d.Zp - 32) * 0.3;
            s += Math.Abs(d.Zr1 - 99) * 0.08;
            s += Math.Abs(d.Zr2 - 100) * 0.08;
            s += d.Zf * 0.03;
            return s;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // geometry
    // ------------------------------------------------------------------------------------------------
    public enum SegKind { Arc, Line, Spline }

    /// <summary>One piece of a tooth outline, as polar points (rho mm, theta rad) about the gear centre.</summary>
    public class Segment
    {
        public SegKind Kind;
        public double[] Rho, Theta;
    }

    public class GearGeom
    {
        public string Name;
        public bool Internal;
        public double M, Alpha, X, Phase, Thinning;
        public int Z;
        public double R, Rb, Ra, Rf, HalfRef;

        public GearGeom(string name, bool isInternal, double m, int z, double alphaRad, double x, double phase, double thinning)
        {
            Name = name; Internal = isInternal; M = m; Z = z; Alpha = alphaRad; X = x; Phase = phase; Thinning = thinning;
            R = m * z / 2;
            Rb = R * Math.Cos(alphaRad);
            const double ha = 1.0, c = 0.25;
            double s;
            if (!isInternal)
            {
                Ra = R + m * (ha + x);
                Rf = R - m * (ha + c - x);
                s = m * (Math.PI / 2 + 2 * x * Math.Tan(alphaRad));
            }
            else
            {
                Ra = R - m * (ha - x);
                Rf = R + m * (ha + c + x);
                s = m * (Math.PI / 2 - 2 * x * Math.Tan(alphaRad));
            }
            HalfRef = (s - thinning) / (2 * R);
        }

        public double Pitch { get { return 2 * Math.PI / Z; } }

        /// <summary>Half tooth angle at radius rho (involute part).</summary>
        public double HalfAngle(double rho)
        {
            double aRho = Math.Acos(Math.Min(1.0, Rb / rho));
            return Internal ? HalfRef - Design.Inv(Alpha) + Design.Inv(aRho)
                            : HalfRef + Design.Inv(Alpha) - Design.Inv(aRho);
        }

        public double[] FlankRadii(int n)
        {
            double lo = Internal ? Math.Max(Rb, Ra) : Math.Max(Rb, Rf);
            double hi = Internal ? Rf : Ra;
            var r = new double[n];
            for (int k = 0; k < n; k++) r[k] = lo + (hi - lo) * Math.Pow((double)k / (n - 1), 1.3);
            r[0] = lo; r[n - 1] = hi;
            return r;
        }

        /// <summary>Segments of tooth k, counter-clockwise (root land first).</summary>
        public List<Segment> ToothSegments(int k, int nFlank)
        {
            double tc = Phase + k * Pitch;
            double pitch = Pitch;
            double[] radii = FlankRadii(nFlank);
            var segs = new List<Segment>();
            if (!Internal)
            {
                double h0 = HalfAngle(radii[0]);
                double hTip = HalfAngle(Ra);
                segs.Add(Arc(Rf, tc - pitch + h0, tc - h0));
                if (Rb > Rf + 1e-9) segs.Add(Line(Rf, tc - h0, Rb, tc - h0));
                segs.Add(Flank(radii, tc, -1, false));
                segs.Add(Arc(Ra, tc - hTip, tc + hTip));
                segs.Add(Flank(radii, tc, +1, true));
                if (Rb > Rf + 1e-9) segs.Add(Line(Rb, tc + h0, Rf, tc + h0));
            }
            else
            {
                double hf = HalfAngle(Rf);
                double hTip = HalfAngle(Ra);
                segs.Add(Arc(Rf, tc - pitch + hf, tc - hf));
                segs.Add(Flank(radii, tc, -1, true));       // from root (outer) inwards to the tip
                segs.Add(Arc(Ra, tc - hTip, tc + hTip));
                segs.Add(Flank(radii, tc, +1, false));      // from tip outwards to the root
            }
            return segs;
        }

        private Segment Flank(double[] radii, double tc, int sgn, bool reversed)
        {
            int n = radii.Length;
            var seg = new Segment { Kind = SegKind.Spline, Rho = new double[n], Theta = new double[n] };
            for (int i = 0; i < n; i++)
            {
                double rho = reversed ? radii[n - 1 - i] : radii[i];
                seg.Rho[i] = rho;
                seg.Theta[i] = tc + sgn * HalfAngle(rho);
            }
            return seg;
        }

        private static Segment Arc(double r, double t0, double t1)
        {
            return new Segment { Kind = SegKind.Arc, Rho = new[] { r, r }, Theta = new[] { t0, t1 } };
        }

        private static Segment Line(double r0, double t0, double r1, double t1)
        {
            return new Segment { Kind = SegKind.Line, Rho = new[] { r0, r1 }, Theta = new[] { t0, t1 } };
        }

        public string Check()
        {
            if (!Internal)
            {
                if (2 * HalfAngle(Math.Max(Rb, Rf)) >= Pitch) return Name + ": teeth overlap at the root";
                if (HalfAngle(Ra) <= 0) return Name + ": pointed teeth";
            }
            else
            {
                if (Ra < Rb) return Name + ": internal tip circle inside the base circle";
                if (2 * HalfAngle(Rf) >= Pitch) return Name + ": internal teeth overlap at the root";
            }
            return null;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // complete model: all outlines, positions, overall dimensions, checks
    // ------------------------------------------------------------------------------------------------
    public class GearModel
    {
        public Design Design;
        public double M, FaceWidth, Gap, Rim, Backlash, ShaftD, PinD, FitClearance, HoleD;
        public int Holes;
        public string Name;

        public GearGeom Sun, Planet, ThinInt, ThinExt, Fixed, Output;
        public double A1, E, RingOuterR, JournalR, PlanetBoreR, CarrierBoreR, HoleCircleR, ShaftIn, ShaftOut;
        public double ZA0, ZA1, ZB0, ZB1, ThinRingLength, ThinRingWall;
        public double ClearFixedTip, ClearFixedRoot, ClearOutTip, ClearOutRoot;
        public List<string> Notes = new List<string>();

        public static GearModel Create(Design d, double module, double faceWidth, double gap, double backlash,
                                       double shaftD, double pinD, double rimModules, int holes, string name)
        {
            var g = new GearModel
            {
                Design = d, M = module, Backlash = backlash, Holes = holes, Name = name,
                FaceWidth = faceWidth > 0 ? faceWidth : Math.Round(10 * module, 3),
                Gap = gap > 0 ? gap : Math.Round(Math.Max(2.0, 1.5 * module), 3),
                ShaftD = shaftD > 0 ? shaftD : Math.Round(0.5 * module * d.Zs, 2),
                PinD = pinD > 0 ? pinD : Math.Round(0.45 * module * d.Zp, 2),
                Rim = rimModules * module,
                FitClearance = Math.Round(0.05 * module + 0.05, 3),
            };
            g.HoleD = Math.Round(0.4 * g.Rim, 2);
            g.Build();
            return g;
        }

        private static double Mod(double a, double p) { return a - Math.Floor(a / p) * p; }

        private static double PhaseFor(int z, double angle, bool wantTooth)
        {
            double pitch = 2 * Math.PI / z;
            return wantTooth ? Mod(angle, pitch) : Mod(angle - pitch / 2, pitch);
        }

        private static bool ToothAt(int z, double phase, double angle)
        {
            double pitch = 2 * Math.PI / z;
            double d = Mod(angle - phase, pitch);
            if (Math.Min(d, pitch - d) < 1e-9) return true;
            if (Math.Abs(d - pitch / 2) < 1e-9) return false;
            throw new InvalidOperationException("direction is neither a tooth nor a space centre");
        }

        private void Build()
        {
            Design d = Design;
            double a = d.Alpha, m = M;
            A1 = m * (d.Zs + d.Zp) / 2;
            E = m * d.D / 2;
            double phS = 0.0;
            double phP = PhaseFor(d.Zp, Math.PI, false);                 // planet space towards the sun
            bool pTooth = ToothAt(d.Zp, phP, 0.0);                        // planet shows ... towards +x
            double phTi = PhaseFor(d.Zr1, 0.0, !pTooth);
            double phTe = 0.0;
            bool teTooth = ToothAt(d.Zr2, phTe, Math.PI);                 // thin ring external at -x
            double phF = PhaseFor(d.Zf, Math.PI, !teTooth);
            double phO = PhaseFor(d.Zo, Math.PI, !teTooth);
            double j = Backlash / 2;
            Sun = new GearGeom("Sun", false, m, d.Zs, a, 0, phS, j);
            Planet = new GearGeom("Planet", false, m, d.Zp, a, 0, phP, j);
            ThinInt = new GearGeom("ThinRing internal", true, m, d.Zr1, a, 0, phTi, j);
            ThinExt = new GearGeom("ThinRing external", false, m, d.Zr2, a, 0, phTe, j);
            Fixed = new GearGeom("FixedRing", true, m, d.Zf, a, d.XFixed, phF, j);
            Output = new GearGeom("OutputRing", true, m, d.Zo, a, d.XOutput, phO, j);

            RingOuterR = Math.Max(Fixed.Rf, Output.Rf) + Rim;
            JournalR = ThinInt.Rf - FitClearance;
            PlanetBoreR = PinD / 2 + FitClearance;
            CarrierBoreR = ShaftD / 2 + FitClearance;
            HoleCircleR = RingOuterR - Rim / 2;
            ShaftIn = 2 * FaceWidth;
            ShaftOut = FaceWidth;
            ZA0 = 0; ZA1 = FaceWidth; ZB0 = FaceWidth + Gap; ZB1 = 2 * FaceWidth + Gap;
            ThinRingLength = ZB1;
            ThinRingWall = ThinExt.Rf - ThinInt.Rf;

            foreach (var gg in new[] { Sun, Planet, ThinInt, ThinExt, Fixed, Output })
            {
                string c = gg.Check();
                if (c != null) Notes.Add(c);
            }
            if (JournalR <= E + CarrierBoreR + 1.0) Notes.Add("carrier journal too small to clear the sun shaft bore");
            if (JournalR + FitClearance < A1 + E + PinD / 2) Notes.Add("planet pin lies outside the carrier journal");
            ClearFixedTip = (Fixed.Ra - E) - ThinExt.Rf;
            ClearFixedRoot = (Fixed.Rf - E) - ThinExt.Ra;
            ClearOutTip = (Output.Ra - E) - ThinExt.Rf;
            ClearOutRoot = (Output.Rf - E) - ThinExt.Ra;
            if (ClearFixedTip < 0.05 * m || ClearFixedRoot < 0.05 * m) Notes.Add("fixed ring: radial clearance to the thin ring too small");
            if (ClearOutTip < 0.05 * m || ClearOutRoot < 0.05 * m) Notes.Add("output ring: radial clearance to the thin ring too small");
            var cr = ContactRatios();
            foreach (var kv in cr) if (kv.Value < 1.0) Notes.Add("contact ratio " + kv.Key + " = " + kv.Value.ToString("0.00") + " < 1");
        }

        public Dictionary<string, double> ContactRatios()
        {
            double a = Design.Alpha, pb = Math.PI * M * Math.Cos(a);
            Func<GearGeom, GearGeom, double, double> extExt = (g1, g2, aw) =>
            {
                double cd = (g1.R + g2.R) * Math.Cos(a) / Math.Cos(aw);
                return (Math.Sqrt(g1.Ra * g1.Ra - g1.Rb * g1.Rb) + Math.Sqrt(g2.Ra * g2.Ra - g2.Rb * g2.Rb) - cd * Math.Sin(aw)) / pb;
            };
            Func<GearGeom, GearGeom, double, double> extInt = (g1, g2, aw) =>
            {
                double cd = (g2.R - g1.R) * Math.Cos(a) / Math.Cos(aw);
                return (Math.Sqrt(g1.Ra * g1.Ra - g1.Rb * g1.Rb) - Math.Sqrt(g2.Ra * g2.Ra - g2.Rb * g2.Rb) + cd * Math.Sin(aw)) / pb;
            };
            double awF = Design.OutputShifted ? a : Design.Aw;
            double awO = Design.OutputShifted ? Design.Aw : a;
            return new Dictionary<string, double>
            {
                { "sun/planet", extExt(Sun, Planet, a) },
                { "planet/thin ring", extInt(Planet, ThinInt, a) },
                { "thin ring/fixed ring", extInt(ThinExt, Fixed, awF) },
                { "thin ring/output ring", extInt(ThinExt, Output, awO) },
            };
        }

        // overall dimensions ---------------------------------------------------------------------
        public double RingOD { get { return 2 * RingOuterR; } }
        public double GearPackLength { get { return ThinRingLength; } }
        public double OverallLength { get { return ShaftIn + ThinRingLength + ShaftOut; } }

        public string DimensionsText()
        {
            var cr = ContactRatios();
            double crMin = double.MaxValue;
            foreach (var v in cr.Values) crMin = Math.Min(crMin, v);
            var sb = new System.Text.StringBuilder();
            string f = "0.##";
            sb.AppendLine("Ratio               " + Design.RatioText + (Design.OutputReversed ? "   (output turns opposite to the input)" : "   (same direction)"));
            sb.AppendLine("Teeth Zs/Zp/Zr1/Zr2/Zf/Zo   " + Design.TeethText);
            sb.AppendLine("Module              " + M.ToString(f, CultureInfo.InvariantCulture) + " mm, pressure angle " + Design.AlphaDeg + " deg");
            sb.AppendLine("Ring outer diameter " + RingOD.ToString(f, CultureInfo.InvariantCulture) + " mm   (fixed and output ring)");
            sb.AppendLine("Gear pack length    " + GearPackLength.ToString(f, CultureInfo.InvariantCulture) + " mm   (2 x " + FaceWidth.ToString(f, CultureInfo.InvariantCulture) + " face width + " + Gap.ToString(f, CultureInfo.InvariantCulture) + " gap)");
            sb.AppendLine("Overall length      " + OverallLength.ToString(f, CultureInfo.InvariantCulture) + " mm   (with " + ShaftIn.ToString(f, CultureInfo.InvariantCulture) + " input stub and " + ShaftOut.ToString(f, CultureInfo.InvariantCulture) + " rear stub of the sun shaft)");
            sb.AppendLine("Sun shaft / planet pin   d " + ShaftD.ToString(f, CultureInfo.InvariantCulture) + " / " + PinD.ToString(f, CultureInfo.InvariantCulture) + " mm");
            sb.AppendLine("Sun-planet centre distance   " + A1.ToString("0.###", CultureInfo.InvariantCulture) + " mm, thin ring eccentricity " + E.ToString("0.###", CultureInfo.InvariantCulture) + " mm");
            sb.AppendLine("Thin ring wall      " + ThinRingWall.ToString("0.##", CultureInfo.InvariantCulture) + " mm between the tooth roots, length " + ThinRingLength.ToString(f, CultureInfo.InvariantCulture) + " mm");
            sb.AppendLine("Profile shift       " + (Design.OutputShifted ? "output" : "fixed") + " ring x = " + Design.XShift.ToString("+0.000;-0.000", CultureInfo.InvariantCulture)
                          + ", working pressure angle " + Design.AwDeg.ToString("0.0", CultureInfo.InvariantCulture) + " deg");
            sb.AppendLine("Contact ratios      min " + crMin.ToString("0.00", CultureInfo.InvariantCulture)
                          + "  (sun/planet " + cr["sun/planet"].ToString("0.00", CultureInfo.InvariantCulture)
                          + ", planet/thin ring " + cr["planet/thin ring"].ToString("0.00", CultureInfo.InvariantCulture)
                          + ", thin/fixed " + cr["thin ring/fixed ring"].ToString("0.00", CultureInfo.InvariantCulture)
                          + ", thin/output " + cr["thin ring/output ring"].ToString("0.00", CultureInfo.InvariantCulture) + ")");
            if (Backlash > 0) sb.AppendLine("Backlash            " + Backlash.ToString("0.###", CultureInfo.InvariantCulture) + " mm per mesh");
            foreach (string n in Notes) sb.AppendLine("WARNING  " + n);
            return sb.ToString();
        }
    }
}
