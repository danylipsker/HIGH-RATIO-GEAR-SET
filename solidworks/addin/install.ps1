<#
.SYNOPSIS
  Build and register the Gear Set add-in for SOLIDWORKS 2020 or newer.

.DESCRIPTION
  1. Detects the installed SOLIDWORKS versions (HKLM\SOFTWARE\SolidWorks\SOLIDWORKS 20xx\Setup).
  2. Compiles the add-in (and GearSetCli.exe) against the interop assemblies of the chosen
     version (-SwVersion; default = oldest installed >= 2020, which loads in every newer release).
  3. Registers the COM server with RegAsm (administrator rights - the script asks for elevation)
     and stores the repository location for the add-in (HKCU\Software\GearSetAddin\Root).

.EXAMPLE
  .\install.ps1                  # auto version
  .\install.ps1 -SwVersion 2020
  .\install.ps1 -BuildOnly       # compile only
  .\install.ps1 -Unregister
  .\install.ps1 -List
#>
[CmdletBinding()]
param(
    [ValidateSet('auto', '2020', '2021', '2022', '2023', '2024', '2025', '2026', '2027', '2028')]
    [string]$SwVersion = 'auto',
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$BuildOnly,
    [switch]$RegisterOnly,
    [switch]$Unregister,
    [switch]$List
)

$ErrorActionPreference = 'Stop'
$AddinDir = $PSScriptRoot
$RepoRoot = (Resolve-Path (Join-Path $AddinDir '..\..')).Path
$Dll = Join-Path $AddinDir "bin\$Configuration\GearSetAddin.dll"
$RegAsm = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
$AddinGuid = '{3E6F2B1A-9C4D-4E8A-B7F1-2D5C8A9E0B47}'
$Title = 'Gear Set'
$Description = 'High-ratio single-planet 3K planetary reducer: enter module and ratio, pick a tooth set, build parts and assembly'

function Get-InstalledSolidWorks {
    $found = @()
    $base = 'HKLM:\SOFTWARE\SolidWorks'
    if (-not (Test-Path $base)) { return $found }
    foreach ($k in Get-ChildItem $base | Where-Object { $_.PSChildName -match '^SOLIDWORKS (20\d\d)$' }) {
        if ($k.PSChildName -notmatch '^SOLIDWORKS (20\d\d)$') { continue }
        $year = [int]$Matches[1]
        $setup = Get-ItemProperty -Path (Join-Path $k.PSPath 'Setup') -ErrorAction SilentlyContinue
        $dir = $setup.'SolidWorks Folder'
        if ($dir -and (Test-Path (Join-Path $dir 'SLDWORKS.exe')) -and (Test-Path (Join-Path $dir 'api\redist\SolidWorks.Interop.sldworks.dll'))) {
            $ver = (Get-Item (Join-Path $dir 'SLDWORKS.exe')).VersionInfo.ProductVersion
            $found += [pscustomobject]@{ Year = $year; Folder = $dir; Version = $ver }
        }
    }
    return $found | Sort-Object Year
}

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

$installed = Get-InstalledSolidWorks
if ($List) {
    if (-not $installed) { Write-Host 'No SOLIDWORKS installation found.'; exit 0 }
    $installed | Format-Table Year, Version, Folder -AutoSize
    exit 0
}

# ---------------------------------------------------------------- unregister
if ($Unregister) {
    if (-not (Test-Admin)) {
        Write-Host 'Elevation required to unregister - requesting administrator rights...'
        Start-Process powershell -Verb RunAs -Wait -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Unregister', '-Configuration', $Configuration)
        exit $LASTEXITCODE
    }
    if (Test-Path $Dll) { & $RegAsm /u "$Dll" | Out-Null }
    Remove-Item "HKLM:\SOFTWARE\SolidWorks\Addins\$AddinGuid" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKCU:\Software\SolidWorks\AddInsStartup\$AddinGuid" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item 'HKCU:\Software\GearSetAddin' -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'Gear Set add-in unregistered. Restart SOLIDWORKS.' -ForegroundColor Green
    exit 0
}

