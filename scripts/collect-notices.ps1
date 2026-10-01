param([Parameter(Mandatory=$true)][string]$Destination)
. "$PSScriptRoot/common.ps1"
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$packages = @{}
foreach ($lock in Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src') -Recurse -File -Filter 'packages*.lock.json') {
    $data = Get-Content -LiteralPath $lock.FullName -Raw | ConvertFrom-Json
    foreach ($framework in $data.dependencies.PSObject.Properties) {
        foreach ($package in $framework.Value.PSObject.Properties) {
            if ($package.Value.type -eq 'Project') { continue }
            $id = $package.Name.ToLowerInvariant()
            $version = $package.Value.resolved
            $packages["$id/$version"] = [ordered]@{ Id=$package.Name; Version=$version; License='Review required'; ProjectUrl='' }
        }
    }
}
foreach ($key in @($packages.Keys)) {
    $path = Join-Path $env:NUGET_PACKAGES $key
    $nuspec = Get-ChildItem -LiteralPath $path -Filter '*.nuspec' -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($nuspec) {
        $metadata = ([xml](Get-Content -LiteralPath $nuspec.FullName -Raw)).package.metadata
        if ($metadata.license) { $packages[$key].License = $metadata.license.InnerText }
        if ($metadata.projectUrl) { $packages[$key].ProjectUrl = [string]$metadata.projectUrl }
        foreach ($notice in Get-ChildItem -LiteralPath $path -Recurse -File | Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE|THIRD.?PARTY.?NOTICES)(\..*)?$' }) {
            $target = Join-Path $Destination ('nuget/' + $key + '/' + $notice.FullName.Substring($path.Length).TrimStart('\'))
            New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
            Copy-Item -LiteralPath $notice.FullName -Destination $target
        }
    }
}
$packages.Values | Sort-Object Id,Version | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Destination 'dependency-inventory.json') -Encoding utf8
Write-Output "Collected metadata for $($packages.Count) locked production dependencies."
