param([switch]$Run,[switch]$Test,[switch]$Primary,[switch]$Zip)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$output=Join-Path $repo $(if($Primary){'dist\BWOS'}else{'dist\BWOS-Next'})
$exeName=if($Primary){'BWOS.exe'}else{'BWOS-Next.exe'}
$version=(Select-String -Path "$PSScriptRoot\host\App.cs" -Pattern 'Version = "([^"]+)"').Matches[0].Groups[1].Value
function Assert-BuildPath([string]$path){
  $absolute=[IO.Path]::GetFullPath($path)
  $allowed=[IO.Path]::GetFullPath((Join-Path $repo 'dist')).TrimEnd('\')+'\'
  if(-not $absolute.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw "Build path outside dist: $absolute"}
  if((Test-Path -LiteralPath $absolute) -and ((Get-Item -LiteralPath $absolute -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "Redirected build path: $absolute"}
}
Assert-BuildPath $output
$running=Get-Process ([IO.Path]::GetFileNameWithoutExtension($exeName)) -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq (Join-Path $output $exeName)}
if($running){throw "Close $exeName before rebuilding. Its data will be preserved."}
New-Item -ItemType Directory -Force $output | Out-Null
$stage=Join-Path $repo ('dist\.build-'+[Guid]::NewGuid().ToString('N'))
Assert-BuildPath $stage
New-Item -ItemType Directory -Force "$stage\ui\js","$stage\extensions\bwos-tv" | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$refs=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Management.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll')
$refs+=Get-ChildItem (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Speech') -Filter System.Speech.dll -Recurse | Select-Object -First 1 -ExpandProperty FullName
$source=@(Get-ChildItem "$repo\src\BWOS.Host\*.cs" | Where-Object Name -notin @('App.cs','Program.cs','Config.cs','ShellManager.cs','PlaybackService.cs','PhoneRemote.cs','InputBridge.cs') | ForEach-Object FullName)
$source+=Get-ChildItem "$PSScriptRoot\host\*.cs" | ForEach-Object FullName
$defines=if($Primary){'/define:BWOS_PRIMARY'}else{'/define:BWOS_NEXT'}
try {
  & $compiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output $defines "/out:$stage\$exeName" "/win32icon:$repo\assets\bwos.ico" "/win32manifest:$repo\src\BWOS.Host\app.manifest" ($refs | ForEach-Object {"/reference:$_"}) $source
  if($LASTEXITCODE -ne 0){throw 'Replacement compilation failed'}
  # Keep the established Liquid Glass UI and controller/focus engine intact.
  Copy-Item "$repo\ui\*" "$stage\ui" -Recurse -Force
  Copy-Item "$repo\extensions\bwos-tv\*.js","$repo\extensions\bwos-tv\*.css","$repo\extensions\bwos-tv\rules.json" "$stage\extensions\bwos-tv" -Force
  Copy-Item "$repo\extensions\bwos-tv\qr.js" "$stage\ui\js" -Force
  Copy-Item "$PSScriptRoot\ui\js\core-compat.js","$PSScriptRoot\ui\js\screen-viewer.js" "$stage\ui\js" -Force
  $index=Join-Path $stage 'ui\index.html'
  $text=Get-Content -LiteralPath $index -Raw
  $text=$text.Replace('<script type="module" src="js/app.js"></script>','<script type="module" src="js/core-compat.js"></script>' + "`n" + '  <script type="module" src="js/app.js"></script>')
  if(-not $Primary){$text=$text.Replace('<title>BWOS</title>','<title>BWOS Next</title>')}
  $text | Set-Content -LiteralPath $index -Encoding UTF8
  Copy-Item "$PSScriptRoot\extension\*" "$stage\extensions\bwos-tv" -Recurse -Force
  Copy-Item "$PSScriptRoot\core\kernel.js" "$stage\extensions\bwos-tv" -Force
  if($Primary){
    $homeBridge=Join-Path $stage 'extensions\bwos-tv\home.js'
    $text=Get-Content -LiteralPath $homeBridge -Raw
    $text.Replace('http://127.0.0.1:47923','http://127.0.0.1:47913') | Set-Content -LiteralPath $homeBridge -Encoding UTF8
    $index=Join-Path $stage 'ui\index.html'
    $text=Get-Content -LiteralPath $index -Raw
    $text.Replace('<title>BWOS Next</title>','<title>BWOS</title>').Replace('<small>NEXT</small>','<small>2.0</small>') | Set-Content -LiteralPath $index -Encoding UTF8
  }
  if($Test){
    & $compiler /nologo /target:exe /utf8output /main:Bwos.NextTests $defines "/out:$stage\NextTests.exe" ($refs | ForEach-Object {"/reference:$_"}) $source "$PSScriptRoot\tests\NextTests.cs"
    if($LASTEXITCODE -ne 0){throw 'Replacement test compilation failed'}
    & "$stage\NextTests.exe"
    if($LASTEXITCODE -ne 0){throw 'Replacement tests failed'}
  }
  # Only executable/UI/adapter payloads are replaced. data/ is never copied or deleted.
  foreach($dir in @('ui','extensions')){
    $dest=Join-Path $output $dir;Assert-BuildPath $dest
    if(Test-Path -LiteralPath $dest){Remove-Item -LiteralPath $dest -Recurse -Force}
    Copy-Item -LiteralPath (Join-Path $stage $dir) -Destination $output -Recurse
  }
  Copy-Item -LiteralPath "$stage\$exeName" -Destination "$output\$exeName" -Force
  Copy-Item -LiteralPath "$repo\THIRD_PARTY_NOTICES.md" -Destination $output -Force
  Write-Host "Built $output\$exeName"
  if($Zip){
    if(-not $Primary){throw 'Release packaging uses the primary BWOS build'}
    $package=Join-Path $stage 'package'
    New-Item -ItemType Directory -Force "$package\docs" | Out-Null
    Copy-Item "$output\BWOS.exe","$output\ui","$output\extensions" $package -Recurse
    Copy-Item "$repo\README.md","$repo\THIRD_PARTY_NOTICES.md","$repo\LICENSE","$repo\tools\install.ps1" $package
    Copy-Item "$repo\docs\RELEASE_$version.md","$repo\docs\VOICE_AND_DEVICES.md" "$package\docs"
    Copy-Item "$PSScriptRoot\README.md" "$package\docs\ARCHITECTURE.md"
    Copy-Item "$PSScriptRoot\VERIFICATION.md" "$package\docs\VERIFICATION.md"
    $zipPath=Join-Path $repo "dist\BWOS-$version.zip"
    Compress-Archive -Path "$package\*" -DestinationPath $zipPath -Force
    $hash=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  BWOS-$version.zip" | Set-Content -LiteralPath "$zipPath.sha256" -Encoding ascii
    Write-Host "Packaged $zipPath"
  }
} finally {Assert-BuildPath $stage;if(Test-Path -LiteralPath $stage){Remove-Item -LiteralPath $stage -Recurse -Force}}
if($Run){Start-Process -FilePath "$output\$exeName" -WindowStyle Hidden}
