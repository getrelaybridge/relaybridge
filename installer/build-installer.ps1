# SPDX-License-Identifier: MPL-2.0

[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version = '0.9.2',

    [ValidateSet('Release')]
    [string] $Configuration = 'Release',

    [string] $SigningCertificateThumbprint = '',

    [switch] $SkipBundle
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$env:MSBUILDDISABLENODEREUSE = '1'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\installer'))
$publishRoot = Join-Path $artifactRoot 'publish'
$stageRoot = Join-Path $artifactRoot 'stage'
$cacheRoot = Join-Path $artifactRoot 'cache'
$prerequisiteRoot = Join-Path $artifactRoot 'prerequisites'
$packageRoot = Join-Path $artifactRoot 'package'
$symbolsRoot = Join-Path $artifactRoot 'symbols'
$brandingRoot = Join-Path $artifactRoot 'branding'
$lockPath = Join-Path $PSScriptRoot 'tooling-lock.json'
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
$externalMsiExcludedRoots = @()

if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw 'The pinned .NET SDK host was not found at the standard x64 Program Files path.'
}

function Assert-UnderArtifactRoot {
    param([Parameter(Mandatory)][string] $Path)

    $full = [IO.Path]::GetFullPath($Path)
    $boundary = $artifactRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to alter a path outside the installer artifact root: $full"
    }
}

function Reset-Directory {
    param([Parameter(Mandatory)][string] $Path)

    Assert-UnderArtifactRoot $Path
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    New-Item -ItemType Directory -Path $Path | Out-Null
}

function Get-HexHash {
    param(
        [Parameter(Mandatory)][string] $Path,
        [ValidateSet('SHA256', 'SHA512')][string] $Algorithm = 'SHA256'
    )

    return (Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash.ToUpperInvariant()
}

function New-RelayBridgeBrandAssets {
    $source = Join-Path $PSScriptRoot 'branding\relaybridge-mark.svg'
    if ((Get-HexHash $source) -cne '407DA34A58F095CF192CCC3AECCF02B0CF2BF22E3975F8367D634DCD2B443569') {
        throw 'The RelayBridge website mark does not match its reviewed public source.'
    }

    Add-Type -AssemblyName System.Drawing.Common
    $bitmap = [Drawing.Bitmap]::new(128, 128, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.ScaleTransform(2, 2)
        $background = [Drawing.Drawing2D.GraphicsPath]::new()
        try {
            $background.AddArc(0, 0, 28, 28, 180, 90)
            $background.AddArc(36, 0, 28, 28, 270, 90)
            $background.AddArc(36, 36, 28, 28, 0, 90)
            $background.AddArc(0, 36, 28, 28, 90, 90)
            $background.CloseFigure()
            $brush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(11, 31, 41))
            try { $graphics.FillPath($brush, $background) } finally { $brush.Dispose() }
        } finally { $background.Dispose() }

        $pen = [Drawing.Pen]::new([Drawing.Color]::FromArgb(58, 216, 177), 5)
        $pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
        try {
            $left = [Drawing.Drawing2D.GraphicsPath]::new()
            try {
                $left.StartFigure()
                $left.AddBezier(13, 24, 13, 19, 17, 15, 22, 15)
                $left.AddLine(22, 15, 34, 15)
                $left.AddLine(34, 15, 34, 24)
                $left.AddLine(34, 24, 22, 24)
                $left.AddLine(22, 24, 22, 32)
                $left.AddLine(22, 32, 29, 32)
                $left.AddLine(29, 32, 29, 41)
                $left.AddLine(29, 41, 22, 41)
                $left.AddBezier(22, 41, 17, 41, 13, 37, 13, 32)
                $left.AddLine(13, 32, 13, 24)
                $graphics.DrawPath($pen, $left)
            } finally { $left.Dispose() }

            $right = [Drawing.Drawing2D.GraphicsPath]::new()
            try {
                $right.StartFigure()
                $right.AddBezier(51, 40, 51, 45, 47, 49, 42, 49)
                $right.AddLine(42, 49, 30, 49)
                $right.AddLine(30, 49, 30, 40)
                $right.AddLine(30, 40, 42, 40)
                $right.AddLine(42, 40, 42, 32)
                $right.AddLine(42, 32, 35, 32)
                $right.AddLine(35, 32, 35, 23)
                $right.AddLine(35, 23, 42, 23)
                $right.AddBezier(42, 23, 47, 23, 51, 27, 51, 32)
                $right.AddLine(51, 32, 51, 40)
                $graphics.DrawPath($pen, $right)
            } finally { $right.Dispose() }
        } finally { $pen.Dispose() }
    } finally {
        $graphics.Dispose()
    }

    $png = Join-Path $brandingRoot 'RelayBridge.png'
    $ico = Join-Path $brandingRoot 'RelayBridge.ico'
    $bitmap.Save($png, [Drawing.Imaging.ImageFormat]::Png)
    $iconHandle = $bitmap.GetHicon()
    $icon = [Drawing.Icon]::FromHandle($iconHandle)
    try {
        $stream = [IO.File]::Create($ico)
        try { $icon.Save($stream) } finally { $stream.Dispose() }
    } finally {
        $icon.Dispose()
        $bitmap.Dispose()
    }
}

