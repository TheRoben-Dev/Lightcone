# Installs Lightcone for this user: %LOCALAPPDATA%\Programs\Lightcone, a Start menu entry, an entry in
# Settings > Apps (to uninstall), and (when Steam is closed) a Steam shortcut with Lightcone's art.
# No admin rights needed. Re-run to repair; updates happen in the app.
#   install.ps1 [-Dest <folder>] [-NoSteam] [-RestartSteam] [-DesktopShortcut]
param(
    [string]$Dest = (Join-Path $env:LOCALAPPDATA 'Programs\Lightcone'),
    [switch]$NoSteam,
    [switch]$RestartSteam,
    [switch]$DesktopShortcut
)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$Dest = [IO.Path]::GetFullPath($Dest)

if ($src.TrimEnd('\') -ne $Dest.TrimEnd('\')) {
    if (Get-Process -Name Lightcone -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($Dest, 'OrdinalIgnoreCase') }) {
        throw "Lightcone is running from $Dest. Close it, then run the installer again."
    }
    $staging = "$Dest.installing"
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Split-Path $Dest) | Out-Null
    Copy-Item $src $staging -Recurse -Force
    if (Test-Path $Dest) { Remove-Item $Dest -Recurse -Force }
    Move-Item $staging $Dest
}
# Files from a downloaded zip carry the "from the internet" mark; this copy is the one the user chose to install.
Get-ChildItem $Dest -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

$exe = Join-Path $Dest 'Lightcone.exe'
$cli = Join-Path $Dest 'lightcone-cli.exe'
$version = (Get-Content (Join-Path $Dest 'README.txt') -TotalCount 1) -replace '^Lightcone ([^ ]+).*$', '$1'

$shell = New-Object -ComObject WScript.Shell
function New-Shortcut([string]$path) {
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $Dest
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = 'Controller-first media hub'
    $lnk.Save()
}
$programs = [Environment]::GetFolderPath('Programs')
New-Shortcut (Join-Path $programs 'Lightcone.lnk')
if ($DesktopShortcut) { New-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Lightcone.lnk') }

# Settings > Apps entry, for this user only.
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Lightcone'
New-Item -Path $key -Force | Out-Null
$uninstall = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $Dest 'uninstall.ps1')`""
@{
    DisplayName = 'Lightcone'; DisplayVersion = $version; Publisher = 'Lightcone'; DisplayIcon = "$exe,0"
    InstallLocation = $Dest; UninstallString = $uninstall; QuietUninstallString = "$uninstall -Quiet"
    URLInfoAbout = 'https://github.com/TheRoben-Dev/Lightcone'
}.GetEnumerator() | ForEach-Object { New-ItemProperty -Path $key -Name $_.Key -Value $_.Value -PropertyType String -Force | Out-Null }
New-ItemProperty -Path $key -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $key -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null

Write-Host "Installed Lightcone $version to $Dest"
if (-not $NoSteam) {
    $steamArgs = @('steam-add')
    if ($RestartSteam) { $steamArgs += '--restart' }
    & $cli @steamArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Add Lightcone to Steam later from Lightcone > Settings > Updates, or run: `"$(Join-Path $Dest 'Install Lightcone.cmd')`" -RestartSteam"
    }
}
exit 0
