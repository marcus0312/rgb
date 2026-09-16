<#
.SYNOPSIS
  Remove Unified RGB installed via Install-UnifiedRgb.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$DisplayName = "Unified RGB"
$InstallRoot = Join-Path $env:LOCALAPPDATA "Programs\UnifiedRgb"
$StartMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$ShortcutPath = Join-Path $StartMenuDir "$DisplayName.lnk"
$UninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\UnifiedRgb"
$RunKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"

Write-Host "Uninstalling Unified RGB…"

Get-Process -Name "UnifiedRgb.App" -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping UnifiedRgb.App (PID $($_.Id))…"
    $_ | Stop-Process -Force -ErrorAction SilentlyContinue
}

Remove-ItemProperty -Path $RunKey -Name "UnifiedRgb" -ErrorAction SilentlyContinue
Remove-Item -Path $UninstallKey -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path $ShortcutPath -Force -ErrorAction SilentlyContinue

if (Test-Path $InstallRoot) {
    # Don't delete settings/profiles under LocalAppData\UnifiedRgb — only the Programs install.
    Remove-Item -Path $InstallRoot -Recurse -Force
    Write-Host "Removed $InstallRoot"
}

Write-Host "Done. Profiles/settings kept at $env:LOCALAPPDATA\UnifiedRgb\ (delete manually if desired)."