function Get-LockedFile {
    param(
        [Parameter(Mandatory)][string] $Uri,
        [Parameter(Mandatory)][string] $Destination,
        [Parameter(Mandatory)][string] $ExpectedHash,
        [ValidateSet('SHA256', 'SHA512')][string] $Algorithm = 'SHA256'
    )

    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        if ((Get-HexHash $Destination $Algorithm) -eq $ExpectedHash.ToUpperInvariant()) {
            return
        }
        Remove-Item -LiteralPath $Destination -Force
    }

    $partial = $Destination + '.partial'
    Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
    try {
        Invoke-WebRequest -Uri $Uri -OutFile $partial -MaximumRedirection 5
        if ((Get-HexHash $partial $Algorithm) -ne $ExpectedHash.ToUpperInvariant()) {
            throw "The acquired package hash does not match the reviewed lock: $Uri"
        }
        Move-Item -LiteralPath $partial -Destination $Destination
    }
    finally {
        Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]] $Arguments)

    $effectiveArguments = @($Arguments)
    if ($effectiveArguments.Count -gt 0 -and $effectiveArguments[0] -in @('build', 'publish')) {
        $effectiveArguments += @('-m:1', '-nr:false', '--tl:off')
    }

    & $dotnet @effectiveArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE."
    }
}

function Assert-LockedExternalPackage {
    param(
        [Parameter(Mandatory)][string] $PackagePath,
        [Parameter(Mandatory)] $Identity
    )

    $file = Get-Item -LiteralPath $PackagePath
    if ($file.Length -ne [long]$Identity.size -or
        (Get-HexHash $PackagePath 'SHA256') -ne $Identity.sha256.ToUpperInvariant() -or
        (Get-HexHash $PackagePath 'SHA512') -ne $Identity.burnSha512.ToUpperInvariant()) {
        throw "External package bytes do not match the acquisition lock: $($Identity.id)"
    }

    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $nuspecs = @($archive.Entries | Where-Object {
            $_.FullName.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) -and
            -not $_.FullName.Contains('/') -and -not $_.FullName.Contains('\')
        })
        if ($nuspecs.Count -ne 1 -or
            -not ($archive.Entries | Where-Object FullName -eq '.signature.p7s')) {
            throw "External package structure is not the approved Gallery form: $($Identity.id)"
        }

        $reader = [IO.StreamReader]::new($nuspecs[0].Open())
        try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $nuspec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        $value = {
            param([string] $Name)
            return $metadata.SelectSingleNode(('*[local-name()="{0}"]' -f $Name)).InnerText
        }
        $requireAcceptance = [bool]::Parse((& $value 'requireLicenseAcceptance'))
        if ((& $value 'id') -cne $Identity.id -or
            (& $value 'version') -cne $Identity.version -or
            $requireAcceptance -ne [bool]$Identity.requireLicenseAcceptance -or
            (& $value 'licenseUrl') -cne $Identity.licenseUri) {
            throw "External package metadata differs from the acquisition lock: $($Identity.id)"
        }

        $actualDependencies = @(
            $metadata.SelectNodes('.//*[local-name()="dependency"]') |
                ForEach-Object { $_.GetAttribute('id') + '|' + $_.GetAttribute('version') } |
                Sort-Object
        )
        $expectedDependencies = @(
            $Identity.dependencies |
                ForEach-Object { $_.id + '|' + $_.versionRange } |
                Sort-Object
        )
        if ([string]::Join("`n", $actualDependencies) -cne [string]::Join("`n", $expectedDependencies)) {
            throw "External package dependencies differ from the acquisition lock: $($Identity.id)"
        }

        if ($requireAcceptance -and ($archive.Entries | Where-Object {
                $_.FullName.EndsWith('license.txt', [StringComparison]::OrdinalIgnoreCase)
            })) {
            throw "The approved missing-license.txt identity changed: $($Identity.id)"
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory)] $Value,
        [Parameter(Mandatory)][string] $Path,
        [int] $Depth = 12
    )

    $json = $Value | ConvertTo-Json -Depth $Depth -Compress
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

