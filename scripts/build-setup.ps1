param([string]$Version = '', [string]$Notes = '', [ValidateSet('Studio', 'Server', 'Client')][string]$Edition = 'Studio')   # Notes appear in the update banner; pass a short plain-language summary of the release.
. "$PSScriptRoot/common.ps1"
$info = Get-EditionInfo $Edition
$releaseVersion = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if (-not $Version) { $Version = $releaseVersion }
if ($Version -ne $releaseVersion) { throw 'Package version must match Directory.Build.props.' }
$packageRoot = Get-EditionPackageRoot $Version $info
$msi = Join-Path $packageRoot "Mockingbird-$($info.Name)-$Version-win-x64.msi"
if (-not (Test-Path -LiteralPath $msi)) { throw 'Run package.ps1 and build-msi.ps1 first.' }
$wixExtension = Join-Path $repoRoot '.tools/wix-extensions/WixToolset.BootstrapperApplications.wixext/6.0.2/WixToolset.BootstrapperApplications.wixext.dll'
if (-not (Test-Path -LiteralPath $wixExtension)) {
    throw "Missing WiX bootstrapper extension. From an empty folder run: wix extension add WixToolset.BootstrapperApplications.wixext/6.0.2 and copy the extracted .wixext.dll to $wixExtension"
}
$icon = Join-Path $repoRoot 'src/TriAsr.App/Assets/TriASR.ico'
$logo = Join-Path $repoRoot 'src/TriAsr.App/Assets/TriASR.png'
foreach ($asset in @($icon, $logo)) { if (-not (Test-Path -LiteralPath $asset)) { throw "Missing asset: $asset" } }
function XmlEscape([string]$value) { [Security.SecurityElement]::Escape($value) }
# The wizard wraps the already-verified MSI unchanged, so the MSI hash and
# integrity manifest stay valid. The bundle is per-user: no administrator prompt.
$wxs = @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs" xmlns:bal="http://wixtoolset.org/schemas/v4/wxs/bal">
  <Bundle Name="$($info.Product)" Version="$Version" Manufacturer="PufferfishGaming" UpgradeCode="$($info.BundleUpgrade)"
          Copyright="Copyright (c) 2026 PufferfishGaming. GPL-3.0-or-later." AboutUrl="https://github.com/PufferfishGaming/Mockingbird"
          IconSourceFile="$(XmlEscape $icon)">
    <BootstrapperApplication>
      <bal:WixStandardBootstrapperApplication Theme="hyperlinkLicense" LicenseUrl="https://github.com/PufferfishGaming/Mockingbird/blob/main/LICENSE"
                                              LogoFile="$(XmlEscape $logo)" SuppressOptionsUI="yes" ShowVersion="yes" />
    </BootstrapperApplication>
    <Variable Name="LaunchTarget" Value="[LocalAppDataFolder]Programs\$($info.Folder)\TriAsr.App.exe" />
    <Variable Name="LaunchWorkingFolder" Value="[LocalAppDataFolder]Programs\$($info.Folder)" />
    <Chain>
      <MsiPackage Id="Mockingbird$($info.Name)" SourceFile="$(XmlEscape $msi)" Vital="yes">
        <MsiProperty Name="ARPSYSTEMCOMPONENT" Value="1" />
      </MsiPackage>
    </Chain>
  </Bundle>
</Wix>
"@
$source = Join-Path $packageRoot 'Setup.wxs'
[IO.File]::WriteAllText($source, $wxs)
$env:DOTNET_ROOT = Split-Path $script:Dotnet -Parent
$env:DOTNET_ROLL_FORWARD = 'Major'
$wix = Join-Path $repoRoot '.tools/wix/wix.exe'
$setup = Join-Path $packageRoot $info.Setup
if (Test-Path -LiteralPath $setup) { Remove-Item -LiteralPath $setup -Force }
& $wix build $source -arch x64 -ext $wixExtension -o $setup
if ($LASTEXITCODE -ne 0) { throw 'Setup build failed.' }
$hash = Get-FileHash -LiteralPath $setup -Algorithm SHA256
"$($hash.Hash)  $($info.Setup)" | Set-Content -LiteralPath (Join-Path $packageRoot $info.Sums) -Encoding ascii
# The manifest (latest.json for Studio, latest-server.json, latest-client.json) is what installed apps read to learn about this release. Upload it AFTER the Setup.exe so
# no app is told about a version whose installer is not yet available. Written without a byte-order mark.
$manifest = [ordered]@{
    schema = 1
    version = $Version
    url = "https://github.com/PufferfishGaming/Mockingbird/releases/download/download/$($info.Setup)"
    sha256 = $hash.Hash
    bytes = (Get-Item -LiteralPath $setup).Length
    notes = $Notes
}
[IO.File]::WriteAllText((Join-Path $packageRoot $info.Manifest), ($manifest | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
$hash
