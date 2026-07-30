$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$Version = "1.0.2"

$projectRoot = Split-Path -Parent $PSScriptRoot
$standaloneProject = Join-Path $projectRoot "standalone\NoNoStandalone.csproj"
$releaseRoot = Join-Path $projectRoot "release"
$installerProject = Join-Path $PSScriptRoot "NoNoInstaller.wixproj"

dotnet build $standaloneProject -c Release
if ($LASTEXITCODE -ne 0) {
    throw "NoNo application build failed."
}

# Package.wxs lists every payload file explicitly. Debug symbols, local models,
# caches, virtual environments, and user configuration are therefore excluded.
New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null

dotnet build $installerProject -c Release -t:Rebuild -p:ProductVersion=$Version
if ($LASTEXITCODE -ne 0) {
    throw "NoNo MSI build failed."
}

$msi = Get-ChildItem -Path $releaseRoot -Recurse -Filter "NoNo-Desktop-Pet-$Version-x64.msi" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($null -eq $msi) {
    throw "The MSI build completed but the expected package was not found."
}

$hash = Get-FileHash -LiteralPath $msi.FullName -Algorithm SHA256
Write-Host "Built: $($msi.FullName)"
Write-Host "SHA256: $($hash.Hash)"
