. "$PSScriptRoot/common.ps1"
$exe = Join-Path $repoRoot 'src/TriAsr.App/bin/x64/Release/net10.0-windows/win-x64/TriAsr.App.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Build Release x64 first.' }
$smokeRoot = Join-Path $repoRoot ('artifacts/smoke/' + [guid]::NewGuid().ToString('N'))
$priorRoot = $env:TRIASR_DATA_ROOT
$env:TRIASR_DATA_ROOT = $smokeRoot
# The apphost must find the private runtime as well as the SDK.
$priorDotnetRoot = $env:DOTNET_ROOT
$env:DOTNET_ROOT = Split-Path $script:Dotnet -Parent
try {
    $process = Start-Process -FilePath $exe -ArgumentList '--smoke-test' -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(30000)) {
        Stop-Process -Id $process.Id -Force
        throw 'Bootstrap smoke timed out after 30 seconds.'
    }
    if ($process.ExitCode -ne 0) { throw "Bootstrap smoke failed: $($process.ExitCode)" }
    $report = Get-Content -LiteralPath (Join-Path $smokeRoot 'bootstrap-smoke.json') -Raw | ConvertFrom-Json
    if (-not $report.success -or -not $report.windowCreated -or $report.architecture -ne 'X64') { throw 'Invalid smoke report.' }
    if (-not (Get-ChildItem -LiteralPath (Join-Path $smokeRoot 'Logs') -Filter '*.clef')) { throw 'No structured log produced.' }
    $report | ConvertTo-Json
} finally {
    $env:TRIASR_DATA_ROOT = $priorRoot
    $env:DOTNET_ROOT = $priorDotnetRoot
}