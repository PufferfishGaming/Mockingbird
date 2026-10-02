param([string]$Version = '', [string]$Source = 'H:\Whisper\felvetel.wav', [ValidateSet('Studio', 'Server', 'Client')][string]$Edition = 'Studio')
. "$PSScriptRoot/common.ps1"
$info = Get-EditionInfo $Edition
$releaseVersion = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if (-not $Version) { $Version = $releaseVersion }
if ($Version -ne $releaseVersion) { throw 'Package version must match Directory.Build.props.' }
$parsedVersion = $null
if (-not [Version]::TryParse($Version, [ref]$parsedVersion)) { throw 'Invalid package version.' }
$packageRoot = Get-EditionPackageRoot $Version $info
$extracted = Join-Path $packageRoot 'msi-extracted'
if (Test-Path -LiteralPath $extracted) { throw 'MSI extraction already exists; preserve this verification snapshot.' }
$msi = Join-Path $packageRoot "Mockingbird-$($info.Name)-$Version-win-x64.msi"
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
$marker = (Get-Content -LiteralPath (Join-Path $payload 'edition.txt') -Raw).Trim()
if ($marker -ne $Edition.ToLowerInvariant()) { throw "The package says it is '$marker', not $Edition." }
if (-not $info.Engines -and (Test-Path -LiteralPath (Join-Path $payload 'Runtimes'))) { throw 'The client package must not carry the speech programs.' }
$priorData=$env:TRIASR_DATA_ROOT; $priorModel=$env:TRIASR_MODEL_ROOT; $priorRuntime=$env:TRIASR_RUNTIME_ROOT; $priorDotnet=$env:DOTNET_ROOT; $priorEdition=$env:TRIASR_EDITION
try {
    $env:DOTNET_ROOT=''; $env:TRIASR_RUNTIME_ROOT=''; $env:TRIASR_MODEL_ROOT=$repoRoot; $env:TRIASR_EDITION=''
    # The program finds out which edition it is from the marker in the payload, as it does once installed.
    $env:TRIASR_DATA_ROOT=Join-Path $packageRoot 'msi-edition-smoke'
    Write-SmokeSettings $env:TRIASR_DATA_ROOT $Edition
    $process=Start-Process -FilePath (Join-Path $payload 'TriAsr.App.exe') -ArgumentList @('--smoke-test') -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(120000)) { $process.Kill($true); throw 'MSI payload smoke timeout.' }
    if ($process.ExitCode -ne 0) { throw 'MSI payload smoke failed. Inspect msi-edition-smoke/Logs.' }
    $started=Get-Content -LiteralPath (Join-Path $env:TRIASR_DATA_ROOT 'bootstrap-smoke.json') -Raw | ConvertFrom-Json
    if ($started.edition -ne $Edition) { throw "The MSI payload started as $($started.edition), not $Edition." }
    $report=[ordered]@{Edition=$Edition;VerifiedFiles=$manifest.Count;Renders=$started.shellMatrix;MsiSha256=(Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash;Utc=[DateTimeOffset]::UtcNow}
    if ($info.Engines) {
        # The speech programs are the same in Studio and Server, and the real-audio pipeline smoke is Studio's own start-up path.
        $env:TRIASR_EDITION='studio'
        $env:TRIASR_DATA_ROOT=Join-Path $packageRoot 'msi-pipeline-smoke'
        $process=Start-Process -FilePath (Join-Path $payload 'TriAsr.App.exe') -ArgumentList @('--smoke-test','--pipeline-smoke',('"'+$Source+'"')) -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit(180000)) { $process.Kill($true); throw 'MSI payload pipeline timeout.' }
        if ($process.ExitCode -ne 0) { throw 'MSI payload pipeline failed. Inspect msi-pipeline-smoke/Logs.' }
        $result=Get-Content -LiteralPath (Join-Path $env:TRIASR_DATA_ROOT 'pipeline-smoke.json') -Raw | ConvertFrom-Json
        $report.State=$result.job.State; $report.Language=$result.result.Language; $report.Regions=$result.result.Regions.Count; $report.CheckpointReused=$result.checkpointReused; $report.Source=$Source
    }
    $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageRoot 'msi-verification.json') -Encoding utf8
    $report | ConvertTo-Json
} finally { $env:TRIASR_DATA_ROOT=$priorData; $env:TRIASR_MODEL_ROOT=$priorModel; $env:TRIASR_RUNTIME_ROOT=$priorRuntime; $env:DOTNET_ROOT=$priorDotnet; $env:TRIASR_EDITION=$priorEdition }
