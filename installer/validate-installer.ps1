# SPDX-License-Identifier: MPL-2.0

[CmdletBinding()]
param(
    [string] $Version = '',

    [string] $ArtifactRoot = (Join-Path $PSScriptRoot '..\artifacts\installer')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repositoryRoot 'eng\versioning.ps1')
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-RelayBridgeProductVersion -RepositoryRoot $repositoryRoot
}
$msiVersion = ConvertTo-RelayBridgeMsiVersion -Version $Version

$artifactRoot = [IO.Path]::GetFullPath($ArtifactRoot)
$stageRoot = Join-Path $artifactRoot 'stage'
$packageRoot = Join-Path $artifactRoot 'package'
$hostRoot = Join-Path $stageRoot 'Host'
$setupRoot = Join-Path $stageRoot 'Setup'
$toolingRoot = Join-Path $stageRoot 'Tooling'
$docsRoot = Join-Path $stageRoot 'Docs'

function Get-HexHash([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Assert-Manifest {
    param(
        [Parameter(Mandatory)][string] $Root,
        [Parameter(Mandatory)][string] $ManifestPath,
        [Parameter(Mandatory)][int] $Version
    )

    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    if ($manifest.Version -ne $Version -or $manifest.Files.Count -eq 0) {
        throw "Invalid manifest schema: $ManifestPath"
    }

    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.Files) {
        $path = [IO.Path]::GetFullPath((Join-Path $Root $entry.RelativePath))
        $boundary = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $path.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase) -or
            -not $expected.Add($path) -or
            -not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-HexHash $path) -ne $entry.Sha256) {
            throw "Manifest verification failed: $($entry.RelativePath)"
        }
    }

    $actual = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    Get-ChildItem -LiteralPath $Root -Recurse -File |
        Where-Object FullName -ne $ManifestPath |
        ForEach-Object { [void]$actual.Add($_.FullName) }
    if (-not $actual.SetEquals($expected)) {
        throw "Manifest closure differs from the staged tree: $ManifestPath"
    }
}

$required = @(
    (Join-Path $hostRoot 'RelayBridge.Host.exe'),
    (Join-Path $setupRoot 'RelayBridge.SetupLauncher.exe'),
    (Join-Path $setupRoot 'RelayBridge.PrinterConfigurator.exe'),
    (Join-Path $setupRoot 'RelayBridge.ManagementOpener.exe'),
    (Join-Path $setupRoot 'RelayBridge.Setup.exe'),
    (Join-Path $setupRoot 'RelayBridge.Setup.dll'),
    (Join-Path $setupRoot 'RelayBridge.Setup.deps.json'),
    (Join-Path $setupRoot 'RelayBridge.Setup.runtimeconfig.json'),
    (Join-Path $setupRoot 'RelayBridge.Core.dll'),
    (Join-Path $artifactRoot 'publish\Provisioner\RelayBridge.ToolingProvisioner.exe'),
    (Join-Path $toolingRoot 'PowerShell\7.6.4\pwsh.exe'),
    (Join-Path $toolingRoot 'Modules\Microsoft.Graph.Authentication\2.25.0\Microsoft.Graph.Authentication.psd1'),
    (Join-Path $toolingRoot 'Modules\Microsoft.Graph.Applications\2.25.0\Microsoft.Graph.Applications.psd1'),
    (Join-Path $toolingRoot 'Modules\Microsoft.Entra.Authentication\1.3.0\Microsoft.Entra.Authentication.psd1'),
    (Join-Path $toolingRoot 'Modules\Microsoft.Entra.Applications\1.3.0\Microsoft.Entra.Applications.psd1'),
    (Join-Path $toolingRoot 'Modules\ExchangeOnlineManagement\3.9.2\ExchangeOnlineManagement.psd1'),
    (Join-Path $docsRoot 'LICENSE'),
    (Join-Path $docsRoot 'THIRD-PARTY-NOTICES.md'),
    (Join-Path $docsRoot 'GETTING-STARTED.md'),
    (Join-Path $packageRoot "RelayBridge-$Version-win-x64.msi"),
    (Join-Path $packageRoot "RelayBridge-Setup-$Version-win-x64.exe")
    (Join-Path $packageRoot "RelayBridge-$Version-win-x64.cdx.json")
    (Join-Path $packageRoot 'THIRD-PARTY-NOTICES.md')
    (Join-Path $packageRoot 'LICENSE')
    (Join-Path $packageRoot 'GETTING-STARTED.md')
    (Join-Path $packageRoot "RELEASE-NOTES-$Version.md")
    (Join-Path $packageRoot 'RELEASE-PROVENANCE.json')
    (Join-Path $packageRoot 'SHA256SUMS.txt')
)
foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required installer payload is missing: $path"
    }
}

