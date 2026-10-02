param([switch]$RequirePublishReady, [ValidateSet('Studio', 'Server', 'Client')][string]$Edition = 'Studio')
. "$PSScriptRoot/common.ps1"
$info = Get-EditionInfo $Edition
$version = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$package = Get-EditionPackageRoot $version $info
$payload = Join-Path $package $info.Folder
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
if ($info.Engines) { Check 'Packaged pipeline and checkpoint reuse' ($verification.State -eq 8 -and $verification.Regions -gt 0 -and $verification.CheckpointReused) 'Administrative MSI extraction plus real audio smoke.' }
Check 'Packaged edition starts as itself' ($verification.Edition -eq $Edition -and $verification.Renders -gt 0) "$Edition from edition.txt, $($verification.Renders) renders"
Check 'Edition marker matches' ((Get-Content -LiteralPath (Join-Path $payload 'edition.txt') -Raw).Trim() -eq $Edition.ToLowerInvariant()) $Edition
if (-not $info.Engines) { Check 'No speech programs in the client' (-not (Test-Path -LiteralPath (Join-Path $payload 'Runtimes'))) 'The client sends recordings to a server and transcribes nothing.' }
$msi = Join-Path $package "Mockingbird-$($info.Name)-$version-win-x64.msi"
$signature = (Get-AuthenticodeSignature -LiteralPath $msi).Status.ToString()
Check 'Signature status disclosed' ((Get-Content (Join-Path $repoRoot 'README.md') -Raw) -match 'unsigned') $signature
$setup = Join-Path $package $info.Setup
$sums = Join-Path $package $info.Sums
$setupHash = if (Test-Path -LiteralPath $setup) { (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash } else { '' }
Check 'Setup wizard built and checksummed' ($setupHash -ne '' -and (Test-Path -LiteralPath $sums) -and (Get-Content -LiteralPath $sums -Raw).Contains($setupHash)) "$($info.Setup) $setupHash"
$latestPath = Join-Path $package $info.Manifest
$latestOk = $false; $latestDetail = "$($info.Manifest) missing"
if (Test-Path -LiteralPath $latestPath) {
    $latest = Get-Content -LiteralPath $latestPath -Raw | ConvertFrom-Json
    $latestOk = $latest.schema -eq 1 -and $latest.version -eq $version -and $latest.sha256 -eq $setupHash -and $latest.bytes -eq (Get-Item -LiteralPath $setup).Length `
        -and ([Uri]$latest.url).Scheme -eq 'https' -and ([Uri]$latest.url).Host -eq 'github.com' -and ([Uri]$latest.url).AbsolutePath.EndsWith('/' + $info.Setup)
    $latestDetail = "$($info.Manifest) $($latest.version) $($latest.bytes) bytes"
}
Check "Update manifest matches the $($info.Setup)" $latestOk $latestDetail
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
