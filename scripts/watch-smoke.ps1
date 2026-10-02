$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/common.ps1"
$run = Join-Path $RepoRoot "artifacts/watch-smoke/$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $run -Force | Out-Null
$previous = $env:TRIASR_DATA_ROOT
$previousRoot = $env:DOTNET_ROOT
try {
  $env:TRIASR_DATA_ROOT = $run
  $env:DOTNET_ROOT = Split-Path $script:Dotnet -Parent
  $app = Join-Path $RepoRoot 'src/TriAsr.App/bin/x64/Release/net10.0-windows/win-x64/TriAsr.App.exe'
  $process = Start-Process -FilePath $app -ArgumentList @('--smoke-test', '--watch-smoke') -WindowStyle Hidden -PassThru
  if (!$process.WaitForExit(1500000)) { $process.Kill($true); throw "Watch folder smoke timed out. Logs: $run/Logs" }
  if ($process.ExitCode -ne 0) { throw "Watch folder smoke failed ($($process.ExitCode)). Logs: $run/Logs" }
  Get-Content -LiteralPath (Join-Path $run 'watch-smoke.json')
  "renders: $run/renders"
} finally { $env:TRIASR_DATA_ROOT = $previous; $env:DOTNET_ROOT = $previousRoot }
