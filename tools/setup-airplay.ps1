<#
.SYNOPSIS
  Sets up AirPlay screen mirroring for BWOS: builds UxPlay (open source, GPL-3.0) from source with
  a private MSYS2 environment, all inside BWOS's data\tools folder. Nothing is installed system-wide
  and no administrator rights are needed. BWOS starts UxPlay by itself once it's there.

  Downloads (from the projects' official sources): the MSYS2 base archive (~41 MB), MSYS2 packages
  (~600 MB: compiler, CMake, GStreamer and plugins) and the UxPlay source (~2 MB). The compiler and
  build tools are removed afterwards; the GStreamer runtime stays (~1 GB).

  The first time UxPlay runs, Windows asks to allow it through the firewall: allow it on the kind of
  network your Wi-Fi is set to (Private or Public).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\tools\setup-airplay.ps1
  powershell -ExecutionPolicy Bypass -File .\tools\setup-airplay.ps1 -DataDir "$env:LOCALAPPDATA\Programs\BWOS\data"
#>
param(
    [string]$DataDir,                                                    # BWOS data folder (default: the installed or built copy)
    [string]$UxPlayCommit = '45607fef322f3e5fe776d011e6164c043ac68857', # tested UxPlay 1.74 (own mDNS, no Bonjour needed)
    [switch]$KeepBuildTools
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ProgressPreference = 'SilentlyContinue'

if (-not $DataDir) {
    $root = Split-Path -Parent $PSScriptRoot
    $installed = Join-Path $env:LOCALAPPDATA 'Programs\BWOS\data'
    $DataDir = if (Test-Path (Join-Path $env:LOCALAPPDATA 'Programs\BWOS\BWOS.exe')) { $installed } else { Join-Path $root 'dist\BWOS\data' }
}
$tools = Join-Path $DataDir 'tools'
$msys = Join-Path $tools 'msys64'
$bash = Join-Path $msys 'usr\bin\bash.exe'
New-Item -ItemType Directory -Force $tools | Out-Null
Write-Host "BWOS data folder: $DataDir" -ForegroundColor Cyan

function Msys([string]$cmd) {
    $env:MSYSTEM = 'UCRT64'; $env:CHERE_INVOKING = '1'
    & $bash -lc $cmd
    if ($LASTEXITCODE -ne 0) { throw "MSYS2 command failed: $cmd" }
}

# 1. MSYS2 (portable: the self-extracting base archive, signed by the MSYS2 maintainer)
if (-not (Test-Path $bash)) {
    Write-Host 'Downloading MSYS2...' -ForegroundColor Cyan
    $sfx = Join-Path $tools 'msys2-base.sfx.exe'
    Invoke-WebRequest 'https://github.com/msys2/msys2-installer/releases/download/nightly-x86_64/msys2-base-x86_64-latest.sfx.exe' -OutFile $sfx -UseBasicParsing
    if ((Get-AuthenticodeSignature $sfx).Status -ne 'Valid') { throw 'The MSYS2 download is not validly signed' }
    & $sfx -y "-o$tools" | Out-Null
    [IO.File]::Delete($sfx)
    Msys 'true'   # first start: keyring and home folder
}

# 2. Packages
Write-Host 'Installing packages (compiler, CMake, GStreamer)...' -ForegroundColor Cyan
Msys 'pacman -Syuu --noconfirm'
Msys 'pacman -Syuu --noconfirm'
$runtime = 'mingw-w64-ucrt-x86_64-libplist mingw-w64-ucrt-x86_64-openssl mingw-w64-ucrt-x86_64-gstreamer mingw-w64-ucrt-x86_64-gst-plugins-base mingw-w64-ucrt-x86_64-gst-plugins-good mingw-w64-ucrt-x86_64-gst-plugins-bad mingw-w64-ucrt-x86_64-gst-libav'
$build = 'git mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-cmake mingw-w64-ucrt-x86_64-ninja mingw-w64-ucrt-x86_64-pkgconf'
Msys "pacman -S --needed --noconfirm $build $runtime"

# 3. UxPlay, at a tested commit
Write-Host 'Building UxPlay...' -ForegroundColor Cyan
$src = '/tmp/uxplay'
Msys "rm -rf $src && git clone --quiet https://github.com/FDH2/UxPlay.git $src && cd $src && git checkout --quiet $UxPlayCommit"
Msys "cd $src && mkdir -p build && cd build && cmake -G Ninja -DNO_MARCH_NATIVE=ON .. > /dev/null && ninja"
$out = Join-Path $tools 'uxplay'
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item (Join-Path $msys 'tmp\uxplay\build\uxplay.exe') $out -Force
Copy-Item (Join-Path $msys 'tmp\uxplay\LICENSE') (Join-Path $out 'LICENSE.txt') -Force
Msys "rm -rf $src"

# 4. Tidy up: keep only what UxPlay needs to run
if (-not $KeepBuildTools) {
    Write-Host 'Removing build tools...' -ForegroundColor Cyan
    Msys 'pacman -Rns --noconfirm mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-cmake mingw-w64-ucrt-x86_64-ninja mingw-w64-ucrt-x86_64-pkgconf git || true'
    Msys 'pacman -Scc --noconfirm > /dev/null'
}

Write-Host "AirPlay is set up: $out\uxplay.exe" -ForegroundColor Green
Write-Host 'Restart BWOS (tray icon > Exit, then start it again). Your iPhone/Mac will list it under Screen Mirroring.'
