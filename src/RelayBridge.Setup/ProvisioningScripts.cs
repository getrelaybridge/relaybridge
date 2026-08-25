// SPDX-License-Identifier: MPL-2.0

using System.Text;

namespace RelayBridge.Setup;

internal static class ProvisioningScripts
{
    private const string ResultPrefix = "RELAYBRIDGE_RESULT:";

    internal const string EntraFailureInstrumentation = """
        function ConvertTo-RelayBridgeSafeDiagnosticValue {
            param([object]$Value, [int]$MaximumLength)
            if ($null -eq $Value) { return $null }
            $text = [string]$Value
            $text = [Text.RegularExpressions.Regex]::Replace($text, '[^A-Za-z0-9_.:, +`-]', '_')
            if ($text.Length -gt $MaximumLength) { $text = $text.Substring(0, $MaximumLength) }
            return $text
        }
        function Write-RelayBridgeEntraFailure {
            param([string]$ProvisioningStage, [Management.Automation.ErrorRecord]$ErrorRecord)
            $safeCode = switch ($ProvisioningStage) {
                'Connect' { 'EntraConnectionFailed' }
                'ApplicationDiscovery' { 'EntraApplicationDiscoveryFailed' }
                'ApplicationCreate' { 'EntraApplicationCreateFailed' }
                'ServicePrincipalCreate' { 'EntraServicePrincipalCreateFailed' }
                'CertificateCredential' { 'EntraCertificateCredentialFailed' }
                'ApplicationVerification' { 'EntraApplicationVerificationFailed' }
                default { 'MicrosoftProvisioningFailed' }
            }
            $statusCode = $null
            foreach ($exceptionCandidate in @($ErrorRecord.Exception, $ErrorRecord.Exception.InnerException)) {
                if ($null -eq $exceptionCandidate) { continue }
                $statusCandidates = @()
                $statusProperty = $exceptionCandidate.PSObject.Properties['StatusCode']
                if ($null -ne $statusProperty) { $statusCandidates += $statusProperty.Value }
                $responseProperty = $exceptionCandidate.PSObject.Properties['Response']
                if ($null -ne $responseProperty -and $null -ne $responseProperty.Value) {
                    $responseStatusProperty = $responseProperty.Value.PSObject.Properties['StatusCode']
                    if ($null -ne $responseStatusProperty) { $statusCandidates += $responseStatusProperty.Value }
                }
                foreach ($candidate in $statusCandidates) {
                    if ($null -ne $candidate) {
                        try {
                            $numericStatus = [int]$candidate
                            if ($numericStatus -ge 100 -and $numericStatus -le 599) { $statusCode = $numericStatus; break }
                        }
                        catch { }
                    }
                }
                if ($null -ne $statusCode) { break }
            }
            $safe = [ordered]@{
                Code = $safeCode
                ExceptionType = ConvertTo-RelayBridgeSafeDiagnosticValue $ErrorRecord.Exception.GetType().FullName 160
                FullyQualifiedErrorId = ConvertTo-RelayBridgeSafeDiagnosticValue $ErrorRecord.FullyQualifiedErrorId 256
                PowerShellCategory = ConvertTo-RelayBridgeSafeDiagnosticValue $ErrorRecord.CategoryInfo.Category 80
                HttpStatusCode = $statusCode
            }
            $safeJson = $safe | ConvertTo-Json -Compress
            $safeBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($safeJson))
            [Console]::Error.WriteLine('RELAYBRIDGE_ENTRA_FAILURE:' + $safeBase64)
        }
        """;

