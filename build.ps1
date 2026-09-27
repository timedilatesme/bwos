<#
.SYNOPSIS
  Builds BWOS into dist\BWOS using only what ships with Windows
  (the .NET Framework 4.8 C# compiler). No SDK, Node or Visual Studio needed.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\build.ps1
  powershell -ExecutionPolicy Bypass -File .\build.ps1 -Run
  powershell -ExecutionPolicy Bypass -File .\build.ps1 -Zip
#>
param(
    [switch]$Run,   # start the freshly built app
    [switch]$Zip    # also produce dist\BWOS-<version>.zip for sharing
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'dist\BWOS'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw ".NET Framework 4.x compiler not found at $csc" }

# Stop a running copy so the exe can be overwritten (data\ is left untouched).
Get-Process BWOS -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$out*" } | Stop-Process -Force
Start-Sleep -Milliseconds 300

New-Item -ItemType Directory -Force $out | Out-Null
if (-not (Test-Path "$root\assets\bwos.ico")) { & "$root\tools\make-icon.ps1" }

$sources = Get-ChildItem "$root\src\BWOS.Host\*.cs" | ForEach-Object { $_.FullName }
$refs = 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
        'System.Web.Extensions.dll', 'System.Management.dll'

Write-Host "Compiling host..." -ForegroundColor Cyan
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /utf8output `
    "/out:$out\BWOS.exe" `
    "/win32icon:$root\assets\bwos.ico" `
    "/win32manifest:$root\src\BWOS.Host\app.manifest" `
    ($refs | ForEach-Object { "/reference:$_" }) `
    $sources
if ($LASTEXITCODE -ne 0) { throw "Compilation failed" }

Write-Host "Copying UI and extensions..." -ForegroundColor Cyan
foreach ($dir in 'ui', 'extensions') {
    $dest = Join-Path $out $dir
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    Copy-Item (Join-Path $root $dir) $dest -Recurse
}

# One QR generator for both the BWOS screens and the streaming overlay.
Copy-Item "$root\extensions\bwos-tv\qr.js" "$out\ui\js\qr.js" -Force

Write-Host "Built $out\BWOS.exe" -ForegroundColor Green

if ($Zip) {
    $ver = (Select-String -Path "$root\src\BWOS.Host\App.cs" -Pattern 'Version = "([^"]+)"').Matches[0].Groups[1].Value
    $zipPath = Join-Path $root "dist\BWOS-$ver.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    # Ship without personal data (browser profiles, config, logs).
    $stage = Join-Path $root "dist\.stage-$ver"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory $stage | Out-Null
    Copy-Item "$out\BWOS.exe", "$out\ui", "$out\extensions" $stage -Recurse
    Copy-Item "$root\tools\install.ps1", "$root\README.md" $stage
    Compress-Archive -Path "$stage\*" -DestinationPath $zipPath
    Remove-Item $stage -Recurse -Force
    Write-Host "Packaged $zipPath" -ForegroundColor Green
}

if ($Run) { Start-Process "$out\BWOS.exe" }
