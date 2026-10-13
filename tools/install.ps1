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
function Assert-InstallPath([string]$path) {
    $absolute=[IO.Path]::GetFullPath($path)
    $root=[IO.Path]::GetFullPath($target).TrimEnd('\')
    if($absolute -ne $root -and -not $absolute.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw "Path outside BWOS installation: $absolute"}
    if((Test-Path -LiteralPath $absolute) -and ((Get-Item -LiteralPath $absolute -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "Redirected installation path: $absolute"}
}

function Stop-Bwos {
    Get-Process BWOS -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$target*" } | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

if ($Uninstall) {
    Stop-Bwos
    Remove-ItemProperty -Path $runKey -Name 'BWOS' -ErrorAction SilentlyContinue
    if (Test-Path $shortcut) { Remove-Item $shortcut }
    if (Test-Path $target) {
        Assert-InstallPath $target
        if ($KeepData) {
            Get-ChildItem -LiteralPath $target | Where-Object { $_.Name -ne 'data' } | ForEach-Object {Assert-InstallPath $_.FullName;Remove-Item -LiteralPath $_.FullName -Recurse -Force}
        } else {
            Remove-Item -LiteralPath $target -Recurse -Force
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
# Keep a recoverable app payload without copying or changing personal data.
if(Test-Path -LiteralPath (Join-Path $target 'BWOS.exe')) {
    $backup=Join-Path $target ('backups\app-'+[Guid]::NewGuid().ToString('N'))
    Assert-InstallPath $backup
    New-Item -ItemType Directory -Force $backup | Out-Null
    foreach($item in 'BWOS.exe','ui','extensions','THIRD_PARTY_NOTICES.md'){if(Test-Path -LiteralPath (Join-Path $target $item)){Copy-Item -LiteralPath (Join-Path $target $item) -Destination $backup -Recurse}}
    Write-Host "Previous app files backed up to $backup"
}
foreach ($item in 'BWOS.exe', 'ui', 'extensions') {
    $dest = Join-Path $target $item
    Assert-InstallPath $dest
    if (Test-Path $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
    Copy-Item (Join-Path $Source $item) $dest -Recurse
}
if (Test-Path (Join-Path $Source 'THIRD_PARTY_NOTICES.md')) {
    Copy-Item (Join-Path $Source 'THIRD_PARTY_NOTICES.md') $target -Force
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
