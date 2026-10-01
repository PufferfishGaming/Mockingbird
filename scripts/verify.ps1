$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/build.ps1"
& "$PSScriptRoot/test.ps1"
& "$PSScriptRoot/smoke.ps1"