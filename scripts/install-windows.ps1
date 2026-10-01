param([switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
$version = '0.1.15'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'Mockingbird Studio requires Windows x64.'
}
$msiName = "Mockingbird-Studio-$version-win-x64.msi"
$msi = Join-Path $PSScriptRoot $msiName
$checksums = Join-Path $PSScriptRoot 'SHA256SUMS.txt'
if (-not (Test-Path -LiteralPath $msi) -or -not (Test-Path -LiteralPath $checksums)) {
    throw 'Extract all release files into one folder before starting the installer.'
}
$line = @(Get-Content -LiteralPath $checksums | Where-Object { $_ -match ('^[0-9a-fA-F]{64}  ' + [regex]::Escape($msiName) + '$') })
if ($line.Count -ne 1) { throw 'The checksum manifest is missing the installer.' }
$expected = $line[0].Substring(0,64)
if ((Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash -ne $expected) {
    throw 'Installer checksum mismatch. Download the release again.'
}
$process = Start-Process msiexec.exe -ArgumentList @('/i', ('"' + $msi + '"'), '/passive', 'REBOOT=ReallySuppress') -PassThru -Wait
if ($process.ExitCode -notin @(0,3010)) { throw "Windows Installer failed: $($process.ExitCode)" }
if (-not $NoLaunch) {
    $app = Join-Path $env:LOCALAPPDATA 'Programs/TriASR/TriAsr.App.exe'
    Start-Process -FilePath $app
}
Write-Output 'Mockingbird Studio installed.'

