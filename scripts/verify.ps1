$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/build.ps1"
& "$PSScriptRoot/test.ps1"
& "$PSScriptRoot/smoke.ps1"
& "$PSScriptRoot/edition-smoke.ps1" -Editions Server, Client   # Studio is covered by smoke.ps1