# ---------------------------------------------------------------- choose version + build
if (-not $RegisterOnly) {
    if (-not $installed) { throw 'No SOLIDWORKS installation with API interop assemblies found (need 2020 or newer).' }
    $usable = $installed | Where-Object { $_.Year -ge 2020 }
    if (-not $usable) { throw "Only SOLIDWORKS versions older than 2020 are installed: $($installed.Year -join ', ')" }
    if ($SwVersion -eq 'auto') {
        $target = $usable | Select-Object -First 1
        Write-Host "Installed SOLIDWORKS versions: $($installed.Year -join ', ')  -> compiling against $($target.Year)."
    } else {
        $target = $usable | Where-Object { $_.Year -eq [int]$SwVersion }
        if (-not $target) { throw "SOLIDWORKS $SwVersion is not installed. Installed: $($installed.Year -join ', ')" }
        Write-Host "Compiling against SOLIDWORKS $($target.Year) ($($target.Folder))"
    }

    $msbuild = $null
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    }
    $projects = @((Join-Path $AddinDir 'GearSetAddin.csproj'), (Join-Path $AddinDir '..\swcli\GearSetCli.csproj'))
    $props = "/p:Configuration=$Configuration", "/p:SwVersion=$($target.Year)", "/p:SwInstallDir=$($target.Folder)"
    foreach ($proj in $projects) {
        if ($msbuild) {
            Write-Host "Building $(Split-Path $proj -Leaf) with MSBuild"
            & $msbuild $proj /restore /nologo /v:minimal $props
        } elseif (Get-Command dotnet -ErrorAction SilentlyContinue) {
            Write-Host "Building $(Split-Path $proj -Leaf) with dotnet build"
            & dotnet build $proj -c $Configuration -nologo -v minimal $props
        } else {
            throw 'Neither MSBuild (Visual Studio / Build Tools) nor the .NET SDK was found.'
        }
        if ($LASTEXITCODE -ne 0) { throw "Build of $proj failed." }
    }
    if (-not (Test-Path $Dll)) { throw 'Build failed.' }
    Write-Host "Built $Dll" -ForegroundColor Green
    if ($BuildOnly) { exit 0 }
}

# ---------------------------------------------------------------- register
if (-not (Test-Path $Dll)) { throw "Add-in not built: $Dll" }

New-Item -Path 'HKCU:\Software\GearSetAddin' -Force | Out-Null
Set-ItemProperty -Path 'HKCU:\Software\GearSetAddin' -Name 'Root' -Value $RepoRoot
New-Item -Path "HKCU:\Software\SolidWorks\AddInsStartup\$AddinGuid" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\SolidWorks\AddInsStartup\$AddinGuid" -Name '(default)' -Value 1 -Type DWord

$RegLog = Join-Path $env:TEMP 'GearSet-register.log'
if (-not (Test-Admin)) {
    Write-Host 'Registering the COM add-in needs administrator rights - requesting elevation (UAC prompt)...'
    Remove-Item $RegLog -ErrorAction SilentlyContinue
    $p = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-RegisterOnly', '-Configuration', $Configuration)
    if (Test-Path $RegLog) { Get-Content $RegLog | ForEach-Object { Write-Host "  $_" } }
    if ($p.ExitCode -ne 0) { throw "Registration failed (exit code $($p.ExitCode)). See $RegLog" }
} else {
    $ErrorActionPreference = 'Continue'
    $regOut = & cmd.exe /c "`"$RegAsm`" /codebase `"$Dll`" 2>&1"
    $regExit = $LASTEXITCODE
    $regOut | Out-File $RegLog -Encoding utf8
    $ErrorActionPreference = 'Stop'
    $regOut | Where-Object { $_ -match 'error|Types registered' } | ForEach-Object { Write-Host "  $_" }
    if ($regExit -ne 0) { throw "RegAsm failed (exit code $regExit). See $RegLog" }
    if (-not (Test-Path "HKLM:\SOFTWARE\SolidWorks\Addins\$AddinGuid")) {
        New-Item -Path "HKLM:\SOFTWARE\SolidWorks\Addins\$AddinGuid" -Force | Out-Null
        Set-ItemProperty -Path "HKLM:\SOFTWARE\SolidWorks\Addins\$AddinGuid" -Name '(default)' -Value 0 -Type DWord
        Set-ItemProperty -Path "HKLM:\SOFTWARE\SolidWorks\Addins\$AddinGuid" -Name 'Title' -Value $Title
        Set-ItemProperty -Path "HKLM:\SOFTWARE\SolidWorks\Addins\$AddinGuid" -Name 'Description' -Value $Description
    }
    if ($RegisterOnly) { exit 0 }
}

Write-Host ''
Write-Host 'Gear Set add-in registered.' -ForegroundColor Green
Write-Host "  repository : $RepoRoot"
Write-Host '  Start (or restart) SOLIDWORKS. If the tab does not show, enable it under Tools > Add-Ins > Gear Set.'
Write-Host '  Test without SOLIDWORKS:  swcli\bin\Release\GearSetCli.exe solve -ratio 500 -module 1   /   GearSetCli.exe test-dialog'
