param([ValidateSet('Studio', 'Server', 'Client', 'All')][string]$Edition = 'All')
# Starts the Release build of an edition (or all three) so that it can be looked at. Studio uses the usual data and models; Server and Client keep their
# settings under artifacts/try/<edition>, and Server uses the models in the repository folder so that it can really transcribe.
# Build first (scripts/build.ps1). Delete artifacts/try/<edition> to start that edition fresh.
. "$PSScriptRoot/common.ps1"
$exe = Join-Path $repoRoot 'src/TriAsr.App/bin/x64/Release/net10.0-windows/win-x64/TriAsr.App.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Build Release x64 first (scripts/build.ps1).' }
$env:DOTNET_ROOT = Split-Path $script:Dotnet -Parent
$names = if ($Edition -eq 'All') { @('Studio', 'Server', 'Client') } else { @($Edition) }
foreach ($name in $names) {
    $env:TRIASR_EDITION = $name.ToLowerInvariant()
    $env:TRIASR_DATA_ROOT = if ($name -eq 'Studio') { $null } else { Join-Path $repoRoot "artifacts/try/$($name.ToLowerInvariant())" }
    $env:TRIASR_MODEL_ROOT = if ($name -eq 'Server') { $repoRoot } else { $null }
    $process = Start-Process -FilePath $exe -PassThru
    "$name started (process $($process.Id))"
}