    internal const string EntraApplicationPolicy = """
        function Get-RelayBridgeEntraProperty {
            param([AllowNull()][object]$InputObject, [Parameter(Mandatory)][string]$Name)
            if ($null -eq $InputObject) {
                return [pscustomobject]@{ Exists = $false; Value = $null }
            }
            if ($InputObject -is [Collections.IDictionary]) {
                if ($InputObject.Contains($Name)) {
                    return [pscustomobject]@{ Exists = $true; Value = $InputObject[$Name] }
                }
                return [pscustomobject]@{ Exists = $false; Value = $null }
            }
            $property = $InputObject.PSObject.Properties[$Name]
            if ($null -eq $property) {
                return [pscustomobject]@{ Exists = $false; Value = $null }
            }
            return [pscustomobject]@{ Exists = $true; Value = $property.Value }
        }
        function Get-RelayBridgeEntraItems {
            param([AllowNull()][object]$Value)
            if ($null -eq $Value) { return @() }
            return @($Value)
        }
        function Convert-RelayBridgeHexToBytes {
            param([Parameter(Mandatory)][string]$Value)
            if (($Value.Length % 2) -ne 0 -or $Value -notmatch '\A[0-9A-Fa-f]+\z') {
                throw [FormatException]::new('Invalid hexadecimal value.')
            }
            $bytes = [byte[]]::new($Value.Length / 2)
            for ($index = 0; $index -lt $bytes.Length; $index++) {
                $bytes[$index] = [Convert]::ToByte($Value.Substring($index * 2, 2), 16)
            }
            return $bytes
        }
        function New-RelayBridgeEntraKeyCredential {
            param([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)
            return @{
                Type = 'AsymmetricX509Cert'
                Usage = 'Verify'
                CustomKeyIdentifier = [Convert]::ToBase64String($Certificate.GetCertHash())
                Key = [Convert]::ToBase64String($Certificate.RawData)
                DisplayName = 'RelayBridge Authentication'
                StartDateTime = $Certificate.NotBefore.ToUniversalTime()
                EndDateTime = $Certificate.NotAfter.ToUniversalTime()
            }
        }
        function Assert-RelayBridgeRawEntraApplication {
            param(
                [object]$Application,
                [Guid]$ExpectedApplicationObjectId,
                [Guid]$ExpectedClientId,
                [string]$ExpectedThumbprint)
            $id = Get-RelayBridgeEntraProperty $Application 'id'
            $appId = Get-RelayBridgeEntraProperty $Application 'appId'
            $required = Get-RelayBridgeEntraProperty $Application 'requiredResourceAccess'
            $keys = Get-RelayBridgeEntraProperty $Application 'keyCredentials'
            $passwords = Get-RelayBridgeEntraProperty $Application 'passwordCredentials'
            $audience = Get-RelayBridgeEntraProperty $Application 'signInAudience'
            if (-not $id.Exists -or -not $appId.Exists -or -not $required.Exists -or
                -not $keys.Exists -or -not $passwords.Exists -or -not $audience.Exists) {
                throw 'RELAYBRIDGE_CONFLICT:ApplicationVerificationFields'
            }
            $requiredItems = @(Get-RelayBridgeEntraItems $required.Value)
            $keyItems = @(Get-RelayBridgeEntraItems $keys.Value)
            $passwordItems = @(Get-RelayBridgeEntraItems $passwords.Value)
            if ([Guid]$id.Value -ne $ExpectedApplicationObjectId -or
                [Guid]$appId.Value -ne $ExpectedClientId) {
                throw 'RELAYBRIDGE_CONFLICT:ApplicationIdentity'
            }
            if ($requiredItems.Count -ne 0) { throw 'RELAYBRIDGE_CONFLICT:ApiPermissionsPresent' }
            if ($keyItems.Count -ne 1) { throw 'RELAYBRIDGE_CONFLICT:CertificateCredentialSet' }
            if ($passwordItems.Count -ne 0) { throw 'RELAYBRIDGE_CONFLICT:PasswordCredentialPresent' }
            if ([string]$audience.Value -ne 'AzureADMyOrg') { throw 'RELAYBRIDGE_CONFLICT:NotSingleTenant' }
            $customKeyIdentifier = Get-RelayBridgeEntraProperty $keyItems[0] 'customKeyIdentifier'
            $credentialType = Get-RelayBridgeEntraProperty $keyItems[0] 'type'
            $credentialUsage = Get-RelayBridgeEntraProperty $keyItems[0] 'usage'
            if (-not $customKeyIdentifier.Exists -or -not $credentialType.Exists -or
                -not $credentialUsage.Exists -or
                [string]$credentialType.Value -ne 'AsymmetricX509Cert' -or
                [string]$credentialUsage.Value -ne 'Verify' -or
                [string]::IsNullOrWhiteSpace([string]$customKeyIdentifier.Value)) {
                throw 'RELAYBRIDGE_CONFLICT:CertificateCredentialSet'
            }
            try {
                [void][Convert]::FromBase64String([string]$customKeyIdentifier.Value)
                $expectedIdentifier = [Convert]::ToBase64String(
                    (Convert-RelayBridgeHexToBytes $ExpectedThumbprint))
            }
            catch [FormatException] {
                throw 'RELAYBRIDGE_CONFLICT:CertificateCredentialSet'
            }
            if (-not ([string]$customKeyIdentifier.Value).Equals(
                    $expectedIdentifier,
                    [StringComparison]::Ordinal)) {
                throw 'RELAYBRIDGE_CONFLICT:CertificateCredentialSet'
            }
        }
        """;

