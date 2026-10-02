$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$localDotnet = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
$script:Dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.tools/cli-home'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.tools/nuget-packages'
Set-Location -LiteralPath $repoRoot
function Invoke-Dotnet {
    & $script:Dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
# The three editions (docs/decisions/ADR-0014). Studio keeps every name it has always had, so that an installed Studio updates in place; the others
# get names, folders, installer identities and update manifests of their own. Server and Client are packaged next to the Studio package as
# artifacts/packages/<version>-server and <version>-client.
function Get-EditionInfo([string]$Edition) {
    switch ($Edition) {
        'Studio' { $info = @{ Name = 'Studio'; Folder = 'TriASR'; Suffix = ''; Engines = $true;  MsiUpgrade = '10EECA7F-5515-4F31-BF80-14A4168F9BE5'; BundleUpgrade = '5141A589-D800-47E9-8906-181D4668E635'; Manifest = 'latest.json' } }
        'Server' { $info = @{ Name = 'Server'; Folder = 'TriASR-Server'; Suffix = '-server'; Engines = $true;  MsiUpgrade = '6B0C2F52-3D1A-4E8B-9C47-5A1E7D0B8F36'; BundleUpgrade = 'C4D81E2A-7F53-4B96-A0E8-2B6D9F13C5A7'; Manifest = 'latest-server.json' } }
        'Client' { $info = @{ Name = 'Client'; Folder = 'TriASR-Client'; Suffix = '-client'; Engines = $false; MsiUpgrade = 'A93E5D17-8C20-4F6B-B1D4-0E7A3C58F29D'; BundleUpgrade = '1F7B4C8E-62A9-4D35-8E01-D5C93A7B6F42'; Manifest = 'latest-client.json' } }
        default { throw "Unknown edition '$Edition'. Use Studio, Server or Client." }
    }
    $info.Product = "Mockingbird $($info.Name)"
    $info.Setup = "Mockingbird-$($info.Name)-Setup.exe"
    $info.Sums = "SHA256SUMS$($info.Suffix).txt"
    $info
}

function Get-EditionPackageRoot([string]$Version, [hashtable]$Info) { Join-Path $repoRoot "artifacts/packages/$Version$($Info.Suffix)" }

# A smoke run must not open the computer to the network: the Server edition starts hosting by itself, so its data folder is given loopback-only settings.
function Write-SmokeSettings([string]$Root, [string]$Edition) {
    New-Item -ItemType Directory -Path (Join-Path $Root 'Config') -Force | Out-Null
    if ($Edition -ne 'Server') { return }
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
    $settings = @{ hostEnabled = $true; hostAllowNetwork = $false; hostPort = $port; hostName = 'Smoke test'; hostPassword = 'k7m2-pq9x-w4hd'; hostId = 'smoke' } | ConvertTo-Json
    [System.IO.File]::WriteAllText((Join-Path $Root 'Config/settings.json'), $settings, [System.Text.UTF8Encoding]::new($false))
}
