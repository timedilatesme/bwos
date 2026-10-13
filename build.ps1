<# Builds the rebuilt BWOS 2.x app by default. -Legacy builds the preserved 1.8.1 app separately. #>
param([switch]$Run,[switch]$Zip,[switch]$Test,[switch]$Legacy)
$ErrorActionPreference='Stop'
if($Legacy){& "$PSScriptRoot\tools\build-legacy.ps1" -Run:$Run -Zip:$Zip}
else {& "$PSScriptRoot\replacement\build.ps1" -Primary -Run:$Run -Zip:$Zip -Test:$Test}
