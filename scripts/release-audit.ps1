param([switch]$RequirePublishReady)
. "$PSScriptRoot/common.ps1"
$version = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$package = Join-Path $repoRoot "artifacts/packages/$version"
$payload = Join-Path $package 'TriASR'
$checks = [Collections.Generic.List[object]]::new()
function Check([string]$name, [bool]$passed, [string]$detail) {
    $checks.Add([ordered]@{ Name=$name; Passed=$passed; Detail=$detail })
}
$assembly = Join-Path $payload 'TriAsr.App.dll'
$metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($assembly)
Check 'Version metadata' ($metadata.ProductVersion.Split('+')[0] -eq $version) $metadata.ProductVersion
Check 'Privacy bundled' (Test-Path -LiteralPath (Join-Path $payload 'PRIVACY.md')) 'Policy embedded in app and present in package.'
Check 'GitHub README' (Test-Path -LiteralPath (Join-Path $repoRoot 'README.md')) 'README.md'
Check 'Source license selected' (Test-Path -LiteralPath (Join-Path $payload 'LICENSE')) 'GPL-3.0-or-later; dependencies retain their own licenses.'
Check 'Linux disclosure' ((Get-Content (Join-Path $repoRoot 'README.md') -Raw) -match 'native Linux build') 'Native Linux pending by user decision.'
$manifest = Get-Content -LiteralPath (Join-Path $payload 'integrity.json') -Raw | ConvertFrom-Json
$valid = $true
foreach ($entry in $manifest) {
    $file = [IO.Path]::GetFullPath((Join-Path $payload $entry.path))
    if (-not $file.StartsWith($payload + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid manifest path.' }
    if (-not (Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file).Hash -ne $entry.sha256) { $valid = $false }
}
Check 'Payload integrity' $valid "$($manifest.Count) files"
$verification = Get-Content (Join-Path $package 'msi-verification.json') -Raw | ConvertFrom-Json
Check 'Packaged pipeline and checkpoint reuse' ($verification.State -eq 8 -and $verification.Regions -gt 0 -and $verification.CheckpointReused) 'Administrative MSI extraction plus real audio smoke.'
$msi = Join-Path $package "Mockingbird-Studio-$version-win-x64.msi"
$signature = (Get-AuthenticodeSignature -LiteralPath $msi).Status.ToString()
Check 'Signature status disclosed' ((Get-Content (Join-Path $repoRoot 'README.md') -Raw) -match 'unsigned') $signature
$setup = Join-Path $package 'Mockingbird-Studio-Setup.exe'
$sums = Join-Path $package 'SHA256SUMS.txt'
$setupHash = if (Test-Path -LiteralPath $setup) { (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash } else { '' }
Check 'Setup wizard built and checksummed' ($setupHash -ne '' -and (Test-Path -LiteralPath $sums) -and (Get-Content -LiteralPath $sums -Raw).Contains($setupHash)) "Mockingbird-Studio-Setup.exe $setupHash"
$pending = @(
    'Complete third-party notices and exact FFmpeg GPL corresponding-source/build bundle',
    'Clean-machine install/download/offline/upgrade/uninstall acceptance',
    'Physical DPI/high-contrast/keyboard acceptance',
    'Real HU/EN, long-recording and silence/noise accuracy acceptance'
)
$publishReady = ($checks | Where-Object { -not $_.Passed }).Count -eq 0 -and $pending.Count -eq 0
$report = [ordered]@{ Version=$version; CheckedAt=[DateTimeOffset]::UtcNow.ToString('o'); Checks=$checks; Pending=$pending; PublishReady=$publishReady }
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $package 'release-audit.json') -Encoding utf8
$report | ConvertTo-Json -Depth 6
if (($checks | Where-Object { -not $_.Passed }).Count -gt 0) { throw 'Automated release audit failed.' }
if ($RequirePublishReady -and -not $publishReady) { throw 'Public distribution gates remain open; see release-audit.json.' }
