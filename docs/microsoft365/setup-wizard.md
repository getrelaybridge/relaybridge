# Microsoft 365 setup wizard

RelayBridge's recommended new-application path uses a short-lived NativeAOT desktop
launcher and subordinate managed worker to run two fixed Microsoft-supported
provisioning stages. PowerShell remains
an isolated internal implementation detail: the administrator does not download a
script, open a terminal, or paste JSON. RelayBridge never receives the Microsoft
administrator password, and no administrator token enters the Windows Service.

## Recommended flow

1. Open the loopback management site and select **Connect Microsoft 365**.
2. Create a RelayBridge certificate or select a suitable certificate from the
   configured Windows Personal store. Downloading the certificate exports public
   `.cer` material only.
3. Enter the authorized sender and choose **Continue Microsoft setup**. The registered
   `RelayBridge.SetupLauncher` opens in the current Windows 10/11 interactive session
   and starts only the fixed managed setup worker.
4. Review the native confirmation and sign in through Microsoft's WAM experience for
   Entra, then Exchange. The processes are separate, short lived, and explicitly
   disconnect before exit.
5. RelayBridge automatically validates the sanitized setup result, certificate-backed
   Exchange token acquisition, STARTTLS, XOAUTH2, sender authorization, and final SMTP
   acceptance.
6. Only after Exchange's final `250` does RelayBridge atomically activate the candidate
   and report Microsoft 365 ready. This proves Microsoft acceptance, not inbox receipt.

When a newly configured Exchange authorization returns the exact propagation-eligible SMTP 535,
RelayBridge first performs brief bounded retries. If those exhaust, the active Step 5 page can
check the same authoritative candidate every five minutes for up to 12 attempts or one hour.
The page-scoped checks are cancellable, never mutate tenant configuration, acquire no broader
permission, and send no `MAIL`, `RCPT`, or `DATA` while authentication is rejected. RelayBridge
describes propagation only as a possibility; other failures retain their normal classification.

The existing-application path asks for the same tenant ID, client ID, certificate,
and sender mailbox. The Entra verification script remains important because the
runtime application deliberately lacks permission to inspect its own administrative
configuration.

## Administrative model

The native Entra stage uses pinned Microsoft.Entra PowerShell modules to create or verify one
single-tenant application, require exactly one matching public-certificate credential
and no client secret, create its service principal, and verify that both configured API
permission entries and service-principal app-role assignments are zero. It never requests `Mail.Send`,
`Mail.ReadWrite`, or a classic SMTP application permission.

The Exchange stage uses ExchangeOnlineManagement in a fresh process. It creates or
verifies the Exchange service-principal reference, a dedicated
`RelayBridge Allowed Senders` distribution group containing only the selected sender,
an exact group-backed management scope, and exactly one expected scoped
`Application SMTP.SendAsApp` role assignment for the application. It uses the documented
assignee-wide role-assignment query and rejects every extra scoped, unscoped, different,
or custom/derived role assigned to that dedicated principal. The group is marked
with the expected application ID so a same-name object belonging to another setup
causes a fail-closed error. The script uses
`Test-ServicePrincipalAuthorization` to require the selected sender to be in scope.

Both internal executors are fixed and idempotent for the same installation. They use
`-ErrorAction Stop`, validate objects before reuse, make no destructive changes,
install no modules at runtime, and output identifiers and boolean checks only. Values
are Base64-encoded JSON data rather than executable interpolation. The private
PowerShell executable and every module file must reside beneath the approved absolute,
non-reparse tooling root owned by SYSTEM, Administrators, or TrustedInstaller by SID,
grant no ordinary-user explicit or raw-generic mutation/DACL/delete-child replacement
rights, and match its version, manifest hash,
per-file hash, deterministic tree, and applicable Microsoft signature. Integrity is
rechecked immediately before each child launch. There is no PATH, user-module,
system-module, or Gallery fallback.

The sidecar-free NativeAOT `RelayBridge.SetupLauncher.exe` and framework-dependent
`RelayBridge.Setup` worker occupy a separate protected setup directory. A second release
manifest covers the launcher, worker apphost, `RelayBridge.Setup.dll`,
`RelayBridge.Core.dll`, `.deps.json`, `.runtimeconfig.json`, and every other file in that
exact directory. Host validates this closure before asking the browser to launch the
launcher and again before granting that connected native process setup authority. The
worker cannot connect to the Host bootstrap pipe. A managed self-contained single-file
boundary is not used because native .NET runtime libraries can be extracted under the
interactive user's writable temporary profile by default.

The launcher builds the worker environment from a reviewed allowlist of required
Windows paths, current-user Windows known folders, Program Files, and a Host-issued
protected per-session scratch directory. It does not inherit raw proxy variables,
.NET startup hooks, profilers, diagnostic ports,
additional dependency/shared-store controls, `DOTNET_ROOT*`, or `COMPlus_*` runtime
configuration. `DOTNET_EnableDiagnostics=0` is set explicitly, and the worker apphost
searches only the trusted global .NET installation. The worker applies the same clean
environment policy to both private PowerShell children and imports only exact modules
from fixed absolute paths.

