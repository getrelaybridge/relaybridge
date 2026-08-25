# Milestone 5.1 — Native Microsoft 365 setup

## Status

Lab-proven Entra serialization/raw-read-back and Exchange WAM console-race corrections
complete; not frozen. Local Release build and focused correction verification pass. The
dedicated-tenant production-native run passed provisioning, final SMTP acceptance, atomic
activation, negative sender authorization, and restart validation. The focused security
review remains.

## Implemented architecture

The recommended new-application path no longer asks an administrator to download a
script, open PowerShell, or paste JSON. The loopback wizard launches the registered,
parameter-free `relaybridge-setup://start` handler. A sidecar-free NativeAOT
`RelayBridge.SetupLauncher.exe` connects to the service-owned bootstrap pipe from the
interactive desktop. The pipe rejects
remote clients and permits only one first instance. The service uses the kernel-reported
client PID, checks that process token's SID against the impersonated pipe SID, then
checks Windows session, installed executable path, and approved executable SHA-256.
Before launching the custom URI and again before accepting the running process, the Host
validates a pinned release manifest for the exact execution closure: native launcher,
managed `RelayBridge.Setup.exe` apphost, `RelayBridge.Setup.dll`, `RelayBridge.Core.dll`, dependency metadata, runtime
configuration, and every other file in the dedicated helper directory. Only then can it
issue a connection-bound random session ID. The pre-authentication handshake,
identity checks, candidate capture, certificate read, and first response share one
short bootstrap deadline; a failed auxiliary listener is retried without stopping mail.
Before sending its hello, the launcher reciprocally verifies that the pipe server is a
LocalSystem process in Session 0, so an unprivileged local process cannot impersonate
the service and feed the approved helper false provisioning inputs.
The authenticated launcher starts only the fixed sibling managed worker with inherited
standard handles, remains the sole Host connection, and relays the existing bounded frames.
The worker has no bootstrap-pipe code. Before showing native confirmation or starting cloud
work, it verifies its kernel-reported parent PID, exact protected sibling launcher path/hash,
Windows session, and interactive SID against the Host-authenticated frame. Direct or copied
worker execution fails.
The launcher owns a kill-on-close Job Object containing the worker and its descendants.

The launcher constructs the worker environment from a reviewed allowlist rather than the
browser/user environment. Fixed Windows paths, current-user Windows known folders, the
Host-issued protected scratch path, and Program Files are retained. Raw proxy and low-value
session environment variables are not inherited. `DOTNET_STARTUP_HOOKS`, `DOTNET_ROOT*`,
additional-dependency/shared-store/host-path controls, `CORECLR_*`, profiler `DOTNET_*`,
legacy `COR_*`, and `COMPlus_*` are omitted. `DOTNET_EnableDiagnostics=0` is explicit.
The framework-dependent worker apphost is published with `AppHostDotNetSearch=Global`.
The worker applies the same clean-environment policy again before both private PowerShell
children. Because PowerShell can reconstruct normal module locations during startup,
an import-only preflight loads the exact approved Graph 2.25.0 modules and Entra 1.3.0
wrappers by absolute manifest path, verifies canonical bases/versions and command sources,
then clears the in-process `PSModulePath` while autoload remains disabled. Unexpected
stderr fails closed before WAM. The authentication-bearing Entra process repeats the same
deterministic bootstrap.

After authenticating the launcher, the LocalSystem Host creates one fresh unpredictable
session directory beneath `C:\ProgramData\RelayBridge\SetupScratch`. The installer-owned
root must have a trusted owner and no ordinary-user mutation/delete-child rights; root,
session, and contained paths reject reparse traversal. The session DACL grants only SYSTEM,
Administrators, and the exact interactive SID, without change-permission or take-ownership
rights. Launcher, worker, Entra PowerShell and Exchange PowerShell set `TEMP` and `TMP` to
this directory. ExchangeOnlineManagement is pinned to 3.9.2 GA and receives the same path
through `-EXOModuleBasePath`. A lock prevents stale cleanup of an active session; normal
cleanup waits for launcher-tree exit, and uncertainty preserves the directory conservatively.

After confirmation the helper verifies the separate installer-owned tooling manifest and
deterministic private tooling tree. The helper, private PowerShell, manifest, modules,
files, and relevant path hierarchy must be owned by SYSTEM, Administrators, or
TrustedInstaller by SID, grant no ordinary-user mutation/replacement rights, and contain
no reparse points. It starts two separate private PowerShell 7
processes by absolute path: first Microsoft Entra, then ExchangeOnlineManagement only
after the Entra process disconnects and exits. Both use Microsoft's default WAM
interactive authentication. Device Code and automatic `-DisableWAM` fallback are
intentionally absent.