$helperManifestPath = Join-Path $setupRoot 'helper-manifest.json'
$toolingManifestPath = Join-Path $toolingRoot 'tooling-manifest.json'
Assert-Manifest $setupRoot $helperManifestPath 1
Assert-Manifest $toolingRoot $toolingManifestPath 2

$settings = Get-Content -LiteralPath (Join-Path $hostRoot 'appsettings.json') -Raw | ConvertFrom-Json
if (-not $settings.NativeMicrosoftSetup.Enabled -or
    $settings.Storage.DataDirectory -ne 'C:\ProgramData\RelayBridge\Data' -or
    $settings.NativeMicrosoftSetup.InstallationRoot -ne 'C:\Program Files\RelayBridge' -or
    $settings.NativeMicrosoftSetup.ExpectedLauncherSha256 -ne (Get-HexHash (Join-Path $setupRoot 'RelayBridge.SetupLauncher.exe')) -or
    $settings.NativeMicrosoftSetup.ExpectedHelperManifestSha256 -ne (Get-HexHash $helperManifestPath) -or
    $settings.NativeMicrosoftSetup.ExpectedToolingManifestSha256 -ne (Get-HexHash $toolingManifestPath) -or
    -not $settings.PrinterConnectivityApply.Enabled -or
    $settings.PrinterConnectivityApply.HelperPath -ne 'C:\Program Files\RelayBridge\Setup\RelayBridge.PrinterConfigurator.exe' -or
    $settings.PrinterConnectivityApply.ExpectedHelperSha256 -ne (Get-HexHash (Join-Path $setupRoot 'RelayBridge.PrinterConfigurator.exe'))) {
    throw 'The staged Host trust-anchor configuration does not match the staged release.'
}

$hostVersion = [Reflection.AssemblyName]::GetAssemblyName(
    (Join-Path $hostRoot 'RelayBridge.Host.dll')).Version.ToString()
$hostFileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo(
    (Join-Path $hostRoot 'RelayBridge.Host.dll'))
if ($hostVersion -ne (($Version -split '-', 2)[0] + '.0') -or
    $hostFileVersion.ProductVersion -ne $Version -or
    $hostFileVersion.FileVersion -ne (($Version -split '-', 2)[0] + '.0')) {
    throw 'The staged Host version metadata differs from the requested product version.'
}

$forbiddenNames = @('*.pfx', '*.p12', '*.key', '*.pem', '*.db', '*.db-wal', '*.db-shm', '*.eml', '*.pdb', '*.trx')
foreach ($pattern in $forbiddenNames) {
    if (Get-ChildItem -LiteralPath $stageRoot -Recurse -File -Filter $pattern | Select-Object -First 1) {
        throw "Forbidden package payload found: $pattern"
    }
}

