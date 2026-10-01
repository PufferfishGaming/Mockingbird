$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$localDotnet = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
$script:Dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.tools/cli-home'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.tools/nuget-packages'
Set-Location -LiteralPath $repoRoot
function Invoke-Dotnet {
    & $script:Dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}