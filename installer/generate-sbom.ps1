# SPDX-License-Identifier: MPL-2.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(?:-rc\.[1-9]\d*)?$')][string] $Version,
    [Parameter(Mandatory)][string] $PublishRoot,
    [Parameter(Mandatory)][string] $ToolingLockPath,
    [Parameter(Mandatory)][string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$components = [ordered]@{}

function Add-Component {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $ComponentVersion,
        [ValidateSet('application', 'framework', 'library')][string] $Type = 'library',
        [ValidateSet('required', 'excluded')][string] $Scope = 'required',
        [string] $Purl = '',
        [string] $Distribution = 'Redistributed',
        [string] $LicenseId = '',
        [string] $SourceUrl = ''
    )

    $key = "$Name@$ComponentVersion"
    if ($components.Contains($key)) {
        return
    }

    $properties = @(
        [ordered]@{ name = 'relaybridge:distribution'; value = $Distribution }
    )
    if (-not [string]::IsNullOrWhiteSpace($SourceUrl)) {
        $properties += [ordered]@{ name = 'relaybridge:source'; value = $SourceUrl }
    }

    $component = [ordered]@{
        type = $Type
        'bom-ref' = $key
        name = $Name
        version = $ComponentVersion
        scope = $Scope
        properties = $properties
    }
    if (-not [string]::IsNullOrWhiteSpace($Purl)) {
        $component.purl = $Purl
    }
    if (-not [string]::IsNullOrWhiteSpace($LicenseId)) {
        $component.licenses = @(
            [ordered]@{ license = [ordered]@{ id = $LicenseId } }
        )
    }
    $components[$key] = $component
}

foreach ($depsPath in Get-ChildItem -LiteralPath $PublishRoot -Recurse -File -Filter '*.deps.json') {
    $deps = Get-Content -LiteralPath $depsPath.FullName -Raw | ConvertFrom-Json -AsHashtable
    foreach ($entry in $deps.libraries.GetEnumerator()) {
        $separator = $entry.Key.LastIndexOf('/')
        if ($separator -lt 1) {
            continue
        }
        $name = $entry.Key.Substring(0, $separator)
        $componentVersion = $entry.Key.Substring($separator + 1)
        if ($entry.Value.type -eq 'package') {
            Add-Component $name $componentVersion 'library' 'required' `
                ("pkg:nuget/{0}@{1}" -f [Uri]::EscapeDataString($name), [Uri]::EscapeDataString($componentVersion))
        }
        elseif ($entry.Value.type -eq 'project' -and $name.StartsWith('RelayBridge.', [StringComparison]::Ordinal)) {
            Add-Component $name $Version 'application' 'required' '' 'RelayBridge'
        }
    }
}

$lock = Get-Content -LiteralPath $ToolingLockPath -Raw | ConvertFrom-Json
Add-Component 'PowerShell' $lock.powerShell.version 'application' 'required' '' 'Redistributed'
$directAcquisitionNames = @($lock.externalAcquisition.packages | ForEach-Object { $_.id })
foreach ($module in $lock.modules) {
    $distribution = if ($directAcquisitionNames -ccontains $module.name) {
        'Installer-acquired directly from Microsoft'
    } else {
        'Redistributed'
    }
    Add-Component $module.name $module.version 'library' 'required' `
        ("pkg:nuget/{0}@{1}" -f [Uri]::EscapeDataString($module.name), [Uri]::EscapeDataString($module.version)) `
        $distribution
}
foreach ($runtime in $lock.dotNetPrerequisites) {
    Add-Component $runtime.name $runtime.version 'framework' 'required' '' 'Installer-acquired from Microsoft'
}
$wixSource = 'https://github.com/wixtoolset/wix/tree/v6.0.2'
Add-Component 'WiX Toolset SDK and compiler' '6.0.2' 'application' 'excluded' `
    'pkg:nuget/WixToolset.Sdk@6.0.2' 'Build tooling; not redistributed as a RelayBridge runtime component' `
    'MS-RL' $wixSource
Add-Component 'WiX Burn engine' '6.0.2' 'application' 'required' '' `
    'Redistributed installer runtime' 'MS-RL' $wixSource
Add-Component 'WiX Standard Bootstrapper Application' '6.0.2' 'application' 'required' `
    'pkg:nuget/WixToolset.Bal.wixext@6.0.2' 'Redistributed installer runtime' 'MS-RL' $wixSource
Add-Component 'WiX NetFx extension runtime and custom actions' '6.0.2' 'library' 'required' `
    'pkg:nuget/WixToolset.Netfx.wixext@6.0.2' 'Redistributed installer runtime' 'MS-RL' $wixSource
Add-Component 'WiX Util extension runtime and custom actions' '6.0.2' 'library' 'required' `
    'pkg:nuget/WixToolset.Util.wixext@6.0.2' 'Redistributed installer runtime' 'MS-RL' $wixSource

$sbom = [ordered]@{
    bomFormat = 'CycloneDX'
    specVersion = '1.6'
    version = 1
    metadata = [ordered]@{
        component = [ordered]@{
            type = 'application'
            'bom-ref' = "RelayBridge@$Version"
            name = 'RelayBridge'
            version = $Version
        }
        properties = @(
            [ordered]@{ name = 'relaybridge:platform'; value = 'win-x64' },
            [ordered]@{ name = 'relaybridge:artifact'; value = 'Windows installer distribution' }
        )
    }
    components = @($components.Values)
}

$parent = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$json = $sbom | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText($OutputPath, $json, [Text.UTF8Encoding]::new($false))
Write-Output "RELAYBRIDGE_SBOM=$OutputPath"
