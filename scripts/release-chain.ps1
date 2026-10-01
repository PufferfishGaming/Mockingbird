<#
.SYNOPSIS
  Builds and verifies a release candidate in one go: verify, package, build-msi, build-setup, verify-package, release-audit.
.DESCRIPTION
  Reads the version from Directory.Build.props. A version is never rebuilt: if artifacts/packages/<version> already exists
  the script stops and asks for a new version, because the installer hash of a published version must not change.
  Writes a log to artifacts/release-logs/<version>.log and stops at the first failing step.
  -Notes is the short plain-language summary shown in the update banner of installed apps (latest.json).
  -DryRun checks the version and the README and prints the plan without building anything.
  Nothing is uploaded or pushed by this script; see docs/RELEASE_PROCESS.md for the upload order.
#>
param([string]$Notes = '', [switch]$DryRun)
. "$PSScriptRoot/common.ps1"
$ErrorActionPreference = 'Stop'
$version = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$package = Join-Path $repoRoot "artifacts/packages/$version"
if (Test-Path -LiteralPath $package) { throw "artifacts/packages/$version already exists. Bump <Version> in Directory.Build.props (and the version line in README.md): a built version is never rebuilt." }
if (-not (Select-String -LiteralPath (Join-Path $repoRoot 'README.md') -Pattern ([regex]::Escape("Version $version")) -Quiet)) { throw "README.md does not mention 'Version $version'. Update its version line before releasing." }
if ([string]::IsNullOrWhiteSpace($Notes)) { Write-Warning 'No -Notes given: the update banner will show no release notes.' }
$steps = @('verify', 'package', 'build-msi', 'build-setup', 'verify-package', 'release-audit')
if ($DryRun) { "Dry run for version $version. Would run: $($steps -join ' -> ')"; "Log: artifacts/release-logs/$version.log"; return }

New-Item -ItemType Directory -Force -Path (Join-Path $repoRoot 'artifacts/release-logs') | Out-Null
$log = Join-Path $repoRoot "artifacts/release-logs/$version.log"
"Release chain for $version started $(Get-Date -Format s)" | Set-Content -LiteralPath $log -Encoding utf8
$ErrorActionPreference = 'Continue'
foreach ($step in $steps) {
    "=== $step $(Get-Date -Format T) ===" | Add-Content -LiteralPath $log -Encoding utf8
    Write-Host "=== $step ===" -ForegroundColor Cyan
    try {
        $arguments = if ($step -eq 'build-setup') { @{ Notes = $Notes } } else { @{} }
        & (Join-Path $PSScriptRoot "$step.ps1") @arguments *>&1 | Out-String -Width 250 | Add-Content -LiteralPath $log -Encoding utf8
        "=== $step OK ===" | Add-Content -LiteralPath $log -Encoding utf8
    }
    catch {
        "=== $step FAILED: $($_.Exception.Message) ===" | Add-Content -LiteralPath $log -Encoding utf8
        throw "Step '$step' failed: $($_.Exception.Message). See $log"
    }
}
$audit = Get-Content -LiteralPath (Join-Path $package 'release-audit.json') -Raw | ConvertFrom-Json
$failed = @($audit.Checks | Where-Object { -not $_.Passed })
Write-Host "Release audit: $(@($audit.Checks).Count - $failed.Count) of $(@($audit.Checks).Count) automated checks passed." -ForegroundColor $(if ($failed.Count) { 'Red' } else { 'Green' })
Write-Host 'Upload to the GitHub release in this order (latest.json last):' -ForegroundColor Cyan
foreach ($name in 'Mockingbird-Studio-Setup.exe', 'SHA256SUMS.txt', 'latest.json') {
    $file = Get-Item -LiteralPath (Join-Path $package $name)
    '{0,-32} {1,14:N0} bytes  SHA256 {2}' -f $name, $file.Length, (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
}
Write-Host "Folder: $package"
if ($failed.Count) { throw 'The automated release audit has failures; do not publish.' }
