# Architecture Overview

## Scope

RelayBridge V1 runtime is one ASP.NET Core process, normally installed as one Windows
Service. It hosts the local Blazor status UI, SMTP listener, durable queue workers,
Microsoft application authentication, and outbound Exchange SMTP transport. Two
short-lived setup executables run only in the interactive Windows user session while a
Microsoft administrator provisions the tenant: a NativeAOT launcher establishes the
authenticated local boundary, then a subordinate managed worker performs fixed setup
operations. Neither is a service, listener, delivery provider, or persistent
administration identity.

## Projects and dependency direction

```text
RelayBridge.Host ───────> RelayBridge.Infrastructure ───────> RelayBridge.Core
        └──────────────────────────────────────────────────> RelayBridge.Core
RelayBridge.SetupLauncher ─────────────────────────────────> RelayBridge.Core
RelayBridge.Setup ─────────────────────────────────────────> RelayBridge.Core
```

- **Core**: business rules, immutable domain concepts, validation, retry policy,
  and interfaces only at genuine external boundaries. It must not depend on the
  host, SQLite, MSAL, or SMTP libraries.
- **Infrastructure**: adapters for SQLite, filesystem spool, certificates,
  Microsoft identity, inbound SMTP, and outbound SMTP. Only capabilities required
  by the active milestone belong here.
- **Host**: composition root, Blazor UI, configuration, health endpoints, hosted
  services, and Windows Service integration.
- **SetupLauncher**: sidecar-free NativeAOT pipe client authenticated by the Host;
  constructs the worker's explicit environment, owns the child Job Object, and relays
  only bounded setup frames over inherited standard handles.
- **Setup**: subordinate managed confirmation, private-tool integrity checks, and fixed
  short-lived Microsoft administration executors. Direct launch has no Host authority.

There is no separate application layer, mediator, event bus, repository wrapper,
or generic administration engine. The two setup executables are a narrow privilege
boundary: the native launcher prevents user-controlled CLR initialization before the
first trusted instruction, while the managed worker keeps interactive administrator
authentication and PowerShell assemblies out of the Session-0 service. Both exit after
setup.

## Milestone 7 runtime

The host can run interactively or under the Windows Service Control Manager. Its
management HTTP endpoint is code-owned loopback on configurable `Management:Port`;
generic URL/Kestrel configuration is accepted only when every requested listener is
also loopback, and a non-loopback request fails startup. The separate SMTP listener
binds to loopback by default and uses port 2525.
A fresh database has no devices and cannot accept a mail transaction. The Blazor
site provides an operational dashboard, device list, add/edit/reset/disable flows,
the Microsoft setup wizard, and a diagnostics page. It does not expose message bodies
or attachments.
Management is enforced loopback-only; authenticated remote management and installer
ACL/service-identity hardening remain later work.
Startup reconciles SQLite metadata with the two spool directories before any
worker can claim mail. Admission reserves message count and bytes across active
SMTP sessions and preserves a configurable free-disk floor.

Inbound sessions are asynchronous and bounded globally and per source. Every
device has source and sender allow-lists. Authenticated devices use LOGIN or PLAIN
against a salted password verifier; Legacy devices omit AUTH but are restricted
to private/local IP ranges. STARTTLS is not advertised and cleartext AUTH is
disabled by default until certificate provisioning and key protection can be
implemented safely. Cleartext SMTP AUTH can be enabled only on one explicit RFC1918
IPv4 or IPv6 ULA address. Wildcard, loopback, link-local, multicast, and public
authenticated bindings fail authoritative listener validation.

Diagnostics are an in-process read model, not a second monitoring subsystem. Each status
includes its observation time and whether it came from runtime state, configuration,
persisted state, last verification, or an explicit active probe. The page reads the actual
server/listener/queue state where available and does not describe configured intent as a
live observation. Microsoft configuration, current-process readiness, and unauthenticated
network reachability remain separate signals.

The optional Exchange connectivity action is fixed to `smtp.office365.com:587` and performs
only DNS resolution, TCP connection, SMTP greeting, EHLO, STARTTLS advertisement, and a
normal hostname/certificate-validated TLS handshake. It has one bounded deadline and never
acquires a token or sends AUTH, MAIL, RCPT, DATA, or message content. The database quick
check is likewise explicit rather than page-load work.

The separate post-restart `Verify connection` action uses the active saved tenant, application,
certificate, and sender configuration. It validates certificate/private-key access, acquires the
normal Exchange token, and reuses the production SMTP path through DNS, TCP, STARTTLS with platform
certificate validation, and XOAUTH2 authentication, then sends QUIT. It sends no MAIL, RCPT, DATA,
or message content, performs no tenant provisioning, and does not replace or reactivate the saved
configuration. `Repair connection` remains the explicit route into the provisioning workflow.

