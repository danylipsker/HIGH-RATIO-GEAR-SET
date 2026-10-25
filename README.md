# HIGH-RATIO-GEAR-SET - parametric 3K planetary reducer for SolidWorks (1:500 and others)

A single-planet compound planetary ("3K" / Wolfrom type) with a floating thin ring
gear, generated for any requested ratio, module and size by `gearset.py`, and built
as native SolidWorks parts + assembly by the generated macro.

```
HIGH-RATIO-GEAR-SET/
├── gearset.py                          solver + involute geometry + all writers (Python 3, stdlib; numpy optional)
├── build_all.py                        regenerates every committed set below (exact command line of each)
├── setup_local.ps1                     copies this folder to C:\Users\Dany\source\claudecode\HIGH-RATIO-GEAR-SET, checks Python
├── CLAUDE.md                           hand-off for a local Claude Code session with SolidWorks: test checklist, macro risk points
├── solidworks/addin/                   **SOLIDWORKS add-in** (C#, 2020+): "Gear Set" tab -> dialog: module, ratio, pick a solution,
│                                       overall dimensions, Build -> .SLDPRT + .SLDASM only.  install.ps1 builds and registers it
├── solidworks/swcli/                   GearSetCli.exe: the same solver / builder from the command line (solve, test-dialog, build)
├── solidworks/BuildGearSet.bas.template VBA macro template (parameter block is filled by gearset.py) - the no-build alternative
├── output/ratio-500_m1/                the default 1:500 set, module 1 (sun 24)
├── output/ratio-500_m1_sun16/          the same for the 16-tooth sun (16/18/96/123/167/164), same file set
├── output/ratio-500_m1_planet32/       the only exact 1:500 set with a 32-tooth planet (20/32/135/153/204/200)
├── output/ratio-528.7_m1_thin_ring/    sun 16 / planet 32 with a 1.5 mm thin ring: 1:528.69, not 1:500
├── output/ratio-528.7_m2_thin_ring/    the same set at module 2 (3 mm wall, 20 mm face width, ring OD 343 mm)
├── output/ratio-528.7_m1.25_thin_ring/ the same set at module 1.25 (1.9 mm wall, 12.5 mm face width, ring OD 214 mm)
├── output/ratio-528.7_m1.5_thin_ring/  the same set at module 1.5 (2.25 mm wall, 15 mm face width, ring OD 257 mm)
│   ├── design_report.md                tooth counts, ratio derivation, geometry table, mesh checks, alternatives
│   ├── BuildGearSet.bas                SolidWorks macro -> Sun/Planet/ThinRing/Carrier/FixedRing/OutputRing.SLDPRT + GearSet.SLDASM
│   ├── equations.txt                   all parameters as SolidWorks global variables (Equations > Link to external file)
│   ├── gearset_params.json             the same, machine readable
│   ├── dxf/                            exact 2D outlines (lines + arcs) of every part and of both sections -> Insert > DXF into a sketch
│   ├── stl/                            quick 3D preview of the toothed bodies (open directly in SolidWorks or any viewer)
│   └── layout_sectionA/B.svg|png       2D layout drawings
└── README.md
```

## The mechanism

```
                 section A (z = 0 .. b)                    section B (z = b+g .. 2b+g)
      ┌─────────────────────────────────────┐    ┌───────────────────────────────────┐
      │ FIXED RING  Zf  (housing)           │    │ OUTPUT RING  Zo  (= Zf ± 1..3)    │
      │   ╲ meshes the thin ring's          │    │   ╲ meshes the SAME external      │
      │     external teeth at -x            │    │     teeth of the thin ring at -x  │
      │ THIN RING  Zr2 external / Zr1 internal, centre offset by e = m/2 (Zr1-Zs-2Zp) │
      │   PLANET Zp  on the carrier pin, meshes the sun (+x) and the thin ring's      │
      │   SUN Zs  input shaft (centre)      │    │ CARRIER journal (eccentric, idle)  │
      └─────────────────────────────────────┘    └───────────────────────────────────┘
```

* The sun drives the single planet; the planet runs inside the thin ring's internal teeth.
  With one planet the thin ring cannot be concentric with the sun (that needs Zr1 = Zs + 2Zp),
  so the thin ring orbits with eccentricity `e`, carried by the eccentric journal of an idle carrier.
