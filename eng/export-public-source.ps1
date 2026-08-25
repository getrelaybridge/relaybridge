# SPDX-License-Identifier: MPL-2.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Destination
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$destinationFull = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $destinationFull) {
    throw "Refusing to overwrite an existing public-source destination: $destinationFull"
}

$trackedChanges = @(& git -C $repositoryRoot status --short --untracked-files=no)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect the tracked working tree.'
}
if ($trackedChanges.Count -ne 0) {
    throw 'Commit or revert tracked changes before exporting the public source snapshot.'
}

$parent = Split-Path -Parent $destinationFull
if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
    throw "The public-source destination parent does not exist: $parent"
}

$archivePath = Join-Path $parent ('.relaybridge-public-' + [Guid]::NewGuid().ToString('N') + '.zip')
try {
    & git -C $repositoryRoot archive --format=zip --output=$archivePath HEAD
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
        throw 'Git could not create the tracked-source archive.'
    }

    Expand-Archive -LiteralPath $archivePath -DestinationPath $destinationFull
    if (Test-Path -LiteralPath (Join-Path $destinationFull '.git')) {
        throw 'The public source snapshot unexpectedly contains Git metadata.'
    }
}
finally {
    Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
}

Write-Output 'PUBLIC_SOURCE_EXPORT=PASS'
Write-Output "PUBLIC_SOURCE_PATH=$destinationFull"
Write-Output "PUBLIC_SOURCE_FILES=$((Get-ChildItem -LiteralPath $destinationFull -Recurse -File).Count)"