    internal const string ExchangeScopePolicy = """
        function Assert-RelayBridgeExchangeScope {
            param(
                [object[]]$Members,
                [string]$SenderAddress,
                [string]$ActualFilter,
                [string]$ExpectedFilter,
                [object[]]$Assignments,
                [string]$ExpectedAssignmentName,
                [string]$ExpectedScopeName,
                [string]$ExpectedRoleName)
            $senderMembers = @($Members | Where-Object { [string]$_.PrimarySmtpAddress -eq $SenderAddress })
            if ($Members.Count -ne 1 -or $senderMembers.Count -ne 1) { throw 'RELAYBRIDGE_CONFLICT:SenderGroupMembers' }
            if (-not $ActualFilter.Trim().Equals($ExpectedFilter, [StringComparison]::OrdinalIgnoreCase)) { throw 'RELAYBRIDGE_CONFLICT:ScopeFilter' }
            if ($Assignments.Count -ne 1 -or
                [string]$Assignments[0].Name -ne $ExpectedAssignmentName -or
                [string]$Assignments[0].Role -ne $ExpectedRoleName -or
                [string]$Assignments[0].RoleAssigneeType -ne 'ServicePrincipal' -or
                [string]$Assignments[0].CustomResourceScope -ne $ExpectedScopeName) {
                throw 'RELAYBRIDGE_CONFLICT:ApplicationRoleAssignments'
            }
        }
        """;

