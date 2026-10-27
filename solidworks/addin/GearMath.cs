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
    public enum SegKind { Arc, Line, Spline, Fillet }

    /// <summary>One piece of a tooth outline, as polar points (rho mm, theta rad) about the gear centre.
    /// Arc: about the gear centre, Rho[0] radius, Theta[0]..Theta[1] counter-clockwise.
    /// Fillet: arc about (Cx, Cy) (gear coords, mm) with radius R, traversed from the first to the last point,
    /// counter-clockwise about its centre when Ccw; points are a sampling of it.</summary>
    public class Segment
    {
        public SegKind Kind;
        public double[] Rho, Theta;
        public double Cx, Cy, R;
        public bool Ccw;
    }

    /// <summary>Root fillet of one tooth flank, tooth centre at angle 0, leading (negative-angle) side.</summary>
    public class FilletGeom
    {
        public double Rho, Cx, Cy;          // radius and centre (mm)
        public double T2R, T2Th;            // tangent point on the root circle (polar)
        public double T1R, T1Th;            // tangent point on the flank / radial line (polar)
        public bool Ccw;                    // traversal T2 -> T1 is counter-clockwise about the centre
        public double[] PtsR, PtsTh;        // sampled points T2 -> T1 (polar about the gear centre)
    }

    public class GearGeom
    {
        public string Name;
        public bool Internal;
        public double M, Alpha, X, Phase, Thinning, FilletReq;
        public int Z;
        public double R, Rb, Ra, Rf, HalfRef;
        public FilletGeom Fil;              // null = sharp root corner

        public double TipShorten;           // tip taken back (mm) to clear the mate's base circle (involute interference)

        public GearGeom(string name, bool isInternal, double m, int z, double alphaRad, double x, double phase, double thinning, double filletMm = 0.0, double tipShorten = 0.0)
        {
            Name = name; Internal = isInternal; M = m; Z = z; Alpha = alphaRad; X = x; Phase = phase; Thinning = thinning; FilletReq = filletMm; TipShorten = tipShorten;
            R = m * z / 2;
            Rb = R * Math.Cos(alphaRad);
            const double ha = 1.0, c = 0.25;
            double s;
            if (!isInternal)
            {
                Ra = R + m * (ha + x) - tipShorten;
                Rf = R - m * (ha + c - x);
                s = m * (Math.PI / 2 + 2 * x * Math.Tan(alphaRad));
            }
            else
            {
                Ra = R - m * (ha - x) + tipShorten;      // internal: the tip is the inner radius
                Rf = R + m * (ha + c + x);
                s = m * (Math.PI / 2 - 2 * x * Math.Tan(alphaRad));
            }
            HalfRef = (s - thinning) / (2 * R);
            Fil = ComputeFillet();
        }

        public double Pitch { get { return 2 * Math.PI / Z; } }

        private double[] FlankPoint(double rho)
        {
            double t = -HalfAngle(rho);
            return new[] { rho * Math.Cos(t), rho * Math.Sin(t) };
        }

        /// <summary>Circular fillet tangent to the root circle and to the leading flank (or its radial root line).</summary>
        private FilletGeom ComputeFillet()
        {
            if (FilletReq <= 0) return null;
            bool ext = !Internal;
            double pitch = Pitch, th0;
            double[] p0, d;
            if (ext && Rb > Rf + 1e-9)
            {
                th0 = -HalfAngle(Rb);
                p0 = new[] { Rf * Math.Cos(th0), Rf * Math.Sin(th0) };
                d = new[] { Math.Cos(th0), Math.Sin(th0) };
            }
            else
            {
                th0 = -HalfAngle(Rf);
                p0 = new[] { Rf * Math.Cos(th0), Rf * Math.Sin(th0) };
                double eps = 1e-4 * M;
                double[] q = ext ? FlankPoint(Rf + eps) : FlankPoint(Rf - eps);
                d = new[] { q[0] - p0[0], q[1] - p0[1] };
                double ln = Math.Sqrt(d[0] * d[0] + d[1] * d[1]);
                d[0] /= ln; d[1] /= ln;
            }
            double[] eSpace = { Math.Sin(th0), -Math.Cos(th0) };
            double[] n = { -d[1], d[0] };
            if (n[0] * eSpace[0] + n[1] * eSpace[1] < 0) { n[0] = -n[0]; n[1] = -n[1]; }
            double rho = FilletReq;
            for (int iter = 0; iter < 40; iter++)
            {
                if (rho < 0.02 * M) return null;
                double Rc = ext ? Rf + rho : Rf - rho;
                double qx = p0[0] + rho * n[0], qy = p0[1] + rho * n[1];
                double b = qx * d[0] + qy * d[1];
                double c = qx * qx + qy * qy - Rc * Rc;
                double disc = b * b - c;
                bool ok = disc >= 0;
                double sdist = 0, cx = 0, cy = 0, r1 = 0, th2 = 0;
                double[] t1 = null, t2 = null;
                if (ok)
                {
                    double ra_ = -b - Math.Sqrt(disc), rb_ = -b + Math.Sqrt(disc);
                    if (ra_ >= -1e-12) sdist = ra_; else if (rb_ >= -1e-12) sdist = rb_; else ok = false;
                }
                if (ok)
                {
                    cx = qx + sdist * d[0]; cy = qy + sdist * d[1];
                    t1 = new[] { p0[0] + sdist * d[0], p0[1] + sdist * d[1] };
                    double cl = Math.Sqrt(cx * cx + cy * cy);
                    t2 = new[] { cx / cl * Rf, cy / cl * Rf };
                    r1 = Math.Sqrt(t1[0] * t1[0] + t1[1] * t1[1]);
                    th2 = Math.Atan2(t2[1], t2[0]);
                    if (ext) ok = (Rb > Rf + 1e-9 ? r1 <= Rb + 1e-9 : r1 <= Ra - 0.5 * M) && th2 > -pitch / 2 + 1e-6;
                    else ok = r1 >= Ra + 0.3 * M && th2 > -pitch / 2 + 1e-6;
                }
                if (ok)
                {
                    double a2 = Math.Atan2(t2[1] - cy, t2[0] - cx);
                    double a1 = Math.Atan2(t1[1] - cy, t1[0] - cx);
                    double da = a1 - a2 + Math.PI;
                    da = da - Math.Floor(da / (2 * Math.PI)) * (2 * Math.PI) - Math.PI;
                    const int npts = 7;
                    var f = new FilletGeom { Rho = rho, Cx = cx, Cy = cy, Ccw = da > 0, PtsR = new double[npts], PtsTh = new double[npts] };
                    for (int k = 0; k < npts; k++)
                    {
                        double a = a2 + da * k / (npts - 1);
                        double x = cx + rho * Math.Cos(a), y = cy + rho * Math.Sin(a);
                        f.PtsR[k] = Math.Sqrt(x * x + y * y); f.PtsTh[k] = Math.Atan2(y, x);
                    }
                    f.PtsR[0] = Rf; f.PtsTh[0] = th2;
                    f.PtsR[npts - 1] = r1; f.PtsTh[npts - 1] = Math.Atan2(t1[1], t1[0]);
                    f.T2R = Rf; f.T2Th = th2; f.T1R = r1; f.T1Th = f.PtsTh[npts - 1];
                    return f;
                }
                rho *= 0.8;
            }
            return null;
        }

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
            if (Fil != null && !Internal && Rb <= Rf + 1e-9) lo = Fil.T1R;      // flank starts where the fillet ends
            if (Fil != null && Internal) hi = Fil.T1R;
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
            FilletGeom fil = Fil;
            double hTip = HalfAngle(Ra);
            if (!Internal)
            {
                double h0 = HalfAngle(radii[0]);
                double rootEdge = fil != null ? -fil.T2Th : h0;
                segs.Add(Arc(Rf, tc - pitch + rootEdge, tc - rootEdge));
                Segment lead;
                if (fil != null)
                {
                    segs.Add(FilletSeg(tc, false));
                    bool radialLine = Rb > Rf + 1e-9 && Rb > fil.T1R + 1e-9;
                    if (radialLine) segs.Add(Line(fil.T1R, tc + fil.T1Th, Rb, tc - h0));
                    lead = Flank(radii, tc, -1, false);
                    // the flank starts where the fillet ends only when the fillet meets the involute itself;
                    // with a radial line between them the flank starts at the base circle, where the line ends
                    // (starting it at the fillet end made the spline double the line)
                    if (!radialLine) { lead.Rho[0] = fil.T1R; lead.Theta[0] = tc + fil.T1Th; }
                }
                else
                {
                    if (Rb > Rf + 1e-9) segs.Add(Line(Rf, tc - h0, Rb, tc - h0));
                    lead = Flank(radii, tc, -1, false);
                }
                segs.Add(lead);
                segs.Add(Arc(Ra, tc - hTip, tc + hTip));
                segs.Add(Mirror(lead, tc));
                if (fil != null)
                {
                    if (Rb > Rf + 1e-9 && Rb > fil.T1R + 1e-9) segs.Add(Line(Rb, tc + h0, fil.T1R, tc - fil.T1Th));
                    segs.Add(FilletSeg(tc, true));
                }
                else if (Rb > Rf + 1e-9) segs.Add(Line(Rb, tc + h0, Rf, tc + h0));
            }
            else
            {
                double hf = HalfAngle(Rf);
                double rootEdge = fil != null ? -fil.T2Th : hf;
                segs.Add(Arc(Rf, tc - pitch + rootEdge, tc - rootEdge));
                Segment lead = Flank(radii, tc, -1, true);          // from the root (outer) inwards to the tip
                if (fil != null)
                {
                    segs.Add(FilletSeg(tc, false));
                    lead.Rho[0] = fil.T1R; lead.Theta[0] = tc + fil.T1Th;
                }
                segs.Add(lead);
                segs.Add(Arc(Ra, tc - hTip, tc + hTip));
                segs.Add(Mirror(lead, tc));
                if (fil != null) segs.Add(FilletSeg(tc, true));
            }
            return segs;
        }

        /// <summary>The trailing-flank copy of a leading-flank segment: mirrored about the tooth centre line, reversed.</summary>
        private static Segment Mirror(Segment lead, double tc)
        {
            int n = lead.Rho.Length;
            var seg = new Segment { Kind = lead.Kind, Rho = new double[n], Theta = new double[n] };
            for (int i = 0; i < n; i++) { seg.Rho[i] = lead.Rho[n - 1 - i]; seg.Theta[i] = tc - (lead.Theta[n - 1 - i] - tc); }
            return seg;
        }

        private Segment FilletSeg(double tc, bool mirror)
        {
            FilletGeom f = Fil;
            int n = f.PtsR.Length;
            var seg = new Segment { Kind = SegKind.Fillet, Rho = new double[n], Theta = new double[n], R = f.Rho };
            double c = Math.Cos(tc), s = Math.Sin(tc);
            if (!mirror)
            {
                for (int i = 0; i < n; i++) { seg.Rho[i] = f.PtsR[i]; seg.Theta[i] = tc + f.PtsTh[i]; }
                seg.Cx = f.Cx * c - f.Cy * s; seg.Cy = f.Cx * s + f.Cy * c;
                seg.Ccw = f.Ccw;
            }
            else
            {
                for (int i = 0; i < n; i++) { seg.Rho[i] = f.PtsR[n - 1 - i]; seg.Theta[i] = tc - f.PtsTh[n - 1 - i]; }
                seg.Cx = f.Cx * c + f.Cy * s; seg.Cy = f.Cx * s - f.Cy * c;
                // mirrored AND traversed backwards (T1 -> T2): the two reversals cancel, the turning
                // sense in the segment's own order is the original one (a flipped flag drew every
                // trailing fillet the long way round, a 270 degree loop)
                seg.Ccw = f.Ccw;
            }
            return seg;
        }

        /// <summary>Closed outline as polar points (theta increasing), arcs sampled every arcStepDeg - same as gearset.py.</summary>
        public List<double[]> Polyline(int nFlank, double arcStepDeg)
        {
            var pts = new List<double[]>();
            for (int k = 0; k < Z; k++)
                foreach (Segment seg in ToothSegments(k, nFlank))
                {
                    var p = new List<double[]>();
                    if (seg.Kind == SegKind.Arc)
                    {
                        double t0 = seg.Theta[0], t1 = seg.Theta[1];
                        int n = Math.Max(2, (int)Math.Ceiling((t1 - t0) * 180 / Math.PI / arcStepDeg) + 1);
                        for (int j = 0; j < n; j++) p.Add(new[] { seg.Rho[0], t0 + (t1 - t0) * j / (n - 1) });
                    }
                    else
                        for (int j = 0; j < seg.Rho.Length; j++) p.Add(new[] { seg.Rho[j], seg.Theta[j] });
                    int start = 0;
                    if (pts.Count > 0 && Math.Abs(pts[pts.Count - 1][0] - p[0][0]) < 1e-9 && Math.Abs(pts[pts.Count - 1][1] - p[0][1]) < 1e-9) start = 1;
                    for (int j = start; j < p.Count; j++) pts.Add(p[j]);
                }
            return pts;
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
    // housing with bearings (all mm, absolute Z of the assembly: sun axis = Z, Front Plane = z 0)
    // ------------------------------------------------------------------------------------------------
    public class Bearing
    {
        public string Name; public double Bore, OD, Width;
        // a catalogue part (BearingLibrary in GearBuilder.cs): the .SLDPRT, its configuration ("" = the only one),
        // the axis of its bore in the part's own frame (X, Y or Z) and where the bearing's mid-width lies on that
        // axis (mm from the part origin). Null File = a plain ring the builder makes itself.
        public string File, Config; public char Axis = 'Z'; public double AxisOffset;
        public bool FromCatalogue { get { return !string.IsNullOrEmpty(File); } }
        public string Source
        {
            get
            {
                if (!FromCatalogue) return null;
                string f = System.IO.Path.GetFileNameWithoutExtension(File);
                return string.IsNullOrEmpty(Config) ? f : f + " [" + Config + "]";
            }
        }
        public override string ToString() { return Name + " (" + Bore + " x " + OD + " x " + Width + ")" + (FromCatalogue ? " = " + Source : ""); }

        /// <summary>The ISO 60-series designation of these dimensions (6000..6020), or null.</summary>
        public static string IsoName(double bore, double od, double width)
        {
            foreach (var b in HousingSpec.Series60)
                if (Math.Abs(b.Bore - bore) < 0.05 && Math.Abs(b.OD - od) < 0.05 && Math.Abs(b.Width - width) < 0.05) return b.Name;
            return null;
        }
    }

    public class HousingSpec
    {
        // miniature deep-groove ball bearings 60x (bore 3..9), the reference sizes for catalogue parts below 10 mm
        public static readonly Bearing[] Series60Small =
        {
            new Bearing { Name = "603", Bore = 3, OD = 9, Width = 3 }, new Bearing { Name = "604", Bore = 4, OD = 12, Width = 4 },
            new Bearing { Name = "605", Bore = 5, OD = 14, Width = 5 }, new Bearing { Name = "606", Bore = 6, OD = 17, Width = 6 },
            new Bearing { Name = "607", Bore = 7, OD = 19, Width = 6 }, new Bearing { Name = "608", Bore = 8, OD = 22, Width = 7 },
            new Bearing { Name = "609", Bore = 9, OD = 24, Width = 7 },
        };
        // deep-groove ball bearings 60xx, by bore (ISO 15): bore, OD, width
        public static readonly Bearing[] Series60 =
        {
            new Bearing { Name = "6000", Bore = 10, OD = 26, Width = 8 }, new Bearing { Name = "6001", Bore = 12, OD = 28, Width = 8 },
            new Bearing { Name = "6002", Bore = 15, OD = 32, Width = 9 }, new Bearing { Name = "6003", Bore = 17, OD = 35, Width = 10 },
            new Bearing { Name = "6004", Bore = 20, OD = 42, Width = 12 }, new Bearing { Name = "6005", Bore = 25, OD = 47, Width = 12 },
            new Bearing { Name = "6006", Bore = 30, OD = 55, Width = 13 }, new Bearing { Name = "6007", Bore = 35, OD = 62, Width = 14 },
            new Bearing { Name = "6008", Bore = 40, OD = 68, Width = 15 }, new Bearing { Name = "6009", Bore = 45, OD = 75, Width = 16 },
            new Bearing { Name = "6010", Bore = 50, OD = 80, Width = 16 }, new Bearing { Name = "6011", Bore = 55, OD = 90, Width = 18 },
            new Bearing { Name = "6012", Bore = 60, OD = 95, Width = 18 }, new Bearing { Name = "6013", Bore = 65, OD = 100, Width = 18 },
            new Bearing { Name = "6014", Bore = 70, OD = 110, Width = 20 }, new Bearing { Name = "6015", Bore = 75, OD = 115, Width = 20 },
            new Bearing { Name = "6016", Bore = 80, OD = 125, Width = 22 }, new Bearing { Name = "6018", Bore = 90, OD = 140, Width = 24 },
            new Bearing { Name = "6020", Bore = 100, OD = 150, Width = 24 },
        };

        public static Bearing ForBore(double minBore)
        {
            foreach (var b in Series60) if (b.Bore >= minBore - 1e-9) return b;
            return Series60[Series60.Length - 1];
        }

        /// <summary>The 60-series bearing of (nearly) this bore: what a catalogue part of that bore is compared with.</summary>
        public static Bearing Reference(double bore)
        {
            Bearing best = null;
            foreach (var tbl in new[] { Series60Small, Series60 })
                foreach (var b in tbl)
                    if (best == null || Math.Abs(b.Bore - bore) < Math.Abs(best.Bore - bore) - 1e-9) best = b;
            return best;
        }

        private static bool IsPlain(Bearing b)
        {
            string n = (b.Name ?? "") + " " + (b.Config ?? "");
            return n.IndexOf("ZZ", StringComparison.OrdinalIgnoreCase) < 0 && n.IndexOf("RS", StringComparison.OrdinalIgnoreCase) < 0;
        }

        /// <summary>The catalogue bearing for a shaft: the smallest bore that takes it, and of those the one nearest the
        /// 60-series size (so a 608 is chosen for an 8 mm shaft, not a thin MR 128 or a wide 638); an open bearing before a
        /// shielded one. Without a catalogue, or when nothing in it is large enough, the plain 60xx ring.</summary>
        public static Bearing ForBore(double minBore, IList<Bearing> catalogue)
        {
            if (catalogue == null || catalogue.Count == 0) return ForBore(minBore);
            Bearing best = null; double bestScore = double.MaxValue;
            foreach (var b in catalogue)
            {
                if (b.Bore < minBore - 1e-9) continue;
                Bearing r = Reference(b.Bore);
                double score = 10 * (b.Bore - minBore) + Math.Abs(b.OD - r.OD) + 0.5 * Math.Abs(b.Width - r.Width) + (IsPlain(b) ? 0 : 0.01);
                if (score < bestScore - 1e-9) { bestScore = score; best = b; }
            }
            return best ?? ForBore(minBore);
        }

        /// <summary>The catalogue bearing with exactly these dimensions (an open one first), or null.</summary>
        public static Bearing ForDims(double bore, double od, double width, IList<Bearing> catalogue)
        {
            Bearing best = null;
            if (catalogue == null) return null;
            foreach (var b in catalogue)
            {
                if (Math.Abs(b.Bore - bore) > 0.05 || Math.Abs(b.OD - od) > 0.05 || Math.Abs(b.Width - width) > 0.05) continue;
                if (best == null || (IsPlain(b) && !IsPlain(best))) best = b;
            }
            return best;
        }

        public Bearing SunBearing, HubBearing;
        public double Clearance;                         // radial running clearance of the output cup in the shell
        public double ShellInnerR, ShellOuterR, ShellZ0, ShellZ1, LipInnerR, LipZ0, LipZ1;
        public double FrontZ0, FrontZ1, FrontRecessR, FrontRecessDepth, FrontSeatR, FrontSeatZ0, FrontSeatZ1, FrontLipR;
        public double WebZ0, WebZ1, WebInnerR, HubR, HubZ0, HubZ1, HubBoreR, HubBoreZ0, HubBoreZ1, OutShaftR, OutShaftZ0, OutShaftZ1;
        public double RearZ0, RearZ1, RearSeatR, RearSeatZ0, RearSeatZ1, RearLipR;
        public double BoltCircleR, BoltD; public int Bolts;
        public double SunShaftEndZ;                      // where the sun shaft must end (inside the hub bearing)
        public double SunBearingFrontZ0, SunBearingRearZ0, HubBearingZ0;
        public double OverallLength, OverallOD;

        /// <summary>Lay the housing out around a gear model; with a catalogue, on its bearings (ForBore).</summary>
        public static HousingSpec Create(GearModel g, IList<Bearing> catalogue = null)
        {
            var h = new HousingSpec();
            double m = g.M;
            h.Clearance = Math.Max(0.2, 0.2 * m);
            h.SunBearing = ForBore(g.ShaftD, catalogue);
            double hubMin = Math.Max(3 * g.ShaftD, h.SunBearing.OD + 8 * m);     // hub must carry the sun's rear bearing inside
            h.HubBearing = ForBore(hubMin, catalogue);
            double R = g.RingOuterR;
            double wall = Math.Max(8.0, 8 * m);
            h.ShellInnerR = R + h.Clearance;
            h.ShellOuterR = h.ShellInnerR + wall;
            h.ShellZ0 = g.ZA0;
            // lip between fixed ring and output ring: in the axial gap, clear of the thin ring's teeth
            double thinTipMaxR = g.E + g.ThinExt.Ra;
            h.LipInnerR = Math.Max(R - wall * 0.6, thinTipMaxR + 1.0 * m);
            h.LipZ0 = g.ZA1; h.LipZ1 = g.ZB0 - h.Clearance;
            // output cup: web closes the output ring behind section B, hub runs in the rear bearing
            h.WebZ0 = g.ZB1 + h.Clearance; h.WebZ1 = h.WebZ0 + Math.Max(5.0, 5 * m);
            h.WebInnerR = g.ShaftD / 2 + Math.Max(1.0, 1 * m);
            h.HubR = h.HubBearing.Bore / 2;
            h.HubZ0 = h.WebZ1; h.HubZ1 = h.WebZ1 + h.HubBearing.Width;
            h.HubBoreR = h.SunBearing.OD / 2;
            h.HubBoreZ0 = h.HubZ0; h.HubBoreZ1 = h.HubZ0 + h.SunBearing.Width;
            h.OutShaftR = Math.Max(h.HubR - 5, h.HubR * 0.75);
            h.OutShaftZ0 = h.HubZ1; h.OutShaftZ1 = h.HubZ1 + Math.Max(20.0, 2 * g.FaceWidth);
            h.ShellZ1 = h.WebZ1;
            // rear cover: seat for the hub bearing, lip behind it, through hole for the output shaft
            h.RearZ0 = h.WebZ1; h.RearSeatR = h.HubBearing.OD / 2;
            h.RearSeatZ0 = h.RearZ0; h.RearSeatZ1 = h.RearZ0 + h.HubBearing.Width;
            h.RearLipR = h.OutShaftR + Math.Max(1.0, 1 * m);
            h.RearZ1 = h.RearSeatZ1 + Math.Max(3.0, 3 * m);
            // front cover: seat for the sun bearing from the front, lip towards the gears, shallow recess over the gear faces
            double frontT = h.SunBearing.Width + Math.Max(3.0, 3 * m);
            h.FrontZ1 = g.ZA0; h.FrontZ0 = g.ZA0 - frontT;
            h.FrontSeatR = h.SunBearing.OD / 2; h.FrontSeatZ0 = h.FrontZ0; h.FrontSeatZ1 = h.FrontZ0 + h.SunBearing.Width;
            h.FrontLipR = g.ShaftD / 2 + Math.Max(1.0, 1 * m);
            h.FrontRecessR = g.Fixed.Rf + 0.5 * m; h.FrontRecessDepth = Math.Max(0.5, 0.5 * m);
            h.Bolts = Math.Max(6, g.Holes); h.BoltD = Math.Max(4.0, 4 * m);
            h.BoltCircleR = h.ShellInnerR + wall / 2;
            h.SunBearingFrontZ0 = h.FrontSeatZ0; h.SunBearingRearZ0 = h.HubBoreZ0; h.HubBearingZ0 = h.RearSeatZ0;
            h.SunShaftEndZ = h.HubBoreZ1 - Math.Max(0.5, 0.5 * m);
            h.OverallLength = h.OutShaftZ1 - h.FrontZ0 + g.ShaftIn;
            h.OverallOD = 2 * h.ShellOuterR;
            return h;
        }

        public string Text()
        {
            var sb = new System.Text.StringBuilder();
            string f = "0.##";
            sb.AppendLine("Housing             shell OD " + OverallOD.ToString(f, CultureInfo.InvariantCulture) + " mm, " + Bolts + " x M" + BoltD.ToString("0", CultureInfo.InvariantCulture)
                          + " on " + (2 * BoltCircleR).ToString(f, CultureInfo.InvariantCulture) + " mm, overall length " + OverallLength.ToString(f, CultureInfo.InvariantCulture) + " mm with the input and output shafts");
            sb.AppendLine("Bearings            sun " + SunBearing + " front and inside the output hub, output hub " + HubBearing + " in the rear cover");
            sb.AppendLine("Output              cup on the output ring, hub d " + (2 * HubR).ToString(f, CultureInfo.InvariantCulture) + ", output shaft d " + (2 * OutShaftR).ToString(f, CultureInfo.InvariantCulture)
                          + " x " + (OutShaftZ1 - OutShaftZ0).ToString(f, CultureInfo.InvariantCulture) + " mm");
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------------------------------------------
    // complete model: all outlines, positions, overall dimensions, checks
    // ------------------------------------------------------------------------------------------------
    public class GearModel
    {
        public Design Design;
        public double M, FaceWidth, Gap, Rim, Backlash, ShaftD, PinD, FitClearance, HoleD, FilletMod;
        public int Holes;
        public string Name;
        public HousingSpec Housing;                  // null = gears only

        public GearGeom Sun, Planet, ThinInt, ThinExt, Fixed, Output;
        public double A1, E, RingOuterR, JournalR, PlanetBoreR, CarrierBoreR, HoleCircleR, ShaftIn, ShaftOut;
        public double ZA0, ZA1, ZB0, ZB1, ThinRingLength, ThinRingWall;
        public double ClearFixedTip, ClearFixedRoot, ClearOutTip, ClearOutRoot;
        public List<string> Notes = new List<string>();

        public static GearModel Create(Design d, double module, double faceWidth, double gap, double backlash,
                                       double shaftD, double pinD, double rimModules, int holes, string name, double filletModules = 0.30)
        {
            var g = new GearModel
            {
                Design = d, M = module, Backlash = backlash, Holes = holes, Name = name, FilletMod = filletModules,
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

        /// <summary>Add the housing (front cover, shell, output cup, rear cover, bearings); the sun shaft then ends inside
        /// the hub bearing. With a catalogue of bearing parts the housing is laid out on those (seats to their OD and width).</summary>
        public void AddHousing(IList<Bearing> catalogue = null)
        {
            Housing = HousingSpec.Create(this, catalogue);
            ShaftOut = Housing.SunShaftEndZ - ZB1;
        }

        /// <summary>The gear back with its tip shortened when that tip passes the mate's base-tangent point.</summary>
        private GearGeom TipClear(GearGeom tg, string mate, double cd, double aw, double j, double rf, double phase)
        {
            double limit = Math.Sqrt(tg.Rb * tg.Rb + Math.Pow(cd * Math.Sin(aw), 2));
            double over = tg.Internal ? limit - tg.Ra : tg.Ra - limit;
            if (over <= 1e-9) return tg;
            double delta = over + 0.05 * M;
            Notes.Add(tg.Name + ": tip shortened by " + delta.ToString("0.000", CultureInfo.InvariantCulture) + " mm - it reached "
                + over.ToString("0.000", CultureInfo.InvariantCulture) + " mm past the base-tangent point of the " + mate + " (involute interference)");
            return new GearGeom(tg.Name, tg.Internal, M, tg.Z, tg.Alpha, tg.X, phase, j, rf, delta);
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
            double j = Backlash / 2, rf = FilletMod * m;
            Sun = new GearGeom("Sun", false, m, d.Zs, a, 0, phS, j, rf);
            Planet = new GearGeom("Planet", false, m, d.Zp, a, 0, phP, j, rf);
            ThinInt = new GearGeom("ThinRing internal", true, m, d.Zr1, a, 0, phTi, j, rf);
            ThinExt = new GearGeom("ThinRing external", false, m, d.Zr2, a, 0, phTe, j, rf);
            Fixed = new GearGeom("FixedRing", true, m, d.Zf, a, d.XFixed, phF, j, rf);
            Output = new GearGeom("OutputRing", true, m, d.Zo, a, d.XOutput, phO, j, rf);

            // involute (tip) interference: on the line of action a tooth tip must not pass the mate's base-tangent
            // point, else it digs into the mate's flank below the base circle (the 16/18/96 set: the thin ring's
            // internal tips 36 um past the planet's base circle, 40 um of penetration at some phases). The tip that
            // passes it is taken back by the overshoot plus 0.05 m. External pairs: either tip; internal pairs: the ring's.
            double awF = d.OutputShifted ? a : d.Aw, awO = d.OutputShifted ? d.Aw : a;
            double rp = m * d.Zp / 2;
            Sun = TipClear(Sun, "planet", A1, a, j, rf, phS);
            Planet = TipClear(Planet, "sun", A1, a, j, rf, phP);
            ThinInt = TipClear(ThinInt, "planet", ThinInt.R - rp, a, j, rf, phTi);
            Fixed = TipClear(Fixed, "thin ring", E, awF, j, rf, phF);
            Output = TipClear(Output, "thin ring", E, awO, j, rf, phO);

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
            sb.AppendLine("Root fillets        " + (Sun.Fil == null ? "none" : "r = " + (FilletMod * M).ToString("0.###", CultureInfo.InvariantCulture) + " mm (" + FilletMod.ToString("0.##", CultureInfo.InvariantCulture) + " x module), reduced where a space is too narrow"));
            if (Housing != null) sb.Append(Housing.Text());
            foreach (string n in Notes) sb.AppendLine("WARNING  " + n);
            return sb.ToString();
        }
    }
}
