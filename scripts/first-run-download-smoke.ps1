param([Parameter(Mandatory)][string]$ModelRoot)
# Runs the real first-run download into an EMPTY model folder, cancels it once progress shows, and checks the partial file is kept.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/common.ps1"
New-Item -ItemType Directory -Path $ModelRoot -Force | Out-Null
if (Get-ChildItem -LiteralPath $ModelRoot -Force | Select-Object -First 1) { throw "$ModelRoot is not empty." }
$run = Join-Path $RepoRoot "artifacts/first-run-smoke/$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $run -Force | Out-Null
$previous = $env:TRIASR_DATA_ROOT; $previousModels = $env:TRIASR_MODEL_ROOT; $previousRoot = $env:DOTNET_ROOT
try {
  $env:TRIASR_DATA_ROOT = $run; $env:TRIASR_MODEL_ROOT = $ModelRoot
  $env:DOTNET_ROOT = Split-Path $script:Dotnet -Parent
  $app = Join-Path $RepoRoot 'src/TriAsr.App/bin/x64/Release/net10.0-windows/win-x64/TriAsr.App.exe'
  $process = Start-Process -FilePath $app -ArgumentList @('--smoke-test', '--first-run-download-smoke') -WindowStyle Hidden -PassThru
  if (!$process.WaitForExit(600000)) { $process.Kill($true); throw "Timed out. Logs: $run/Logs" }
  if ($process.ExitCode -ne 0) { throw "Failed ($($process.ExitCode)). Logs: $run/Logs" }
  Get-Content -LiteralPath (Join-Path $run 'first-run-download-smoke.json')
  "renders: $run/renders"
} finally { $env:TRIASR_DATA_ROOT = $previous; $env:TRIASR_MODEL_ROOT = $previousModels; $env:DOTNET_ROOT = $previousRoot }
