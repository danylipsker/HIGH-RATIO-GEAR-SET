#!/usr/bin/env python3
"""
gearset.py - parametric generator for a 3K ("Wolfrom"-type) compound planetary
reducer with a single planet and a floating thin ring gear.

    sun (input)  ->  one planet on a free carrier  ->  thin ring (internal teeth)
    thin ring (external teeth)  ->  fixed ring  (housing)      \
                                ->  output ring (differs by a few teeth)

Ratio (exact, rational):

    i = Zo * (Zf*Zr1 + Zs*Zr2) / ( Zs * Zr2 * (Zo - Zf) )

Geometric closure (one module everywhere, standard centre distances):

    e  = m/2 * (Zr1 - Zs - 2*Zp)        eccentricity of the thin ring
    Zstd = Zr2 + (Zr1 - Zs - 2*Zp)      ring that meshes at standard centre distance
    the other ring (Zstd +/- delta) is profile shifted to the same centre distance.

Usage examples
    python gearset.py --ratio 500
    python gearset.py --ratio 500 --module 1.5
    python gearset.py --ratio 500 --max-od 120
    python gearset.py --ratio 350 --sun 16 --planet 32 --list 15
    python gearset.py --ratio 500 --set 18 23 108 123 167 164 --module 1

Everything is standard-library Python (numpy is optional, used only for the
independent kinematic check and the STL export).
"""
from __future__ import annotations

import argparse
import json
import math
import os
import struct
import sys
from dataclasses import dataclass, field, asdict
from fractions import Fraction
from typing import List, Tuple, Optional

try:
    import numpy as np
except Exception:  # pragma: no cover
    np = None

TAU = 2 * math.pi
STANDARD_MODULES = [0.5, 0.6, 0.7, 0.8, 0.9, 1.0, 1.125, 1.25, 1.375, 1.5, 1.75, 2.0,
                    2.25, 2.5, 2.75, 3.0, 3.5, 4.0, 4.5, 5.0, 5.5, 6.0, 7.0, 8.0, 9.0, 10.0]


# ----------------------------------------------------------------------------- helpers
def inv(a: float) -> float:
    """Involute function."""
    return math.tan(a) - a


def deg(a: float) -> float:
    return math.degrees(a)


def rad(a: float) -> float:
    return math.radians(a)


def mod(a: float, p: float) -> float:
    return a - math.floor(a / p) * p


# ----------------------------------------------------------------------------- design
@dataclass
class Limits:
    """Search ranges and design rules (all tooth differences in teeth, lengths in modules)."""
    sun_min: int = 12
    sun_max: int = 30
    planet_min: int = 12
    planet_max: int = 80
    ecc_min: int = 10          # Zr1 - Zs - 2Zp  (= Zf - Zr2): tooth difference of the eccentric internal meshes
    ecc_max: int = 70
    wall_min_mod: float = 1.5  # radial wall of the thin ring between internal and external tooth roots, in modules
    wall_max_teeth: int = 30   # Zr2 - Zr1 upper bound
    delta_max: int = 3         # |Zo - Zf|
    aw_min_deg: float = 14.0   # working pressure angle of the shifted ring mesh
    aw_max_deg: float = 31.0
    pressure_angle_deg: float = 20.0
    addendum: float = 1.0      # ha* (modules)
    clearance: float = 0.25    # c*  (modules)


@dataclass
class Design:
    Zs: int
    Zp: int
    Zr1: int
    Zr2: int
    Zf: int
    Zo: int
    shifted: str                     # "output" or "fixed": which ring is profile shifted
    alpha_deg: float = 20.0
    addendum: float = 1.0
    clearance: float = 0.25

    # derived -----------------------------------------------------------------
    @property
    def D(self) -> int:
        return self.Zr1 - self.Zs - 2 * self.Zp

    @property
    def ratio(self) -> Fraction:
        return Fraction(self.Zo * (self.Zf * self.Zr1 + self.Zs * self.Zr2),
                        self.Zs * self.Zr2 * (self.Zo - self.Zf))

    @property
    def Zshifted(self) -> int:
        return self.Zo if self.shifted == "output" else self.Zf

    @property
    def Zstd(self) -> int:
        return self.Zf if self.shifted == "output" else self.Zo

    @property
    def alpha(self) -> float:
        return rad(self.alpha_deg)

    @property
    def aw(self) -> float:
        """Working pressure angle of the shifted ring / thin-ring external mesh."""
        c = math.cos(self.alpha) * (self.Zshifted - self.Zr2) / self.D
        return math.acos(c)

    @property
    def x_shift(self) -> float:
        """Profile shift coefficient of the shifted ring (thin ring external teeth unshifted)."""
        return (inv(self.aw) - inv(self.alpha)) * (self.Zshifted - self.Zr2) / (2 * math.tan(self.alpha))

    @property
    def x_fixed(self) -> float:
        return self.x_shift if self.shifted == "fixed" else 0.0

    @property
    def x_output(self) -> float:
        return self.x_shift if self.shifted == "output" else 0.0

    def valid(self, lim: Limits) -> Tuple[bool, str]:
        if self.D < 1:
            return False, "thin ring internal gear too small for the sun/planet (Zr1 <= Zs + 2Zp)"
        if self.Zstd != self.Zr2 + self.D:
            return False, "standard ring must have Zr2 + (Zr1 - Zs - 2Zp) teeth"
        c = math.cos(self.alpha) * (self.Zshifted - self.Zr2) / self.D
        if not (-1 < c < 1):
            return False, "shifted ring cannot reach the thin ring centre distance"
        awd = deg(math.acos(c))
        if not (lim.aw_min_deg <= awd <= lim.aw_max_deg):
            return False, f"working pressure angle {awd:.1f} deg outside [{lim.aw_min_deg}, {lim.aw_max_deg}]"
        wall = (self.Zr2 - self.Zr1) / 2 - (self.addendum + self.clearance) * 2
        if wall < lim.wall_min_mod:
            return False, f"thin ring wall {wall:.2f} m below {lim.wall_min_mod} m"
        if self.Zshifted - self.Zr2 < lim.ecc_min - lim.delta_max:
            return False, "shifted ring / thin ring tooth difference too small"
        return True, "ok"


def search(target: Fraction, lim: Limits, prefer: dict, exact_only: bool = True,
           max_results: int = 20) -> List[Tuple[float, Design]]:
    """Enumerate tooth sets; return list of (score, Design) sorted best first.
    Integer arithmetic only in the hot loop; Design objects are built for hits only."""
    alpha = rad(lim.pressure_angle_deg)
    cmin, cmax = math.cos(rad(lim.aw_max_deg)), math.cos(rad(lim.aw_min_deg))
    wall_teeth_min = math.ceil(2 * (lim.wall_min_mod + 2 * (lim.addendum + lim.clearance)) - 1e-9)
    tgt = abs(target)
    tn, td = tgt.numerator, tgt.denominator
    ca = math.cos(alpha)
    hits = []          # (err, Zs, Zp, Zr1, Zr2, Zf, Zo, shifted)
    err_window = 0.05
    deltas = [d for d in range(-lim.delta_max, lim.delta_max + 1) if d]
    suns = [prefer["sun"]] if prefer.get("sun") else range(lim.sun_min, lim.sun_max + 1)
    planets = [prefer["planet"]] if prefer.get("planet") else range(lim.planet_min, lim.planet_max + 1)
    for Zs in suns:
        for Zp in planets:
            base = Zs + 2 * Zp
            for D in range(lim.ecc_min, lim.ecc_max + 1):
                Zr1 = base + D
                # admissible deltas for this D (working pressure angle window)
                dd = [d for d in deltas if cmin <= ca * (D + d) / D <= cmax]
                if not dd:
                    continue
                for W in range(wall_teeth_min, lim.wall_max_teeth + 1):
                    Zr2 = Zr1 + W
                    Zstd = Zr2 + D
                    for d in dd:
                        Zsh = Zstd + d
                        for shifted in ("output", "fixed"):
                            Zf, Zo = (Zstd, Zsh) if shifted == "output" else (Zsh, Zstd)
                            num = Zo * (Zf * Zr1 + Zs * Zr2)
                            den = Zs * Zr2 * (Zo - Zf)
                            num, den = abs(num), abs(den)       # compare magnitudes; sign = direction
                            if exact_only:
                                if num * td != tn * den:
                                    continue
                                err = 0.0
                            else:
                                err = abs(num * td - tn * den) / (tn * den)
                                if err > err_window:
                                    continue
                            hits.append((err, Zs, Zp, Zr1, Zr2, Zf, Zo, shifted))
    if not hits and not exact_only:
        # nothing within 5 %: keep the closest few hundred whatever their error
        best = []
        for Zs in suns:
            for Zp in planets:
                base = Zs + 2 * Zp
                for D in range(lim.ecc_min, lim.ecc_max + 1):
                    Zr1 = base + D
                    dd = [d for d in deltas if cmin <= ca * (D + d) / D <= cmax]
                    for W in range(wall_teeth_min, lim.wall_max_teeth + 1):
                        Zr2 = Zr1 + W
                        Zstd = Zr2 + D
                        for d in dd:
                            Zsh = Zstd + d
                            for shifted in ("output", "fixed"):
                                Zf, Zo = (Zstd, Zsh) if shifted == "output" else (Zsh, Zstd)
                                num = abs(Zo * (Zf * Zr1 + Zs * Zr2)); den = abs(Zs * Zr2 * (Zo - Zf))
                                err = abs(num * td - tn * den) / (tn * den)
                                best.append((err, Zs, Zp, Zr1, Zr2, Zf, Zo, shifted))
                        if len(best) > 5000:
                            best.sort(); del best[300:]
        best.sort()
        hits = best[:300]
    out = []
    for err, Zs, Zp, Zr1, Zr2, Zf, Zo, shifted in hits:
        des = Design(Zs, Zp, Zr1, Zr2, Zf, Zo, shifted, lim.pressure_angle_deg, lim.addendum, lim.clearance)
        out.append((score(des, err, prefer), des))
    out.sort(key=lambda t: t[0])
    return out[:max_results]