Entra keeps the existing hidden child-process mode. Exchange alone acquires a small
Windows console lease in the already-authenticated interactive worker. The lease rejects
Session 0 or a mismatched launcher session, requires a usable window station/desktop,
calls `AllocConsole` only when no console exists, and requires a nonzero
`GetConsoleWindow`. Exchange private PowerShell starts with `CreateNoWindow=false` but
keeps stdin/stdout/stderr redirected. Before the fixed script is written, the worker uses
`GetConsoleProcessList` in a small cancellation-aware bounded poll to prove both worker and
the exact expected child are attached to that console. A wrong or missing child fails closed. An
owned console remains visible with a neutral title for reliable WAM modality and is freed
only after child exit and output drain; a borrowed console is never freed. Failure stops
with a sanitized compatibility result and never falls back to Device Code,
`-DisableWAM`, or an administrator-token handoff.

The Entra executor creates or verifies one single-tenant application and supplies the public
DER certificate and certificate hash as the Base64 strings required by Graph. A narrow raw
Graph v1.0 read is authoritative for `AzureADMyOrg`, exactly one matching certificate
credential, zero password credentials, and zero configured API permissions; inconsistent
Microsoft.Entra 1.3.0 wrapper projections are diagnostic only. It creates the service
principal and requires zero service-principal app-role assignments. The Exchange executor requires the
selected sender to be the marked group's only member, requires an exact group-backed
management-scope filter, enumerates assignments with the documented assignee-wide
`Get-ManagementRoleAssignment -RoleAssignee` query, requires exactly one expected scoped
`Application SMTP.SendAsApp` assignment for that principal, and runs
`Test-ServicePrincipalAuthorization`. Existing objects are reused only when their exact
identity and security properties match; additive credentials, members, filters, or role
assignments fail closed. Production-script coverage supplies a wrong-assignee object and
asserts the exact expected service-principal object ID passed to `-RoleAssignee`.
Scope-filter comparison deliberately remains strict and
case-insensitive after trimming; the exact Exchange-returned representation is a
real-tenant native validation gate rather than being weakened with substring matching.

The named-pipe protocol is strict, versioned, and limited to 4 KiB messages. It has
no fields for administrator passwords, tokens, auth codes, private keys, PFX data,
PowerShell source, or arbitrary commands. PowerShell accepts fixed script source plus
Base64 JSON data and emits exactly one bounded structured result. Unexpected output,
unknown/duplicate fields, and token-like result fields fail closed.

## Activation and recovery

Schema v9 retains the schema-v7 `ActivationId` and adds a candidate revision plus an
authoritative Active/Cancelled/Activated lifecycle. Every
new/replacement candidate receives a fresh ID. Native Entra and Exchange results carry
the immutable candidate ID/revision/fingerprint and are committed only by compare-and-swap;
a stale helper cannot update a replacement candidate. Candidate M3/M4 evidence is keyed
by that activation ID plus the existing non-secret tenant/client/certificate/sender
fingerprint. The final SMTP-accepted candidate is activated in the same SQLite transaction
that rechecks its identity, revision, required completion flags, and sender. Cancellation
or replacement before that commit leaves the previous active configuration unchanged;
after the atomic commit, activation is authoritative. An A→B→identical-A activation
therefore cannot inherit readiness from the earlier A epoch.

Entra completion can be saved as safe partial candidate progress. Cancellation owns
both the helper process tree and the Host-side candidate verification token until the
final accepted candidate is committed. Cancellation, helper/process failure, or
Exchange failure leaves the prior active configuration unchanged and allows an
idempotent retry with fresh administrator authentication.
No cloud rollback or destructive cleanup is attempted. The existing manual scripts
remain under Advanced recovery.

## Tooling prerequisite

