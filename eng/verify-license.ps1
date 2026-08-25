# SPDX-License-Identifier: MPL-2.0

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$licensePath = Join-Path $repositoryRoot 'LICENSE'
$expectedLicenseHash = '3F3D9E0024B1921B067D6F7F88DEB4A60CBE7A78E76C64E3F1D7FC3B779B9D04'
$expectedMarker = 'SPDX-License-Identifier: MPL-2.0'

if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
    throw 'The root MPL-2.0 LICENSE file is missing.'
}

$normalizedLicense = (Get-Content -LiteralPath $licensePath -Raw) -replace "`r`n", "`n"
$licenseBytes = [Text.Encoding]::UTF8.GetBytes($normalizedLicense)
$actualLicenseHash = [Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData($licenseBytes))
if ($actualLicenseHash -ne $expectedLicenseHash) {
    throw "The root LICENSE is not the expected unmodified MPL-2.0 text. SHA-256: $actualLicenseHash"
}

$sourceExtensions = @(
    '.cs', '.csproj', '.css', '.js', '.props', '.ps1', '.razor', '.wixproj', '.wxl', '.wxs', '.yml'
)
$sourceFiles = @(
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -Recurse -File
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tests') -Recurse -File
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'installer') -Recurse -File
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'eng') -Recurse -File
    Get-Item -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props')
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot '.github\workflows') -File
) | Where-Object {
    $_.Extension -in $sourceExtensions -and
    $_.FullName -notmatch '[\\/](bin|obj|artifacts)[\\/]'
}

$missingMarkers = @()
foreach ($sourceFile in $sourceFiles) {
    $content = Get-Content -LiteralPath $sourceFile.FullName -Raw
    if (-not $content.Contains($expectedMarker, [StringComparison]::Ordinal)) {
        $missingMarkers += [IO.Path]::GetRelativePath($repositoryRoot, $sourceFile.FullName)
    }
}

if ($missingMarkers.Count -gt 0) {
    throw "RelayBridge-owned source lacks MPL-2.0 SPDX markers: $($missingMarkers -join ', ')"
}

[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
$licenseExpressions = @($buildProperties.Project.PropertyGroup.PackageLicenseExpression)
if ($licenseExpressions.Count -ne 1 -or $licenseExpressions[0] -ne 'MPL-2.0') {
    throw 'Directory.Build.props must declare exactly one MPL-2.0 PackageLicenseExpression.'
}

$thirdPartyNoticesPath = Join-Path $repositoryRoot 'docs\release\THIRD-PARTY-NOTICES.md'
$thirdPartyNotices = Get-Content -LiteralPath $thirdPartyNoticesPath -Raw
foreach ($requiredIdentity in @(
        'Microsoft.Graph.Authentication',
        'ExchangeOnlineManagement',
        'WiX Toolset 6.0.2',
        'Burn engine',
        'WixStandardBootstrapperApplication',
        'NetFx extension runtime/custom-action components',
        'Util extension runtime/custom-action components',
        'Microsoft Reciprocal License (MS-RL)',
        'https://github.com/wixtoolset/wix/tree/v6.0.2')) {
    if (-not $thirdPartyNotices.Contains($requiredIdentity, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Third-party notices do not recognize required component: $requiredIdentity"
    }
}

$gitIgnore = Get-Content -LiteralPath (Join-Path $repositoryRoot '.gitignore') -Raw
if (-not $gitIgnore.Contains('artifacts/', [StringComparison]::Ordinal)) {
    throw 'Generated artifacts must remain excluded from the tracked source tree.'
}

Write-Output "LICENSE_COMPLIANCE=PASS"
Write-Output "LICENSE_SHA256=$actualLicenseHash"
Write-Output "LICENSED_SOURCE_FILES=$($sourceFiles.Count)"
