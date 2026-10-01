param([string]$Version = '')
. "$PSScriptRoot/common.ps1"
$releaseVersion = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if (-not $Version) { $Version = $releaseVersion }
if ($Version -ne $releaseVersion) { throw 'Package version must match Directory.Build.props.' }
$packageRoot = Join-Path $repoRoot "artifacts/packages/$Version"
$portable = Join-Path $packageRoot 'TriASR'
if (-not (Test-Path -LiteralPath (Join-Path $portable 'integrity.json'))) { throw 'Run package.ps1 first.' }
function XmlEscape([string]$value) { [Security.SecurityElement]::Escape($value) }
$builder = [Text.StringBuilder]::new()
[void]$builder.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$builder.AppendLine("<Package Name='Mockingbird Studio' Manufacturer='Mockingbird Studio' Version='$Version' UpgradeCode='10EECA7F-5515-4F31-BF80-14A4168F9BE5' Scope='perUser' Language='1033'>")
[void]$builder.AppendLine('<MajorUpgrade DowngradeErrorMessage="A newer Mockingbird Studio build is already installed." /><MediaTemplate EmbedCab="yes" />')
[void]$builder.AppendLine("<Icon Id='TriASRIcon' SourceFile='$(XmlEscape (Join-Path $portable 'Assets/TriASR.ico'))' /><Property Id='ARPPRODUCTICON' Value='TriASRIcon' />")
[void]$builder.AppendLine('<StandardDirectory Id="LocalAppDataFolder"><Directory Id="ProgramsFolder" Name="Programs"><Directory Id="INSTALLFOLDER" Name="TriASR">')
$componentIds = [Collections.Generic.List[string]]::new()
$script:counter = 0
function AddDirectory([string]$directory) {
    foreach ($file in Get-ChildItem -LiteralPath $directory -File | Sort-Object Name) {
        $script:counter++
        $id = 'C' + $script:counter
        $componentIds.Add($id)
        [void]$builder.AppendLine("<Component Id='$id' Guid='*'><File Id='F$script:counter' Source='$(XmlEscape $file.FullName)' KeyPath='yes' />")
        if ($file.Name -eq 'TriAsr.App.exe' -and $directory -eq $portable) {
            [void]$builder.AppendLine('<Shortcut Id="StartMenuShortcut" Directory="ProgramMenuFolder" Name="Mockingbird Studio" Advertise="no" Target="[INSTALLFOLDER]TriAsr.App.exe" WorkingDirectory="INSTALLFOLDER" />')
        }
        [void]$builder.AppendLine('</Component>')
    }
    foreach ($child in Get-ChildItem -LiteralPath $directory -Directory | Sort-Object Name) {
        $script:counter++
        [void]$builder.AppendLine("<Directory Id='D$script:counter' Name='$(XmlEscape $child.Name)'>")
        AddDirectory $child.FullName
        [void]$builder.AppendLine('</Directory>')
    }
}
AddDirectory $portable
[void]$builder.AppendLine('</Directory></Directory></StandardDirectory><StandardDirectory Id="ProgramMenuFolder" />')
[void]$builder.AppendLine('<Feature Id="Main" Title="Mockingbird Studio" Level="1">')
foreach ($id in $componentIds) { [void]$builder.AppendLine("<ComponentRef Id='$id' />") }
[void]$builder.AppendLine('</Feature></Package></Wix>')
$source = Join-Path $packageRoot 'TriASR.wxs'
[IO.File]::WriteAllText($source,$builder.ToString())
$env:DOTNET_ROOT = Split-Path $script:Dotnet -Parent
$env:DOTNET_ROLL_FORWARD = 'Major'
$wix = Join-Path $repoRoot '.tools/wix/wix.exe'
$outputMsi = Join-Path $packageRoot "Mockingbird-Studio-$Version-win-x64.msi"
& $wix build $source -arch x64 -o $outputMsi
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }
Get-FileHash -LiteralPath $outputMsi -Algorithm SHA256
