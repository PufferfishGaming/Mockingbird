<#
.SYNOPSIS
  Installs Mockingbird Studio, Server or Client from the GitHub release: downloads the installer, checks it against the published SHA256 file, runs it.
.DESCRIPTION
  Run it from PowerShell on Windows 10 or 11 (x64). No administrator permission is needed; the installer installs for the current user.
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/PufferfishGaming/Mockingbird/main/scripts/install.ps1))) -Edition Server
  Without -Quiet the normal setup wizard opens and you accept the license there. -Quiet installs without any window, which means you accept the
  license (GPL-3.0-or-later, see the LICENSE file of the project) by choosing it. -DownloadOnly stops after the check and leaves the installer in
  a folder. The installer is not signed, and a file downloaded by this script is not marked as coming from the internet, so Windows SmartScreen does
  not warn about it; the SHA256 check is what this script offers instead.
  Nothing else is changed on the computer. Uninstall in Settings > Apps > Installed apps.
.PARAMETER Edition
  Studio (everything on one computer), Server (only the server, with the models) or Client (only the window that sends recordings to a server).
.PARAMETER BaseUrl
  Where the release files are. Only a test changes it.
#>
param(
    [ValidateSet('Studio', 'Server', 'Client')][string]$Edition = 'Studio',
    [switch]$Quiet,
    [switch]$NoLaunch,
    [switch]$DownloadOnly,
    [string]$BaseUrl = 'https://github.com/PufferfishGaming/Mockingbird/releases/download/download'
)
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'Mockingbird requires Windows x64.'
}
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$ProgressPreference = 'SilentlyContinue'   # Windows PowerShell 5.1 downloads many times faster without the progress bar

# The names the release uses for each edition (scripts/common.ps1 makes them; a test keeps the two in step).
$files = @{
    Studio = @{ Setup = 'Mockingbird-Studio-Setup.exe'; Sums = 'SHA256SUMS.txt';        Folder = 'TriASR' }
    Server = @{ Setup = 'Mockingbird-Server-Setup.exe'; Sums = 'SHA256SUMS-server.txt'; Folder = 'TriASR-Server' }
    Client = @{ Setup = 'Mockingbird-Client-Setup.exe'; Sums = 'SHA256SUMS-client.txt'; Folder = 'TriASR-Client' }
}[$Edition]

$folder = Join-Path ([IO.Path]::GetTempPath()) ("Mockingbird-$Edition-install-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$base = $BaseUrl.TrimEnd('/')

function Get-ReleaseFile([string]$name) {
    $target = Join-Path $folder $name
    try { Invoke-WebRequest -Uri "$base/$name" -OutFile $target -UseBasicParsing }
    catch {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        if ($status -eq 404) { throw "$name is not published yet. Mockingbird $Edition has not been released; look at the releases page of the project." }
        throw "$name could not be downloaded: $($_.Exception.Message)"
    }
    $target
}

Write-Output "Downloading Mockingbird $Edition ..."
$sums = Get-ReleaseFile $files.Sums
$setup = Get-ReleaseFile $files.Setup

$line = @(Get-Content -LiteralPath $sums | Where-Object { $_ -match ('^[0-9a-fA-F]{64}\s+\*?' + [regex]::Escape($files.Setup) + '\s*$') })
if ($line.Count -ne 1) { throw "The checksum file does not list $($files.Setup)." }
$expected = $line[0].Substring(0, 64)
$actual = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
if ($actual -ne $expected) {
    Remove-Item -LiteralPath $setup -Force
    throw "The downloaded installer does not match its published SHA256 ($actual instead of $expected). It was deleted. Try again, or download it from the releases page."
}
Write-Output "Checked: SHA256 $actual"

if ($DownloadOnly) { Write-Output "Installer saved: $setup"; return }

$arguments = if ($Quiet) { @('/quiet', '/norestart') } else { @() }
$process = if ($arguments.Count) { Start-Process -FilePath $setup -ArgumentList $arguments -Wait -PassThru } else { Start-Process -FilePath $setup -Wait -PassThru }
if ($process.ExitCode -notin @(0, 3010)) { throw "The installer ended with code $($process.ExitCode). Run it again without -Quiet to see why." }
Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue

$app = Join-Path $env:LOCALAPPDATA "Programs\$($files.Folder)\TriAsr.App.exe"
if (-not (Test-Path -LiteralPath $app)) { Write-Output "Setup finished without installing Mockingbird $Edition (it may have been cancelled)."; return }
Write-Output "Mockingbird $Edition is installed."
if ($Quiet -and -not $NoLaunch) { Start-Process -FilePath $app }
