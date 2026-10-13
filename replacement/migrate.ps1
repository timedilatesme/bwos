param([Parameter(Mandatory=$true)][string]$SourceData,[switch]$IncludeBrowser,[switch]$IncludeVoiceAssets)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$target=[IO.Path]::GetFullPath((Join-Path $repo 'dist\BWOS-Next\data'))
$source=[IO.Path]::GetFullPath($SourceData).TrimEnd('\')
if(-not (Test-Path -LiteralPath $source -PathType Container)){throw 'Source data directory does not exist'}
if($source -eq $target -or $target.StartsWith($source+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Migration requires separate source and destination directories'}
if(Test-Path -LiteralPath $target){throw 'Replacement data already exists. Use a fresh build directory or keep the existing candidate; migration never overwrites data.'}
if(Get-Process BWOS-Next -ErrorAction SilentlyContinue){throw 'Close BWOS Next before migration'}
if($IncludeBrowser){
  $profile=Join-Path $source 'profiles'
  $usingProfile=Get-CimInstance Win32_Process | Where-Object { $_.Name -in @('msedge.exe','chrome.exe') -and $_.CommandLine -and $_.CommandLine.IndexOf($profile,[StringComparison]::OrdinalIgnoreCase) -ge 0 }
  if($usingProfile){throw 'Close the original BWOS browser before copying its profile. It remains the rollback installation.'}
}
# Assemble into a staging directory; the original source is always read-only.
$stage=Join-Path $repo ('dist\migration-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
foreach($entry in Get-ChildItem -LiteralPath $source){
  if($entry.Name -in @('profiles','tools','bwos.log','host-token','api-token','session.token','session.key')){continue}
  Copy-Item -LiteralPath $entry.FullName -Destination $stage -Recurse
}
if($IncludeBrowser -and (Test-Path -LiteralPath "$source\profiles")){Copy-Item -LiteralPath "$source\profiles" -Destination $stage -Recurse}
if($IncludeVoiceAssets -and (Test-Path -LiteralPath "$source\tools")){Copy-Item -LiteralPath "$source\tools" -Destination $stage -Recurse}
$configFile=Join-Path $stage 'config.json'
if(Test-Path -LiteralPath $configFile){
  $config=Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
  foreach($pair in @(@('port',47923),@('castEnabled',$false),@('airplayEnabled',$false),@('voiceEnabled',$false))){$config | Add-Member -NotePropertyName $pair[0] -NotePropertyValue $pair[1] -Force}
  $config | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath $configFile -Encoding UTF8
}
Move-Item -LiteralPath $stage -Destination $target
Write-Host "Imported into $target. Original data remains at $source."
Write-Host 'Voice/casting are disabled during parallel development. Copied sign-ins depend on browser and OS encryption; verify them in each original service.'