Support bundles are generated locally in memory from fixed anonymous allowlist projections,
use a versioned fixed entry list and size cap, and are never uploaded by RelayBridge. Raw
configuration, SQLite, logs, spool files, addresses, message data, password/verifier data,
tokens, private keys, certificate references, and detailed PowerShell errors are excluded.
The bundle retains only the documented non-secret operational metadata that an administrator
is asked to review before sharing.

Microsoft identity configuration contains one tenant GUID, client GUID, and
Windows `My` certificate reference in SQLite. The private key remains in the
Windows key store. A reusable MSAL confidential client acquires only the Exchange
resource token and keeps its application cache in memory. No Microsoft call occurs
during startup, `/health` remains local queue health, and the status UI keeps
identity and Exchange delivery readiness distinct. Exchange readiness is updated
by real delivery or an explicit diagnostic rather than dashboard polling.

Schema version 9 keeps the authorized sender with that existing identity, one safe,
resumable candidate-setup row, an optional bounded device description, and separate
device/candidate revisions used for optimistic updates. The candidate contains identifiers,
certificate reference, sender, current step, validation flags, a collision-safe
activation ID, revision, and authoritative lifecycle only. The recommended path uses the authenticated interactive helper,
a strict bounded pipe, and separate fixed private Entra/Exchange PowerShell processes;
administrator tokens never enter the Host or protocol. Candidate token
and real SMTP sender validation reuse M3/M4 services. Only after both succeed does
one SQLite transaction rechecks the immutable candidate identity/revision/lifecycle and activates
the identity, sender, and verified activation ID, preserving any prior working
configuration on failure or cancellation before commit. Cancellation itself is a conditional
SQLite mutation, so late Entra/Exchange results and activation lose compare-and-swap after
cancellation; a cancellation arriving after activation cannot roll it back. Credentials remain salted verifiers,
and queue rows continue to reference stable device IDs.

The bootstrap URI is parameter-free and grants no authority. It launches only the
sidecar-free NativeAOT launcher. The service's first-instance pipe rejects remote clients.
It authenticates the launcher's kernel-reported client PID, requires the process-token
SID to equal the impersonated pipe SID, and then checks interactive session, absolute
launcher path, and exact launcher hash. A separate pinned manifest authenticates the
launcher plus exact framework-dependent worker execution closure before URI launch and
again before the running launcher receives a connection-bound random session. One
bootstrap deadline covers the unauthenticated handshake and identity/candidate
checks, and auxiliary-listener failure is contained from SMTP/queue workers. The launcher
also requires the kernel-reported pipe-server PID to equal the running SCM-owned
`RelayBridge` service PID. The service must be a running own-process service configured as
`LocalSystem` with the exact installer-protected sibling `Host\RelayBridge.Host.exe` binary
path. This avoids process/session/token inspection that Windows denies across the
medium-integrity-to-LocalSystem boundary.
It starts only the fixed sibling worker over inherited standard handles. The worker has
no bootstrap-pipe implementation and, before any confirmation or provisioning, verifies
its kernel-reported parent PID, exact protected sibling launcher path/hash, Windows
session, and interactive SID against the Host-authenticated start frame. Direct and
copied-worker launches fail. IPC has no token, password,
private-key, PFX, script, or arbitrary-command fields. Private tooling is installer-owned;
the absolute root and
all traversed children must be non-reparse, owned by SYSTEM, Administrators, or
TrustedInstaller by SID, and deny ordinary-user explicit or raw-generic
mutation/replacement rights. M8's MSI packages a dedicated helper directory and the wider
Program Files hierarchy and emits exact helper/tooling manifests. Disposable Windows 11
installed-machine acceptance verified effective ownership and ACLs, ordinary-user tamper denial,
non-reparse traversal, and the frozen path and closure verifiers against the installed tree.
M5.1 verifies the paths and closures before privileged sign-in. The approved manifest hashes,
exact complete trees, per-file
hashes, module paths/versions, and applicable
signatures are checked initially and immediately before each child launch. Missing,
modified, or unsafe tooling fails closed; Advanced manual
setup remains available.

