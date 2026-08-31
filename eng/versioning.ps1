# SPDX-License-Identifier: MPL-2.0

Set-StrictMode -Version Latest

function Get-RelayBridgeProductVersion {
    param(
        [string] $RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    )

    $propsPath = Join-Path $RepositoryRoot 'Directory.Build.props'
    [xml]$props = Get-Content -LiteralPath $propsPath -Raw
    $versionNodes = @(
        @($props.Project.PropertyGroup.RelayBridgeVersion) |
            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
    )
    if ($versionNodes.Count -ne 1) {
        throw 'Directory.Build.props must define exactly one RelayBridgeVersion.'
    }

    $version = [string]$versionNodes[0]
    [void](ConvertTo-RelayBridgeMsiVersion -Version $version)
    return $version
}

function ConvertTo-RelayBridgeMsiVersion {
    param(
        [Parameter(Mandatory)]
        [string] $Version
    )

    $match = [regex]::Match(
        $Version,
        '^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-rc\.(?<rc>[1-9][0-9]*))?$',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
        throw "Unsupported RelayBridge product version: $Version"
    }

    $major = 0
    $minor = 0
    $patch = 0
    if (-not [int]::TryParse($match.Groups['major'].Value, [ref]$major) -or
        -not [int]::TryParse($match.Groups['minor'].Value, [ref]$minor) -or
        -not [int]::TryParse($match.Groups['patch'].Value, [ref]$patch)) {
        throw "RelayBridge product version exceeds supported numeric bounds: $Version"
    }

    if ($major -gt 255 -or $minor -gt 255 -or $patch -gt 255) {
        throw "RelayBridge product version exceeds Windows Installer bounds: $Version"
    }

    $stage = 255
    if ($match.Groups['rc'].Success) {
        $rc = 0
        if (-not [int]::TryParse($match.Groups['rc'].Value, [ref]$rc) -or $rc -gt 254) {
            throw "RelayBridge release-candidate number must be between 1 and 254: $Version"
        }
        $stage = $rc
    }

    $build = ($patch * 256) + $stage
    if ($build -gt 65535) {
        throw "RelayBridge product version exceeds Windows Installer build bounds: $Version"
    }

    return "$major.$minor.$build"
}
