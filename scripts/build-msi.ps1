param([string]$Version = '', [ValidateSet('Studio', 'Server', 'Client')][string]$Edition = 'Studio')
. "$PSScriptRoot/common.ps1"
$info = Get-EditionInfo $Edition
$releaseVersion = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if (-not $Version) { $Version = $releaseVersion }
if ($Version -ne $releaseVersion) { throw 'Package version must match Directory.Build.props.' }
$packageRoot = Get-EditionPackageRoot $Version $info
$portable = Join-Path $packageRoot $info.Folder
if (-not (Test-Path -LiteralPath (Join-Path $portable 'integrity.json'))) { throw 'Run package.ps1 first.' }
function XmlEscape([string]$value) { [Security.SecurityElement]::Escape($value) }
$builder = [Text.StringBuilder]::new()
[void]$builder.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$builder.AppendLine("<Package Name='$($info.Product)' Manufacturer='$($info.Product)' Version='$Version' UpgradeCode='$($info.MsiUpgrade)' Scope='perUser' Language='1033'>")
[void]$builder.AppendLine('<MajorUpgrade DowngradeErrorMessage="A newer ' + $info.Product + ' build is already installed." /><MediaTemplate EmbedCab="yes" />')
[void]$builder.AppendLine("<Icon Id='TriASRIcon' SourceFile='$(XmlEscape (Join-Path $portable 'Assets/TriASR.ico'))' /><Property Id='ARPPRODUCTICON' Value='TriASRIcon' />")
[void]$builder.AppendLine('<StandardDirectory Id="LocalAppDataFolder"><Directory Id="ProgramsFolder" Name="Programs"><Directory Id="INSTALLFOLDER" Name="' + $info.Folder + '">')
$componentIds = [Collections.Generic.List[string]]::new()
$script:counter = 0
function AddDirectory([string]$directory) {
    foreach ($file in Get-ChildItem -LiteralPath $directory -File | Sort-Object Name) {
        $script:counter++
        $id = 'C' + $script:counter
        $componentIds.Add($id)
        [void]$builder.AppendLine("<Component Id='$id' Guid='*'><File Id='F$script:counter' Source='$(XmlEscape $file.FullName)' KeyPath='yes' />")
        if ($file.Name -eq 'TriAsr.App.exe' -and $directory -eq $portable) {
            [void]$builder.AppendLine('<Shortcut Id="StartMenuShortcut" Directory="ProgramMenuFolder" Name="' + $info.Product + '" Advertise="no" Target="[INSTALLFOLDER]TriAsr.App.exe" WorkingDirectory="INSTALLFOLDER" />')
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
[void]$builder.AppendLine('<Feature Id="Main" Title="' + $info.Product + '" Level="1">')
foreach ($id in $componentIds) { [void]$builder.AppendLine("<ComponentRef Id='$id' />") }
[void]$builder.AppendLine('</Feature></Package></Wix>')
$source = Join-Path $packageRoot 'TriASR.wxs'
[IO.File]::WriteAllText($source,$builder.ToString())
$env:DOTNET_ROOT = Split-Path $script:Dotnet -Parent
$env:DOTNET_ROLL_FORWARD = 'Major'
$wix = Join-Path $repoRoot '.tools/wix/wix.exe'
$outputMsi = Join-Path $packageRoot "Mockingbird-$($info.Name)-$Version-win-x64.msi"
& $wix build $source -arch x64 -o $outputMsi
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }
Get-FileHash -LiteralPath $outputMsi -Algorithm SHA256