    internal static string CreateEntraScript(VerifiedTooling tooling, string payloadBase64)
    {
        return $$"""
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $InformationPreference = 'SilentlyContinue'
            $WarningPreference = 'SilentlyContinue'
            {{EntraApplicationPolicy}}
            {{EntraFailureInstrumentation}}
            $setup = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{payloadBase64}}')) | ConvertFrom-Json
            {{CreateEntraModuleBootstrap(tooling)}}
            $provisioningStage = 'Connect'
            try {
                Connect-Entra -Scopes 'Application.ReadWrite.All' -ContextScope Process -NoWelcome -ErrorAction Stop | Out-Null
                $context = Get-EntraContext -ErrorAction Stop
                if (-not $context -or -not $context.TenantId) { throw 'RELAYBRIDGE_RESULT_INVALID:Tenant' }

                $provisioningStage = 'CertificateCredential'
                $certificateBytes = [Convert]::FromBase64String([string]$setup.PublicCertificateBase64)
                $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificateBytes)
                if ($certificate.HasPrivateKey) { throw 'RELAYBRIDGE_RESULT_INVALID:PrivateKey' }

                $provisioningStage = 'ApplicationDiscovery'
                $escapedName = ([string]$setup.ApplicationDisplayName).Replace("'", "''")
                $matches = @(Get-EntraApplication -Filter "displayName eq '$escapedName'" -All -ErrorAction Stop)
                if ($matches.Count -gt 1) { throw 'RELAYBRIDGE_CONFLICT:MultipleApplications' }
                if ($matches.Count -eq 0) {
                    $provisioningStage = 'CertificateCredential'
                    $keyCredential = New-RelayBridgeEntraKeyCredential $certificate
                    $provisioningStage = 'ApplicationCreate'
                    $application = New-EntraApplication -DisplayName ([string]$setup.ApplicationDisplayName) -SignInAudience 'AzureADMyOrg' -RequiredResourceAccess @() -KeyCredentials @($keyCredential) -ErrorAction Stop
                }
                else { $application = $matches[0] }

                $provisioningStage = 'ApplicationVerification'
                $applicationId = Get-RelayBridgeEntraProperty $application 'Id'
                $applicationClientId = Get-RelayBridgeEntraProperty $application 'AppId'
                if (-not $applicationId.Exists -or -not $applicationClientId.Exists -or
                    $null -eq $applicationId.Value -or $null -eq $applicationClientId.Value) {
                    throw 'RELAYBRIDGE_CONFLICT:ApplicationIdentity'
                }
                $applicationObjectId = [Guid]$applicationId.Value
                $clientId = [Guid]$applicationClientId.Value
                $escapedApplicationObjectId = [Uri]::EscapeDataString($applicationObjectId.ToString('D'))
                $rawApplication = Invoke-MgGraphRequest `
                    -Method GET `
                    -Uri ("https://graph.microsoft.com/v1.0/applications/${escapedApplicationObjectId}?`$select=id,appId,requiredResourceAccess,keyCredentials,passwordCredentials,signInAudience") `
                    -ErrorAction Stop
                Assert-RelayBridgeRawEntraApplication `
                    -Application $rawApplication `
                    -ExpectedApplicationObjectId $applicationObjectId `
                    -ExpectedClientId $clientId `
                    -ExpectedThumbprint $certificate.Thumbprint

                $servicePrincipals = @(Get-EntraServicePrincipal -Filter "appId eq '$clientId'" -All -ErrorAction Stop)
                if ($servicePrincipals.Count -gt 1) { throw 'RELAYBRIDGE_CONFLICT:MultipleServicePrincipals' }
                if ($servicePrincipals.Count -eq 0) {
                    $provisioningStage = 'ServicePrincipalCreate'
                    $servicePrincipal = New-EntraServicePrincipal -AppId $clientId -ErrorAction Stop
                }
                else { $servicePrincipal = $servicePrincipals[0] }

                $provisioningStage = 'ApplicationVerification'
                $assignments = @(Get-EntraServicePrincipalAppRoleAssignment -ServicePrincipalId $servicePrincipal.Id -All -ErrorAction Stop)
                if ($assignments.Count -ne 0) { throw 'RELAYBRIDGE_CONFLICT:AppRoleAssignmentsPresent' }
                [ordered]@{
                    TenantId = [Guid]$context.TenantId
                    ClientId = $clientId
                    ServicePrincipalObjectId = [Guid]$servicePrincipal.Id
                    ApiPermissionEntryCount = 0
                } | ConvertTo-Json -Compress | ForEach-Object { [Console]::Out.WriteLine('RELAYBRIDGE_RESULT:' + $_) }
            }
            catch {
                $errorCode = if ($_.Exception.PSObject.Properties['ErrorCode']) { [string]$_.Exception.ErrorCode } else { '' }
                $errorId = [string]$_.FullyQualifiedErrorId
                $message = [string]$_.Exception.Message
                if ($_.Exception -is [OperationCanceledException] -or
                    $errorCode -in @('authentication_canceled','user_canceled') -or
                    $errorId -match '^(AuthenticationCanceled|UserCanceled)' -or
                    $message -match '^The user canceled (the )?Web Account Manager') {
                    [Console]::Error.WriteLine('RELAYBRIDGE_CANCELLED')
                }
                elseif ($message -match '^RELAYBRIDGE_TOOL_INTEGRITY:') { [Console]::Error.WriteLine('RELAYBRIDGE_TOOL_INTEGRITY') }
                elseif ($message -match 'AADSTS53003|Conditional Access') { [Console]::Error.WriteLine('RELAYBRIDGE_CA') }
                elseif ($message -match 'Authorization_RequestDenied|Insufficient privileges|does not have permission') { [Console]::Error.WriteLine('RELAYBRIDGE_PERMISSION') }
                elseif ($message -match '^RELAYBRIDGE_CONFLICT:') { [Console]::Error.WriteLine('RELAYBRIDGE_CONFLICT') }
                else { Write-RelayBridgeEntraFailure -ProvisioningStage $provisioningStage -ErrorRecord $_ }
                exit 1
            }
            finally {
                Disconnect-Entra -ErrorAction SilentlyContinue | Out-Null
            }
            """;
    }

    internal static string CreateEntraImportPreflightScript(VerifiedTooling tooling)
    {
        return $$"""
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $InformationPreference = 'SilentlyContinue'
            $WarningPreference = 'SilentlyContinue'
            {{CreateEntraModuleBootstrap(tooling)}}
            $result = [ordered]@{
                PowerShellVersion = $PSVersionTable.PSVersion.ToString()
                GraphAuthenticationVersion = $loadedGraphAuthentication.Version.ToString()
                GraphAuthenticationPathMatches = $true
                GraphApplicationsVersion = $loadedGraphApplications.Version.ToString()
                GraphApplicationsPathMatches = $true
                EntraAuthenticationVersion = $loadedEntraAuthentication.Version.ToString()
                EntraAuthenticationPathMatches = $true
                EntraApplicationsVersion = $loadedEntraApplications.Version.ToString()
                EntraApplicationsPathMatches = $true
                ConnectMgGraphAvailable = $true
                ConnectEntraAvailable = $true
                GetMgApplicationAvailable = $true
                PSModulePathLocked = ($env:PSModulePath -eq '')
                UnexpectedModuleDiscovery = $false
            }
            [Console]::Out.Write('{{ResultPrefix}}' + ($result | ConvertTo-Json -Compress))
            """;
    }

