# HIGH-RATIO-GEAR-SET - notes for Claude Code (desktop, SolidWorks 2020 available)

This folder was produced in a cloud session **without SolidWorks**. Everything geometric is verified
numerically (see README "What the generator verifies"), but the VBA macro that builds the native
parts has never been executed. Your first job here is to run it, fix whatever SolidWorks 2020
rejects, and confirm the assembly meshes. Dany is the user; SolidWorks 2020 is installed locally.

## Layout
- `gearset.py` - generator (solver, involutes, DXF/STL/SVG/report, fills the macro template). Python 3,
  stdlib; numpy + Pillow optional (rolling check, STL, PNG).
- `solidworks/BuildGearSet.bas.template` - the macro. **Edit the template, not the generated copies**,
  then `python build_all.py` regenerates all `output/*/BuildGearSet.bas` (or `build_all.py <set-name>`).
- `output/<set>/` - one folder per tooth set: `BuildGearSet.bas`, `design_report.md`, `dxf/`, `stl/`,
  `equations.txt`, `gearset_params.json`, layout SVG/PNG. The macro writes its `.SLDPRT`/`.SLDASM`
  into the same folder (`OUT_DIR` constant at the top).
- Default set for testing: `output/ratio-500_m1/` (24/20/102/114/152/150, exact -500, module 1).

## Running the macro (manual)
Tools > Macro > New... (save e.g. `BuildGearSet.swp` anywhere) -> VBA editor opens -> File > Import
File... -> `output\ratio-500_m1\BuildGearSet.bas` -> delete the empty default module -> F5 on `Main`.
Expected: a scratch part is created and closed (direction calibration), then six parts and
`GearSet.SLDASM` are saved in the folder, and a message box reports the ratio. 1-3 minutes.

## Verification checklist (do all, report results to Dany)
1. Macro runs to the final message box without an error box. If it stops, the error names the step
   (`gStep`) - see "Known risk points" below.
2. Every part rebuilds without errors; each gear has the expected tooth count (custom property `Teeth`,
   or count in a section view) and the face width / Z extents from `design_report.md` "Axial layout".
3. Assembly `GearSet.SLDASM`: Tools > Evaluate > Interference Detection on the whole assembly -> zero
   interferences (a zero-backlash design touches at the pitch points; "treat coincidence as
   interference" must be off). If tiny tooth-flank interferences show up, regenerate with
   `--backlash 0.05` and re-run the macro.
4. Kinematic check: add mates (concentric sun/fixed ring, concentric planet/carrier pin, concentric
   thin ring/carrier journal, concentric output ring/fixed ring, coincident faces for axial
   positions), then gear mates sun-planet (Zs:Zp), planet-thin ring internal (Zp:Zr1), thin ring
   external-fixed ring (Zr2:Zf), thin ring external-output ring (Zr2:Zo). Rotate the sun 10 turns:
   the output ring must turn 10/500 = 7.2 degrees, opposite direction. A Motion Study with a rotary
   motor on the sun shows the same.
5. Open `dxf/layout_sectionA.dxf` in a sketch and compare with the parts if anything looks off - the
   DXF is the reference geometry; the macro is supposed to reproduce it.

## Known risk points in the macro (check these first if it fails)
- `Calibrate`: uses `ISketchArc::GetNormalVector`, `IPartDoc::GetBodies2`, `IBody2::GetBodyBox` to
  learn the sign conventions of `FeatureExtrusion3` (Dir flag, StartOffset flip) and `CreateArc`
  direction. If calibration itself fails, hard-code `gDirFlip`, `gOffFlip`, `gArcCCW` after one manual
  test and delete the call.
- `FeatureExtrusion3` has 23 arguments (SW 2014+). If your type library shows a different count, use
  `FeatureExtrusion2` (20 args, no start offset) and create offset reference planes instead.
- `CreateSpline2((v), False)` needs a Variant holding a `Double()` array of x,y,z triplets in metres.
- Sketch closure: flank splines, tip/root arcs and radial lines meet at identical computed points
  (`AddToDB = True` suppresses snapping). If an extrude fails with "open contour", the sketch has a
  gap: check `AddExternalOutline` / `AddInternalOutline` against the DXF of the same part.
- `StartSketch` selects the first `RefPlane` feature (Front Plane in any language).
- `AddComponent5` expects the part documents to be open - they are, since the macro keeps them open
  after saving. If it returns Nothing, open the parts with `OpenDoc6` first.
- `SaveAs` (IModelDocExtension) with 6 args - if deprecated on your build, switch to `SaveAs3`.
- Equations/custom properties are added under `On Error Resume Next`; they are cosmetic.

## Conventions
- API lengths in metres, parameters in mm/degrees; all parts share the sun axis as Z, Front Plane as
  z = 0; the planet sits at +x, the thin-ring centre at -x (eccentricity e); meshes lie on the x axis.
- Commit messages: `feat: ...`, `fix: ...`, `docs: ...`; add a dated `SESSION-YYYY-MM-DD.md` at the
  repository root for a session that changes behaviour (see `SESSION-2026-10-25.md`).
- When a set is requested ("ratio R, module m, max OD D, sun/planet counts"), run `gearset.py` with
  those options, read the report (warnings, contact ratios, rolling check), then run the macro.