def score(des: Design, err: float, prefer: dict) -> float:
    """Lower is better: ratio error, then working pressure angle, tooth difference of the rings,
    undercut-free pinions, closeness to the preferred tooth counts and compactness."""
    s = err * 1e4
    s += max(0.0, deg(des.aw) - 27.0) * 2.0         # high working pressure angle of the shifted mesh
    s += max(0.0, 20.0 - deg(des.aw)) * 1.0         # very low working pressure angle
    s += (abs(des.Zo - des.Zf) - 1) * 1.0           # small tooth difference between the rings preferred
    s += max(0, 17 - des.Zs) * 1.0                  # undercut below 17 teeth at 20 deg
    s += max(0, 17 - des.Zp) * 1.0
    s += abs(des.Zs - prefer.get("sun_soft", 16)) * 0.6
    s += abs(des.Zp - prefer.get("planet_soft", 32)) * 0.3
    s += abs(des.Zr1 - prefer.get("ring_int_soft", 99)) * 0.08
    s += abs(des.Zr2 - prefer.get("ring_ext_soft", 100)) * 0.08
    s += des.Zf * 0.03                              # compactness
    return s


# ----------------------------------------------------------------------------- geometry
@dataclass
class GearGeom:
    """Everything needed to draw one toothed outline (lengths in mm, angles rad)."""
    name: str
    kind: str            # "external" | "internal"
    m: float
    Z: int
    alpha: float
    x: float
    phase: float         # angle of the first tooth centre
    thinning: float = 0.0  # tooth thickness reduction at the reference circle (mm) = half the mesh backlash
    fillet: float = 0.0  # requested root fillet radius (mm); 0 = sharp root corner
    r: float = 0.0       # reference radius
    rb: float = 0.0      # base radius
    ra: float = 0.0      # tip radius (internal: inner)
    rf: float = 0.0      # root radius (internal: outer)
    half_ref: float = 0.0  # tooth half angle at the reference circle

    def __post_init__(self):
        m, Z, a, x = self.m, self.Z, self.alpha, self.x
        self.r = m * Z / 2
        self.rb = self.r * math.cos(a)
        ha, c = 1.0, 0.25
        if self.kind == "external":
            self.ra = self.r + m * (ha + x)
            self.rf = self.r - m * (ha + c - x)
            s = m * (math.pi / 2 + 2 * x * math.tan(a))          # tooth thickness
        else:
            self.ra = self.r - m * (ha - x)
            self.rf = self.r + m * (ha + c + x)
            s = m * (math.pi / 2 - 2 * x * math.tan(a))          # internal tooth thickness
        self.half_ref = (s - self.thinning) / (2 * self.r)
        self.fil = self._compute_fillet()

    # ---- root fillet -------------------------------------------------------------------------
    def _flank_point(self, rho: float):
        """Leading flank point (tooth centre at angle 0), Cartesian."""
        t = -self.half_angle(rho)
        return (rho * math.cos(t), rho * math.sin(t))

    def _compute_fillet(self):
        """Circular fillet tangent to the root circle and to the leading flank (or its radial root line).
        Returns None or dict(rho, c=(cx,cy), t2=(r,theta) on the root circle, t1=(r,theta) on the flank,
        ccw=True if the arc from t2 to t1 runs counter-clockwise about c, pts=[(rho,theta)...] t2->t1)."""
        if self.fillet <= 0:
            return None
        rf, rb, ra, ext = self.rf, self.rb, self.ra, self.kind == "external"
        pitch = TAU / self.Z
        if ext and rb > rf + 1e-9:
            th0 = -self.half_angle(rb)                     # radial line from rf to rb at this angle
            p0 = (rf * math.cos(th0), rf * math.sin(th0))
            d = (math.cos(th0), math.sin(th0))
        else:
            th0 = -self.half_angle(rf)
            p0 = (rf * math.cos(th0), rf * math.sin(th0))
            eps = 1e-4 * self.m
            q = self._flank_point(rf + eps) if ext else self._flank_point(rf - eps)
            d = (q[0] - p0[0], q[1] - p0[1])
            ln = math.hypot(*d); d = (d[0] / ln, d[1] / ln)
        e_space = (math.sin(th0), -math.cos(th0))         # towards the tooth space (decreasing angle)
        n = (-d[1], d[0])
        if n[0] * e_space[0] + n[1] * e_space[1] < 0:
            n = (-n[0], -n[1])
        rho = self.fillet
        for _ in range(40):
            if rho < 0.02 * self.m:
                return None
            R = rf + rho if ext else rf - rho
            qx, qy = p0[0] + rho * n[0], p0[1] + rho * n[1]
            b = qx * d[0] + qy * d[1]
            c = qx * qx + qy * qy - R * R
            disc = b * b - c
            ok = disc >= 0
            if ok:
                roots = [-b - math.sqrt(disc), -b + math.sqrt(disc)]
                roots = [x for x in roots if x >= -1e-12]
                ok = bool(roots)
            if ok:
                sdist = min(roots)
                cx, cy = qx + sdist * d[0], qy + sdist * d[1]
                t1 = (p0[0] + sdist * d[0], p0[1] + sdist * d[1])
                cl = math.hypot(cx, cy)
                t2 = (cx / cl * rf, cy / cl * rf)
                r1 = math.hypot(*t1)
                th2 = math.atan2(t2[1], t2[0])
                if ext:
                    ok = (r1 <= rb + 1e-9 if rb > rf + 1e-9 else r1 <= ra - 0.5 * self.m) and th2 > -pitch / 2 + 1e-6
                else:
                    ok = r1 >= ra + 0.3 * self.m and th2 > -pitch / 2 + 1e-6
            if ok:
                a2 = math.atan2(t2[1] - cy, t2[0] - cx)
                a1 = math.atan2(t1[1] - cy, t1[0] - cx)
                da = (a1 - a2 + math.pi) % TAU - math.pi             # signed sweep, |da| <= pi
                npts = 7
                pts = []
                for k in range(npts):
                    a = a2 + da * k / (npts - 1)
                    x, y = cx + rho * math.cos(a), cy + rho * math.sin(a)
                    pts.append((math.hypot(x, y), math.atan2(y, x)))
                pts[0] = (rf, th2)
                pts[-1] = (r1, math.atan2(t1[1], t1[0]))
                return {"rho": rho, "c": (cx, cy), "t2": pts[0], "t1": pts[-1], "ccw": da > 0, "pts": pts,
                        "a_start": a2, "a_end": a1}
            rho *= 0.8
        return None

    def half_angle(self, rho: float) -> float:
        """Half tooth angle at radius rho (involute part)."""
        a_rho = math.acos(min(1.0, self.rb / rho))
        if self.kind == "external":
            return self.half_ref + inv(self.alpha) - inv(a_rho)
        return self.half_ref - inv(self.alpha) + inv(a_rho)

    def flank_radii(self, n: int) -> List[float]:
        if self.kind == "external":
            lo, hi = max(self.rb, self.rf), self.ra
            if self.fil and self.rb <= self.rf + 1e-9:
                lo = self.fil["t1"][0]                       # flank starts where the fillet ends
        else:
            lo, hi = max(self.rb, self.ra), self.rf
            if self.fil:
                hi = self.fil["t1"][0]
        # denser near the base circle where curvature is highest
        return [lo + (hi - lo) * (k / (n - 1)) ** 1.3 for k in range(n)]

    def tooth_segments(self, k: int, n_flank: int = 12):
        """Segments of tooth k going counter-clockwise.
        Returns list of (kind, points[, meta]) with kind in 'line' | 'arc' (about the gear centre) |
        'spline' | 'fillet' (arc about meta centre); points are polar (rho, theta) about the gear centre."""
        tc = self.phase + k * TAU / self.Z
        pitch = TAU / self.Z
        radii = self.flank_radii(n_flank)
        fil = self.fil
        segs = []

        def rot(pts, mirror=False):
            if mirror:
                return [(r, tc - t) for r, t in reversed(pts)]
            return [(r, tc + t) for r, t in pts]

        def fillet_seg(mirror):
            # meta: centre (gear coords, rotated), radius, ccw flag of the traversal t2->t1 (mirrored: t1->t2)
            cx, cy = fil["c"]
            if mirror:
                cxr, cyr = cx * math.cos(tc) + cy * math.sin(tc), cx * math.sin(tc) - cy * math.cos(tc)
                ccw = not fil["ccw"]
            else:
                cxr, cyr = cx * math.cos(tc) - cy * math.sin(tc), cx * math.sin(tc) + cy * math.cos(tc)
                ccw = fil["ccw"]
            return ("fillet", rot(fil["pts"], mirror), {"c": (cxr, cyr), "r": fil["rho"], "ccw": ccw})

        if self.kind == "external":
            h0 = self.half_angle(radii[0])
            ha_ = self.half_angle(self.ra)
            root_edge = -fil["t2"][1] if fil else h0        # half angle of the root land ends
            segs.append(("arc", [(self.rf, tc - pitch + root_edge), (self.rf, tc - root_edge)]))
            if fil:
                segs.append(fillet_seg(False))
                r1, th1 = fil["t1"]
                if self.rb > self.rf + 1e-9 and self.rb > r1 + 1e-9:
                    segs.append(("line", [(r1, tc + th1), (self.rb, tc - h0)]))
                lead = [(r1, tc + th1)] + [(rho, tc - self.half_angle(rho)) for rho in radii[1:]]
            else:
                if self.rb > self.rf + 1e-9:
                    segs.append(("line", [(self.rf, tc - h0), (self.rb, tc - h0)]))
                lead = [(rho, tc - self.half_angle(rho)) for rho in radii]
            segs.append(("spline", lead))
            segs.append(("arc", [(self.ra, tc - ha_), (self.ra, tc + ha_)]))
            trail = [(r, tc - (t - tc)) for r, t in reversed(lead)]
            segs.append(("spline", trail))
            if fil:
                r1, th1 = fil["t1"]
                if self.rb > self.rf + 1e-9 and self.rb > r1 + 1e-9:
                    segs.append(("line", [(self.rb, tc + h0), (r1, tc - th1)]))
                segs.append(fillet_seg(True))
            else:
                if self.rb > self.rf + 1e-9:
                    segs.append(("line", [(self.rb, tc + h0), (self.rf, tc + h0)]))
        else:
            hf = self.half_angle(self.rf)
            ha_ = self.half_angle(self.ra)
            root_edge = -fil["t2"][1] if fil else hf
            segs.append(("arc", [(self.rf, tc - pitch + root_edge), (self.rf, tc - root_edge)]))
            if fil:
                segs.append(fillet_seg(False))
                r1, th1 = fil["t1"]
                lead = [(r1, tc + th1)] + [(rho, tc - self.half_angle(rho)) for rho in reversed(radii[:-1])]
            else:
                lead = [(rho, tc - self.half_angle(rho)) for rho in reversed(radii)]
            segs.append(("spline", lead))
            segs.append(("arc", [(self.ra, tc - ha_), (self.ra, tc + ha_)]))
            trail = [(r, tc - (t - tc)) for r, t in reversed(lead)]
            segs.append(("spline", trail))
            if fil:
                segs.append(fillet_seg(True))
        return segs

    def check(self) -> List[str]:
        msgs = []
        pitch = TAU / self.Z
        if self.kind == "external":
            h0 = self.half_angle(max(self.rb, self.rf))
            if 2 * h0 >= pitch:
                msgs.append(f"{self.name}: teeth overlap at the root (no root land)")
            if self.half_angle(self.ra) <= 0:
                msgs.append(f"{self.name}: pointed teeth (tip land <= 0)")
        else:
            if self.ra < self.rb:
                msgs.append(f"{self.name}: internal tip circle inside base circle (undercut flank)")
            if 2 * self.half_angle(self.rf) >= pitch:
                msgs.append(f"{self.name}: internal teeth overlap at the root")
        return msgs

    def polyline(self, n_flank: int = 12, arc_step_deg: float = 1.0) -> List[Tuple[float, float]]:
        """Closed outline as polar points (rho, theta), theta increasing."""
        pts: List[Tuple[float, float]] = []
        for k in range(self.Z):
            for seg_ in self.tooth_segments(k, n_flank):
                kind, p = seg_[0], seg_[1]
                if kind == "arc":
                    (r0, t0), (r1, t1) = p
                    n = max(2, int(math.ceil(deg(t1 - t0) / arc_step_deg)) + 1)
                    seg = [(r0, t0 + (t1 - t0) * j / (n - 1)) for j in range(n)]
                else:
                    seg = p
                if pts and abs(pts[-1][0] - seg[0][0]) < 1e-9 and abs(pts[-1][1] - seg[0][1]) < 1e-9:
                    seg = seg[1:]
                pts.extend(seg)
        return pts


