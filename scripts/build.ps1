param([switch]$UpdateLockFiles)
. "$PSScriptRoot/common.ps1"
if ($UpdateLockFiles) {
    Invoke-Dotnet restore TriAsr.slnx '-p:Platform=x64' --force-evaluate
} else {
    Invoke-Dotnet restore TriAsr.slnx '-p:Platform=x64' --locked-mode
}
Invoke-Dotnet build TriAsr.slnx -c Release '-p:Platform=x64' --no-restore