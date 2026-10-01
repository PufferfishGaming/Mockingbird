param([string]$Version = '', [string]$Source = 'H:\Whisper\felvetel.wav')
. "$PSScriptRoot/common.ps1"
$releaseVersion = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if (-not $Version) { $Version = $releaseVersion }
if ($Version -ne $releaseVersion) { throw 'Package version must match Directory.Build.props.' }
$parsedVersion = $null
if (-not [Version]::TryParse($Version, [ref]$parsedVersion)) { throw 'Invalid package version.' }
$packageRoot = Join-Path $repoRoot "artifacts/packages/$Version"
$extracted = Join-Path $packageRoot 'msi-extracted'
if (Test-Path -LiteralPath $extracted) { throw 'MSI extraction already exists; preserve this verification snapshot.' }
$msi = Join-Path $packageRoot "Mockingbird-Studio-$Version-win-x64.msi"
$process = Start-Process msiexec.exe -ArgumentList @('/a',('"'+$msi+'"'),'/qn',('TARGETDIR="'+$extracted+'"'),'/l*v',('"'+(Join-Path $packageRoot 'admin-extract.log')+'"')) -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(60000)) { throw 'Administrative extraction timed out.' }
if ($process.ExitCode -ne 0) { throw "Administrative extraction failed: $($process.ExitCode)" }
$payload = Split-Path (Get-ChildItem -LiteralPath $extracted -Recurse -Filter TriAsr.App.exe | Select-Object -First 1).FullName
$manifest = Get-Content -LiteralPath (Join-Path $payload 'integrity.json') -Raw | ConvertFrom-Json
foreach ($entry in $manifest) {
    $file = [IO.Path]::GetFullPath((Join-Path $payload $entry.path))
    if (-not $file.StartsWith($payload + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid integrity path.' }
    if (-not (Test-Path -LiteralPath $file) -or (Get-Item -LiteralPath $file).Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Integrity mismatch: $($entry.path)" }
}
$priorData=$env:TRIASR_DATA_ROOT; $priorModel=$env:TRIASR_MODEL_ROOT; $priorRuntime=$env:TRIASR_RUNTIME_ROOT; $priorDotnet=$env:DOTNET_ROOT
try {
    $env:DOTNET_ROOT=''; $env:TRIASR_RUNTIME_ROOT=''; $env:TRIASR_MODEL_ROOT=$repoRoot
    $env:TRIASR_DATA_ROOT=Join-Path $packageRoot 'msi-pipeline-smoke'
    $process=Start-Process -FilePath (Join-Path $payload 'TriAsr.App.exe') -ArgumentList @('--smoke-test','--pipeline-smoke',('"'+$Source+'"')) -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(180000)) { $process.Kill($true); throw 'MSI payload pipeline timeout.' }
    if ($process.ExitCode -ne 0) { throw 'MSI payload pipeline failed. Inspect msi-pipeline-smoke/Logs.' }
    $result=Get-Content -LiteralPath (Join-Path $env:TRIASR_DATA_ROOT 'pipeline-smoke.json') -Raw | ConvertFrom-Json
    $report=[ordered]@{VerifiedFiles=$manifest.Count;State=$result.job.State;Language=$result.result.Language;Regions=$result.result.Regions.Count;CheckpointReused=$result.checkpointReused;MsiSha256=(Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash;Source=$Source;Utc=[DateTimeOffset]::UtcNow}
    $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageRoot 'msi-verification.json') -Encoding utf8
    $report | ConvertTo-Json
} finally { $env:TRIASR_DATA_ROOT=$priorData; $env:TRIASR_MODEL_ROOT=$priorModel; $env:TRIASR_RUNTIME_ROOT=$priorRuntime; $env:DOTNET_ROOT=$priorDotnet }