def polar_to_xy(pts, cx=0.0, cy=0.0):
    return [(cx + r * math.cos(t), cy + r * math.sin(t)) for r, t in pts]


def phase_for(Z: int, angle: float, want: str) -> float:
    """Phase (first tooth centre angle) so that direction `angle` shows a tooth or a space centre."""
    pitch = TAU / Z
    if want == "tooth":
        return mod(angle, pitch)
    return mod(angle - pitch / 2, pitch)


def tooth_or_space(Z: int, phase: float, angle: float) -> str:
    pitch = TAU / Z
    d = mod(angle - phase, pitch)
    if min(d, pitch - d) < 1e-9:
        return "tooth"
    if abs(d - pitch / 2) < 1e-9:
        return "space"
    raise ValueError("direction is neither a tooth nor a space centre")


def complement(ts: str) -> str:
    return "space" if ts == "tooth" else "tooth"


# ----------------------------------------------------------------------------- full model
@dataclass
class Model:
    des: Design
    m: float
    face_width: float          # b, mm (one mesh section)
    gap: float                 # axial gap between the two sections, mm
    rim: float                 # radial rim of the fixed/output rings beyond the tooth root, mm
    n_holes: int
    hole_d: float
    shaft_d: float             # sun shaft
    pin_d: float               # planet pin
    fit_clearance: float       # radial clearance pin/bore, journal/thin ring
    backlash: float = 0.0      # circumferential backlash per mesh, mm
    fillet_mod: float = 0.30   # root fillet radius in modules (0 = none); 0.38 (ISO rack) grazes ring tips without tip relief
    name: str = "gearset"
    gears: dict = field(default_factory=dict)
    layout: dict = field(default_factory=dict)
    notes: List[str] = field(default_factory=list)
    mesh_check: dict = field(default_factory=dict)

    def build(self):
        d, m = self.des, self.m
        a = d.alpha
        Zs, Zp, Zr1, Zr2, Zf, Zo = d.Zs, d.Zp, d.Zr1, d.Zr2, d.Zf, d.Zo
        a1 = m * (Zs + Zp) / 2                 # sun-planet centre distance
        e = m * d.D / 2                        # eccentricity of the thin ring (centre at (-e, 0))
        rp = m * Zp / 2
        # -- phases: all meshes lie on the x axis -------------------------------
        ph_s = 0.0                                                # sun tooth on +x
        ph_p = phase_for(Zp, math.pi, "space")                    # planet space towards the sun
        at0 = tooth_or_space(Zp, ph_p, 0.0)                       # planet shows ... towards +x
        ph_t_int = phase_for(Zr1, 0.0, complement(at0))           # thin ring internal teeth at +x from C
        ph_t_ext = 0.0
        atpi = tooth_or_space(Zr2, ph_t_ext, math.pi)             # thin ring external at -x from C
        ph_f = phase_for(Zf, math.pi, complement(atpi))
        ph_o = phase_for(Zo, math.pi, complement(atpi))
        j = self.backlash / 2
        rf_ = self.fillet_mod * m
        self.gears = {
            "sun": GearGeom("sun", "external", m, Zs, a, 0.0, ph_s, j, rf_),
            "planet": GearGeom("planet", "external", m, Zp, a, 0.0, ph_p, j, rf_),
            "thin_ring_internal": GearGeom("thin_ring_internal", "internal", m, Zr1, a, 0.0, ph_t_int, j, rf_),
            "thin_ring_external": GearGeom("thin_ring_external", "external", m, Zr2, a, 0.0, ph_t_ext, j, rf_),
            "fixed_ring": GearGeom("fixed_ring", "internal", m, Zf, a, d.x_fixed, ph_f, j, rf_),
            "output_ring": GearGeom("output_ring", "internal", m, Zo, a, d.x_output, ph_o, j, rf_),
        }
        g = self.gears
        b, gap = self.face_width, self.gap
        ring_outer_r = max(g["fixed_ring"].rf, g["output_ring"].rf) + self.rim
        journal_r = g["thin_ring_internal"].rf - self.fit_clearance
        L_T = 2 * b + gap
        self.layout = {
            "a1": a1, "e": e,
            "sun_pos": (0.0, 0.0, 0.0),
            "planet_pos": (a1, 0.0, 0.0),
            "thin_ring_pos": (-e, 0.0, 0.0),
            "carrier_pos": (0.0, 0.0, 0.0),
            "fixed_ring_pos": (0.0, 0.0, 0.0),
            "output_ring_pos": (0.0, 0.0, b + gap),
            "section_A": (0.0, b),
            "section_B": (b + gap, 2 * b + gap),
            "thin_ring_length": L_T,
            "ring_outer_r": ring_outer_r,
            "journal_r": journal_r,
            "journal_centre": (-e, 0.0),
            "planet_bore_r": self.pin_d / 2 + self.fit_clearance,
            "carrier_bore_r": self.shaft_d / 2 + self.fit_clearance,
            "shaft_r": self.shaft_d / 2,
            "pin_r": self.pin_d / 2,
            "shaft_in": 2 * b,          # input stub beyond z = 0
            "shaft_out": b,             # stub beyond the carrier
            "hole_circle_r": ring_outer_r - self.rim / 2,
        }
        # -- checks -------------------------------------------------------------
        for gg in g.values():
            self.notes += gg.check()
        if journal_r <= e + self.layout["carrier_bore_r"] + 1.0:
            self.notes.append("carrier journal too small to clear the sun shaft bore")
        if journal_r + self.fit_clearance < math.hypot(a1 + e, 0) + self.pin_d / 2:
            self.notes.append("planet pin lies outside the carrier journal")
        if g["sun"].ra + self.fit_clearance > a1 - rp + g["planet"].rf - g["planet"].ra:  # trivial sanity
            pass
        # clearances of the shifted mesh (thin ring external vs rings), on the line of centres
        for key in ("fixed_ring", "output_ring"):
            ring = g[key]
            te = g["thin_ring_external"]
            c_tip = (ring.ra - e) - te.rf      # ring tip vs thin ring root (far side)
            c_root = (ring.rf - e) - te.ra     # ring root vs thin ring tip
            self.layout[f"{key}_tip_clearance"] = c_tip
            self.layout[f"{key}_root_clearance"] = c_root
            if c_tip < 0.05 * m or c_root < 0.05 * m:
                self.notes.append(f"{key}: radial clearance to the thin ring too small ({c_tip:.3f}/{c_root:.3f} mm)")
        self.layout["thin_ring_wall"] = g["thin_ring_external"].rf - g["thin_ring_internal"].rf
        return self

    # --- analysis ---------------------------------------------------------------
    def contact_ratios(self) -> dict:
        d, m, g = self.des, self.m, self.gears
        a = d.alpha
        pb = math.pi * m * math.cos(a)

        def ext_ext(g1, g2, aw):
            cd = (g1.r + g2.r) * math.cos(a) / math.cos(aw)
            return (math.sqrt(g1.ra ** 2 - g1.rb ** 2) + math.sqrt(g2.ra ** 2 - g2.rb ** 2) - cd * math.sin(aw)) / pb

        def ext_int(g1, g2, aw):
            cd = (g2.r - g1.r) * math.cos(a) / math.cos(aw)
            return (math.sqrt(g1.ra ** 2 - g1.rb ** 2) - math.sqrt(g2.ra ** 2 - g2.rb ** 2) + cd * math.sin(aw)) / pb

        aw_f = d.aw if d.shifted == "fixed" else a
        aw_o = d.aw if d.shifted == "output" else a
        return {
            "sun/planet": ext_ext(g["sun"], g["planet"], a),
            "planet/thin ring internal": ext_int(g["planet"], g["thin_ring_internal"], a),
            "thin ring external/fixed ring": ext_int(g["thin_ring_external"], g["fixed_ring"], aw_f),
            "thin ring external/output ring": ext_int(g["thin_ring_external"], g["output_ring"], aw_o),
        }

    def kinematics_numeric(self) -> Optional[dict]:
        """Independent check: pitch-point velocity compatibility in the fixed frame (needs numpy)."""
        if np is None:
            return None
        d, m, g = self.des, self.m, self.gears
        rs, rp = g["sun"].r, g["planet"].r
        Rr1, Rr2 = g["thin_ring_internal"].r, g["thin_ring_external"].r
        a1, e = self.layout["a1"], self.layout["e"]
        # working pitch radii of the thin ring / output ring mesh at centre distance e
        RwT = e * d.Zr2 / (d.Zo - d.Zr2)
        RwO = e * d.Zo / (d.Zo - d.Zr2)
        # fixed ring mesh at centre distance e as well
        RwTf = e * d.Zr2 / (d.Zf - d.Zr2)
        # unknowns: wc, wp, wt, wo  (ws = 1, wf = 0); y-velocities at the contact points
        A = np.array([
            [a1, -rp, 0.0, 0.0],              # sun/planet at (rs,0):      ws*rs = wc*a1 - wp*rp
            [a1 + e, rp, -Rr1, 0.0],          # planet/thin ring at (a1+rp,0): wc*a1 + wp*rp = -wc*e + wt*Rr1
            [-e, 0.0, -RwTf, 0.0],            # thin ring/fixed ring at (-Rf,0): -wc*e - wt*RwTf = 0
            [-e, 0.0, -RwT, RwO],             # thin ring/output ring: -wc*e - wt*RwT = -wo*RwO
        ], dtype=float)
        bvec = np.array([rs, 0.0, 0.0, 0.0])
        wc, wp, wt, wo = np.linalg.solve(A, bvec)
        return {"carrier": wc, "planet": wp, "thin_ring": wt, "output": wo, "ratio": 1.0 / wo}


