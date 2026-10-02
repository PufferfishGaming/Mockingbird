param(
    [string[]]$Editions = @('Studio', 'Server', 'Client'),
    [string]$Executable = ''
)
# Starts each edition hidden with its own data folder, lets it render its window in every interface language and checks its report.
# The Server edition is given loopback-only settings, so that the smoke does not open the computer to the network (Write-SmokeSettings).
. "$PSScriptRoot/common.ps1"
$exe = if ($Executable) { $Executable } else { Join-Path $repoRoot 'src/TriAsr.App/bin/x64/Release/net10.0-windows/win-x64/TriAsr.App.exe' }
if (-not (Test-Path -LiteralPath $exe)) { throw 'Build Release x64 first.' }
$priorRoot = $env:TRIASR_DATA_ROOT
$priorEdition = $env:TRIASR_EDITION
$priorDotnetRoot = $env:DOTNET_ROOT
$env:DOTNET_ROOT = Split-Path $script:Dotnet -Parent
$results = @()
try {
    foreach ($edition in $Editions) {
        $smokeRoot = Join-Path $repoRoot ('artifacts/smoke/' + $edition.ToLowerInvariant() + '-' + [guid]::NewGuid().ToString('N'))
        Write-SmokeSettings $smokeRoot $edition
        $env:TRIASR_DATA_ROOT = $smokeRoot
        $env:TRIASR_EDITION = $edition.ToLowerInvariant()
        $process = Start-Process -FilePath $exe -ArgumentList '--smoke-test' -PassThru -WindowStyle Hidden
        if (-not $process.WaitForExit(120000)) { Stop-Process -Id $process.Id -Force; throw "$edition smoke timed out." }
        if ($process.ExitCode -ne 0) { throw "$edition smoke failed: $($process.ExitCode)" }
        $report = Get-Content -LiteralPath (Join-Path $smokeRoot 'bootstrap-smoke.json') -Raw | ConvertFrom-Json
        if (-not $report.success -or -not $report.windowCreated -or $report.architecture -ne 'X64') { throw "Invalid $edition smoke report." }
        if ($report.edition -ne $edition) { throw "The $edition smoke ran as $($report.edition)." }
        if (-not (Get-ChildItem -LiteralPath (Join-Path $smokeRoot 'Logs') -Filter '*.clef')) { throw "$edition produced no structured log." }
        $results += [pscustomobject]@{ edition = $edition; renders = $report.shellMatrix; folder = $smokeRoot }
    }
    $results | ConvertTo-Json
} finally {
    $env:TRIASR_DATA_ROOT = $priorRoot
    $env:TRIASR_EDITION = $priorEdition
    $env:DOTNET_ROOT = $priorDotnetRoot
}