$forbiddenPathSegments = @('\.local\', '\TestResults\', '\tests\', '\spool\')
foreach ($file in Get-ChildItem -LiteralPath $stageRoot -Recurse -File) {
    foreach ($segment in $forbiddenPathSegments) {
        if ($file.FullName.Contains($segment, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Development/test payload found: $($file.FullName)"
        }
    }
}

$generatedWix = Get-Content -LiteralPath (Join-Path $stageRoot 'GeneratedFiles.wxs') -Raw
if ($generatedWix -notmatch 'ComponentGroup Id="RelayBridgePayload"' -or
    $generatedWix -notmatch 'RelayBridgeDesktopShortcut' -or
    $generatedWix -notmatch 'RelayBridge\.ManagementOpener\.exe' -or
    $generatedWix -match '\.local\\' -or
    $generatedWix -match 'appsettings\.Development' -or
    $generatedWix -match 'Tooling\\Modules\\Microsoft\.(Graph|Entra)\.') {
    throw 'The generated MSI payload source is incomplete or contains development content.'
}

$generatedAcquisition = Get-Content -LiteralPath (Join-Path $stageRoot 'GeneratedAcquisition.wxs') -Raw
foreach ($package in (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'tooling-lock.json') -Raw |
        ConvertFrom-Json).externalAcquisition.packages) {
    foreach ($requiredValue in @($package.id, $package.version, $package.downloadUrl,
            [string]$package.size, $package.burnSha512)) {
        if (-not $generatedAcquisition.Contains($requiredValue, [StringComparison]::Ordinal)) {
            throw "The generated Burn acquisition source omits locked package identity: $requiredValue"
        }
    }
}

if (Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Filter '*.nupkg' | Select-Object -First 1) {
    throw 'Public installer artifacts contain Microsoft Graph/Entra package bytes.'
}

if (Get-ChildItem -LiteralPath $packageRoot -File |
        Where-Object Extension -in '.pdb', '.wixpdb', '.dbg' |
        Select-Object -First 1) {
    throw 'Public installer artifacts contain build symbols or debug databases.'
}

$sbomPath = Join-Path $packageRoot "RelayBridge-$Version-win-x64.cdx.json"
$sbomText = Get-Content -LiteralPath $sbomPath -Raw
$sbom = $sbomText | ConvertFrom-Json
if ($sbom.bomFormat -ne 'CycloneDX' -or $sbom.specVersion -ne '1.6' -or
    $sbom.metadata.component.name -ne 'RelayBridge' -or $sbom.metadata.component.version -ne $Version -or
    -not ($sbom.components | Where-Object { $_.name -eq 'PowerShell' -and $_.version -eq '7.6.4' }) -or
    -not ($sbom.components | Where-Object { $_.name -eq 'ExchangeOnlineManagement' -and $_.version -eq '3.9.2' }) -or
    -not ($sbom.components | Where-Object { $_.name -eq 'Microsoft.Graph.Authentication' -and $_.version -eq '2.25.0' }) -or
    -not ($sbom.components | Where-Object { $_.name -eq 'Microsoft.Entra.Authentication' -and $_.version -eq '1.3.0' }) -or
    -not ($sbom.components | Where-Object { $_.name -eq 'WiX Toolset SDK and compiler' -and $_.version -eq '6.0.2' -and $_.scope -eq 'excluded' }) -or
    -not ($sbom.components | Where-Object { $_.name -eq 'WiX Burn engine' -and $_.version -eq '6.0.2' -and $_.scope -eq 'required' }) -or
    -not ($sbom.components | Where-Object { $_.name -eq 'WiX Standard Bootstrapper Application' -and $_.version -eq '6.0.2' -and $_.scope -eq 'required' }) -or
    -not ($sbom.components | Where-Object { $_.name -eq 'WiX NetFx extension runtime and custom actions' -and $_.version -eq '6.0.2' -and $_.scope -eq 'required' }) -or
    -not ($sbom.components | Where-Object { $_.name -eq 'WiX Util extension runtime and custom actions' -and $_.version -eq '6.0.2' -and $_.scope -eq 'required' }) -or
    $sbomText -match '(?i)C:\\Users\\|SetupScratch|tenantId|clientId|access[_ -]?token') {
    throw 'The release SBOM is incomplete or contains machine/tenant credential context.'
}

$expectedPackageNames = @(
    'GETTING-STARTED.md',
    'LICENSE',
    'RELEASE-PROVENANCE.json',
    "RELEASE-NOTES-$Version.md",
    "RelayBridge-$Version-win-x64.cdx.json",
    "RelayBridge-$Version-win-x64.msi",
    "RelayBridge-Setup-$Version-win-x64.exe",
    'SHA256SUMS.txt',
    'THIRD-PARTY-NOTICES.md'
) | Sort-Object
$actualPackageNames = @(Get-ChildItem -LiteralPath $packageRoot -File | ForEach-Object Name | Sort-Object)
if ([string]::Join("`n", $actualPackageNames) -cne [string]::Join("`n", $expectedPackageNames)) {
    throw "The public release package differs from the exact allowlist: $($actualPackageNames -join ', ')"
}

foreach ($copy in @(
    @{ Source = (Join-Path $repositoryRoot 'LICENSE'); Package = 'LICENSE' },
    @{ Source = (Join-Path $repositoryRoot 'docs\release\GETTING-STARTED.md'); Package = 'GETTING-STARTED.md' },
    @{ Source = (Join-Path $repositoryRoot 'docs\release\THIRD-PARTY-NOTICES.md'); Package = 'THIRD-PARTY-NOTICES.md' },
    @{ Source = (Join-Path $repositoryRoot "docs\release\RELEASE-NOTES-$Version.md"); Package = "RELEASE-NOTES-$Version.md" }
)) {
    if ((Get-HexHash $copy.Source) -cne (Get-HexHash (Join-Path $packageRoot $copy.Package))) {
        throw "Release documentation differs from its source: $($copy.Package)"
    }
}

$gettingStarted = Get-Content -LiteralPath (Join-Path $packageRoot 'GETTING-STARTED.md') -Raw
$releaseNotes = Get-Content -LiteralPath (Join-Path $packageRoot "RELEASE-NOTES-$Version.md") -Raw
foreach ($requiredText in @(
    'unsigned pre-release for evaluation and community testing',
    "RelayBridge-Setup-$Version-win-x64.exe",
    'Unknown Publisher',
    'Microsoft Defender SmartScreen',
    'Apply printer connectivity',
    'inbound STARTTLS is unavailable',
    'does not download or install anything',
    'C:\ProgramData\RelayBridge'
)) {
    if (-not $gettingStarted.Contains($requiredText, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Getting Started omits required RC guidance: $requiredText"
    }
}
foreach ($requiredText in @(
    "# RelayBridge v$Version",
    'unsigned public pre-release for evaluation and community testing',
    'not the final stable production release',
    'Unknown Publisher',
    'Microsoft Defender SmartScreen',
    "RelayBridge-Setup-$Version-win-x64.exe",
    'signed stable `v1.0.0` release is not yet available'
)) {
    if (-not $releaseNotes.Contains($requiredText, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release notes omit required RC guidance: $requiredText"
    }
}

$sourceCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
$sourceStatus = @(& git -C $repositoryRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') {
    throw 'The source revision/state could not be validated against release provenance.'
}
$provenancePath = Join-Path $packageRoot 'RELEASE-PROVENANCE.json'
$provenanceText = Get-Content -LiteralPath $provenancePath -Raw
$provenance = $provenanceText | ConvertFrom-Json
$expectedSignatureState = if (
    (Get-AuthenticodeSignature -LiteralPath (Join-Path $packageRoot "RelayBridge-Setup-$Version-win-x64.exe")).Status -eq 'NotSigned' -and
    (Get-AuthenticodeSignature -LiteralPath (Join-Path $packageRoot "RelayBridge-$Version-win-x64.msi")).Status -eq 'NotSigned') {
    'NotSigned'
}
else {
    'Signed'
}
if ($provenance.SchemaVersion -ne 1 -or
    $provenance.Product -cne 'RelayBridge' -or
    $provenance.ProductVersion -cne $Version -or
    $provenance.SourceRepository -cne 'https://github.com/getrelaybridge/relaybridge' -or
    $provenance.SourceCommit -cne $sourceCommit -or
    $provenance.SourceRef -cne "v$Version" -or
    $provenance.SourceTreeClean -ne ($sourceStatus.Count -eq 0) -or
    $provenance.SupportedInstaller -cne "RelayBridge-Setup-$Version-win-x64.exe" -or
    $provenance.SupportingMsi -cne "RelayBridge-$Version-win-x64.msi" -or
    $provenance.SignatureState -cne $expectedSignatureState -or
    $provenanceText -match '(?i)C:\\Users\\|SetupScratch|tenantId|clientId|servicePrincipal|access[_ -]?token|sender@') {
    throw 'Release provenance is incomplete, inconsistent, or contains private machine/tenant context.'
}
$expectedClassification = if ($expectedSignatureState -eq 'NotSigned') {
    'UnsignedPublicPrereleaseCandidate'
}
else {
    'SignedReleaseCandidate'
}
if ($provenance.ArtifactClassification -cne $expectedClassification) {
    throw 'Release provenance classification differs from the actual signature state.'
}
$lockedExternal = @((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'tooling-lock.json') -Raw |
        ConvertFrom-Json).externalAcquisition.packages)
if ($provenance.ExternalAcquisition.Count -ne $lockedExternal.Count) {
    throw 'Release provenance external-acquisition inventory is incomplete.'
}
foreach ($lockedPackage in $lockedExternal) {
    $entry = @($provenance.ExternalAcquisition | Where-Object Id -CEQ $lockedPackage.id)
    if ($entry.Count -ne 1 -or
        $entry[0].Version -cne $lockedPackage.version -or
        $entry[0].DownloadUrl -cne $lockedPackage.downloadUrl -or
        $entry[0].Sha256 -cne $lockedPackage.sha256 -or
        $entry[0].Size -ne $lockedPackage.size) {
        throw "Release provenance differs from the locked external package: $($lockedPackage.id)"
    }
}

$checksumPath = Join-Path $packageRoot 'SHA256SUMS.txt'
$checksumFiles = @(
    Get-ChildItem -LiteralPath $packageRoot -File |
        Where-Object Name -ne 'SHA256SUMS.txt' |
        Sort-Object Name
)
$expectedChecksumText = (($checksumFiles | ForEach-Object {
            "$(Get-HexHash $_.FullName)  $($_.Name)"
        }) -join "`n") + "`n"
$actualChecksumText = [IO.File]::ReadAllText($checksumPath, [Text.Encoding]::UTF8)
if ($actualChecksumText -cne $expectedChecksumText -or $actualChecksumText.Contains("`r")) {
    throw 'SHA256SUMS.txt is incomplete, incorrectly ordered, or differs from the public artifacts.'
}

$packageSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Package.wxs') -Raw
foreach ($requiredText in @(
    'Name="RelayBridge"',
    'Scope="perMachine"',
    'ProgramFiles64Folder',
    'ServiceInstall',
    'ServiceControl',
    'Software\Classes\relaybridge-setup',
    'Software\Classes\relaybridge-printer',
    'DesktopFolder',
    'RelayBridge.PrinterConfigurator.exe',
    'ARPPRODUCTICON',
    'CommonAppDataFolder',
    'Permanent="yes"',
    'MajorUpgrade'
)) {
    if (-not $packageSource.Contains($requiredText, [StringComparison]::Ordinal)) {
        throw "Required MSI authoring is absent: $requiredText"
    }
}

$bundleSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Bundle.wxs') -Raw
foreach ($requiredText in @(
    'RelayBridgeAcceptMicrosoftGraphTerms',
    'https://aka.ms/devservicesagreement',
    'PackageGroupRef Id="RelayBridgeExternalMicrosoftModules"',
    'RELAYBRIDGE_EXTERNAL_TOOLING_TRANSACTION'
    'LogoFile=',
    'IconSourceFile=',
    'LaunchTarget=',
    'LaunchArguments="--setup"',
    'LocalizationFile="Bundle.en-us.wxl"',
    'Theme="hyperlinkLargeLicense"'
)) {
    if (-not $bundleSource.Contains($requiredText, [StringComparison]::Ordinal)) {
        throw "Required bundle acquisition/acceptance authoring is absent: $requiredText"
    }
}

[xml]$bundleLocalization = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Bundle.en-us.wxl') -Raw
$bundleStrings = @($bundleLocalization.SelectNodes('/*[local-name()="WixLocalization"]/*[local-name()="String"]'))
$requiredBundleStringIds = @(
    'Caption', 'Title', 'CheckingForUpdatesLabel', 'UpdateButton',
    'InstallHeader', 'InstallMessage', 'InstallMessageOptions', 'InstallVersion',
    'ConfirmCancelMessage', 'ExecuteUpgradeRelatedBundleMessage',
    'HelpHeader', 'HelpText', 'HelpCloseButton',
    'InstallLicenseLinkText', 'InstallAcceptCheckbox', 'InstallOptionsButton',
    'InstallInstallButton', 'InstallCancelButton',
    'OptionsHeader', 'OptionsLocationLabel', 'OptionsBrowseButton', 'OptionsOkButton', 'OptionsCancelButton',
    'ProgressHeader', 'ProgressLabel', 'OverallProgressPackageText', 'ProgressCancelButton',
    'ModifyHeader', 'ModifyRepairButton', 'ModifyUninstallButton', 'ModifyCancelButton',
    'SuccessHeader', 'SuccessCacheHeader', 'SuccessInstallHeader', 'SuccessLayoutHeader',
    'SuccessModifyHeader', 'SuccessRepairHeader', 'SuccessUninstallHeader', 'SuccessUnsafeUninstallHeader',
    'SuccessLaunchButton', 'SuccessRestartText', 'SuccessUninstallRestartText',
    'SuccessRestartButton', 'SuccessCloseButton',
    'FailureHeader', 'FailureCacheHeader', 'FailureInstallHeader', 'FailureLayoutHeader',
    'FailureModifyHeader', 'FailureRepairHeader', 'FailureUninstallHeader', 'FailureUnsafeUninstallHeader',
    'FailureHyperlinkLogText', 'FailureRestartText', 'FailureRestartButton', 'FailureCloseButton',
    'FilesInUseTitle', 'FilesInUseLabel', 'FilesInUseNetfxCloseRadioButton',
    'FilesInUseCloseRadioButton', 'FilesInUseDontCloseRadioButton',
    'FilesInUseRetryButton', 'FilesInUseIgnoreButton', 'FilesInUseExitButton'
)
$actualBundleStringIds = @($bundleStrings | ForEach-Object { $_.GetAttribute('Id') })
$missingBundleStringIds = @($requiredBundleStringIds | Where-Object { $_ -notin $actualBundleStringIds })
$duplicateBundleStringIds = @($actualBundleStringIds | Group-Object | Where-Object Count -gt 1)
if ($missingBundleStringIds.Count -gt 0 -or $duplicateBundleStringIds.Count -gt 0) {
    throw "The WixStdBA localization contract is incomplete or ambiguous. Missing: $($missingBundleStringIds -join ', ')."
}
if ($bundleStrings | Where-Object { $_.GetAttribute('Value').Contains('#(loc.', [StringComparison]::Ordinal) }) {
    throw 'The WixStdBA localization contains an unresolved localization reference.'
}
$acceptanceText = ($bundleStrings | Where-Object { $_.GetAttribute('Id') -eq 'InstallAcceptCheckbox' }).GetAttribute('Value')
if ($acceptanceText -ne 'I &accept the Microsoft Graph terms') {
    throw 'The Microsoft Graph acceptance label is not the tested concise installer text.'
}

Write-Output 'RELAYBRIDGE_INSTALLER_VALIDATION=PASS'
Write-Output "PRODUCT_VERSION=$Version"
Write-Output "MSI_VERSION=$msiVersion"
Write-Output "HELPER_FILES=$((Get-Content -LiteralPath $helperManifestPath -Raw | ConvertFrom-Json).Files.Count)"
Write-Output "TOOLING_FILES=$((Get-Content -LiteralPath $toolingManifestPath -Raw | ConvertFrom-Json).Files.Count)"
Write-Output "STAGED_FILES=$((Get-ChildItem -LiteralPath $stageRoot -Recurse -File).Count)"
Write-Output "MSI_SHA256=$(Get-HexHash (Join-Path $packageRoot "RelayBridge-$Version-win-x64.msi"))"