# ----------------------------------------------------------------------------- mesh verification
def mesh_penetration(gA: GearGeom, cA, phiA: float, gB: GearGeom, cB, phiB: float, n_flank=16) -> float:
    """Largest penetration (mm) of gear B's material into gear A's material (and vice versa),
    with each outline rotated by phi about its own centre. 0 = no interference. Needs numpy."""
    def polar_arrays(g):
        p = g.polyline(n_flank=n_flank, arc_step_deg=0.5)
        th = np.array([t for _, t in p]); rr = np.array([r for r, _ in p])
        th = np.concatenate([th, [th[0] + TAU]]); rr = np.concatenate([rr, [rr[0]]])
        for i in range(1, len(th)):
            if th[i] <= th[i - 1]:
                th[i] = th[i - 1] + 1e-9
        return th, rr

    thA, rA = polar_arrays(gA)
    thB, rB = polar_arrays(gB)

    def radius_of(th, rr, query):
        return np.interp(np.mod(query - th[0], TAU) + th[0], th, rr)

    def one_way(thS, rS, cS, phiS, gS_kind, thT, rT, cT, phiT, gT_kind):
        # points of the source outline S, expressed in the target gear's polar frame
        x = cS[0] + rS * np.cos(thS + phiS); y = cS[1] + rS * np.sin(thS + phiS)
        dx, dy = x - cT[0], y - cT[1]
        rho = np.hypot(dx, dy); ang = np.arctan2(dy, dx) - phiT
        Rt = radius_of(thT, rT, ang)
        if gT_kind == "external":
            pen = Rt - rho              # inside the external gear's material if rho < R(theta)
        else:
            pen = rho - Rt              # inside the internal gear's material if rho > R(theta)
        # only points radially within the target's tooth band matter
        lo = min(gA.rf, gA.ra) if gT_kind == gA.kind and thT is thA else None
        return float(np.max(pen))

    pAB = one_way(thB, rB, cB, phiB, gB.kind, thA, rA, cA, phiA, gA.kind)
    pBA = one_way(thA, rA, cA, phiA, gA.kind, thB, rB, cB, phiB, gB.kind)
    return max(pAB, pBA)


def verify_meshing(model: Model, steps: int = 24, pitches: float = 2.0) -> dict:
    """Rotate every gear in the carrier frame through `pitches` sun tooth pitches and report the
    worst penetration of each mesh (mm). Positive values = teeth collide, 0 = clean."""
    if np is None:
        return {}
    d, g, lay = model.des, model.gears, model.layout
    cS, cP, cT = (0.0, 0.0), lay["planet_pos"][:2], lay["thin_ring_pos"][:2]
    # relative speeds in the carrier frame (sun = 1)
    ws, wp, wt = 1.0, -d.Zs / d.Zp, -d.Zs / d.Zr1
    wf, wo = wt * d.Zr2 / d.Zf, wt * d.Zr2 / d.Zo
    worst = {"sun/planet": -1e9, "planet/thin ring internal": -1e9,
             "thin ring external/fixed ring": -1e9, "thin ring external/output ring": -1e9}
    span = pitches * TAU / d.Zs
    for k in range(steps + 1):
        t = span * k / steps
        worst["sun/planet"] = max(worst["sun/planet"],
            mesh_penetration(g["sun"], cS, ws * t, g["planet"], cP, wp * t))
        worst["planet/thin ring internal"] = max(worst["planet/thin ring internal"],
            mesh_penetration(g["thin_ring_internal"], cT, wt * t, g["planet"], cP, wp * t))
        worst["thin ring external/fixed ring"] = max(worst["thin ring external/fixed ring"],
            mesh_penetration(g["fixed_ring"], cS, wf * t, g["thin_ring_external"], cT, wt * t))
        worst["thin ring external/output ring"] = max(worst["thin ring external/output ring"],
            mesh_penetration(g["output_ring"], cS, wo * t, g["thin_ring_external"], cT, wt * t))
    return worst