M5.1 defines the runtime integrity contract but does not implement the M8 installer.
Native setup is enabled only when configuration points to an installer-owned installation
root, a dedicated NativeAOT-launcher/framework-dependent-worker directory, a private PowerShell tree, and
signed-release-approved helper/tooling manifest hashes. Self-contained single-file was
evaluated but rejected for this trust boundary: bundling native runtime libraries requires
extraction under `%TEMP%\.net` by default, which creates another writable execution path.
The helper manifest instead lists the complete exact directory and hashes every executable
or configuration dependency. The helper and tooling roots and every traversed child must
be non-reparse, owned by SYSTEM, Administrators, or TrustedInstaller, and non-writable by
ordinary users; raw `GENERIC_WRITE`/`GENERIC_ALL`, explicit mutation, DACL change, and
parent replacement rights all fail closed. Helper integrity is checked before URI launch
and again on connection; tooling integrity is checked at setup start and immediately
before each child launch. Runtime never
falls back to PATH, a user/system module, PowerShell Gallery, or “latest.” Legal
approval for bundling Microsoft modules remains separate; exact verified installer
download is the compatible alternative.
M8 must additionally create and protect the ProgramData scratch root and ship or
exact-download complete release-manifest-pinned trees for private PowerShell,
Microsoft.Graph.Authentication 2.25.0, Microsoft.Graph.Applications 2.25.0,
Microsoft.Entra.Authentication 1.3.0, Microsoft.Entra.Applications 1.3.0, and
ExchangeOnlineManagement 3.9.2. The Entra manifests permit Graph versions at or above
2.25.0, while RelayBridge deliberately pins exactly 2.25.0 for reproducible execution.
The official Gallery 3.9.2 package inspected on 23 Aug 2026 was 16,228,146 bytes with
SHA-256 `3C9D862971E7E620FB3B3B76DCC5BF8949E8E40CF860FA1C3099FBC78669EF93`;
this evidence is not a substitute for the M8 release's generated complete-tree manifest.

## Platform and validation limits

Initial native setup support is Windows 10/11 with one locally logged-in interactive
administrator. Windows Server/RDP/Server Core/multi-user native setup is not claimed.
The existing M3/M4 runtime and Advanced manual setup remain separate from that claim.

Automated inventory: 42 unit + 388 integration = 430 tests. The complete trusted suite passes
with zero failed or skipped after the validation service was cleanly stopped and its process
exited, including all fixed-bootstrap-pipe tests. The correction-focused suite also passes,
including the real-process console test in 20/20 isolated runs.
The focused pre-closure publish observed a 2,423,808-byte NativeAOT launcher; the final
low-risk closure publish observes a single 2,440,704-byte runtime executable with no
`.dll`/`.deps.json`/`.runtimeconfig.json` sidecars. These sizes are observational build
evidence only; the exact release manifest and its file hashes are the authoritative integrity
boundary. The suite runs a working
startup-hook positive control, and proves hostile startup-hook, profiler, additional-deps,
and `DOTNET_ROOT*` settings do not execute or redirect the launcher or its sanitized children.
Real PowerShell startup-hook positive controls cover both logical Entra and Exchange child
environments. Tests also cover exact scratch ACL/owner/SID revalidation before cleanup,
containment/reparse/active-lock retention policy, live Exchange `PSModulePath` clearing, exact
private EXO identity, and generated `tmpEXO_*` rejection outside protected scratch, alongside
proxy removal and audit-listener non-contact, worker parent/path/hash/session/SID policy,
direct-worker rejection before confirmation, exact Graph tree/version/tamper rejection,
private-module poisoning resistance, deterministic import order, command-source identity,
and post-import module-search lockdown. The exact staged Microsoft packages passed the
production import-only preflight under private PowerShell 7.6.4 without Microsoft sign-in.
The focused WinExe process fixture additionally proves hidden PowerShell observes no
console window, Exchange-hosted PowerShell observes a nonzero console window, redirected
stdin/stdout/stderr still function, cancellation terminates the Job-contained child, and
the owned console is released afterward. No Microsoft connection occurs in those tests.
The corrected native path completed real Entra and Exchange provisioning, strict object/scope
verification, authorized token/STARTTLS/XOAUTH2/final SMTP `250`, atomic activation, and an
out-of-scope control-sender denial at `AUTH 535`. A post-restart authorized check again reached
final `250`, while Host readiness correctly reset to `Verification required`. The first immediate
SMTP check returned `535` until the newly created Exchange authorization propagated; a later
manual retry succeeded without changing permissions. No MFA challenge was presented, so observed
MFA and representative Conditional Access are not claimed. The focused adversarial review and
pre-freeze low-risk closure are complete; M5.1 remains unfrozen pending an explicit freeze action.

The parameter-free custom URI is only a launcher and contains no setup authority. A
local page can still cause the approved helper to display its confirmation; adding a
short-lived launch-intent nonce is deferred as low-risk prompt-spam hardening because
the pipe identity checks, confirmation, and Microsoft authentication remain authoritative.