The scratch directory lives beneath installer-protected
`C:\ProgramData\RelayBridge\SetupScratch`; `TEMP` and `TMP` point there for the worker and
both PowerShell stages. ExchangeOnlineManagement is pinned to 3.9.2 GA and
`Connect-ExchangeOnline` receives the same directory through `-EXOModuleBasePath`, so its
generated temporary module does not use inherited `%TMP%`. M8 must create the protected
root and package or exact-download the manifest-pinned 3.9.2 tooling tree.

The former generated Graph/Exchange scripts remain under **Advanced manual Microsoft
setup** for recovery, existing applications, unusual tenant policy, or unavailable
native tooling. They are no longer the normal recommended flow.

Administrative object names include a deterministic candidate identifier derived
from the certificate or client ID. This allows a replacement candidate to coexist
with the still-active application during validation. The marker, expected identity,
scope, and role must all match before an existing object is reused.

Current Microsoft references rechecked on 2026-08-23:

- [SMTP App RBAC onboarding](https://learn.microsoft.com/en-us/exchange/client-developer/legacy-protocols/smtp-app-rbac-onboarding)
- [Role-based access control for applications in Exchange Online](https://learn.microsoft.com/en-us/exchange/permissions-exo/application-rbac)
- [Get-ManagementRoleAssignment](https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/get-managementroleassignment?view=exchange-ps)
- [Connect to Exchange Online PowerShell](https://learn.microsoft.com/en-us/powershell/exchange/connect-to-exchange-online-powershell)
- [Connect-Entra](https://learn.microsoft.com/en-us/powershell/module/microsoft.entra.authentication/connect-entra)
- [New-EntraApplication](https://learn.microsoft.com/en-us/powershell/module/microsoft.entra.applications/new-entraapplication)
- [Create an application with Microsoft Graph PowerShell](https://learn.microsoft.com/en-us/powershell/microsoftgraph/tutorial-app-only-auth)
- [Microsoft identity platform certificate credentials](https://learn.microsoft.com/en-us/entra/identity-platform/certificate-credentials)
- [.NET single-file deployment and native-library extraction](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)

## Persistence and repair

SQLite schema version 9 retains the safe, resumable setup-state row and schema-v7
`ActivationId`, and adds a candidate revision plus authoritative lifecycle for
compare-and-swap updates. Candidate state may
contain only setup mode, step, tenant/client/service-principal identifiers,
certificate-store reference, sender address, and validation flags. It never stores
administrator sessions, tokens, scripts, PFX data, or private keys.

An existing working configuration is unchanged while a replacement is prepared. Every
native result is bound to the immutable candidate activation/revision/fingerprint it
originated from; stale results cannot update a replacement. RelayBridge validates the
candidate certificate, token, and real SMTP sender path before one SQLite transaction
rechecks the candidate revision/identity/flags and commits identity, sender, and the same
verified activation ID. Every replacement activation gets a fresh ID even for identical
settings, preventing old runtime readiness evidence from being reused. Cancel
conditionally marks only the matching active candidate cancelled and advances its
revision; late stage results and activation then fail compare-and-swap. Back navigation
and restart preserve safe progress;
certificates and Microsoft resources are never deleted automatically. Repair uses
the same wizard and starts at the first locally detectable failing layer.

## Plain-language troubleshooting

- **Certificate missing** — install/select the certificate for the Windows account
  running RelayBridge and confirm its private key is usable.
- **Microsoft rejected the certificate** — register the matching public certificate
  on the Entra application.
- **Exchange SMTP unavailable** — verify SMTP AUTH and tenant/network policy; token
  acquisition alone does not prove SMTP readiness.
- **Sender outside scope** — rerun the Exchange stage for the intended mailbox and
  confirm its positive authorization result.

Technical details contain only a bounded category, SMTP/enhanced code, safe
correlation identifier, and timestamp. They never contain access tokens, XOAUTH2
payloads, administrator credentials, or private-key material.

## Current limitations

- V1 configures one tenant, one application identity, and one authorized sender.
- RelayBridge generates guidance but does not manage certificate renewal or delete
  Entra/Exchange resources.
- Management remains loopback-only. Production remote administration and its
  authentication/installer hardening are later milestones.
- The dedicated-tenant real wizard flow is validated and M5 is frozen. Temporary
  validation resources require explicit administrator cleanup; RelayBridge never
  deletes Entra or Exchange resources automatically.
- M5.1 native setup is initially supported only on Windows 10/11 with one local
  interactive administrator. Native Windows Server/RDP/Server Core/multi-user setup
  is not yet claimed.
- M5.1 is frozen after production native-path tenant provisioning, exact assignee-wide role
  inventory/scope verification, authorized and out-of-scope mailbox controls, restart validation,
  and focused adversarial review. Microsoft may or may not present MFA according to tenant policy;
  RelayBridge does not claim it can force an MFA challenge. The M5 manual flow remains the
  validated Advanced recovery path.