After launcher authentication, the LocalSystem Host creates a fresh unpredictable directory
beneath the installer-owned `C:\ProgramData\RelayBridge\SetupScratch` root. The root and
parents are reparse- and replacement-checked; the session DACL permits SYSTEM,
Administrators, and only the exact interactive SID, without DACL/ownership rights. The
launcher, worker, Entra process, and Exchange process use this path for `TEMP` and `TMP`.
ExchangeOnlineManagement is pinned to 3.9.2 GA and also receives the same path through
`-EXOModuleBasePath`, preventing generated `tmpEXO_*` code from using inherited temporary
storage. Cleanup occurs only after the launcher-owned process tree exits; uncertain live
sessions are retained for lock-aware stale cleanup.

The Windows installation boundary is an x64 per-machine WiX MSI chained by a Burn bootstrapper.
The fixed layout is `Program Files\RelayBridge\{Host,Setup,Tooling}` plus protected, permanent
`ProgramData\RelayBridge\{Data,SetupScratch}` state. Standard MSI service, registry, ACL, repair,
major-upgrade, rollback, and uninstall facilities are used; there are no installer custom actions
or firewall changes. The MSI is an internal chained package and rejects a clean direct install.
Both global .NET 10 Runtime and ASP.NET Core Runtime prerequisites are exact-version/hash pinned.
The installed own-process LocalSystem service has bounded recovery: two crash restarts separated
by 15 seconds, a one-day reset window, and no third automatic restart. Normal stop/uninstall remains
SCM/MSI controlled.

M9.1 keeps private PowerShell 7.6.4 and ExchangeOnlineManagement 3.9.2 in the reviewed M8 payload,
but removes the four Graph/Entra package byte streams from public-candidate artifacts. After an
explicit Microsoft Graph terms boundary, Burn downloads exact package versions from fixed official
PowerShell Gallery HTTPS URLs and validates size/SHA-512. A NativeAOT provisioner independently
validates SHA-256, package ID/version/license/dependencies/signature payload, safely extracts into
protected random ProgramData staging, validates the schema-v2 file closure, and transactionally
commits only the owned module roots. Normal runtime uses only the protected Program Files copies;
it never contacts Gallery or falls back to user/system modules. Uninstall removes acquired module
roots and installer acceptance state while preserving durable Data, certificates, and tenant
objects. Disposable Windows 11 acceptance verified clean/silent installation, cache-backed repair,
corrupt-file restoration, uninstall/data preservation, reinstall, major upgrade, downgrade
rejection, and rollback. Windows Server, RDP, and Server Core support are not claimed.
The release pipeline also emits a CycloneDX 1.6 SBOM and categorized third-party component
inventory. These artifacts contain component identities only, never machine paths, tenant IDs,
mailbox data, tokens, or package-cache locations.

The NativeAOT launcher creates the managed worker environment from a fixed allowlist:
required Windows paths, interactive-user Windows known folders, and Program Files only.
Raw `HTTP_PROXY`, `HTTPS_PROXY`, `ALL_PROXY`, and `NO_PROXY` variants are not inherited.
It omits all `DOTNET_STARTUP_HOOKS`, `DOTNET_ROOT*`,
additional-dependency/shared-store/host-path, `CORECLR_*`, profiler `DOTNET_*`, legacy
`COR_*`, and `COMPlus_*` controls, and explicitly sets `DOTNET_EnableDiagnostics=0`.
The framework-dependent worker apphost uses `AppHostDotNetSearch=Global`, excluding
environment-variable runtime lookup. The worker reconstructs the same clean environment
for both private PowerShell children and fixes module discovery to approved absolute
paths. Before WAM, an import-only private PowerShell process explicitly loads the exact
Graph Authentication 2.25.0, Graph Applications 2.25.0, Entra Authentication 1.3.0,
and Entra Applications 1.3.0 manifests in dependency order. It verifies each canonical
module base/version and required command source, then clears the in-process module search
path while autoload remains disabled. The authentication-bearing Entra process repeats
the same bootstrap. Entra remains hidden. Exchange alone obtains a short-lived console
lease from the authenticated interactive worker because ExchangeOnlineManagement's
default WAM path requires a Windows parent handle. The worker verifies its interactive
session/window station/desktop, allocates a console only when necessary, and uses
`GetConsoleProcessList` in a small cancellation-aware bounded poll to prove the exact private
PowerShell child is attached before writing the fixed Exchange script. A different child is
never accepted as proof. Standard streams remain redirected and bounded. The
neutral console stays visible for reliable WAM modality and an owned console is released
only after the child exits and its output drains. There is no Device Code,
`-DisableWAM`, or access-token fallback. The launcher owns a kill-on-close Job Object for the worker and its descendants;
browser and Microsoft WAM broker processes remain OS-owned and are not killed.