* The thin ring's external teeth roll inside the **fixed** ring and inside the **output** ring,
  which differ by a few teeth. Every mesh centre is fixed in the carrier frame, so Willis' method gives

  ```
  i = ws / wo = Zo (Zf Zr1 + Zs Zr2) / ( Zs Zr2 (Zo - Zf) )          exact rational number
  ```
* The sign of `i` is the output direction (negative = opposite to the input, as in the
  Printables "500 to 1 Ultra-Compact Gear Ratio" model this follows).

### Why the numbers differ from the request (16 / 32 / 99 / 100)

1. **A ring with 99 internal and 100 external teeth of the same module cannot exist** - its internal
   root circle (m·(99/2 + 1.25)) lies *outside* its external root circle (m·(100/2 - 1.25)). The
   external count must exceed the internal one by at least 8 teeth for a 1.5 m wall (`--wall`).
2. **A grounded ring is mandatory.** Sun → planet → thin ring → output ring alone has two degrees
   of freedom; the output would not be driven. The housing ring (fixed) and the output ring both mesh
   with the thin ring's external teeth; their tooth difference is what produces the large ratio.
3. **16 / 32 / 99 is not an exact 1:500 in this topology** (it lands near 1:1000 with a 1-tooth
   difference, near 1:500 with 2). The solver therefore searches all tooth sets that give the exact
   requested ratio and ranks them (working pressure angle, tooth difference, undercut, closeness to
   16/32/99/100, size). The default pick for 1:500:

| | Zs | Zp | Zr1 int | Zr2 ext | Zf fixed | Zo output | ratio | α_w shifted mesh | ring OD @ m=1 |
|---|---|---|---|---|---|---|---|---|---|
| default | 24 | 20 | 102 | 114 | 152 | 150 (x = +1.178) | **-500 exactly** | 27.1° | 164.9 mm |
| `--sun 16` (`output/ratio-500_m1_sun16/`) | 16 | 18 | 96 | 123 | 167 | 164 (x = +1.838) | -500 exactly | 28.9° | 180.2 mm |
| `--planet 32` (`output/ratio-500_m1_planet32/`, needs `--aw-max 32 --delta-max 5 --ecc-min 8`) | 20 | 32 | 135 | 153 | 204 | 200 (x = +2.510) | -500 exactly | 30.0° | 217.5 mm |
| thin ring, `--set 16 32 115 123 158 155` (`output/ratio-528.7_m1_thin_ring/`) | 16 | 32 | 115 | 123 | 158 | 155 (x = +1.913) | **-528.69** (not 500) | 30.8° | 171.3 mm |

Thin ring wall (between the two tooth roots) of these four: 3.5 mm, 11 mm, 6.5 mm and 1.5 mm at module 1.
`--max-wall W` (modules) caps it; note that an exact ratio with a pinned planet count may then not exist
(for planet 32 the thinnest exact set is the one above; a 1.5 m wall with 16/32 lands on 1:528.7).

All exact candidates are listed at the end of every `design_report.md`; build any of them with `--set`.

## SOLIDWORKS add-in (recommended)

Same construction as the Fastener Catalog add-in of the FASTENERS repository: C#, .NET Framework 4.8,
compiled against the interop assemblies of the installed SOLIDWORKS (2020 or newer), registered with RegAsm.

```powershell
cd C:\Users\Dany\source\claudecode\HIGH-RATIO-GEAR-SET\solidworks\addin
.\install.ps1              # detects SOLIDWORKS, compiles add-in + GearSetCli.exe, registers (one UAC prompt)
.\install.ps1 -Unregister
```

Needs Visual Studio Build Tools or the .NET SDK (the same as the fastener add-in). Then start SOLIDWORKS:
a **Gear Set** tab appears with *Create gear set...*:

1. **Module** (mm) and **gear ratio** (1 : N, decimals or fractions accepted), optionally sun / planet
   tooth counts, a maximum ring outer diameter, face width, backlash, root fillet (x module, default 0.3). Or type an **exact tooth set**
   (`16 32 115 123 158 155`) to build precisely that set, or pick a **preset** (the seven committed sets,
   including the 1:528.7 thin ring at modules 1, 1.25, 1.5 and 2), which fills module and set in one click.
