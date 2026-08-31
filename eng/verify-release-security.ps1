# SPDX-License-Identifier: MPL-2.0

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& (Join-Path $PSScriptRoot 'verify-version.ps1')
$tracked = @(& git -C $repositoryRoot ls-files)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to enumerate tracked release inputs.'
}

$forbiddenTracked = @($tracked | Where-Object {
    $_ -match '(^|/)(\.local|artifacts|bin|obj|TestResults|spool|Data)(/|$)' -or
    $_ -match '(?i)\.(db|db-shm|db-wal|eml|exe|key|msi|nupkg|p12|pdb|pem|pfx|snupkg|trx|wixpdb)$'
})
if ($forbiddenTracked.Count -ne 0) {
    throw "Tracked source contains a forbidden release artifact: $($forbiddenTracked[0])"
}

$productionFiles = @(
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -Recurse -File -Include '*.cs','*.razor'
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'installer') -Recurse -File -Include '*.ps1','*.wxs'
)
$unsafeTlsPatterns = @(
    'DangerousAcceptAnyServerCertificateValidator',
    'ServerCertificateCustomValidationCallback\s*=\s*[^;]*=>\s*true',
    'RemoteCertificateValidationCallback\s*=\s*[^;]*=>\s*true'
)
foreach ($pattern in $unsafeTlsPatterns) {
    $hit = $productionFiles | Select-String -Pattern $pattern -List | Select-Object -First 1
    if ($null -ne $hit) {
        throw "Production source contains an unsafe TLS bypass pattern: $($hit.Path):$($hit.LineNumber)"
    }
}

Write-Output 'RELEASE_SECURITY_STATIC_SCAN=PASS'
Write-Output "TRACKED_RELEASE_INPUTS=$($tracked.Count)"
