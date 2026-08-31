# SPDX-License-Identifier: MPL-2.0

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'versioning.ps1')

$canonical = Get-RelayBridgeProductVersion -RepositoryRoot $repositoryRoot
if ($canonical -cne '1.0.0-rc.1') {
    throw "Unexpected canonical RelayBridge version: $canonical"
}

$orderedSemanticVersions = @(
    '0.9.2',
    '1.0.0-rc.1',
    '1.0.0-rc.2',
    '1.0.0',
    '1.0.1-rc.1',
    '1.0.1',
    '1.1.0-rc.1',
    '1.1.0',
    '2.0.0-rc.1',
    '2.0.0'
)
$previous = $null
foreach ($semantic in $orderedSemanticVersions) {
    $numeric = [version](ConvertTo-RelayBridgeMsiVersion -Version $semantic)
    if ($null -ne $previous -and $numeric -le $previous) {
        throw "MSI servicing versions are not strictly increasing at $semantic."
    }
    $previous = $numeric
}

foreach ($invalid in @(
    '1.0',
    '1.0.0-beta',
    '1.0.0-rc.0',
    '1.0.0-rc.255',
    '1.0.256',
    '256.0.0',
    '999999999999.0.0'
)) {
    try {
        [void](ConvertTo-RelayBridgeMsiVersion -Version $invalid)
        throw "Unsupported version was accepted: $invalid"
    }
    catch {
        if ($_.Exception.Message -eq "Unsupported version was accepted: $invalid") {
            throw
        }
    }
}

$installerSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'installer\Package.wxs') -Raw
$installerProject = Get-Content -LiteralPath (Join-Path $repositoryRoot 'installer\RelayBridge.Installer.wixproj') -Raw
$bundleProject = Get-Content -LiteralPath (Join-Path $repositoryRoot 'installer\RelayBridge.Bundle.wixproj') -Raw
$buildScript = Get-Content -LiteralPath (Join-Path $repositoryRoot 'installer\build-installer.ps1') -Raw
$sbomScript = Get-Content -LiteralPath (Join-Path $repositoryRoot 'installer\generate-sbom.ps1') -Raw
if (-not $installerSource.Contains('Version="$(var.MsiVersion)"', [StringComparison]::Ordinal) -or
    -not $installerProject.Contains('MsiVersion=$(MsiVersion)', [StringComparison]::Ordinal) -or
    -not $bundleProject.Contains('$(RelayBridgeVersion)', [StringComparison]::Ordinal) -or
    -not $buildScript.Contains('ConvertTo-RelayBridgeMsiVersion', [StringComparison]::Ordinal) -or
    -not $sbomScript.Contains("version = `$Version", [StringComparison]::Ordinal)) {
    throw 'Version-derived installer or SBOM inputs have drifted from the canonical model.'
}

Write-Output 'RELAYBRIDGE_VERSION_VERIFICATION=PASS'
Write-Output "PRODUCT_VERSION=$canonical"
Write-Output "MSI_VERSION=$(ConvertTo-RelayBridgeMsiVersion -Version $canonical)"