# ----------------------------------------------------------------------------- writers
def write_dxf(path: str, outlines: List[Tuple[GearGeom, Tuple[float, float]]], circles=()):
    """Minimal DXF R12 with LINE and ARC entities. outlines: (gear, (cx, cy)); circles: (cx, cy, r)."""
    L = ["0", "SECTION", "2", "HEADER", "9", "$INSUNITS", "70", "4", "0", "ENDSEC",
         "0", "SECTION", "2", "ENTITIES"]

    def line(x1, y1, x2, y2, layer):
        L.extend(["0", "LINE", "8", layer, "10", f"{x1:.6f}", "20", f"{y1:.6f}", "30", "0",
                  "11", f"{x2:.6f}", "21", f"{y2:.6f}", "31", "0"])

    def arc(cx, cy, r, t0, t1, layer):
        L.extend(["0", "ARC", "8", layer, "10", f"{cx:.6f}", "20", f"{cy:.6f}", "30", "0",
                  "40", f"{r:.6f}", "50", f"{deg(t0) % 360:.6f}", "51", f"{deg(t1) % 360:.6f}"])

    for gg, (cx, cy) in outlines:
        for k in range(gg.Z):
            for seg_ in gg.tooth_segments(k):
                kind, p = seg_[0], seg_[1]
                if kind == "arc":
                    arc(cx, cy, p[0][0], p[0][1], p[1][1], gg.name)
                elif kind == "fillet":
                    meta = seg_[2]
                    fcx, fcy = cx + meta["c"][0], cy + meta["c"][1]
                    (x0, y0), (x1, y1) = polar_to_xy([p[0], p[-1]], cx, cy)
                    a0, a1 = math.atan2(y0 - fcy, x0 - fcx), math.atan2(y1 - fcy, x1 - fcx)
                    if meta["ccw"]:
                        arc(fcx, fcy, meta["r"], a0, a1, gg.name)
                    else:
                        arc(fcx, fcy, meta["r"], a1, a0, gg.name)
                else:
                    xy = polar_to_xy(p, cx, cy)
                    for (x1, y1), (x2, y2) in zip(xy, xy[1:]):
                        line(x1, y1, x2, y2, gg.name)
    for cx, cy, r, layer in circles:
        L.extend(["0", "CIRCLE", "8", layer, "10", f"{cx:.6f}", "20", f"{cy:.6f}", "30", "0", "40", f"{r:.6f}"])
    L.extend(["0", "ENDSEC", "0", "EOF"])
    with open(path, "w", newline="\r\n") as f:
        f.write("\n".join(L) + "\n")


def write_svg(path: str, model: Model, which: str = "A"):
    """2D layout of section A (sun, planet, thin ring, fixed ring) or B (thin ring, output ring, carrier)."""
    g, lay = model.gears, model.layout
    R = lay["ring_outer_r"] + 2
    size = 900
    sc = size / (2 * R)

    def tf(x, y):
        return (size / 2 + x * sc, size / 2 - y * sc)

    def path_d(pts):
        return "M " + " L ".join(f"{tf(x, y)[0]:.2f} {tf(x, y)[1]:.2f}" for x, y in pts) + " Z"

    parts = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {size} {size}" width="{size}" height="{size}">',
             '<rect width="100%" height="100%" fill="white"/>']
    cT = lay["thin_ring_pos"][:2]
    items = []
    if which == "A":
        items = [("fixed_ring", (0, 0), "#9aa", lay["ring_outer_r"]),
                 ("thin_ring_external", cT, "#e8a33d", None),
                 ("thin_ring_internal", cT, "#e8a33d", None),
                 ("planet", lay["planet_pos"][:2], "#3d7be8", lay["planet_bore_r"]),
                 ("sun", (0, 0), "#d33", lay["shaft_r"])]
        title = "Section A: sun, planet, thin ring (internal + external teeth), fixed ring"
    else:
        items = [("output_ring", (0, 0), "#5a5", lay["ring_outer_r"]),
                 ("thin_ring_external", cT, "#e8a33d", None),
                 ("sun", (0, 0), "#d33", None)]
        title = "Section B: thin ring external teeth, output ring (profile shifted), carrier journal"
    for name, (cx, cy), col, extra in items:
        if which == "B" and name == "sun":
            x0, y0 = tf(cx, cy)
            parts.append(f'<circle cx="{x0:.2f}" cy="{y0:.2f}" r="{lay["shaft_r"] * sc:.2f}" fill="none" stroke="{col}" stroke-width="1"/>')
            continue
        pts = polar_to_xy(g[name].polyline(), cx, cy)
        parts.append(f'<path d="{path_d(pts)}" fill="none" stroke="{col}" stroke-width="1"/>')
        if extra:
            x0, y0 = tf(cx, cy)
            parts.append(f'<circle cx="{x0:.2f}" cy="{y0:.2f}" r="{extra * sc:.2f}" fill="none" stroke="{col}" stroke-width="1"/>')
    if which == "B":
        jx, jy = lay["journal_centre"]
        x0, y0 = tf(jx, jy)
        parts.append(f'<circle cx="{x0:.2f}" cy="{y0:.2f}" r="{lay["journal_r"] * sc:.2f}" fill="none" stroke="#777" stroke-dasharray="4 3"/>')
        px, py = lay["planet_pos"][:2]
        x0, y0 = tf(px, py)
        parts.append(f'<circle cx="{x0:.2f}" cy="{y0:.2f}" r="{lay["pin_r"] * sc:.2f}" fill="none" stroke="#777"/>')
    # centre marks
    for (cx, cy), col in (((0, 0), "#000"), (cT, "#e8a33d"), (lay["planet_pos"][:2], "#3d7be8")):
        x0, y0 = tf(cx, cy)
        parts.append(f'<path d="M {x0 - 6:.1f} {y0:.1f} L {x0 + 6:.1f} {y0:.1f} M {x0:.1f} {y0 - 6:.1f} L {x0:.1f} {y0 + 6:.1f}" stroke="{col}" stroke-width="1"/>')
    parts.append(f'<text x="10" y="20" font-family="sans-serif" font-size="14">{title}</text>')
    parts.append("</svg>")
    with open(path, "w") as f:
        f.write("\n".join(parts))


def write_png(path: str, model: Model, which: str = "A", size: int = 1600):
    try:
        from PIL import Image, ImageDraw
    except Exception:
        return False
    g, lay = model.gears, model.layout
    R = lay["ring_outer_r"] + 2
    sc = size / (2 * R)
    img = Image.new("RGB", (size, size), "white")
    dr = ImageDraw.Draw(img)

    def tf(x, y):
        return (size / 2 + x * sc, size / 2 - y * sc)

    cT = lay["thin_ring_pos"][:2]
    if which == "A":
        items = [("fixed_ring", (0, 0), (120, 120, 140)), ("thin_ring_external", cT, (230, 150, 40)),
                 ("thin_ring_internal", cT, (230, 150, 40)), ("planet", lay["planet_pos"][:2], (50, 110, 230)),
                 ("sun", (0, 0), (210, 40, 40))]
    else:
        items = [("output_ring", (0, 0), (60, 160, 60)), ("thin_ring_external", cT, (230, 150, 40))]
    for name, (cx, cy), col in items:
        pts = [tf(x, y) for x, y in polar_to_xy(g[name].polyline(), cx, cy)]
        dr.line(pts + [pts[0]], fill=col, width=1)
    img.save(path)
    return True


