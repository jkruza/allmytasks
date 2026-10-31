# Builds the per-user MSI for one Windows runtime id from an existing publish directory.
# Usage: pwsh packaging/windows/build.ps1 -Rid win-x64 -Version 1.2.0 -PublishDir out/win-x64 -DistDir dist
#   Version may carry a prerelease suffix (1.2.0-rc.1); the MSI product version uses the numeric part only.
# Needs the .NET SDK (installs the pinned WiX tool globally if missing).
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$Rid,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$PublishDir,
    [Parameter(Mandatory)][string]$DistDir
)
$ErrorActionPreference = 'Stop'
$wixVersion = '5.0.2'

$publish = (Resolve-Path $PublishDir).Path
New-Item -ItemType Directory -Force $DistDir | Out-Null
$dist = (Resolve-Path $DistDir).Path

# The MSI installs the executable only. Fail loudly if the publish output grows other files that would be missed.
$extra = Get-ChildItem $publish -Recurse -File | Where-Object { $_.Name -ne 'AllMyTasks.exe' -and $_.Extension -ne '.pdb' }
if ($extra) { throw "Unexpected files in the publish output: $($extra.Name -join ', ')" }

if (-not ($Version -match '^(\d+)\.(\d+)\.(\d+)')) { throw "Version '$Version' is not major.minor.patch" }
$msiVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3])"

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    dotnet tool install --global wix --version $wixVersion
    $env:PATH += [IO.Path]::PathSeparator + (Join-Path $HOME '.dotnet/tools')
}
wix extension add --global "WixToolset.Util.wixext/$wixVersion"

$arch = if ($Rid -eq 'win-arm64') { 'arm64' } else { 'x64' }
$out = Join-Path $dist "AllMyTasks-$Version-windows-$($Rid.Substring(4)).msi"
wix build (Join-Path $PSScriptRoot 'AllMyTasks.wxs') -arch $arch -ext WixToolset.Util.wixext `
    -d "Version=$msiVersion" -d "SourceDir=$publish" -o $out
if ($LASTEXITCODE -ne 0) { throw "wix build failed" }
Get-Item $out
