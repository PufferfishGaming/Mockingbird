. "$PSScriptRoot/common.ps1"
Invoke-Dotnet test TriAsr.slnx -c Release '-p:Platform=x64' --no-build --no-restore --logger trx --results-directory artifacts/test-results