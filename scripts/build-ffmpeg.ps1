<#
.SYNOPSIS
  Builds the ffmpeg.exe that Mockingbird ships, from the official FFmpeg source release, and the source bundle that goes with it.
.DESCRIPTION
  Needs Docker Desktop (Linux containers). Downloads ffmpeg-<version>.tar.xz from ffmpeg.org into artifacts/ffmpeg-build when it is not
  there, checks it against -Sha256, builds the image of scripts/ffmpeg/Dockerfile and runs scripts/ffmpeg/build.sh in it. The result:
    artifacts/ffmpeg-build/out/ffmpeg.exe (+ COPYING.LGPLv2.1.txt, LICENSE.md, BUILD-INFO.txt)
    artifacts/ffmpeg-build/FFmpeg-<version>-source.zip: the unmodified source release, its signature, these build files and BUILD-INFO.txt.
  The program is under the LGPL 2.1 or later (no --enable-gpl, no external libraries but zlib). Publish the source bundle with every
  release whose installers carry this ffmpeg.exe. Copy out/ffmpeg.exe and its licence files to Runtimes/FFmpeg to use it.
#>
param([string]$Version = '9.0.2', [string]$Sha256 = '8C3850283EB25FA026482078A04051E0BE17347B09EF81A0849BEC15A96E002E')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$work = Join-Path $repoRoot 'artifacts/ffmpeg-build'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$source = Join-Path $work "ffmpeg-$Version.tar.xz"
foreach ($name in "ffmpeg-$Version.tar.xz", "ffmpeg-$Version.tar.xz.asc") {
    if (-not (Test-Path -LiteralPath (Join-Path $work $name))) { & curl.exe -sSfL -o (Join-Path $work $name) "https://ffmpeg.org/releases/$name"; if ($LASTEXITCODE) { throw "Could not download $name." } }
}
$actual = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
if ($actual -ne $Sha256) { throw "ffmpeg-$Version.tar.xz has SHA256 $actual, not $Sha256." }

# The build context, with Unix line ends whatever Git did to them on this computer.
$context = Join-Path $work 'context'
New-Item -ItemType Directory -Force -Path $context | Out-Null
foreach ($name in 'Dockerfile', 'build.sh') {
    $text = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "ffmpeg/$name")).Replace("`r`n", "`n")
    [IO.File]::WriteAllText((Join-Path $context $name), $text, [Text.UTF8Encoding]::new($false))
}
# Docker writes its progress to the error stream; Windows PowerShell would take that for a failure, so only the exit code counts.
function Invoke-Docker {
    $ErrorActionPreference = 'Continue'
    & docker @args 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE) { throw "docker $($args[0]) failed with exit code $LASTEXITCODE." }
}
Invoke-Docker build -t mockingbird-ffmpeg-build $context
Invoke-Docker run --rm -v "${work}:/work" mockingbird-ffmpeg-build sh /build.sh $Version
$image = (& docker image inspect mockingbird-ffmpeg-build --format '{{.Id}}')
Add-Content -LiteralPath (Join-Path $work 'out/BUILD-INFO.txt') -Value "Build image: $image (from $(Get-Content (Join-Path $PSScriptRoot 'ffmpeg/Dockerfile') | Where-Object { $_ -like 'FROM *' }))"

# The source bundle: what someone needs to rebuild exactly this program.
$bundle = Join-Path $work "FFmpeg-$Version-source.zip"
$staging = Join-Path $work 'bundle'
New-Item -ItemType Directory -Force -Path $staging | Out-Null
Copy-Item -LiteralPath $source, "$source.asc", (Join-Path $work 'out/BUILD-INFO.txt'), (Join-Path $PSScriptRoot 'ffmpeg/Dockerfile'), (Join-Path $PSScriptRoot 'ffmpeg/build.sh'), $PSCommandPath -Destination $staging -Force
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $bundle -Force
Get-Content -LiteralPath (Join-Path $work 'out/BUILD-INFO.txt')
"Source bundle: $bundle ($((Get-Item -LiteralPath $bundle).Length) bytes)"