Entra creates the application certificate credential with Base64 public DER and certificate
hash fields. Its post-create security decision uses a narrow raw Graph v1.0 application read
to require `AzureADMyOrg`, exactly one matching public-certificate credential, zero password
credentials, and zero API permission entries. Microsoft.Entra wrapper projections remain
non-authoritative diagnostics because their empty collection shape is not stable.

Device management reuses the existing domain rules. Compatible devices get a
generated per-device username and one-time password; only its PBKDF2 verifier is
persisted. Legacy devices still require a private/local IP restriction and sender
allow-list. Configuration edit, enable/disable, and password reset use narrow
compare-and-swap updates. Every security-relevant mutation advances the device
revision, so a stale edit or concurrent reset cannot restore an old security state
or return a losing secret. No mutation rewrites
queue history. Dashboard summaries use a fixed small set of metadata
queries and do not inspect message content. Setup instructions come from the actual
listener binding: loopback or wildcard configuration produces a warning, never a
false LAN address. Expected interface-enumeration failures return safe unavailable
advice, and multiple plausible private interfaces require administrator selection
before deployment JSON is generated.

The dashboard and creation gate use one small readiness policy: persisted Microsoft
identifiers are Verification required after restart. Microsoft identity and Exchange
operations carry immutable attempt contexts containing the configuration actually used.
Only completed attempt-scoped evidence participates, one shared in-process completion
sequence orders identity and Exchange outcomes, and evidence applies only when its
non-secret fingerprint matches the current active tenant/client/certificate/sender
configuration. Overlapping operations cannot transfer an outcome between configurations.
Device creation re-reads the
sender, listener, authentication state, and LAN candidates immediately before save,
then verifies the reviewed Microsoft fingerprint and sender in the same SQLite insert
transaction. A change creates no device. Microsoft remains first, then
a LAN-reachable listener, then authenticated intake where the chosen device mode
requires it. The listener remains startup-bound and is not mutated by the UI.
Printer-connectivity preparation instead emits bounded deployment configuration containing the
selected listener and `Queue.Enabled=true`, plus safe active private-address candidates. The
administrator can download or copy it, is shown the exact environment override destination and
safely quoted elevated copy/restart commands, and receives manual firewall guidance scoped to the
selected local address, port, Host executable, Private profile, and local subnet. The UI does not
write privileged configuration, restart the service, or alter Windows Firewall automatically.
Setup and detail views share the same address renderer; multiple network rules are
edited and persisted as a complete visible collection rather than silently reduced.

## Data and delivery flow

```text
SMTP envelope metadata + streamed RFC 5322 message
                    │
                    ▼
       SQLite metadata + raw filesystem spool
                    │
                    ▼
 bounded queue claim → delivery-result classification
                    │
                    ▼
  STARTTLS + XOAUTH2 Exchange SMTP client submission
```

The raw spool file is the delivery source of truth. SQLite stores queue metadata,
not large message bodies. SMTP success after DATA is forbidden until durable local
acceptance completes.

Milestone 1 writes DATA to `spool/incoming`, durably flushes it, atomically moves
it to `spool/pending`, commits SQLite metadata, and only then returns `250`. A
local preview service reads queue metadata and resolves spool paths for tests and
future authenticated UI work; it is not an outbound provider and does not parse
or reconstruct MIME.

The queue atomically claims one eligible row at a time, closes the SQLite
transaction, then streams its raw payload to a provider. A short second update
records delivery, retry, or permanent failure. A semaphore signal plus periodic
poll avoids busy-waiting. The Exchange provider is composed in the host, but
workers remain disabled by default. If explicitly enabled, startup requires
configured identity metadata and a locally usable certificate; it performs no
Microsoft network call. Reconciliation and local intake remain available when
Microsoft is absent or unavailable.

Outbound delivery opens one connection per attempt to the fixed
`smtp.office365.com:587` production endpoint, requires STARTTLS and post-TLS
XOAUTH2, and uses the envelope sender as both the authenticated RBAC mailbox and
MAIL FROM. Every recipient must be accepted before the raw spool is streamed.
Only the final Exchange `250` marks success; failure after the terminator and
before that response is ambiguous and retryable.

## Architectural constraints

- Microsoft Graph is not a V1 delivery fallback.
- Outbound TLS certificate validation is mandatory.
- Unauthenticated legacy intake requires private/local source-network and sender
  restrictions and fails closed.
- Network and message processing will be asynchronous, streamed, cancellable,
  timeout-bounded, and concurrency-bounded.
- Microsoft connectivity must not be a service startup dependency.
- Microsoft tokens, client assertions, client secrets, PFX data, and private keys
  must never enter SQLite, logs, diagnostics, or application settings.