2. **Find solutions** lists every tooth set that gives the ratio exactly (best first: working pressure
   angle, ring tooth difference, undercut, closeness to 16/32/99/100, size). When none exists and
   "nearest" is ticked, the closest ratios are listed and marked.
3. Picking a row shows the **overall dimensions**: ring outer diameter, gear pack length, overall length
   with shaft stubs, shaft and pin diameters, centre distance, eccentricity, thin ring wall, profile
   shift and contact ratios, with warnings if a rule is violated.
4. **Build SOLIDWORKS files** writes `Sun`, `Planet`, `ThinRing`, `Carrier`, `FixedRing`, `OutputRing`
   `.SLDPRT` and `GearSet.SLDASM` into the chosen folder (default `output\<set name>`), nothing else.
   The teeth are true involutes with the profile shift, already phased so the assembly meshes;
   custom properties carry teeth, module, ratio and the main dimensions. The assembly stays open.
5. With **add housing with bearings** ticked (default) five more parts come out: `FrontCover` (holds the
   sun's input bearing), `Shell` (clamps the fixed ring against an internal lip, bolt holes through the
   wall), `RearCover` (seat for the output hub bearing), and the two bearing parts; the output ring is
   built as an **output cup**: teeth, closing web, hub running in the rear bearing with the sun's rear
   bearing inside it, and an output shaft. Bearings are standard 60xx deep-groove sizes picked by shaft
   diameter (module 1: 6001 for the sun, 6008 for the hub); housing parts and bearings are fixed
   components. The dimensions panel lists shell OD, bolt circle, bearings and overall length.
6. With **add mates** ticked (default) the assembly is fully mated with one degree of freedom: Front
   Planes coincident for the axial positions, concentric mates on the sun shaft, carrier pin, carrier
   journal and ring cylinders, and four gear mates that tie carrier, planet, thin ring and output ring
   to the sun with their exact speed ratios (for 1:500: 0.15, 0.87, 0.05 and 0.002 turns per sun turn).
   Drag the sun, or add a rotary motor to it in a Motion Study: ten sun turns give 7.2° on the output
   ring, opposite direction. (SOLIDWORKS gear mates work in the assembly frame, which is why the parts
   are geared to the sun with absolute ratios rather than to each other with tooth counts.)

Command line, same code: `swcli\bin\Release\GearSetCli.exe solve -ratio 500 -module 1` (table, no
SOLIDWORKS), `GearSetCli.exe test-dialog` (dialog alone), `GearSetCli.exe build -ratio 500 -module 1 -pick 1`
(builds through the running SOLIDWORKS, or a hidden new one with `-new`).

Diagnostics: `%TEMP%\GearSetAddin.log`. Verified on SolidWorks 2020 (2026-10-25): compiles against the
real interop, registers, builds the default 1:500 set, zero interferences in the resulting assembly.
`solidworks\stubs\compile_check.sh` compiles the C# without SolidWorks (Mono) for quick checks after edits.

The **Gear Set** tab, like every CommandManager tab, is only visible while a document is open: open any
part or assembly first. The commands are also under Tools › Gear Set, and the add-in must be ticked in
Tools › Add-Ins.

## Quick start (VBA macro, no build tools)

1. Put this folder at `C:\Users\Dany\source\claudecode\HIGH-RATIO-GEAR-SET` (`setup_local.ps1` copies it
   there from the repository clone). The macros write their parts next to themselves:
   `output\ratio-500_m1\*.SLDPRT`; change `OUT_DIR` at the top of a .bas if the folder lives elsewhere,
   or pass `--win-dir` to the generator.
2. SolidWorks: *Tools › Macro › New…* (save e.g. `BuildGearSet.swp`), then in the VBA editor
   *File › Import File…* → `output\ratio-500_m1\BuildGearSet.bas`, run `Main` (F5).
   The macro first calibrates the extrude/arc direction conventions on a scratch part, then builds
   six parts (true involutes, profile shift included, every tooth already phased so the assembly meshes)
   and `GearSet.SLDASM` with all parts placed. ~1–3 min for the ~660 teeth.
3. No macro? *Insert › DXF/DWG* any file from `dxf/` into a sketch on the Front Plane and extrude
   (the layout DXFs contain all parts in position), or open the `stl/` files to look at it right away.

The macro was written without a SolidWorks seat at hand (see the repository's other macro for the same
caveat). Every API call is a long-standing one (2014+); if one fails, the error box names the step.

