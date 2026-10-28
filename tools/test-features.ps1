<# Default tests never capture the microphone, launch games, or change Bluetooth connections.
   -InstallVoice downloads optional assets. -Audio uses generated English speech.
   -Devices reads hardware inventory. -Microphone probes live capture for six seconds without saving audio. #>
param([switch]$InstallVoice, [switch]$Audio, [switch]$Devices, [switch]$Whisper, [switch]$Cpu, [switch]$PlaybackSocket, [switch]$Microphone, [switch]$StreamHttp, [switch]$Lifecycle)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testOutput = Join-Path $projectRoot 'dist\verification'
New-Item -ItemType Directory -Force $testOutput | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$speechLibrary = Get-ChildItem (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Speech') -Filter System.Speech.dll -Recurse | Select-Object -First 1 -ExpandProperty FullName
$references = 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll', 'System.Management.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll', $speechLibrary
$testExe = Join-Path $testOutput 'FeatureTests.exe'
$sourceFiles = Get-ChildItem (Join-Path $projectRoot 'src\BWOS.Host\*.cs') | ForEach-Object FullName
& $compiler /nologo /target:exe /platform:anycpu /utf8output /main:Bwos.FeatureTests "/out:$testExe" ($references | ForEach-Object { "/reference:$_" }) $sourceFiles (Join-Path $projectRoot 'tests\FeatureTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
if ($InstallVoice) { & $testExe --install; if ($LASTEXITCODE -ne 0) { throw 'Voice setup failed' } }
$testArguments = @()
if ($Audio) { $testArguments += '--audio' }
if ($Devices) { $testArguments += '--devices' }
if ($Whisper) { $testArguments += '--whisper' }
if ($Cpu) { $testArguments += '--cpu' }
if ($PlaybackSocket) { $testArguments += '--websocket' }
if ($Microphone) { $testArguments += '--microphone' }
if ($StreamHttp) { $testArguments += '--stream-http' }
if ($Lifecycle) { $testArguments += '--lifecycle' }
& $testExe @testArguments
if ($LASTEXITCODE -ne 0) { throw 'Feature tests failed' }
