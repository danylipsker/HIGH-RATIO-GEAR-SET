#!/usr/bin/env bash
# Compile check of the add-in and the CLI WITHOUT SOLIDWORKS, with Mono (mcs) against stub interop
# assemblies that mirror the signatures the code uses. Catches C# errors early on a Linux/macOS box;
# the real build is solidworks\addin\install.ps1 on Windows against the genuine interop DLLs.
#   apt-get install mono-mcs libmono-system-windows-forms4.0-cil libmono-system-drawing4.0-cil
#   bash solidworks/stubs/compile_check.sh && mono /tmp/gearset-check/GearSetCli.exe solve -ratio 500
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
A="$HERE/../addin"; C="$HERE/../swcli"; OUT="${1:-/tmp/gearset-check}"
mkdir -p "$OUT"
mcs -nologo -target:library -out:"$OUT/SolidWorks.Interop.sldworks.dll" "$HERE/sldworks.cs"
mcs -nologo -target:library -out:"$OUT/SolidWorks.Interop.swconst.dll" "$HERE/swconst.cs"
mcs -nologo -target:library -out:"$OUT/SolidWorks.Interop.swpublished.dll" "$HERE/swpublished.cs"
REFS="-r:$OUT/SolidWorks.Interop.sldworks.dll -r:$OUT/SolidWorks.Interop.swconst.dll -r:$OUT/SolidWorks.Interop.swpublished.dll -r:System.Windows.Forms -r:System.Drawing -r:Microsoft.CSharp"
mcs -nologo -langversion:latest -target:library -out:"$OUT/GearSetAddin.dll" $REFS "$A/AddIn.cs" "$A/GearBuilder.cs" "$A/GearMath.cs" "$A/GearSetForm.cs"
mcs -nologo -langversion:latest -target:exe -out:"$OUT/GearSetCli.exe" $REFS "$C/Program.cs" "$A/GearBuilder.cs" "$A/GearMath.cs" "$A/GearSetForm.cs"
echo "compiled: $OUT/GearSetAddin.dll  $OUT/GearSetCli.exe"