function New-FileManifestEntries {
    param(
        [Parameter(Mandatory)][string] $Root,
        [string[]] $ExcludedRelativePaths = @()
    )

    $excluded = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $ExcludedRelativePaths) {
        [void]$excluded.Add($item)
    }

    return @(
        Get-ChildItem -LiteralPath $Root -Recurse -File |
            ForEach-Object {
                $relative = [IO.Path]::GetRelativePath($Root, $_.FullName)
                if (-not $excluded.Contains($relative)) {
                    [ordered]@{
                        RelativePath = $relative
                        Sha256 = Get-HexHash $_.FullName
                        RequireMicrosoftSignature = $false
                    }
                }
            } |
            Sort-Object { $_.RelativePath }
    )
}

function Get-StableId {
    param(
        [Parameter(Mandatory)][string] $Prefix,
        [Parameter(Mandatory)][string] $Value
    )

    $bytes = [Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant())
    try {
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
    return $Prefix + $hash.Substring(0, 28)
}

function Escape-XmlAttribute {
    param([Parameter(Mandatory)][string] $Value)
    return [Security.SecurityElement]::Escape($Value).Replace('"', '&quot;')
}

function Write-WixDirectoryContents {
    param(
        [Parameter(Mandatory)][Text.StringBuilder] $Builder,
        [Parameter(Mandatory)][IO.DirectoryInfo] $Directory,
        [Parameter(Mandatory)][string] $StageRelativeRoot,
        [Collections.Generic.List[string]] $ComponentIds,
        [int] $Indent = 3
    )

    $padding = '  ' * $Indent
    foreach ($file in @($Directory.EnumerateFiles() | Sort-Object Name)) {
        $relative = [IO.Path]::GetRelativePath($stageRoot, $file.FullName)
        if ($relative.Equals('Host\RelayBridge.Host.exe', [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $componentId = Get-StableId 'Cmp_' $relative
        $fileId = Get-StableId 'Fil_' $relative
        # SQLitePCLRaw's native e_sqlite3 binary is a DLL without version-language
        # metadata. Supplying a default language is the narrow ICE60-compatible
        # treatment; all other payload files retain normal binder inspection.
        $defaultLanguage = if ($file.Name.Equals('e_sqlite3.dll', [StringComparison]::OrdinalIgnoreCase)) {
            ' DefaultLanguage="1033"'
        }
        else {
            ''
        }
        $ComponentIds.Add($componentId)
        [void]$Builder.AppendLine(('{0}<Component Id="{1}" Guid="*" Bitness="always64">' -f $padding, $componentId))
        if ($relative.Equals('Setup\RelayBridge.ManagementOpener.exe', [StringComparison]::OrdinalIgnoreCase)) {
            [void]$Builder.AppendLine(('{0}  <File Id="{1}" Source="{2}" KeyPath="yes"{3}>' -f $padding, $fileId, (Escape-XmlAttribute $file.FullName), $defaultLanguage))
            [void]$Builder.AppendLine(('{0}    <Shortcut Id="RelayBridgeDesktopShortcut" Directory="DesktopFolder" Name="RelayBridge" Description="Open RelayBridge local management" WorkingDirectory="INSTALLFOLDER" Icon="RelayBridge.ico" Advertise="yes" />' -f $padding))
            [void]$Builder.AppendLine("$padding  </File>")
        }
        else {
            [void]$Builder.AppendLine(('{0}  <File Id="{1}" Source="{2}" KeyPath="yes"{3} />' -f $padding, $fileId, (Escape-XmlAttribute $file.FullName), $defaultLanguage))
        }
        [void]$Builder.AppendLine("$padding</Component>")
    }

    foreach ($child in @($Directory.EnumerateDirectories() | Sort-Object Name)) {
        $relative = [IO.Path]::GetRelativePath($stageRoot, $child.FullName)
        if ($externalMsiExcludedRoots -contains $relative) {
            continue
        }
        $directoryId = Get-StableId 'Dir_' $relative
        [void]$Builder.AppendLine(('{0}<Directory Id="{1}" Name="{2}">' -f $padding, $directoryId, (Escape-XmlAttribute $child.Name)))
        Write-WixDirectoryContents $Builder $child $StageRelativeRoot $ComponentIds ($Indent + 1)
        [void]$Builder.AppendLine("$padding</Directory>")
    }
}

function New-GeneratedAcquisitionWixSource {
    $external = $lock.externalAcquisition
    $builder = [Text.StringBuilder]::new()
    [void]$builder.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    [void]$builder.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
    [void]$builder.AppendLine('  <Fragment>')
    [void]$builder.AppendLine('    <PackageGroup Id="RelayBridgeExternalMicrosoftModules">')
    [void]$builder.AppendLine('      <ExePackage Id="RelayBridgeToolingProvisioner"')
    [void]$builder.AppendLine('                  SourceFile="$(var.ProvisionerPath)"')
    [void]$builder.AppendLine(('                  InstallArguments="install --cache &quot;[WixBundleExecutePackageCacheFolder].&quot; --release $(var.ProductVersion) --ui-level [WixBundleUILevel] --accept-variable [RelayBridgeAcceptMicrosoftGraphTerms]"'))
    [void]$builder.AppendLine(('                  RepairArguments="repair --cache &quot;[WixBundleExecutePackageCacheFolder].&quot; --release $(var.ProductVersion) --ui-level [WixBundleUILevel] --accept-variable [RelayBridgeAcceptMicrosoftGraphTerms]"'))
    [void]$builder.AppendLine('                  UninstallArguments="uninstall"')
    [void]$builder.AppendLine(('                  DetectCondition="RelayBridgeExternalToolingIdentity = &quot;{0}&quot; AND RelayBridgeExternalToolingRelease = &quot;$(var.ProductVersion)&quot;"' -f $external.toolingIdentitySha256))
    [void]$builder.AppendLine('                  PerMachine="yes" Vital="yes" Cache="keep">')
    [void]$builder.AppendLine('        <Payload Id="RelayBridgeToolingClosureManifest"')
    [void]$builder.AppendLine('                 Name="Metadata\tooling-manifest.json"')
    [void]$builder.AppendLine('                 SourceFile="$(var.ToolingManifestPath)" />')
    foreach ($package in $external.packages) {
        $payloadId = 'Payload_' + ($package.id -replace '[^A-Za-z0-9_]', '_')
        $fileName = $package.id + '.' + $package.version + '.nupkg'
        [void]$builder.AppendLine(('        <Payload Id="{0}"' -f $payloadId))
        [void]$builder.AppendLine(('                 Name="Packages\{0}"' -f (Escape-XmlAttribute $fileName)))
        [void]$builder.AppendLine(('                 DownloadUrl="{0}"' -f (Escape-XmlAttribute $package.downloadUrl)))
        [void]$builder.AppendLine(('                 Size="{0}"' -f $package.size))
        [void]$builder.AppendLine(('                 Hash="{0}" />' -f $package.burnSha512))
    }
    [void]$builder.AppendLine('      </ExePackage>')
    [void]$builder.AppendLine('    </PackageGroup>')
    [void]$builder.AppendLine('  </Fragment>')
    [void]$builder.AppendLine('</Wix>')
    [IO.File]::WriteAllText(
        (Join-Path $stageRoot 'GeneratedAcquisition.wxs'),
        $builder.ToString(),
        [Text.UTF8Encoding]::new($false))
}

function New-GeneratedWixSource {
    $componentIds = [Collections.Generic.List[string]]::new()
    $builder = [Text.StringBuilder]::new()
    [void]$builder.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    [void]$builder.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')

    foreach ($topLevel in @(
        @{ Id = 'HOSTDIR'; Name = 'Host' },
        @{ Id = 'SETUPDIR'; Name = 'Setup' },
        @{ Id = 'TOOLINGDIR'; Name = 'Tooling' },
        @{ Id = 'DOCSDIR'; Name = 'Docs' }
    )) {
        [void]$builder.AppendLine('  <Fragment>')
        [void]$builder.AppendLine(('    <DirectoryRef Id="{0}">' -f $topLevel.Id))
        $directory = [IO.DirectoryInfo]::new((Join-Path $stageRoot $topLevel.Name))
        Write-WixDirectoryContents $builder $directory $topLevel.Name $componentIds 3
        [void]$builder.AppendLine('    </DirectoryRef>')
        [void]$builder.AppendLine('  </Fragment>')
    }

    [void]$builder.AppendLine('  <Fragment>')
    [void]$builder.AppendLine('    <ComponentGroup Id="RelayBridgePayload">')
    foreach ($componentId in $componentIds | Sort-Object) {
        [void]$builder.AppendLine(('      <ComponentRef Id="{0}" />' -f $componentId))
    }
    [void]$builder.AppendLine('    </ComponentGroup>')
    [void]$builder.AppendLine('  </Fragment>')
    [void]$builder.AppendLine('</Wix>')
    [IO.File]::WriteAllText(
        (Join-Path $stageRoot 'GeneratedFiles.wxs'),
        $builder.ToString(),
        [Text.UTF8Encoding]::new($false))
}

function Invoke-OptionalSigning {
    param([Parameter(Mandatory)][string[]] $Paths)

    if ([string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
        return
    }

    $signtool = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -Recurse -File |
        Where-Object FullName -Match '\\x64\\signtool\.exe$' |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -eq $signtool) {
        throw 'A Windows SDK x64 signtool.exe is required for release signing.'
    }

    foreach ($path in $Paths) {
        & $signtool.FullName sign /sha1 $SigningCertificateThumbprint /fd SHA256 /tr https://timestamp.digicert.com /td SHA256 $path
        if ($LASTEXITCODE -ne 0) {
            throw "Authenticode signing failed: $path"
        }
    }
}

$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
if ($lock.version -ne 2 -or $lock.powerShell.version -ne '7.6.4' -or $lock.modules.Count -ne 5 -or
    $lock.externalAcquisition.schemaVersion -ne 1 -or $lock.externalAcquisition.packages.Count -ne 4) {
    throw 'The installer tooling lock is not the reviewed schema/version set.'
}
$externalMsiExcludedRoots = @(
    $lock.externalAcquisition.packages | ForEach-Object { 'Tooling\Modules\' + $_.id }
)

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
Reset-Directory $publishRoot
Reset-Directory $stageRoot
Reset-Directory $packageRoot
Reset-Directory $symbolsRoot
Reset-Directory $brandingRoot
New-RelayBridgeBrandAssets
New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null
New-Item -ItemType Directory -Path $prerequisiteRoot -Force | Out-Null

$commonPublish = @(
    '-c', $Configuration,
    '-r', 'win-x64',
    '--self-contained', 'false',
    ('-p:Version=' + $Version),
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '--nologo'
)

$hostPublish = Join-Path $publishRoot 'Host'
$setupPublish = Join-Path $publishRoot 'Setup'
$launcherPublish = Join-Path $publishRoot 'Launcher'
$provisionerPublish = Join-Path $publishRoot 'Provisioner'
$printerConfiguratorPublish = Join-Path $publishRoot 'PrinterConfigurator'
$managementOpenerPublish = Join-Path $publishRoot 'ManagementOpener'

Invoke-DotNet (@('publish', 'src\RelayBridge.Host\RelayBridge.Host.csproj') + $commonPublish + @('-o', $hostPublish))
Invoke-DotNet (@('publish', 'src\RelayBridge.Setup\RelayBridge.Setup.csproj') + $commonPublish + @('-o', $setupPublish))
Invoke-DotNet @(
    'publish',
    'src\RelayBridge.SetupLauncher\RelayBridge.SetupLauncher.csproj',
    '-c', $Configuration,
    '-r', 'win-x64',
    '--self-contained', 'true',
    ('-p:Version=' + $Version),
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-o', $launcherPublish,
    '--nologo'
)
Invoke-DotNet @(
    'publish',
    'src\RelayBridge.PrinterConfigurator\RelayBridge.PrinterConfigurator.csproj',
    '-c', $Configuration,
    '-r', 'win-x64',
    '--self-contained', 'true',
    ('-p:Version=' + $Version),
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-o', $printerConfiguratorPublish,
    '--nologo'
)
Invoke-DotNet @(
    'publish',
    'src\RelayBridge.ManagementOpener\RelayBridge.ManagementOpener.csproj',
    '-c', $Configuration,
    '-r', 'win-x64',
    '--self-contained', 'true',
    ('-p:Version=' + $Version),
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-o', $managementOpenerPublish,
    '--nologo'
)
Invoke-DotNet @(
    'publish',
    'src\RelayBridge.ToolingProvisioner\RelayBridge.ToolingProvisioner.csproj',
    '-c', $Configuration,
    '-r', 'win-x64',
    '--self-contained', 'true',
    ('-p:Version=' + $Version),
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-o', $provisionerPublish,
    '--nologo'
)

$hostStage = Join-Path $stageRoot 'Host'
$setupStage = Join-Path $stageRoot 'Setup'
$toolingStage = Join-Path $stageRoot 'Tooling'
$docsStage = Join-Path $stageRoot 'Docs'
New-Item -ItemType Directory -Path $hostStage, $setupStage, $toolingStage, $docsStage | Out-Null
Copy-Item -Path (Join-Path $hostPublish '*') -Destination $hostStage -Recurse
Copy-Item -Path (Join-Path $setupPublish '*') -Destination $setupStage -Recurse
Copy-Item -LiteralPath (Join-Path $launcherPublish 'RelayBridge.SetupLauncher.exe') -Destination $setupStage
Copy-Item -LiteralPath (Join-Path $printerConfiguratorPublish 'RelayBridge.PrinterConfigurator.exe') -Destination $setupStage
Copy-Item -LiteralPath (Join-Path $managementOpenerPublish 'RelayBridge.ManagementOpener.exe') -Destination $setupStage
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $docsStage
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\release\THIRD-PARTY-NOTICES.md') -Destination $docsStage
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\release\GETTING-STARTED.md') -Destination $docsStage

Get-ChildItem -LiteralPath $hostStage, $setupStage -Recurse -File |
    Where-Object { $_.Extension -in @('.pdb', '.dbg') -or $_.Name -eq 'appsettings.Development.json' } |
    Remove-Item -Force

$powerShellPackage = Join-Path $cacheRoot 'PowerShell-7.6.4-win-x64.zip'
Get-LockedFile $lock.powerShell.source $powerShellPackage $lock.powerShell.sha256
$powerShellDestination = Join-Path $toolingStage 'PowerShell\7.6.4'
New-Item -ItemType Directory -Path $powerShellDestination -Force | Out-Null
[IO.Compression.ZipFile]::ExtractToDirectory($powerShellPackage, $powerShellDestination, $true)

$moduleRoot = Join-Path $toolingStage 'Modules'
foreach ($module in $lock.modules) {
    $packagePath = Join-Path $cacheRoot ($module.name + '.' + $module.version + '.nupkg')
    Get-LockedFile $module.source $packagePath $module.sha256
    $externalIdentity = $lock.externalAcquisition.packages |
        Where-Object id -CEQ $module.name |
        Select-Object -First 1
    if ($null -ne $externalIdentity) {
        Assert-LockedExternalPackage $packagePath $externalIdentity
    }
    $destination = Join-Path $moduleRoot ($module.name + '\' + $module.version)
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($packagePath, $destination, $true)
    if ($module.requirePackageSignature -and
        -not (Test-Path -LiteralPath (Join-Path $destination '.signature.p7s') -PathType Leaf)) {
        throw "The signed PowerShell Gallery package is missing its package signature: $($module.name)"
    }
}

# Debug-symbol payloads are not required to execute the private provisioning
# runtime. Exclude symbols supplied by upstream archives before the frozen
# tooling closure is generated.
Get-ChildItem -LiteralPath $toolingStage -Recurse -File |
    Where-Object { $_.Extension -in @('.pdb', '.dbg') } |
    Remove-Item -Force

$pwshSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $powerShellDestination 'pwsh.exe')
if ($pwshSignature.Status -ne 'Valid' -or
    $pwshSignature.SignerCertificate.Subject -notmatch '(^|, )CN=Microsoft Corporation(,|$)') {
    throw 'The private PowerShell executable is not validly signed by Microsoft Corporation.'
}

$provenance = [ordered]@{
    Version = 1
    PowerShell = $lock.powerShell
    Modules = $lock.modules
}
Write-JsonFile $provenance (Join-Path $toolingStage 'package-provenance.json') 8

Invoke-OptionalSigning @(
    (Join-Path $hostStage 'RelayBridge.Host.exe'),
    (Join-Path $setupStage 'RelayBridge.SetupLauncher.exe'),
    (Join-Path $setupStage 'RelayBridge.PrinterConfigurator.exe'),
    (Join-Path $setupStage 'RelayBridge.ManagementOpener.exe'),
    (Join-Path $setupStage 'RelayBridge.Setup.exe'),
    (Join-Path $setupStage 'RelayBridge.Setup.dll'),
    (Join-Path $setupStage 'RelayBridge.Core.dll'),
    (Join-Path $provisionerPublish 'RelayBridge.ToolingProvisioner.exe')
)

$toolingManifestPath = Join-Path $toolingStage 'tooling-manifest.json'
$toolingManifest = [ordered]@{
    Version = 2
    PowerShellRelativePath = 'PowerShell\7.6.4\pwsh.exe'
    GraphAuthenticationModuleRelativePath = 'Modules\Microsoft.Graph.Authentication\2.25.0\Microsoft.Graph.Authentication.psd1'
    GraphAuthenticationModuleVersion = '2.25.0'
    GraphApplicationsModuleRelativePath = 'Modules\Microsoft.Graph.Applications\2.25.0\Microsoft.Graph.Applications.psd1'
    GraphApplicationsModuleVersion = '2.25.0'
    EntraAuthenticationModuleRelativePath = 'Modules\Microsoft.Entra.Authentication\1.3.0\Microsoft.Entra.Authentication.psd1'
    EntraAuthenticationModuleVersion = '1.3.0'
    EntraApplicationsModuleRelativePath = 'Modules\Microsoft.Entra.Applications\1.3.0\Microsoft.Entra.Applications.psd1'
    EntraApplicationsModuleVersion = '1.3.0'
    ExchangeOnlineModuleRelativePath = 'Modules\ExchangeOnlineManagement\3.9.2\ExchangeOnlineManagement.psd1'
    ExchangeOnlineModuleVersion = '3.9.2'
    Files = New-FileManifestEntries $toolingStage @('tooling-manifest.json')
}
Write-JsonFile $toolingManifest $toolingManifestPath 8
$toolingManifestHash = Get-HexHash $toolingManifestPath
New-GeneratedAcquisitionWixSource

$helperManifestPath = Join-Path $setupStage 'helper-manifest.json'
$helperEntries = @(
    New-FileManifestEntries $setupStage @('helper-manifest.json') |
        ForEach-Object {
            [ordered]@{ RelativePath = $_.RelativePath; Sha256 = $_.Sha256 }
        }
)
$helperManifest = [ordered]@{ Version = 1; Files = $helperEntries }
Write-JsonFile $helperManifest $helperManifestPath 6
$helperManifestHash = Get-HexHash $helperManifestPath
$launcherHash = Get-HexHash (Join-Path $setupStage 'RelayBridge.SetupLauncher.exe')
$printerConfiguratorHash = Get-HexHash (Join-Path $setupStage 'RelayBridge.PrinterConfigurator.exe')

$settingsPath = Join-Path $hostStage 'appsettings.json'
$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
$settings.Storage.DataDirectory = 'C:\ProgramData\RelayBridge\Data'
$settings.NativeMicrosoftSetup.Enabled = $true
$settings.NativeMicrosoftSetup.InstallationRoot = 'C:\Program Files\RelayBridge'
$settings.NativeMicrosoftSetup.LauncherPath = 'C:\Program Files\RelayBridge\Setup\RelayBridge.SetupLauncher.exe'
$settings.NativeMicrosoftSetup.ExpectedLauncherSha256 = $launcherHash
$settings.NativeMicrosoftSetup.WorkerPath = 'C:\Program Files\RelayBridge\Setup\RelayBridge.Setup.exe'
$settings.NativeMicrosoftSetup.HelperManifestPath = 'C:\Program Files\RelayBridge\Setup\helper-manifest.json'
$settings.NativeMicrosoftSetup.ExpectedHelperManifestSha256 = $helperManifestHash
$settings.NativeMicrosoftSetup.ToolingRoot = 'C:\Program Files\RelayBridge\Tooling'
$settings.NativeMicrosoftSetup.ToolingManifestPath = 'C:\Program Files\RelayBridge\Tooling\tooling-manifest.json'
$settings.NativeMicrosoftSetup.ExpectedToolingManifestSha256 = $toolingManifestHash
$settings.PrinterConnectivityApply.Enabled = $true
$settings.PrinterConnectivityApply.HelperPath = 'C:\Program Files\RelayBridge\Setup\RelayBridge.PrinterConfigurator.exe'
$settings.PrinterConnectivityApply.ExpectedHelperSha256 = $printerConfiguratorHash
Write-JsonFile $settings $settingsPath 12

New-GeneratedWixSource

foreach ($prerequisite in $lock.dotNetPrerequisites) {
    $fileName = [IO.Path]::GetFileName(([Uri]$prerequisite.source).AbsolutePath)
    Get-LockedFile $prerequisite.source (Join-Path $prerequisiteRoot $fileName) $prerequisite.sha512 'SHA512'
}

Invoke-DotNet @(
    'build',
    'installer\RelayBridge.Installer.wixproj',
    '-c', $Configuration,
    ('-p:ProductVersion=' + $Version),
    ('-p:StageRoot=' + $stageRoot),
    ('-p:BrandingRoot=' + $brandingRoot),
    ('-p:OutputPath=' + $packageRoot + '\'),
    '--nologo'
)

$msiPath = Join-Path $packageRoot ("RelayBridge-$Version-win-x64.msi")
if (-not (Test-Path -LiteralPath $msiPath -PathType Leaf)) {
    throw 'The authoritative RelayBridge MSI was not produced.'
}
Invoke-OptionalSigning @($msiPath)

if (-not $SkipBundle) {
    Invoke-DotNet @(
        'build',
        'installer\RelayBridge.Bundle.wixproj',
        '-c', $Configuration,
        ('-p:ProductVersion=' + $Version),
        ('-p:PackageRoot=' + $packageRoot),
        ('-p:PrerequisiteRoot=' + $prerequisiteRoot),
        ('-p:ProvisionerPath=' + (Join-Path $provisionerPublish 'RelayBridge.ToolingProvisioner.exe')),
        ('-p:ToolingManifestPath=' + $toolingManifestPath),
        ('-p:GeneratedAcquisitionSource=' + (Join-Path $stageRoot 'GeneratedAcquisition.wxs')),
        ('-p:BrandingRoot=' + $brandingRoot),
        ('-p:OutputPath=' + $packageRoot + '\'),
        '--nologo'
    )
    $bundlePath = Join-Path $packageRoot ("RelayBridge-Setup-$Version-win-x64.exe")
    if (-not (Test-Path -LiteralPath $bundlePath -PathType Leaf)) {
        throw 'The RelayBridge prerequisite bootstrapper was not produced.'
    }
    Invoke-OptionalSigning @($bundlePath)
}

# WiX emits symbol/debug databases beside its primary outputs. Preserve those
# for internal diagnosis, but keep them outside the public-candidate package
# directory so they cannot be published accidentally.
Get-ChildItem -LiteralPath $packageRoot -File -Filter '*.wixpdb' |
    Move-Item -Destination $symbolsRoot

$sbomPath = Join-Path $packageRoot ("RelayBridge-$Version-win-x64.cdx.json")
& (Join-Path $PSScriptRoot 'generate-sbom.ps1') `
    -Version $Version `
    -PublishRoot $publishRoot `
    -ToolingLockPath $lockPath `
    -OutputPath $sbomPath
if ($LASTEXITCODE -ne 0) {
    throw 'Release SBOM generation failed.'
}
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\release\THIRD-PARTY-NOTICES.md') `
    -Destination (Join-Path $packageRoot 'THIRD-PARTY-NOTICES.md')

& (Join-Path $PSScriptRoot 'validate-installer.ps1') -Version $Version -ArtifactRoot $artifactRoot
if ($LASTEXITCODE -ne 0) {
    throw 'Installer package validation failed.'
}

Write-Output 'RELAYBRIDGE_INSTALLER_BUILD=PASS'
Write-Output "VERSION=$Version"
Write-Output "MSI=$msiPath"
Write-Output "SBOM=$sbomPath"
if (-not $SkipBundle) {
    Write-Output "BOOTSTRAPPER=$(Join-Path $packageRoot ("RelayBridge-Setup-$Version-win-x64.exe"))"
}
