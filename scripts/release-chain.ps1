<#
.SYNOPSIS
  Builds and verifies a release candidate in one go: verify, package, build-msi, build-setup, verify-package, release-audit.
.DESCRIPTION
  Reads the version from Directory.Build.props. A version is never rebuilt: if artifacts/packages/<version> already exists
  the script stops and asks for a new version, because the installer hash of a published version must not change.
  Writes a log to artifacts/release-logs/<version>.log and stops at the first failing step.
  -Notes is the short plain-language summary shown in the update banner of installed apps (latest.json).
  -DryRun checks the version and the README and prints the plan without building anything.
  -Edition builds Studio (the default), Server or Client; all three share the version in Directory.Build.props. Run it once per edition, adding -SkipVerify after the first.
  Nothing is uploaded or pushed by this script. Upload the files of a package in this order: Mockingbird-<Edition>-Setup.exe, then for Studio and Server the FFmpeg source bundle (FFmpeg-<version>-source.zip, the same file for both), then the SHA256SUMS file, then the update manifest (latest.json, latest-server.json or latest-client.json) last, because installed apps read it to learn about a new version.
#>
param([string]$Notes = '', [switch]$DryRun, [ValidateSet('Studio', 'Server', 'Client')][string]$Edition = 'Studio', [switch]$SkipVerify)
. "$PSScriptRoot/common.ps1"
$info = Get-EditionInfo $Edition
$ErrorActionPreference = 'Stop'
$version = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$package = Get-EditionPackageRoot $version $info
if (Test-Path -LiteralPath $package) { throw "$package already exists. Bump <Version> in Directory.Build.props (and the version line in README.md): a built version is never rebuilt." }
if (-not (Select-String -LiteralPath (Join-Path $repoRoot 'README.md') -Pattern ([regex]::Escape("Version $version")) -Quiet)) { throw "README.md does not mention 'Version $version'. Update its version line before releasing." }
if ([string]::IsNullOrWhiteSpace($Notes)) { Write-Warning 'No -Notes given: the update banner will show no release notes.' }
$steps = @('verify', 'package', 'build-msi', 'build-setup', 'verify-package', 'release-audit')
if ($SkipVerify) { $steps = $steps | Where-Object { $_ -ne 'verify' } }   # the second and third edition of a version: the build and the tests have just passed for the first
if ($DryRun) { "Dry run for $($info.Product) $version. Would run: $($steps -join ' -> ')"; "Log: artifacts/release-logs/$version$($info.Suffix).log"; return }

New-Item -ItemType Directory -Force -Path (Join-Path $repoRoot 'artifacts/release-logs') | Out-Null
$log = Join-Path $repoRoot "artifacts/release-logs/$version$($info.Suffix).log"
"Release chain for $($info.Product) $version started $(Get-Date -Format s)" | Set-Content -LiteralPath $log -Encoding utf8
$ErrorActionPreference = 'Continue'
foreach ($step in $steps) {
    "=== $step $(Get-Date -Format T) ===" | Add-Content -LiteralPath $log -Encoding utf8
    Write-Host "=== $step ===" -ForegroundColor Cyan
    try {
        $arguments = if ($step -eq 'build-setup') { @{ Notes = $Notes; Edition = $Edition } } elseif ($step -eq 'verify') { @{} } else { @{ Edition = $Edition } }
        # Line by line, so that a step that fails keeps everything it wrote before (Out-String would hold it all until the step ends). Lines a program
        # wrote to stderr arrive as error records and are written as the text they are; only objects (a table of hashes, say) are formatted.
        & (Join-Path $PSScriptRoot "$step.ps1") @arguments *>&1 | ForEach-Object {
            if ($_ -is [string]) { $_ }
            elseif ($_ -is [System.Management.Automation.ErrorRecord] -and $_.FullyQualifiedErrorId -like 'NativeCommandError*') { $_.Exception.Message }
            else { ($_ | Out-String -Width 250).TrimEnd() }
        } | Add-Content -LiteralPath $log -Encoding utf8
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
Write-Host "Upload to the GitHub release in this order ($($info.Manifest) last):" -ForegroundColor Cyan
# Studio and Server also carry the FFmpeg source bundle (the same file for both: upload it once).
$bundles = @(Get-ChildItem -LiteralPath $package -Filter 'FFmpeg-*-source.zip' -File | ForEach-Object { $_.Name })
foreach ($name in @($info.Setup) + $bundles + @($info.Sums, $info.Manifest)) {
    $file = Get-Item -LiteralPath (Join-Path $package $name)
    '{0,-32} {1,14:N0} bytes  SHA256 {2}' -f $name, $file.Length, (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
}
Write-Host "Folder: $package"
if ($failed.Count) { throw 'The automated release audit has failures; do not publish.' }
