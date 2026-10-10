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
function Assert-BuildPath([string]$path) {
    $resolved = [IO.Path]::GetFullPath($path)
    $allowed = [IO.Path]::GetFullPath((Join-Path $root 'dist')).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw "Build path outside dist: $resolved" }
}
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw ".NET Framework 4.x compiler not found at $csc" }

# Stop a running copy so the exe can be overwritten (data\ is left untouched).
Get-Process BWOS -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$out*" } | Stop-Process -Force
Start-Sleep -Milliseconds 300

New-Item -ItemType Directory -Force $out | Out-Null
if (-not (Test-Path "$root\assets\bwos.ico")) { & "$root\tools\make-icon.ps1" }

$sources = Get-ChildItem "$root\src\BWOS.Host\*.cs" | ForEach-Object { $_.FullName }
$refs = 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
        'System.Web.Extensions.dll', 'System.Management.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll'
$refs += Get-ChildItem (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Speech') -Filter System.Speech.dll -Recurse | Select-Object -First 1 -ExpandProperty FullName

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
    Assert-BuildPath $dest
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    Copy-Item (Join-Path $root $dir) $dest -Recurse
}

# One QR generator for both the BWOS screens and the streaming overlay.
Copy-Item "$root\extensions\bwos-tv\qr.js" "$out\ui\js\qr.js" -Force
Copy-Item "$root\THIRD_PARTY_NOTICES.md" $out -Force

Write-Host "Built $out\BWOS.exe" -ForegroundColor Green

if ($Zip) {
    $ver = (Select-String -Path "$root\src\BWOS.Host\App.cs" -Pattern 'Version = "([^"]+)"').Matches[0].Groups[1].Value
    $zipPath = Join-Path $root "dist\BWOS-$ver.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    # Ship without personal data (browser profiles, config, logs).
    $stage = Join-Path $root "dist\.stage-$ver"
    Assert-BuildPath $stage
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory $stage | Out-Null
    Copy-Item "$out\BWOS.exe", "$out\ui", "$out\extensions" $stage -Recurse
    Copy-Item "$root\tools\install.ps1", "$root\README.md", "$root\THIRD_PARTY_NOTICES.md" $stage
    New-Item -ItemType Directory (Join-Path $stage 'docs') | Out-Null
    Copy-Item "$root\docs\VOICE_AND_DEVICES.md", "$root\docs\QUALITY_PASS.md" (Join-Path $stage 'docs')
    $releaseNotes = Join-Path $root "docs\RELEASE_$ver.md"
    if (Test-Path $releaseNotes) { Copy-Item $releaseNotes (Join-Path $stage 'docs') }
    Compress-Archive -Path "$stage\*" -DestinationPath $zipPath
    Remove-Item $stage -Recurse -Force
    Write-Host "Packaged $zipPath" -ForegroundColor Green
}

if ($Run) { Start-Process "$out\BWOS.exe" }
