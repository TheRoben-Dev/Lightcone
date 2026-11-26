# Removes Lightcone's program files, Start menu entry and Settings > Apps entry.
# Settings, watch progress and the artwork cache (%LOCALAPPDATA%\Lightcone, %APPDATA%\Lightcone) are kept unless -RemoveData.
# Your media is never touched.
param([switch]$Quiet, [switch]$RemoveData)
$ErrorActionPreference = 'Continue'
$dest = $PSScriptRoot
if (-not $Quiet) {
    $answer = Read-Host "Uninstall Lightcone from $dest? Your settings and watch progress are kept. [y/N]"
    if ($answer -notmatch '^[yY]') { exit 1 }
}
Get-Process -Name Lightcone -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($dest, 'OrdinalIgnoreCase') } | Stop-Process -Force
foreach ($lnk in @((Join-Path ([Environment]::GetFolderPath('Programs')) 'Lightcone.lnk'), (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Lightcone.lnk'))) {
    if (Test-Path $lnk) { Remove-Item $lnk -Force }
}
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Lightcone' -Recurse -Force -ErrorAction SilentlyContinue
if ($RemoveData) {
    Remove-Item (Join-Path $env:LOCALAPPDATA 'Lightcone'), (Join-Path $env:APPDATA 'Lightcone') -Recurse -Force -ErrorAction SilentlyContinue
}
# This script runs from the folder being removed: finish from a separate process once it exits.
Start-Process cmd.exe -ArgumentList '/c', "timeout /t 2 /nobreak >nul & rmdir /s /q `"$dest`" & rmdir /s /q `"$dest.previous`"" -WindowStyle Hidden
Write-Host 'Lightcone was uninstalled.'
