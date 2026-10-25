# HIGH-RATIO-GEAR-SET - notes for Claude Code (desktop, SolidWorks 2020 available)

This folder was produced in a cloud session without SolidWorks and then verified on Dany's machine with
SolidWorks 2020 (2026-10-25): the **add-in** compiles against the real interop, registers, builds the six
parts and `GearSet.SLDASM` for the default 1:500 set, and Interference Detection on that assembly reports
zero interferences. Everything geometric is also verified numerically (README "What the generator
verifies"). Still unverified: the kinematic check with gear mates (checklist item 4) and the VBA macro
path (same geometry, older delivery). Dany is the user; SolidWorks 2020 is installed locally.

## Layout
- `solidworks/addin/` - the **SOLIDWORKS add-in** (C#): `AddIn.cs` (ISwAddin, "Gear Set" tab), `GearSetForm.cs`
  (dialog), `GearMath.cs` (solver + involutes, port of gearset.py, no SOLIDWORKS dependency),
  `GearBuilder.cs` (parts + assembly through the API), `install.ps1` (build with MSBuild/dotnet, RegAsm).
  `solidworks/swcli/GearSetCli.exe` shares the same files: `solve` and `test-dialog` need no SOLIDWORKS,
  `build` drives it. Build + register: `solidworks\addin\install.ps1`. Log: `%TEMP%\GearSetAddin.log`.
  Verified: `solidworks/stubs/compile_check.sh` (Mono, stub interop, 0 errors; run it after every C# change),
  `GearSetCli solve -set ... -dump <gear>` prints a gear outline that equals `GearGeom.polyline()` of
  gearset.py to 1e-13 mm (run this comparison after any geometry change in either language),
  `GearSetCli solve -ratio 500` equals `python gearset.py --ratio 500` (24/20/102/114/152/150 first), the
  real `install.ps1` build on SolidWorks 2020, and a real build of the default set with zero interferences.
  Registration note: SolidWorks lists the add-in from HKLM *or* HKCU `Software\SolidWorks\Addins\{guid}`;
  install.ps1 writes both and must run in 64-bit PowerShell (it re-launches itself).
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

## Verification checklist (items 1-4 passed on 2026-10-25 via the add-in, including the direction convention and 10 sun turns = 7.2 deg on the output ring; 5 open)
Item 4 is built in: GearBuilder.AddMates adds Front-Plane coincident mates, concentric mates and gear
mates sun->carrier/planet/thin ring/output with the absolute speed ratios (ks, kp, kt, ko). To verify:
drag the sun - the carrier must turn the SAME way as the sun, the planet, thin ring and output ring the
OPPOSITE way (for the default set), and 10 sun turns give 7.2 deg on the output ring. Confirmed on
SolidWorks 2020: GearMateFlipMeansSameDirection = true is the correct AddMate5 convention (carrier turns
with the sun, output ring 7.2 deg per 10 sun turns, opposite direction). The message box after a build reports every mate (ok / entity not found /
AddMate5 error); the same lines are in %TEMP%\GearSetAddin.log.
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

## Known risk points in the add-in builder (GearBuilder.cs) and the macro (check these first)
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
- Equations/custom properties are added under `On Error Resume Next` / try-catch; they are cosmetic.
- C# specifics: `(Feature)sketch` and `(SketchArc)segment` are COM QueryInterface casts; `GetBodies2`,
  `GetBodyBox`, `GetNormalVector`, `FirstFeature`, `GetNextFeature` return `object` and are cast.
  If `FeatureExtrusion3` does not exist in the referenced interop, the project is compiling against a
  pre-2014 version: check `SwVersion` / `SwInstallDir` in the csproj.

## Geometry rules shared by gearset.py and GearMath.cs (keep them identical)
- Tooth outline per tooth, counter-clockwise: root arc, [root fillet], [radial line rf->rb for small
  external pinions], leading flank spline, tip arc, trailing flank, [line], [fillet]. Internal gears:
  root arc (outer), fillet, flank inwards, tip arc (inner), flank outwards, fillet.
- Root fillet: circle tangent to the root circle and to the flank's start direction (radial line, or the
  involute tangent at the root), radius FilletMod x module (default 0.30), shrunk by 0.8 steps until it
  ends below the base circle / tip and stays within half the tooth space. Fillet arcs are true arcs in
  SolidWorks and DXF; the flank spline starts exactly at the fillet end.

## Conventions
- API lengths in metres, parameters in mm/degrees; all parts share the sun axis as Z, Front Plane as
  z = 0; the planet sits at +x, the thin-ring centre at -x (eccentricity e); meshes lie on the x axis.
- Commit messages: `feat: ...`, `fix: ...`, `docs: ...`; add a dated `SESSION-YYYY-MM-DD.md` at the
  repository root for a session that changes behaviour (see `SESSION-2026-10-25.md`).
- When a set is requested ("ratio R, module m, max OD D, sun/planet counts"), run `gearset.py` with
  those options, read the report (warnings, contact ratios, rolling check), then run the macro.
