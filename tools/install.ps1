<#
.SYNOPSIS
  Installs BWOS for the current user: copies it to %LOCALAPPDATA%\Programs\BWOS and adds a
  Start-menu shortcut. Your settings, watch history and sign-ins (the data folder) are kept
  across updates. No administrator rights are needed.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\tools\install.ps1              # install / update
  powershell -ExecutionPolicy Bypass -File .\tools\install.ps1 -Autostart   # …and start with Windows
  powershell -ExecutionPolicy Bypass -File .\tools\install.ps1 -Uninstall
#>
param(
    [string]$Source,        # folder containing BWOS.exe (default: this release folder or dist\BWOS)
    [switch]$Autostart,     # start BWOS (in the tray) when you sign in to Windows
    [switch]$Uninstall,
    [switch]$KeepData       # with -Uninstall: keep settings, history and sign-ins
)
$ErrorActionPreference = 'Stop'
$target   = Join-Path $env:LOCALAPPDATA 'Programs\BWOS'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'BWOS.lnk'
$runKey   = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Stop-Bwos {
    Get-Process BWOS -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$target*" } | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

if ($Uninstall) {
    Stop-Bwos
    Remove-ItemProperty -Path $runKey -Name 'BWOS' -ErrorAction SilentlyContinue
    if (Test-Path $shortcut) { Remove-Item $shortcut }
    if (Test-Path $target) {
        if ($KeepData) {
            Get-ChildItem $target | Where-Object { $_.Name -ne 'data' } | Remove-Item -Recurse -Force
        } else {
            Remove-Item $target -Recurse -Force
        }
    }
    Write-Host 'BWOS was uninstalled.' -ForegroundColor Green
    return
}

# Where is the build to install?
if (-not $Source) {
    $here = Split-Path -Parent $PSScriptRoot
    foreach ($candidate in @($here, (Join-Path $here 'dist\BWOS'), $PSScriptRoot)) {
        if (Test-Path (Join-Path $candidate 'BWOS.exe')) { $Source = $candidate; break }
    }
}
if (-not $Source -or -not (Test-Path (Join-Path $Source 'BWOS.exe'))) {
    throw 'BWOS.exe not found. Build first (build.ps1) or pass -Source <folder>.'
}

Stop-Bwos
New-Item -ItemType Directory -Force $target | Out-Null
foreach ($item in 'BWOS.exe', 'ui', 'extensions') {
    $dest = Join-Path $target $item
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    Copy-Item (Join-Path $Source $item) $dest -Recurse
}

$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($shortcut)
$lnk.TargetPath = Join-Path $target 'BWOS.exe'
$lnk.WorkingDirectory = $target
$lnk.Description = 'BWOS – controller home screen'
$lnk.Save()

if ($Autostart) {
    Set-ItemProperty -Path $runKey -Name 'BWOS' -Value ('"' + (Join-Path $target 'BWOS.exe') + '" --background')
}

Write-Host "BWOS installed to $target" -ForegroundColor Green
Write-Host 'Start it from the Start menu, or press the Home button on your controller once it is running.'
