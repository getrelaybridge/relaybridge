# SPDX-License-Identifier: MPL-2.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath($Path)
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Public source directory not found: $root"
}

foreach ($required in @(
        'LICENSE', 'README.md', 'SECURITY.md', 'CONTRIBUTING.md', 'CODE_OF_CONDUCT.md',
        'RelayBridge.sln', 'Directory.Build.props', 'src', 'tests', 'installer', 'eng', 'docs', '.github')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $required))) {
        throw "Public source snapshot is missing: $required"
    }
}

$forbiddenDirectories = @('.git', '.local', 'artifacts', 'bin', 'obj', 'TestResults', 'spool', 'Data')
foreach ($directory in Get-ChildItem -LiteralPath $root -Recurse -Directory -Force) {
    if ($forbiddenDirectories -ccontains $directory.Name) {
        throw "Public source snapshot contains a forbidden directory: $($directory.FullName)"
    }
}

$forbiddenExtensions = @(
    '.db', '.db-shm', '.db-wal', '.eml', '.exe', '.key', '.msi', '.nupkg', '.p12', '.pdb',
    '.pem', '.pfx', '.snupkg', '.trx', '.wixpdb'
)
$files = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force)
foreach ($file in $files) {
    if ($forbiddenExtensions -ccontains $file.Extension.ToLowerInvariant()) {
        throw "Public source snapshot contains a forbidden artifact: $($file.FullName)"
    }
}

$privatePathPrefix = 'C:' + '\Users\'
$privatePathHits = @($files | Select-String -SimpleMatch $privatePathPrefix -List)
if ($privatePathHits.Count -ne 0) {
    throw "Public source snapshot contains a workstation user path: $($privatePathHits[0].Path)"
}

Write-Output 'PUBLIC_SOURCE_VERIFICATION=PASS'
Write-Output "PUBLIC_SOURCE_FILES=$($files.Count)"
Write-Output 'PUBLIC_SOURCE_GIT_HISTORY=ABSENT'
Write-Output 'PUBLIC_SOURCE_BINARY_ARTIFACTS=ABSENT'
