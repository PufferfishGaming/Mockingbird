$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/common.ps1"
$run = Join-Path $RepoRoot "artifacts/api-smoke/$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $run -Force | Out-Null
$previous = $env:TRIASR_DATA_ROOT
$previousRoot = $env:DOTNET_ROOT
try {
  $env:TRIASR_DATA_ROOT = $run
  $env:DOTNET_ROOT = Split-Path $script:Dotnet -Parent
  $app = Join-Path $RepoRoot 'src/TriAsr.App/bin/x64/Release/net10.0-windows/win-x64/TriAsr.App.exe'
  $process = Start-Process -FilePath $app -ArgumentList @('--smoke-test', '--api-smoke') -WindowStyle Hidden -PassThru
  if (!$process.WaitForExit(1800000)) { $process.Kill($true); throw "Network API smoke timed out. Logs: $run/Logs" }
  if ($process.ExitCode -ne 0) { throw "Network API smoke failed ($($process.ExitCode)). Logs: $run/Logs" }
  Get-Content -LiteralPath (Join-Path $run 'api-smoke.json')
  "renders: $run/renders"
} finally { $env:TRIASR_DATA_ROOT = $previous; $env:DOTNET_ROOT = $previousRoot }