def write_stl(path: str, outer: List[Tuple[float, float]], inner: Optional[List[Tuple[float, float]]],
              z0: float, z1: float):
    """Binary STL of a prism between two star-shaped polar outlines (theta increasing). inner=None -> solid."""
    if np is None:
        return False
    def resample(poly, thetas):
        th = np.array([t for _, t in poly]); rr = np.array([r for r, _ in poly])
        th = np.concatenate([th, [th[0] + TAU]]); rr = np.concatenate([rr, [rr[0]]])
        # make strictly increasing (radial steps get a tiny angular offset)
        for i in range(1, len(th)):
            if th[i] <= th[i - 1]:
                th[i] = th[i - 1] + 1e-7
        return np.interp(thetas, th, rr, period=TAU)
    t_all = np.array([t for _, t in outer] + ([t for _, t in inner] if inner else []))
    t_all = np.unique(np.mod(t_all, TAU))
    ro = resample(outer, t_all)
    ri = resample(inner, t_all) if inner else np.zeros_like(t_all)
    n = len(t_all)
    tris = []
    def P(r, t, z): return (r * math.cos(t), r * math.sin(t), z)
    for i in range(n):
        j = (i + 1) % n
        t0, t1 = t_all[i], t_all[j]
        o0, o1, i0, i1 = ro[i], ro[j], ri[i], ri[j]
        # top (z1) and bottom (z0)
        for z, flip in ((z1, False), (z0, True)):
            a, b, c, d = P(i0, t0, z), P(o0, t0, z), P(o1, t1, z), P(i1, t1, z)
            quad = [(a, b, c), (a, c, d)] if not flip else [(a, c, b), (a, d, c)]
            tris += quad
        # outer wall
        a, b, c, d = P(o0, t0, z0), P(o1, t1, z0), P(o1, t1, z1), P(o0, t0, z1)
        tris += [(a, b, c), (a, c, d)]
        if inner:
            a, b, c, d = P(i0, t0, z0), P(i0, t0, z1), P(i1, t1, z1), P(i1, t1, z0)
            tris += [(a, b, c), (a, c, d)]
    with open(path, "wb") as f:
        f.write(b"gearset.py".ljust(80, b"\0"))
        f.write(struct.pack("<I", len(tris)))
        for a, b, c in tris:
            u = np.subtract(b, a); v = np.subtract(c, a); nrm = np.cross(u, v)
            ln = np.linalg.norm(nrm); nrm = nrm / ln if ln > 0 else nrm
            f.write(struct.pack("<3f", *nrm))
            f.write(struct.pack("<9f", *a, *b, *c))
            f.write(struct.pack("<H", 0))
    return True


def circle_polar(r: float, n: int = 360):
    return [(r, TAU * k / n) for k in range(n)]


def write_equations(path: str, model: Model):
    d, lay, g = model.des, model.layout, model.gears
    lines = ['"Module" = %g' % model.m,
             '"PressureAngle" = %g' % d.alpha_deg,
             '"Z_sun" = %d' % d.Zs, '"Z_planet" = %d' % d.Zp,
             '"Z_thin_ring_internal" = %d' % d.Zr1, '"Z_thin_ring_external" = %d' % d.Zr2,
             '"Z_fixed_ring" = %d' % d.Zf, '"Z_output_ring" = %d' % d.Zo,
             '"x_fixed_ring" = %.6f' % d.x_fixed, '"x_output_ring" = %.6f' % d.x_output,
             '"Ratio" = %.6f' % float(d.ratio),
             '"FaceWidth" = %g' % model.face_width, '"SectionGap" = %g' % model.gap,
             '"CentreDistance_SunPlanet" = %.6f' % lay["a1"],
             '"Eccentricity" = %.6f' % lay["e"],
             '"RingOuterRadius" = %.6f' % lay["ring_outer_r"],
             '"JournalRadius" = %.6f' % lay["journal_r"],
             '"ShaftDiameter" = %g' % model.shaft_d, '"PinDiameter" = %g' % model.pin_d,
             '"FitClearance" = %g' % model.fit_clearance, '"Backlash" = %g' % model.backlash,
             '"RootFilletModules" = %g' % model.fillet_mod]
    for k, gg in g.items():
        lines.append('"Phase_%s_deg" = %.6f' % (k, deg(gg.phase)))
        lines.append('"TipRadius_%s" = %.6f' % (k, gg.ra))
        lines.append('"RootRadius_%s" = %.6f' % (k, gg.rf))
    with open(path, "w", newline="\r\n") as f:
        f.write("\n".join(lines) + "\n")


def fmt_ratio(fr: Fraction) -> str:
    s = "-" if fr < 0 else ""
    fr = abs(fr)
    return f"{s}{fr.numerator}/{fr.denominator} = {s}{float(fr):.6f}" if fr.denominator != 1 else f"{s}{fr.numerator}"


def write_report(path: str, model: Model, candidates, target: Fraction, args_text: str):
    d, lay, g = model.des, model.layout, model.gears
    cr = model.contact_ratios()
    kin = model.kinematics_numeric()
    i = d.ratio
    direction = "opposite to" if i < 0 else "same as"
    L = []
    L.append(f"# Gear set design report - target ratio {target}:1\n")
    L.append(f"Generated by `gearset.py {args_text}`\n")
    L.append("## Result\n")
    L.append(f"| | |\n|---|---|")
    L.append(f"| Exact ratio input : output | **{fmt_ratio(i)}** (output turns {direction} the input) |")
    L.append(f"| Module | {model.m} mm |")
    L.append(f"| Pressure angle | {d.alpha_deg} deg |")
    L.append(f"| Sun Zs (input) | {d.Zs} |")
    L.append(f"| Planet Zp (one planet, on a free carrier) | {d.Zp} |")
    L.append(f"| Thin ring, internal teeth Zr1 | {d.Zr1} |")
    L.append(f"| Thin ring, external teeth Zr2 | {d.Zr2} |")
    L.append(f"| Fixed ring Zf (housing) | {d.Zf} (x = {d.x_fixed:+.4f}) |")
    L.append(f"| Output ring Zo | {d.Zo} (x = {d.x_output:+.4f}) |")
    L.append(f"| Profile-shifted ring | {d.shifted} ring, working pressure angle {deg(d.aw):.2f} deg |")
    L.append(f"| Sun-planet centre distance a1 | {lay['a1']:.4f} mm |")
    L.append(f"| Thin ring eccentricity e (centre at x = -e) | {lay['e']:.4f} mm |")
    L.append(f"| Thin ring wall between tooth roots | {lay['thin_ring_wall']:.3f} mm |")
    L.append(f"| Ring outer diameter | {2 * lay['ring_outer_r']:.2f} mm |")
    L.append(f"| Face width per section / gap / total length | {model.face_width} / {model.gap} / {lay['thin_ring_length']} mm (+ shaft stubs) |")
    L.append(f"| Backlash per mesh | {model.backlash} mm |")
    L.append("")
    L.append("## How the ratio arises\n")
    L.append("All centres are fixed in the carrier frame, so Willis' method applies mesh by mesh:\n")
    L.append("```")
    L.append("carrier speed      wc = ws * Zs*Zr2 / (Zf*Zr1 + Zs*Zr2)")
    L.append("thin ring speed    wt = wc * (Zr2 - Zf) / Zr2          (rolls inside the fixed ring)")
    L.append("output ring speed  wo = wc * (Zo - Zf) / Zo")
    L.append("ratio              i  = ws / wo = Zo*(Zf*Zr1 + Zs*Zr2) / (Zs*Zr2*(Zo - Zf))")
    L.append(f"                      = {d.Zo}*({d.Zf}*{d.Zr1} + {d.Zs}*{d.Zr2}) / ({d.Zs}*{d.Zr2}*({d.Zo} - {d.Zf})) = {fmt_ratio(i)}")
    L.append("```")
    L.append("The fixed ring is required: without a grounded ring the mechanism has two degrees of freedom and the")
    L.append("output ring would not be driven. The output ring differs from the fixed ring by "
             f"{abs(d.Zo - d.Zf)} teeth and meshes with the same external teeth of the thin ring, which is why it is profile shifted.\n")
    if kin:
        L.append("Independent numeric check (pitch-point velocities in the fixed frame, sun speed = 1):\n")
        L.append("```")
        for k, v in kin.items():
            L.append(f"{k:>10s}: {v: .9f}")
        L.append(f"formula   : {float(i): .9f}   difference {abs(kin['ratio'] - float(i)):.2e}")
        L.append("```")
    L.append("## Geometry of every toothed outline (mm)\n")
    L.append("| gear | type | Z | x | reference r | base rb | tip ra | root rf | root fillet | first tooth centre (deg) |")
    L.append("|---|---|---|---|---|---|---|---|---|---|")
    for k, gg in g.items():
        fr = f"{gg.fil['rho']:.3f}" if gg.fil else "none"
        L.append(f"| {k} | {gg.kind} | {gg.Z} | {gg.x:+.4f} | {gg.r:.4f} | {gg.rb:.4f} | {gg.ra:.4f} | {gg.rf:.4f} | {fr} | {deg(gg.phase):.4f} |")
    L.append("")
    L.append("Positions in the assembly (mm): sun at (0,0), planet at (%.4f, 0), thin ring centre at (%.4f, 0), "
             "fixed and output rings concentric with the sun. The sun/planet/thin-ring meshes lie on the +x axis, "
             "the thin-ring/fixed-ring and thin-ring/output-ring meshes on the -x axis. The outlines are already "
             "rotated (phases above) so that all teeth mesh at these positions.\n" % (lay["a1"], -lay["e"]))
    L.append("## Mesh checks\n")
    L.append("| mesh | transverse contact ratio |")
    L.append("|---|---|")
    for k, v in cr.items():
        flag = "" if v >= 1.0 else "  **< 1, check!**"
        L.append(f"| {k} | {v:.3f}{flag} |")
    L.append("")
    L.append(f"Radial clearances on the line of centres: fixed ring tip/thin ring root {lay['fixed_ring_tip_clearance']:.3f} mm, "
             f"fixed ring root/thin ring tip {lay['fixed_ring_root_clearance']:.3f} mm, output ring tip/thin ring root "
             f"{lay['output_ring_tip_clearance']:.3f} mm, output ring root/thin ring tip {lay['output_ring_root_clearance']:.3f} mm.\n")
    if model.mesh_check:
        L.append("Rolling check (every gear rotated through two sun pitches in the carrier frame, worst penetration of")
        L.append("one tooth outline into the mating one; negative = minimum clearance, positive = collision):\n")
        L.append("| mesh | worst penetration (mm) |")
        L.append("|---|---|")
        for k, v in model.mesh_check.items():
            flag = "  **collision!**" if v > 0.02 * model.m else ""
            L.append(f"| {k} | {v:+.4f}{flag} |")
        L.append("")
    if model.notes:
        L.append("**Warnings**\n")
        for n in model.notes:
            L.append(f"- {n}")
        L.append("")
    L.append("## Axial layout\n")
    L.append("```")
    L.append(f"z = {lay['section_A'][0]:g} .. {lay['section_A'][1]:g}   section A : sun, planet, thin ring internal teeth, FIXED ring")
    L.append(f"z = {lay['section_B'][0]:g} .. {lay['section_B'][1]:g}   section B : carrier journal (in the thin ring bore), OUTPUT ring")
    L.append(f"thin ring external teeth run the full length 0 .. {lay['thin_ring_length']:g}")
    L.append(f"carrier journal radius {lay['journal_r']:.3f} centred at ({lay['journal_centre'][0]:.3f}, 0), bore for the sun shaft at (0,0), planet pin at ({lay['a1']:.3f}, 0)")
    L.append("```")
    L.append("## Other exact solutions found\n")
    L.append("| score | Zs | Zp | Zr1 | Zr2 | Zf | Zo | shifted | alpha_w | ratio | ring OD at this module |")
    L.append("|---|---|---|---|---|---|---|---|---|---|---|")
    for s, c in candidates:
        od = model.m * (c.Zf + 2 * (1.25 + max(c.x_fixed, c.x_output))) + 2 * model.rim
        L.append(f"| {s:.1f} | {c.Zs} | {c.Zp} | {c.Zr1} | {c.Zr2} | {c.Zf} | {c.Zo} | {c.shifted} | {deg(c.aw):.1f} | {fmt_ratio(c.ratio)} | {od:.1f} |")
    L.append("")
    L.append("Re-run with `--set Zs Zp Zr1 Zr2 Zf Zo` to build any row, or `--sun/--planet` to pin tooth counts.\n")
    with open(path, "w") as f:
        f.write("\n".join(L))