    private static string CreateEntraModuleBootstrap(VerifiedTooling tooling)
    {
        return $$"""
            function Assert-RelayBridgeModuleIdentity {
                param([string]$Name, [Version]$Version, [string]$ManifestPath)
                $expectedBase = [IO.Path]::GetFullPath([IO.Path]::GetDirectoryName($ManifestPath))
                $loaded = @(Get-Module -Name $Name)
                $allLoaded = @(Get-Module -Name $Name -All)
                if ($loaded.Count -ne 1 -or
                    [string]$loaded[0].Name -ne $Name -or
                    $loaded[0].Version -ne $Version -or
                    -not [IO.Path]::GetFullPath([string]$loaded[0].ModuleBase).Equals($expectedBase, [StringComparison]::OrdinalIgnoreCase) -or
                    @($allLoaded | Where-Object {
                        -not [IO.Path]::GetFullPath([string]$_.ModuleBase).Equals($expectedBase, [StringComparison]::OrdinalIgnoreCase)
                    }).Count -ne 0) {
                    throw ('RELAYBRIDGE_TOOL_INTEGRITY:ModuleIdentity:' + $Name)
                }
                return $loaded[0]
            }
            function Assert-RelayBridgeCommandSource {
                param([string]$Name, [string]$ExpectedModule)
                $commands = @(Get-Command -Name $Name -All -ErrorAction Stop)
                if ($commands.Count -ne 1 -or [string]$commands[0].Source -ne $ExpectedModule) {
                    throw 'RELAYBRIDGE_TOOL_INTEGRITY:CommandSource'
                }
            }
            $graphAuthentication = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{Encode(tooling.GraphAuthenticationModulePath)}}'))
            $graphAuthenticationVersion = [Version]'{{tooling.GraphAuthenticationModuleVersion}}'
            $graphApplications = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{Encode(tooling.GraphApplicationsModulePath)}}'))
            $graphApplicationsVersion = [Version]'{{tooling.GraphApplicationsModuleVersion}}'
            $entraAuthentication = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{Encode(tooling.EntraAuthenticationModulePath)}}'))
            $entraAuthenticationVersion = [Version]'{{tooling.EntraAuthenticationModuleVersion}}'
            $entraApplications = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{Encode(tooling.EntraApplicationsModulePath)}}'))
            $entraApplicationsVersion = [Version]'{{tooling.EntraApplicationsModuleVersion}}'
            Import-Module ([IO.Path]::Combine($PSHOME, 'Modules', 'Microsoft.PowerShell.Management', 'Microsoft.PowerShell.Management.psd1')) -ErrorAction Stop
            Import-Module ([IO.Path]::Combine($PSHOME, 'Modules', 'Microsoft.PowerShell.Utility', 'Microsoft.PowerShell.Utility.psd1')) -ErrorAction Stop
            Import-Module ([IO.Path]::Combine($PSHOME, 'Modules', 'Microsoft.PowerShell.Security', 'Microsoft.PowerShell.Security.psd1')) -ErrorAction Stop
            $PSModuleAutoLoadingPreference = 'None'
            Import-Module $graphAuthentication -Force -ErrorAction Stop
            $loadedGraphAuthentication = Assert-RelayBridgeModuleIdentity -Name 'Microsoft.Graph.Authentication' -Version $graphAuthenticationVersion -ManifestPath $graphAuthentication
            Import-Module $graphApplications -Force -ErrorAction Stop
            $loadedGraphApplications = Assert-RelayBridgeModuleIdentity -Name 'Microsoft.Graph.Applications' -Version $graphApplicationsVersion -ManifestPath $graphApplications
            Import-Module $entraAuthentication -Force -ErrorAction Stop
            $loadedEntraAuthentication = Assert-RelayBridgeModuleIdentity -Name 'Microsoft.Entra.Authentication' -Version $entraAuthenticationVersion -ManifestPath $entraAuthentication
            Import-Module $entraApplications -Force -ErrorAction Stop
            $loadedEntraApplications = Assert-RelayBridgeModuleIdentity -Name 'Microsoft.Entra.Applications' -Version $entraApplicationsVersion -ManifestPath $entraApplications
            $env:PSModulePath = ''
            Assert-RelayBridgeCommandSource -Name 'Connect-MgGraph' -ExpectedModule 'Microsoft.Graph.Authentication'
            Assert-RelayBridgeCommandSource -Name 'Invoke-MgGraphRequest' -ExpectedModule 'Microsoft.Graph.Authentication'
            Assert-RelayBridgeCommandSource -Name 'Get-MgApplication' -ExpectedModule 'Microsoft.Graph.Applications'
            Assert-RelayBridgeCommandSource -Name 'Connect-Entra' -ExpectedModule 'Microsoft.Entra.Authentication'
            Assert-RelayBridgeCommandSource -Name 'Get-EntraApplication' -ExpectedModule 'Microsoft.Entra.Applications'
            """;
    }

