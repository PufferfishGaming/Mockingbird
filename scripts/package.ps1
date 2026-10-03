param([string]$Version = '', [ValidateSet('Studio', 'Server', 'Client')][string]$Edition = 'Studio')
. "$PSScriptRoot/common.ps1"
$info = Get-EditionInfo $Edition
$releaseVersion = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if (-not $Version) { $Version = $releaseVersion }
if ($Version -ne $releaseVersion) { throw 'Package version must match Directory.Build.props.' }
$packageRoot = Get-EditionPackageRoot $Version $info
$portable = Join-Path $packageRoot $info.Folder
if (Test-Path -LiteralPath $portable) { throw 'Package directory already exists. Use a new version to preserve previous artifacts.' }
New-Item -ItemType Directory -Path $portable -Force | Out-Null
$isolatedBuild = '-p:ArtifactsPath=' + (Join-Path $packageRoot 'build')
Invoke-Dotnet publish src/TriAsr.App/TriAsr.App.csproj -c Release -r win-x64 --self-contained true '-p:Platform=x64' '-p:NuGetLockFilePath=packages.publish.lock.json' $isolatedBuild -o $portable
# The client transcribes nothing: it carries no speech programs, no worker and no models.
if ($info.Engines) { Invoke-Dotnet publish src/TriAsr.Worker/TriAsr.Worker.csproj -c Release -r win-x64 --self-contained true '-p:Platform=x64' '-p:NuGetLockFilePath=packages.publish.lock.json' $isolatedBuild -o (Join-Path $portable 'Workers/Canary') }
$runtimeNames = if ($info.Engines) { @('Whisper-Vulkan','Canary','FFmpeg','Llama','Vad','Speakers') } else { @() }
foreach ($runtime in $runtimeNames) {
    $destination = Join-Path $portable "Runtimes/$runtime"
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $source = Join-Path $repoRoot "Runtimes/$runtime"
    foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
        $relative = $file.FullName.Substring($source.TrimEnd('\\').Length).TrimStart('\\')
        if ($runtime -eq 'FFmpeg' -and $file.Name -eq 'ffprobe.exe') { continue }
        if ($runtime -eq 'Llama' -and $file.Extension -eq '.exe' -and $file.Name -ne 'llama-server.exe') { continue }
        $target = Join-Path $destination $relative
        New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
if ($info.Engines) {
    $vcSource = Join-Path $repoRoot 'artifacts/packaging/vcredist/extracted/a4'
    if (-not (Test-Path -LiteralPath $vcSource)) { throw 'Extract the verified official Microsoft x64 VC redistributable first.' }
    $vcFiles = Join-Path $packageRoot 'vc-files'
    New-Item -ItemType Directory -Path $vcFiles -Force | Out-Null
    & expand.exe '-F:*' $vcSource $vcFiles | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'VC runtime extraction failed.' }
    foreach ($native in @('Whisper-Vulkan','Llama','Canary/transcribe-native-windows-x86_64-cpu-vulkan')) {
        foreach ($name in @('msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll','vcomp140.dll')) {
            Copy-Item -LiteralPath (Join-Path $vcFiles ($name + '_amd64')) -Destination (Join-Path $portable "Runtimes/$native/$name")
        }
    }
}
# The marker that tells the program which edition it is (Edition.cs). It is part of the payload, so the integrity manifest covers it.
[IO.File]::WriteAllText((Join-Path $portable 'edition.txt'), $info.Name.ToLowerInvariant(), (New-Object Text.UTF8Encoding($false)))
$readme = switch ($Edition) {
    'Studio' { @"
Mockingbird Studio $Version — Windows x64
Launch TriAsr.App.exe. No separate .NET installation is required.
Models are excluded. Open Models and install recommended models, or set
TRIASR_MODEL_ROOT to an existing model repository before launch.
User data defaults to %LOCALAPPDATA%\TriASR for upgrade compatibility.
"@ }
    'Server' { @"
Mockingbird Server $Version — Windows x64
Launch TriAsr.App.exe. No separate .NET installation is required.
The speech programs and models live on this computer; other computers send their
recordings here. Models are excluded: the first start offers to download them.
User data defaults to %LOCALAPPDATA%\TriASR-Server.
"@ }
    'Client' { @"
Mockingbird Client $Version — Windows x64
Launch TriAsr.App.exe. No separate .NET installation is required.
Nothing is transcribed on this computer: recordings go to a Mockingbird server,
chosen in the window, and the transcripts come back.
User data defaults to %LOCALAPPDATA%\TriASR-Client.
"@ }
}
@"
$readme
This is an internal validation build. Redistribution license/source review and
clean-machine validation are pending. See README.md.
"@ | Set-Content -LiteralPath (Join-Path $portable 'README.txt') -Encoding utf8
New-Item -ItemType Directory -Path (Join-Path $portable 'licenses') -Force | Out-Null
foreach ($name in @('LICENSE.txt','ThirdPartyNotices.txt')) {
    $notice = Join-Path $repoRoot ".tools/dotnet/$name"
    if (Test-Path -LiteralPath $notice) { Copy-Item -LiteralPath $notice -Destination (Join-Path $portable "licenses/dotnet-$name") }
}
foreach ($document in @('LICENSE','README.md','PRIVACY.md','THIRD_PARTY_NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $repoRoot $document) -Destination $portable }
& "$PSScriptRoot/collect-notices.ps1" -Destination (Join-Path $portable 'licenses')
$manifest = foreach ($file in Get-ChildItem -LiteralPath $portable -Recurse -File) {
    [ordered]@{ path=$file.FullName.Substring($portable.TrimEnd('\\').Length).TrimStart('\\'); bytes=$file.Length; sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $portable 'integrity.json') -Encoding utf8
$priorData = $env:TRIASR_DATA_ROOT
$priorDotnet = $env:DOTNET_ROOT
$priorRuntime = $env:TRIASR_RUNTIME_ROOT
$priorEdition = $env:TRIASR_EDITION
try {
    $env:TRIASR_DATA_ROOT = Join-Path $packageRoot 'smoke'
    $env:DOTNET_ROOT = ''
    $env:TRIASR_RUNTIME_ROOT = $portable
    $env:TRIASR_EDITION = ''   # the program must find out which edition it is from edition.txt, as it will once installed
    Write-SmokeSettings $env:TRIASR_DATA_ROOT $Edition
    $process = Start-Process -FilePath (Join-Path $portable 'TriAsr.App.exe') -ArgumentList '--smoke-test' -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw 'Standalone smoke timed out.' }
    if ($process.ExitCode -ne 0) { throw "Standalone smoke failed: $($process.ExitCode)" }
    $smoke = Get-Content -LiteralPath (Join-Path $env:TRIASR_DATA_ROOT 'bootstrap-smoke.json') -Raw
    if (($smoke | ConvertFrom-Json).edition -ne $Edition) { throw "The package started as another edition than $Edition." }
    $smoke
} finally { $env:TRIASR_DATA_ROOT=$priorData; $env:DOTNET_ROOT=$priorDotnet; $env:TRIASR_RUNTIME_ROOT=$priorRuntime; $env:TRIASR_EDITION=$priorEdition }
Write-Output "Portable package: $portable"