# ----------------------------------------------------------------------------- SolidWorks macro
def write_macro(path: str, model: Model, out_dir_windows: str):
    """Write the VBA module with the parameter block filled in. The macro computes the involutes itself."""
    tpl_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "solidworks", "BuildGearSet.bas.template")
    with open(tpl_path) as f:
        tpl = f.read()
    d, lay, g = model.des, model.layout, model.gears
    P = {
        "OUT_DIR": out_dir_windows,
        "SET_NAME": model.name,
        "MODULE": model.m, "ALPHA_DEG": d.alpha_deg,
        "ZS": d.Zs, "ZP": d.Zp, "ZR1": d.Zr1, "ZR2": d.Zr2, "ZF": d.Zf, "ZO": d.Zo,
        "X_FIXED": d.x_fixed, "X_OUTPUT": d.x_output,
        "FACE_WIDTH": model.face_width, "GAP": model.gap,
        "RING_OUTER_R": lay["ring_outer_r"], "JOURNAL_R": lay["journal_r"],
        "SHAFT_R": lay["shaft_r"], "PIN_R": lay["pin_r"], "FIT_CLEARANCE": model.fit_clearance,
        "N_HOLES": model.n_holes, "HOLE_D": model.hole_d, "HOLE_CIRCLE_R": lay["hole_circle_r"],
        "SHAFT_IN": lay["shaft_in"], "SHAFT_OUT": lay["shaft_out"], "BACKLASH": model.backlash,
        "A1": lay["a1"], "ECC": lay["e"],
        "PH_SUN": deg(g["sun"].phase), "PH_PLANET": deg(g["planet"].phase),
        "PH_TINT": deg(g["thin_ring_internal"].phase), "PH_TEXT": deg(g["thin_ring_external"].phase),
        "PH_FIXED": deg(g["fixed_ring"].phase), "PH_OUTPUT": deg(g["output_ring"].phase),
        "RATIO_TEXT": fmt_ratio(d.ratio),
    }
    txt = tpl
    for k, v in P.items():
        if isinstance(v, float):
            v = repr(round(v, 9))
        txt = txt.replace("{{" + k + "}}", str(v))
    if "{{" in txt:
        raise RuntimeError("unfilled placeholder in macro template")
    with open(path, "w", newline="\r\n") as f:
        f.write(txt)