    internal static string CreateExchangeScript(
        VerifiedTooling tooling,
        string payloadBase64,
        string scratchDirectory)
    {
        return $$"""
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $InformationPreference = 'SilentlyContinue'
            $WarningPreference = 'SilentlyContinue'
            {{ExchangeScopePolicy}}
            $exchangeModule = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{Encode(tooling.ExchangeOnlineModulePath)}}'))
            $exchangeModuleVersion = [Version]'{{tooling.ExchangeOnlineModuleVersion}}'
            $exchangeModuleBasePath = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{Encode(scratchDirectory)}}'))
            $setup = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{payloadBase64}}')) | ConvertFrom-Json
            Import-Module ([IO.Path]::Combine($PSHOME, 'Modules', 'Microsoft.PowerShell.Management', 'Microsoft.PowerShell.Management.psd1')) -ErrorAction Stop
            Import-Module ([IO.Path]::Combine($PSHOME, 'Modules', 'Microsoft.PowerShell.Utility', 'Microsoft.PowerShell.Utility.psd1')) -ErrorAction Stop
            Import-Module ([IO.Path]::Combine($PSHOME, 'Modules', 'Microsoft.PowerShell.Security', 'Microsoft.PowerShell.Security.psd1')) -ErrorAction Stop
            $PSModuleAutoLoadingPreference = 'None'
            Import-Module $exchangeModule -Force -ErrorAction Stop
            $exchangeModuleRoot = [IO.Path]::GetDirectoryName($exchangeModule)
            function Assert-RelayBridgeExchangeModuleState {
                $expectedModuleRoot = [IO.Path]::GetFullPath($exchangeModuleRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
                $loadedExchangeModules = @(Get-Module -Name 'ExchangeOnlineManagement')
                if ($loadedExchangeModules.Count -ne 1 -or
                    [IO.Path]::GetFullPath([string]$loadedExchangeModules[0].ModuleBase) -ne $expectedModuleRoot -or
                    $loadedExchangeModules[0].Version -ne $exchangeModuleVersion) {
                    throw 'RELAYBRIDGE_TOOL_INTEGRITY:ModuleIdentity'
                }
                if ($PSModuleAutoLoadingPreference -ne 'None') {
                    throw 'RELAYBRIDGE_TOOL_INTEGRITY:ModuleAutoLoading'
                }
                $scratchRoot = [IO.Path]::GetFullPath($exchangeModuleBasePath).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
                $scratchPrefix = $scratchRoot + [IO.Path]::DirectorySeparatorChar
                foreach ($temporaryModule in @(Get-Module | Where-Object { $_.Name -like 'tmpEXO_*' })) {
                    $temporaryModuleBase = [IO.Path]::GetFullPath([string]$temporaryModule.ModuleBase)
                    if (-not $temporaryModuleBase.StartsWith($scratchPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                        throw 'RELAYBRIDGE_TOOL_INTEGRITY:TemporaryModulePath'
                    }
                }
            }
            $env:PSModulePath = ''
            Assert-RelayBridgeExchangeModuleState
            $roleName = 'Application SMTP.SendAsApp'
            $suffix = ([Guid]$setup.ClientId).ToString('N').Substring(0, 8)
            $groupName = "RelayBridge Allowed Senders $suffix"
            $groupAlias = "RelayBridgeSenders$suffix"
            $scopeName = "RelayBridge Allowed Senders Scope $suffix"
            $assignmentName = "RelayBridge SMTP SendAs $suffix"
            try {
                Connect-ExchangeOnline -ShowBanner:$false -EXOModuleBasePath $exchangeModuleBasePath -ErrorAction Stop
                $env:PSModulePath = ''
                Assert-RelayBridgeExchangeModuleState
                $sender = Get-Recipient -Identity ([string]$setup.SenderMailbox) -ErrorAction Stop
                if (-not $sender.PrimarySmtpAddress) { throw 'RELAYBRIDGE_RESULT_INVALID:Sender' }

                $exchangeServicePrincipals = @(Get-ServicePrincipal | Where-Object { [string]$_.AppId -eq [string]$setup.ClientId })
                if ($exchangeServicePrincipals.Count -gt 1) { throw 'RELAYBRIDGE_CONFLICT:MultipleExchangeServicePrincipals' }
                $exchangeServicePrincipal = $null
                if ($exchangeServicePrincipals.Count -eq 1) {
                    $exchangeServicePrincipal = $exchangeServicePrincipals[0]
                    if ([Guid]$exchangeServicePrincipal.ObjectId -ne [Guid]$setup.ServicePrincipalObjectId) { throw 'RELAYBRIDGE_CONFLICT:ExchangePrincipalMismatch' }
                }

                $group = Get-DistributionGroup -Identity $groupName -ErrorAction SilentlyContinue
                $groupMarker = "RelayBridge:$($setup.ClientId)"
                if ($group -and [string]$group.CustomAttribute15 -ne $groupMarker) { throw 'RELAYBRIDGE_CONFLICT:GroupOwnership' }
                if ($group -and -not $group.DistinguishedName) { throw 'RELAYBRIDGE_CONFLICT:GroupDistinguishedName' }

                $scope = Get-ManagementScope -Identity $scopeName -ErrorAction SilentlyContinue
                if ($scope -and -not $group) { throw 'RELAYBRIDGE_CONFLICT:ScopeWithoutGroup' }
                if ($scope) {
                    $scopeFilter = [string]$scope.RecipientFilter
                    $escapedDn = ([string]$group.DistinguishedName).Replace("'", "''")
                    $expectedScopeFilter = "MemberOfGroup -eq '$escapedDn'"
                    if (-not $scopeFilter.Trim().Equals($expectedScopeFilter, [StringComparison]::OrdinalIgnoreCase)) {
                        throw 'RELAYBRIDGE_CONFLICT:ScopeFilter'
                    }
                }

                $assignment = Get-ManagementRoleAssignment -Identity $assignmentName -ErrorAction SilentlyContinue
                if ($assignment) {
                    if ([string]$assignment.Role -ne $roleName -or
                        [string]$assignment.CustomResourceScope -ne $scopeName -or
                        [string]$assignment.RoleAssigneeType -ne 'ServicePrincipal') { throw 'RELAYBRIDGE_CONFLICT:RoleAssignment' }
                    $assignedPrincipal = Get-ServicePrincipal -Identity $assignment.RoleAssignee -ErrorAction Stop
                    if ([Guid]$assignedPrincipal.AppId -ne [Guid]$setup.ClientId) { throw 'RELAYBRIDGE_CONFLICT:RolePrincipal' }
                }

                if (-not $exchangeServicePrincipal) {
                    $exchangeServicePrincipal = New-ServicePrincipal -AppId ([Guid]$setup.ClientId) -ObjectId ([Guid]$setup.ServicePrincipalObjectId) -DisplayName 'RelayBridge SMTP OAuth'
                }
                if (-not $group) {
                    $group = New-DistributionGroup -Name $groupName -DisplayName $groupName -Alias $groupAlias -Type Distribution
                    Set-DistributionGroup -Identity $group.Identity -CustomAttribute15 $groupMarker
                    $group = Get-DistributionGroup -Identity $group.Identity -ErrorAction Stop
                }

                $members = @(Get-DistributionGroupMember -Identity $group.Identity -ResultSize Unlimited)
                $senderMembers = @($members | Where-Object { [string]$_.PrimarySmtpAddress -eq [string]$sender.PrimarySmtpAddress })
                if ($members.Count -gt 1 -or ($members.Count -eq 1 -and $senderMembers.Count -ne 1)) { throw 'RELAYBRIDGE_CONFLICT:SenderGroupMembers' }
                $senderIsMember = $senderMembers.Count -eq 1
                if (-not $senderIsMember) { Add-DistributionGroupMember -Identity $group.Identity -Member $sender.Identity -BypassSecurityGroupManagerCheck }
                if (-not $scope) {
                    $escapedDn = ([string]$group.DistinguishedName).Replace("'", "''")
                    $scope = New-ManagementScope -Name $scopeName -RecipientRestrictionFilter "MemberOfGroup -eq '$escapedDn'"
                }
                if (-not $assignment) {
                    $assignment = New-ManagementRoleAssignment -Name $assignmentName -Role $roleName -App ([Guid]$setup.ClientId) -CustomResourceScope $scopeName
                }

                $members = @(Get-DistributionGroupMember -Identity $group.Identity -ResultSize Unlimited)
                $senderMembers = @($members | Where-Object { [string]$_.PrimarySmtpAddress -eq [string]$sender.PrimarySmtpAddress })
                if ($members.Count -ne 1 -or $senderMembers.Count -ne 1) { throw 'RELAYBRIDGE_CONFLICT:SenderGroupMembers' }
                $scope = Get-ManagementScope -Identity $scopeName -ErrorAction Stop
                $escapedDn = ([string]$group.DistinguishedName).Replace("'", "''")
                $expectedScopeFilter = "MemberOfGroup -eq '$escapedDn'"

                $applicationAssignments = @(Get-ManagementRoleAssignment -RoleAssignee ([Guid]$exchangeServicePrincipal.ObjectId) -ErrorAction Stop)
                foreach ($candidateAssignment in $applicationAssignments) {
                    if ([string]$candidateAssignment.RoleAssigneeType -ne 'ServicePrincipal') { throw 'RELAYBRIDGE_CONFLICT:ApplicationRoleAssignments' }
                    $candidatePrincipal = Get-ServicePrincipal -Identity $candidateAssignment.RoleAssignee -ErrorAction Stop
                    if ([Guid]$candidatePrincipal.ObjectId -ne [Guid]$exchangeServicePrincipal.ObjectId -or
                        [Guid]$candidatePrincipal.AppId -ne [Guid]$setup.ClientId) {
                        throw 'RELAYBRIDGE_CONFLICT:ApplicationRoleAssignments'
                    }
                }
                Assert-RelayBridgeExchangeScope -Members $members -SenderAddress ([string]$sender.PrimarySmtpAddress) -ActualFilter ([string]$scope.RecipientFilter) -ExpectedFilter $expectedScopeFilter -Assignments $applicationAssignments -ExpectedAssignmentName $assignmentName -ExpectedScopeName $scopeName -ExpectedRoleName $roleName

                $authorization = @(Test-ServicePrincipalAuthorization -Identity ([Guid]$setup.ClientId) -Resource ([string]$setup.SenderMailbox) | Where-Object {
                    [string]$_.RoleName -eq $roleName -and $_.InScope -eq $true
                })
                if ($authorization.Count -eq 0) { throw 'RELAYBRIDGE_MICROSOFT_FAILURE:SenderNotInScope' }
                [ordered]@{
                    ServicePrincipalConfigured = $true
                    ScopeConfigured = $true
                    Role = $roleName
                    SenderInScope = $true
                } | ConvertTo-Json -Compress | ForEach-Object { [Console]::Out.WriteLine('RELAYBRIDGE_RESULT:' + $_) }
            }
            catch {
                $errorCode = if ($_.Exception.PSObject.Properties['ErrorCode']) { [string]$_.Exception.ErrorCode } else { '' }
                $errorId = [string]$_.FullyQualifiedErrorId
                $message = [string]$_.Exception.Message
                if ($_.Exception -is [OperationCanceledException] -or
                    $errorCode -in @('authentication_canceled','user_canceled') -or
                    $errorId -match '^(AuthenticationCanceled|UserCanceled)' -or
                    $message -match '^The user canceled (the )?Web Account Manager') {
                    [Console]::Error.WriteLine('RELAYBRIDGE_CANCELLED')
                }
                elseif ($message -match '^RELAYBRIDGE_TOOL_INTEGRITY:') { [Console]::Error.WriteLine('RELAYBRIDGE_TOOL_INTEGRITY') }
                elseif ($message -match 'AADSTS53003|Conditional Access') { [Console]::Error.WriteLine('RELAYBRIDGE_CA') }
                elseif ($message -match 'Access is denied|not recognized as a management role|does not have permission') { [Console]::Error.WriteLine('RELAYBRIDGE_PERMISSION') }
                elseif ($message -eq 'RELAYBRIDGE_CONFLICT:ApplicationRoleAssignments') { [Console]::Error.WriteLine('RELAYBRIDGE_EXCHANGE_ASSIGNMENT_CONFLICT') }
                elseif ($message -match '^RELAYBRIDGE_CONFLICT:') { [Console]::Error.WriteLine('RELAYBRIDGE_CONFLICT') }
                else { [Console]::Error.WriteLine('RELAYBRIDGE_MICROSOFT_FAILURE') }
                exit 1
            }
            finally {
                Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue
            }
            """;
    }

    private static string Encode(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }
}
