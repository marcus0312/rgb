<#
.SYNOPSIS
  Install Unified RGB to %LocalAppData%\Programs\UnifiedRgb\

.DESCRIPTION
  Copies a win-x64 publish folder (or the folder next to this script named publish-win-x64)
  into LocalAppData, creates a Start Menu shortcut, optionally registers HKCU Run autostart,
  and writes an Uninstall.ps1 + uninstall registry key under HKCU Uninstall.

.PARAMETER SourceDir
  Path to the published app folder containing UnifiedRgb.App.exe.
  Defaults to ..\dist\win-x64 relative to this script, then .\publish-win-x64.

.PARAMETER StartWithWindows
  Register HKCU Run so Unified RGB launches at login (default: $true).

.EXAMPLE
  .\Install-UnifiedRgb.ps1 -SourceDir C:\builds\UnifiedRgb\dist\win-x64
#>
[CmdletBinding()]
param(
    [string]$SourceDir = "",
    [bool]$StartWithWindows = $true
)

$ErrorActionPreference = "Stop"
$DisplayName = "Unified RGB"
$ExeName = "UnifiedRgb.App.exe"
$InstallRoot = Join-Path $env:LOCALAPPDATA "Programs\UnifiedRgb"
$StartMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$ShortcutPath = Join-Path $StartMenuDir "$DisplayName.lnk"
$UninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\UnifiedRgb"
$RunKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"

function Resolve-SourceDir {
    param(
        [string]$Hint,
        [string]$ScriptDir
    )
    if ($Hint -and (Test-Path (Join-Path $Hint $ExeName))) { return (Resolve-Path $Hint).Path }

    $candidates = @(
        (Join-Path $ScriptDir "..\dist\win-x64"),
        (Join-Path $ScriptDir "publish-win-x64"),
        (Join-Path $ScriptDir "..\src\UnifiedRgb.App\bin\Release\net8.0\win-x64\publish"),
        (Join-Path $ScriptDir "win-x64")
    )
    foreach ($c in $candidates) {
        $full = [System.IO.Path]::GetFullPath($c)
        if (Test-Path (Join-Path $full $ExeName)) { return $full }
    }
    throw "Could not find $ExeName. Pass -SourceDir to a win-x64 publish folder, or run scripts/publish-win-x64 first."
}

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$src = Resolve-SourceDir -Hint $SourceDir -ScriptDir $scriptDir
Write-Host "Installing Unified RGB from: $src"
Write-Host "Destination: $InstallRoot"

New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null

# Stop a running instance if present (best-effort)
Get-Process -Name "UnifiedRgb.App" -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping running UnifiedRgb.App (PID $($_.Id))…"
    $_ | Stop-Process -Force -ErrorAction SilentlyContinue
}

Copy-Item -Path (Join-Path $src "*") -Destination $InstallRoot -Recurse -Force

$exePath = Join-Path $InstallRoot $ExeName
if (-not (Test-Path $exePath)) {
    throw "Install copy failed — $exePath missing."
}

# Marker so the app defaults Start with Windows ON
$envMarker = Join-Path $InstallRoot "installed.marker"
Set-Content -Path $envMarker -Value "1" -Encoding ASCII

# Start Menu shortcut
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($ShortcutPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $InstallRoot
$shortcut.Description = $DisplayName
$shortcut.IconLocation = "$exePath,0"
$shortcut.Save()
Write-Host "Start Menu shortcut: $ShortcutPath"

# Autostart (HKCU Run)
if ($StartWithWindows) {
    New-Item -Path $RunKey -Force | Out-Null
    Set-ItemProperty -Path $RunKey -Name "UnifiedRgb" -Value "`"$exePath`""
    Write-Host "Registered Start with Windows (HKCU Run\UnifiedRgb)."
} else {
    Remove-ItemProperty -Path $RunKey -Name "UnifiedRgb" -ErrorAction SilentlyContinue
}

# Copy uninstall script alongside the app
$uninstallScript = Join-Path $InstallRoot "Uninstall.ps1"
$uninstallSrc = Join-Path $scriptDir "Uninstall-UnifiedRgb.ps1"
if (-not (Test-Path $uninstallSrc)) {
    # When installer was copied into dist/ next to Uninstall script
    $uninstallSrc = Join-Path $scriptDir "Uninstall-UnifiedRgb.ps1"
}
if (Test-Path $uninstallSrc) {
    Copy-Item -Path $uninstallSrc -Destination $uninstallScript -Force
} else {
    # Embed a minimal uninstall if the sibling script is missing
    @'
$ErrorActionPreference = "Stop"
$InstallRoot = Join-Path $env:LOCALAPPDATA "Programs\UnifiedRgb"
$ShortcutPath = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Unified RGB.lnk"
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "UnifiedRgb" -ErrorAction SilentlyContinue
Remove-Item -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\UnifiedRgb" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path $ShortcutPath -Force -ErrorAction SilentlyContinue
Get-Process -Name "UnifiedRgb.App" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
if (Test-Path $InstallRoot) { Remove-Item -Path $InstallRoot -Recurse -Force }
Write-Host "Unified RGB uninstalled."
'@ | Set-Content -Path $uninstallScript -Encoding UTF8
}

# HKCU uninstall entry (no admin)
New-Item -Path $UninstallKey -Force | Out-Null
Set-ItemProperty -Path $UninstallKey -Name "DisplayName" -Value $DisplayName
Set-ItemProperty -Path $UninstallKey -Name "Publisher" -Value "Marcus Lee"
Set-ItemProperty -Path $UninstallKey -Name "InstallLocation" -Value $InstallRoot
Set-ItemProperty -Path $UninstallKey -Name "DisplayIcon" -Value $exePath
Set-ItemProperty -Path $UninstallKey -Name "UninstallString" -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstallScript`""
Set-ItemProperty -Path $UninstallKey -Name "NoModify" -Value 1 -Type DWord
Set-ItemProperty -Path $UninstallKey -Name "NoRepair" -Value 1 -Type DWord

Write-Host ""
Write-Host "Installed. Launch from Start Menu: $DisplayName"
Write-Host "Settings / profiles: $env:LOCALAPPDATA\UnifiedRgb\"
Write-Host "Uninstall: $uninstallScript  (or Apps & Features → Unified RGB)"