# ----------------------------------------------------------------------------- main
def choose_module(args, des: Design, lim: Limits) -> Tuple[float, List[str]]:
    notes = []
    rim_factor = args.rim_modules
    xmax = max(des.x_fixed, des.x_output)
    od_per_m = des.Zf + 2 * (lim.addendum + lim.clearance + xmax) + 2 * rim_factor
    if args.module:
        m = args.module
        if args.max_od and m * od_per_m > args.max_od + 1e-9:
            notes.append(f"module {m} gives ring OD {m * od_per_m:.1f} mm > limit {args.max_od} mm")
    elif args.max_od:
        fit = [s for s in STANDARD_MODULES if s * od_per_m <= args.max_od + 1e-9]
        if not fit:
            m = args.max_od / od_per_m
            notes.append(f"no standard module fits OD {args.max_od}; using m = {m:.4f}")
        else:
            m = fit[-1]
    else:
        m = 1.0
    return m, notes


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--ratio", type=str, default="500", help="target ratio input:output, e.g. 500 or 1000/3")
    ap.add_argument("--module", type=float, help="module in mm (default 1, or derived from --max-od)")
    ap.add_argument("--max-od", type=float, help="maximum outer diameter of the ring gears in mm")
    ap.add_argument("--sun", type=int, help="force the sun tooth count")
    ap.add_argument("--planet", type=int, help="force the planet tooth count")
    ap.add_argument("--set", type=int, nargs=6, metavar=("ZS", "ZP", "ZR1", "ZR2", "ZF", "ZO"),
                    help="use this exact tooth set instead of searching")
    ap.add_argument("--nearest", action="store_true", help="accept the nearest ratio when no exact set exists")
    ap.add_argument("--list", type=int, default=12, help="how many candidates to list in the report")
    ap.add_argument("--face-width", type=float, help="face width of one mesh section (mm), default 10*m")
    ap.add_argument("--gap", type=float, help="axial gap between the two sections (mm), default 2")
    ap.add_argument("--rim-modules", type=float, default=5.0, help="rim of the ring gears outside the root, in modules")
    ap.add_argument("--holes", type=int, default=6, help="flange holes in the fixed/output rings (0 = none)")
    ap.add_argument("--shaft-d", type=float, help="sun shaft diameter (mm), default 0.5*sun reference diameter")
    ap.add_argument("--pin-d", type=float, help="planet pin diameter (mm), default 0.45*planet reference diameter")
    ap.add_argument("--aw-max", type=float, default=31.0, help="max working pressure angle of the shifted mesh (deg)")
    ap.add_argument("--ecc-min", type=int, default=10, help="min tooth difference of the eccentric internal meshes")
    ap.add_argument("--wall", type=float, default=1.5, help="min thin ring wall in modules")
    ap.add_argument("--delta-max", type=int, default=3, help="max tooth difference between fixed and output ring")
    ap.add_argument("--max-wall", type=float, help="max thin ring wall in modules (limits Zr2 - Zr1)")
    ap.add_argument("--out", type=str, help="output folder (default output/ratio-<r>_m<module>)")
    ap.add_argument("--win-dir", type=str, default=r"C:\Users\Dany\source\claudecode\HIGH-RATIO-GEAR-SET",
                    help="Windows path of this folder, written into the macro")
    ap.add_argument("--backlash", type=float, default=0.0,
                    help="circumferential backlash per mesh in mm (every tooth thinned by half of it); 0 = theoretical")
    ap.add_argument("--fillet", type=float, default=0.30,
                    help="root fillet radius in modules (0 = sharp root); reduced automatically where the space is too narrow")
    ap.add_argument("--no-stl", action="store_true")
    ap.add_argument("--no-check", action="store_true", help="skip the rolling interference check")
    args = ap.parse_args(argv)
    args_text = " ".join(argv if argv is not None else sys.argv[1:])

    target = Fraction(args.ratio)
    lim = Limits(aw_max_deg=args.aw_max, ecc_min=args.ecc_min, wall_min_mod=args.wall, delta_max=args.delta_max)
    if args.max_wall is not None:
        lim.wall_max_teeth = int(2 * (args.max_wall + 2 * (lim.addendum + lim.clearance)) + 1e-9)
    prefer = {"sun": args.sun, "planet": args.planet}

    if args.set:
        Zs, Zp, Zr1, Zr2, Zf, Zo = args.set
        D = Zr1 - Zs - 2 * Zp
        shifted = "output" if Zf == Zr2 + D else "fixed"
        des = Design(Zs, Zp, Zr1, Zr2, Zf, Zo, shifted, lim.pressure_angle_deg)
        ok, why = des.valid(lim)
        if not ok:
            print("WARNING: given tooth set violates a design rule:", why)
        candidates = [(0.0, des)]
    else:
        candidates = search(target, lim, prefer, exact_only=True, max_results=args.list)
        if not candidates:
            if not args.nearest:
                print(f"No exact tooth set for ratio {target} within the search limits. "
                      f"Re-run with --nearest, or relax --aw-max/--ecc-min/--wall, or free --sun/--planet.")
                return 2
            candidates = search(target, lim, prefer, exact_only=False, max_results=args.list)
        des = candidates[0][1]
    if abs(des.ratio) != abs(target):
        print(f"note: nearest ratio is {fmt_ratio(des.ratio)} (target {target})")

    m, mnotes = choose_module(args, des, lim)
    b = args.face_width if args.face_width else round(10 * m, 3)
    gap = args.gap if args.gap is not None else round(max(2.0, 1.5 * m), 3)
    shaft_d = args.shaft_d if args.shaft_d else round(0.5 * m * des.Zs, 2)
    pin_d = args.pin_d if args.pin_d else round(0.45 * m * des.Zp, 2)
    rim = args.rim_modules * m
    name = f"ratio-{str(target).replace('/', '_')}_m{m:g}"
    if args.sun:
        name += f"_sun{args.sun}"
    if args.planet:
        name += f"_planet{args.planet}"
    out = args.out or os.path.join(os.path.dirname(os.path.abspath(__file__)), "output", name)
    name = os.path.basename(os.path.normpath(out))     # the macro writes next to its own folder name
    os.makedirs(os.path.join(out, "dxf"), exist_ok=True)
    os.makedirs(os.path.join(out, "stl"), exist_ok=True)

    model = Model(des, m, b, gap, rim, args.holes, round(0.4 * rim, 2), shaft_d, pin_d,
                  fit_clearance=round(0.05 * m + 0.05, 3), backlash=args.backlash, fillet_mod=args.fillet, name=name).build()
    model.notes = mnotes + model.notes
    if not args.no_check:
        model.mesh_check = verify_meshing(model)
        for k, v in model.mesh_check.items():
            if v > 0.02 * m:
                model.notes.append(f"rolling check: {k} teeth collide by {v:.3f} mm")

    # --- files -----------------------------------------------------------------
    g, lay = model.gears, model.layout
    write_report(os.path.join(out, "design_report.md"), model, candidates, target, args_text)
    write_equations(os.path.join(out, "equations.txt"), model)
    with open(os.path.join(out, "gearset_params.json"), "w") as f:
        json.dump({"design": asdict(des), "ratio": str(des.ratio), "module": m, "face_width": b, "gap": gap,
                   "layout": {k: v for k, v in lay.items()},
                   "gears": {k: {kk: vv for kk, vv in asdict(gg).items()} for k, gg in g.items()}},
                  f, indent=2, default=str)
    # DXF: one per part (part coordinates, centre at origin) + the two section layouts
    write_dxf(os.path.join(out, "dxf", "sun.dxf"), [(g["sun"], (0, 0))], [(0, 0, lay["shaft_r"], "shaft")])
    write_dxf(os.path.join(out, "dxf", "planet.dxf"), [(g["planet"], (0, 0))], [(0, 0, lay["planet_bore_r"], "bore")])
    write_dxf(os.path.join(out, "dxf", "thin_ring_sectionA.dxf"),
              [(g["thin_ring_external"], (0, 0)), (g["thin_ring_internal"], (0, 0))])
    write_dxf(os.path.join(out, "dxf", "thin_ring_sectionB.dxf"),
              [(g["thin_ring_external"], (0, 0))], [(0, 0, g["thin_ring_internal"].rf, "bore")])
    holes = []
    if model.n_holes:
        for k in range(model.n_holes):
            t = TAU * (k + 0.5) / model.n_holes
            holes.append((lay["hole_circle_r"] * math.cos(t), lay["hole_circle_r"] * math.sin(t), model.hole_d / 2, "holes"))
    write_dxf(os.path.join(out, "dxf", "fixed_ring.dxf"), [(g["fixed_ring"], (0, 0))],
              [(0, 0, lay["ring_outer_r"], "outer")] + holes)
    write_dxf(os.path.join(out, "dxf", "output_ring.dxf"), [(g["output_ring"], (0, 0))],
              [(0, 0, lay["ring_outer_r"], "outer")] + holes)
    jx, jy = lay["journal_centre"]
    write_dxf(os.path.join(out, "dxf", "carrier.dxf"), [],
              [(jx, jy, lay["journal_r"], "journal"), (0, 0, lay["carrier_bore_r"], "bore"),
               (lay["a1"], 0, lay["pin_r"], "pin")])
    cT = lay["thin_ring_pos"][:2]
    write_dxf(os.path.join(out, "dxf", "layout_sectionA.dxf"),
              [(g["fixed_ring"], (0, 0)), (g["thin_ring_external"], cT), (g["thin_ring_internal"], cT),
               (g["planet"], lay["planet_pos"][:2]), (g["sun"], (0, 0))], [(0, 0, lay["ring_outer_r"], "outer")])
    write_dxf(os.path.join(out, "dxf", "layout_sectionB.dxf"),
              [(g["output_ring"], (0, 0)), (g["thin_ring_external"], cT)],
              [(0, 0, lay["ring_outer_r"], "outer"), (jx, jy, lay["journal_r"], "journal"),
               (0, 0, lay["carrier_bore_r"], "bore"), (lay["a1"], 0, lay["pin_r"], "pin")])
    write_svg(os.path.join(out, "layout_sectionA.svg"), model, "A")
    write_svg(os.path.join(out, "layout_sectionB.svg"), model, "B")
    write_png(os.path.join(out, "layout_sectionA.png"), model, "A")
    write_png(os.path.join(out, "layout_sectionB.png"), model, "B")
    if not args.no_stl and np is not None:
        zA0, zA1 = lay["section_A"]; zB0, zB1 = lay["section_B"]
        write_stl(os.path.join(out, "stl", "sun.stl"), g["sun"].polyline(), circle_polar(lay["shaft_r"]), zA0, zA1)
        write_stl(os.path.join(out, "stl", "planet.stl"), g["planet"].polyline(), circle_polar(lay["planet_bore_r"]), zA0, zA1)
        write_stl(os.path.join(out, "stl", "thin_ring_sectionA.stl"), g["thin_ring_external"].polyline(),
                  g["thin_ring_internal"].polyline(), zA0, zA1)
        write_stl(os.path.join(out, "stl", "thin_ring_sectionB.stl"), g["thin_ring_external"].polyline(),
                  circle_polar(g["thin_ring_internal"].rf), zA1, zB1)
        write_stl(os.path.join(out, "stl", "fixed_ring.stl"), circle_polar(lay["ring_outer_r"]),
                  g["fixed_ring"].polyline(), zA0, zA1)
        write_stl(os.path.join(out, "stl", "output_ring.stl"), circle_polar(lay["ring_outer_r"]),
                  g["output_ring"].polyline(), zB0, zB1)
    win_out = args.win_dir.rstrip("\\") + "\\output\\" + name
    write_macro(os.path.join(out, "BuildGearSet.bas"), model, win_out)

    print(f"ratio {fmt_ratio(des.ratio)}  module {m}  Zs={des.Zs} Zp={des.Zp} Zr1={des.Zr1} Zr2={des.Zr2} "
          f"Zf={des.Zf} Zo={des.Zo} ({des.shifted} ring shifted, alpha_w={deg(des.aw):.2f} deg, x={des.x_shift:+.4f})")
    print(f"ring OD {2 * lay['ring_outer_r']:.2f} mm, eccentricity {lay['e']:.3f} mm, a1 {lay['a1']:.3f} mm")
    for n in model.notes:
        print("WARNING:", n)
    print("written to", out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
