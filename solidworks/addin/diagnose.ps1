# One-screen status of the Gear Set add-in installation. Paste the output when something does not show.
#   powershell -ExecutionPolicy Bypass -File .\solidworks\addin\diagnose.ps1
$g = '{3E6F2B1A-9C4D-4E8A-B7F1-2D5C8A9E0B47}'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dll = Join-Path $here 'bin\Release\GearSetAddin.dll'
"user            : $env:USERNAME   64-bit PowerShell: $([Environment]::Is64BitProcess)"
"add-in folder   : $here"
"dll             : " + $(if (Test-Path $dll) { (Get-Item $dll).LastWriteTime.ToString('yyyy-MM-dd HH:mm') + "  " + (Get-Item $dll).Length + " bytes" } else { 'MISSING - run install.ps1' })
"git head        : " + $(try { git -C (Join-Path $here '..\..') log -1 --format='%h %ad %s' --date=short 2>$null } catch { 'n/a' })
"COM class       : " + (Test-Path "Registry::HKEY_CLASSES_ROOT\CLSID\$g\InprocServer32")
if (Test-Path "Registry::HKEY_CLASSES_ROOT\CLSID\$g\InprocServer32") { "  CodeBase      : " + (Get-ItemProperty "Registry::HKEY_CLASSES_ROOT\CLSID\$g\InprocServer32").CodeBase }
"HKLM Addins     : " + (Test-Path "HKLM:\SOFTWARE\SolidWorks\Addins\$g")
"HKLM WOW6432    : " + (Test-Path "HKLM:\SOFTWARE\WOW6432Node\SolidWorks\Addins\$g")
"HKCU Addins     : " + (Test-Path "HKCU:\Software\SolidWorks\Addins\$g")
"HKCU Startup    : " + (Test-Path "HKCU:\Software\SolidWorks\AddInsStartup\$g")
"SOLIDWORKS runs : " + $(try { (Get-Process SLDWORKS -IncludeUserName -ErrorAction Stop | Select-Object -First 1 | ForEach-Object { "yes, as " + $_.UserName }) } catch { try { if (Get-Process SLDWORKS -ErrorAction Stop) { 'yes (user unknown without admin rights)' } } catch { 'no' } })
"add-in logs     :"
Get-ChildItem 'C:\Users\*\AppData\Local\Temp\GearSetAddin.log' -ErrorAction SilentlyContinue | ForEach-Object {
    "  " + $_.FullName + "  (" + $_.LastWriteTime.ToString('yyyy-MM-dd HH:mm') + ")"
    Get-Content $_.FullName -ErrorAction SilentlyContinue | Select-Object -Last 6 | ForEach-Object { "      $_" }
}
if (-not (Get-ChildItem 'C:\Users\*\AppData\Local\Temp\GearSetAddin.log' -ErrorAction SilentlyContinue)) { "  none found under C:\Users\*\AppData\Local\Temp" }
"register log    : " + $(if (Test-Path "$env:TEMP\GearSet-register.log") { (Get-Content "$env:TEMP\GearSet-register.log" | Where-Object { $_ -match '\S' } | Select-Object -Last 1) } else { 'none' })
"other add-ins   : " + ((Get-ChildItem 'HKLM:\SOFTWARE\SolidWorks\Addins' -ErrorAction SilentlyContinue | ForEach-Object { (Get-ItemProperty $_.PSPath).Title }) -join ', ')
