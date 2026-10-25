# Copies this folder to C:\Users\Dany\source\claudecode\HIGH-RATIO-GEAR-SET (the path baked into the
# macros) and checks that Python can run the generator. Run from anywhere:
#     powershell -ExecutionPolicy Bypass -File <repo>\HIGH-RATIO-GEAR-SET\setup_local.ps1
param([string]$Target = "C:\Users\Dany\source\claudecode\HIGH-RATIO-GEAR-SET")
$src = Split-Path -Parent $MyInvocation.MyCommand.Path
if ((Resolve-Path $src).Path -ne $Target) {
    robocopy $src $Target /E /NFL /NDL /NJH /NJS | Out-Null
    Write-Host "copied $src -> $Target"
} else {
    Write-Host "already at $Target"
}
$py = Get-Command python -ErrorAction SilentlyContinue
if (-not $py) { $py = Get-Command py -ErrorAction SilentlyContinue }
if ($py) {
    & $py.Source -c "import sys; print('python', sys.version.split()[0]); import numpy; print('numpy ok')" 2>$null
    if ($LASTEXITCODE -ne 0) { Write-Host "numpy missing (optional: STL export + rolling check): pip install numpy pillow" }
} else {
    Write-Host "python not found on PATH - install Python 3 to regenerate sets; the committed output\ folders work without it"
}
Write-Host ""
Write-Host "Next: SolidWorks 2020 > Tools > Macro > New (save BuildGearSet.swp) > VBA editor File > Import File >"
Write-Host "      $Target\output\ratio-500_m1\BuildGearSet.bas   then run Main (F5)."
Write-Host "      See $Target\CLAUDE.md for the verification checklist."