## Asking for a different ratio, module or size

```
python gearset.py --ratio 500                    # exact 1:500, module 1 (default)
python gearset.py --ratio 500 --module 0.75      # same tooth counts, 75 % size (OD 124 mm)
python gearset.py --ratio 500 --max-od 120       # largest standard module whose ring OD fits 120 mm
python gearset.py --ratio 500 --sun 16           # pin tooth counts (also --planet)
python gearset.py --ratio 750 --list 20          # any integer or fraction, e.g. 1000/3
python gearset.py --ratio 437 --nearest          # closest ratio when no exact set exists
python gearset.py --ratio 500 --set 24 20 102 114 152 150 --backlash 0.1 --face-width 8
```

| option | meaning | default |
|---|---|---|
| `--ratio R` | target ratio (integer or fraction); exact solutions only unless `--nearest` | 500 |
| `--module m` / `--max-od D` | module in mm, or derive the largest standard module that fits ring OD `D` | 1 mm |
| `--sun Z` `--planet Z` | force tooth counts | free |
| `--set Zs Zp Zr1 Zr2 Zf Zo` | build exactly this set (from the candidate table) | – |
| `--face-width b` `--gap g` | width of each mesh section, gap between them | 10·m, 2 mm |
| `--backlash j` | circumferential backlash per mesh (teeth thinned by j/2) | 0 (theoretical) |
| `--rim-modules` `--holes` | rim outside the ring root, flange holes | 5 m, 6 |
| `--shaft-d` `--pin-d` | sun shaft and planet pin diameters | 0.5·d_sun, 0.45·d_planet |
| `--aw-max` `--ecc-min` `--wall` `--max-wall` `--delta-max` | design rules: max working pressure angle of the shifted mesh, min tooth difference of the eccentric meshes, min / max thin-ring wall (modules), max fixed/output tooth difference | 31°, 10, 1.5, –, 3 |
| `--win-dir` | Windows path of this folder, written into the macro | `C:\Users\Dany\source\claudecode\HIGH-RATIO-GEAR-SET` |

Practical range of this topology with one external toothing on the thin ring: roughly 1:250 to
1:5000. Below ~1:250 the two rings would need a larger tooth difference than the shared toothing can
accommodate with a sane pressure angle (`--nearest` then reports the closest achievable ratio).

To have Claude do it: say e.g. *"make me the 1:750 set, module 1.25, outer diameter under 150 mm"*
(it runs the command above, checks the report, and the new `output/<name>/` folder contains the macro,
DXF, STL and report for that set).

## What the generator verifies

* exact rational ratio, plus an independent numeric check (pitch-point velocity compatibility of all
  four meshes in the fixed frame, solved as a linear system - agrees to 1e-13);
* transverse contact ratio of every mesh (> 1.5 for the default set), radial tip/root clearances of
  the two eccentric ring meshes, thin-ring wall, journal/pin/shaft fits;
* a rolling check: every outline is rotated through two sun pitches in the carrier frame and the
  worst penetration of one tooth outline into the mating one is reported (≤ 0.0004 mm for the
  default = discretisation noise; a half-pitch mis-phase is detected as 2 mm, so the check is live).

## Modelling assumptions / limits

* Straight spur involute teeth, 20° pressure angle, ha* = 1, c* = 0.25, **root fillets** of 0.30 x module
  by default (`--fillet`, dialog box "root fillet"): a true arc tangent to the root circle and to the
  flank (or to the radial root line of small pinions), reduced automatically where a tooth space is too
  narrow. 0.38 x module (ISO basic rack) is possible but grazes the ring tips by a few µm because no tip
  relief is modelled; the rolling check reports it. The VBA macro path has no fillets.
* The profile shift sits entirely on the shifted ring; the thin ring's external teeth are standard
  because they serve both rings. Working pressure angle and contact ratio are reported.
* The carrier is an idle eccentric (journal in the thin ring's plain bore, pin for the planet, bore for
  the sun shaft). Bearings, housing, end plates, lubrication and balancing of the single planet are
  outside the scope - this is the geometry kernel of the reducer.
* Efficiency of 3K reducers at 1:500 is low (tens of percent) and they are usually self-locking;
  this matches the original "demo" purpose of the design.
