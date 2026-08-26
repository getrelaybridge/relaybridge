# Build Status

## Current milestone

Milestone 9 — product hardening: **FROZEN**.
The finite hardening pass adds bounded Windows service crash recovery, rejects production SMTP
port zero, closes a failed-SQLite-open resource leak, expands hostile SMTP/storage regression
coverage, and produces a CycloneDX 1.6 release SBOM plus categorized third-party notices. M9,
M9.1, M8, M7, and M5.1 are frozen. Schema remains v9. No Microsoft authentication, tenant
mutation, or Exchange delivery was part of M9 acceptance.

RelayBridge source is public at `https://github.com/getrelaybridge/relaybridge` from an intentional
fresh-history snapshot. RelayBridge-owned source is licensed under MPL-2.0, project metadata and
deterministic license checks agree, governance and security reporting files are public-facing, and
current plus reachable-history scans found no committed secrets or private artifacts. RP-1 adds a
deterministic source-only public export, public release CI/origin-verification foundation, SignPath
pre-application policy, exact WiX v6.0.2 notices/source, and corrected SBOM runtime classification.
This does not authorize binary publication; signing and Microsoft license clarification remain
open. WiX OSMF/license compliance is closed for the current individual, non-revenue-generating use
and requires re-evaluation if use becomes revenue-generating.

## Implemented

- owner personal-test corrections keep the packaged SMTP listener and queue inert by default, while
  generated printer-connectivity configuration now enables both the selected listener and queue;
  the page provides download/copy actions, the exact environment override destination, safely
  quoted elevated copy/restart commands, and manual address/port/program-scoped firewall guidance
- post-restart `Verify connection` now validates the active certificate/private key, acquires the
  normal Exchange application token, and uses the production DNS/TCP/STARTTLS/XOAUTH2 path through
  successful authentication and QUIT without MAIL, RCPT, DATA, tenant mutation, or activation;
  `Repair connection` remains a separate administrator-selected provisioning workflow
- same-active-configuration repair preserves the already validated service-principal object ID;
  starting a replacement candidate does not inherit that prior identity

- RP-1 public release preparation defines a fresh-history Git-tracked source export, rejects
  generated/binary/private workspace material, and adds a source-snapshot verifier suitable for
  scanning the actual publication candidate
- the GitHub-hosted Windows unsigned-release workflow checks out the exact origin commit with
  immutable action SHAs, scans history for secrets, verifies license/release hygiene, audits NuGet,
  builds/tests, builds and validates the installer/SBOM/notices, and uploads only bounded unsigned
  workflow artifacts; it does not call SignPath or publish a release
- SignPath Foundation readiness and code-signing policy now document current eligibility,
  one-maintainer roles, accurate network/privacy behavior, owner MFA/repository actions,
  trusted-build/origin requirements, nested signing scope/order, and the unresolved first-binary
  release wording without representing the pending attribution as approved
- WiX v6.0.2 Burn, WixStdBA, NetFx, and Util runtime output is now distinguished from excluded SDK/
  compiler tooling in notices and CycloneDX; the full MS-RL text and exact v6.0.2 source reference
  accompany the inventory

- WiX service recovery restarts the own-process LocalSystem Host after the first and second crash,
  waits 15 seconds, resets the failure window after one day, and stops retrying after a third
  failure to avoid an unbounded crash loop
- production SMTP configuration now requires port 1 through 65535; ephemeral port zero remains an
  internal test-only facility
- failed SQLite connection initialization disposes the connection before propagating the error;
  corrupt and newer-schema fixtures fail closed without rewriting the database
- focused hostile-input regressions cover command flooding through its protocol-level `421` rejection
  and bounded connection close, partial-line idle timeout,
  malformed MIME/many headers/binary-like payload preservation, database corruption, and future
  schema refusal
- the installer pipeline emits a machine-readable CycloneDX 1.6 SBOM and third-party inventory;
  release gates explicitly distinguish redistributed, Microsoft-direct-acquired, and build-only
  components
- WiX symbol databases are retained in an internal `artifacts/installer/symbols` directory and
  package validation rejects build/debug symbols beside public-candidate artifacts
- the finite disposable Windows 11 Pro x64 acceptance matrix passed all 20 cases, including clean
  install, reboot, bounded service crash recovery, durable/hostile SMTP intake, queue retry and
  permanent failure, corrupt/access-denied/low-disk storage, certificate and controlled
  DNS/TCP/TLS failures, degraded diagnostics, support-bundle privacy, preserved-state upgrade,
  acquisition, direct-MSI refusal, uninstall preservation, and healthy reinstall

- WiX 6.0.2 x64 per-machine MSI with fixed `ProgramFiles64Folder` layout for Host, Setup, and
  Tooling; standard MSI file, MSI 5.0 ACL, registry, service, repair, major-upgrade, and
  uninstall facilities are used with no custom actions or user-selectable security path
- minimal Burn bootstrapper embeds exact hash-pinned Microsoft .NET Runtime 10.0.11 x64 and
  ASP.NET Core Runtime 10.0.11 x64 prerequisites; the MSI is now an internal chained package and
  rejects direct installation unless the exact M9.1 tooling transaction is present
- Burn remotely acquires only Microsoft.Graph.Authentication 2.25.0,
  Microsoft.Graph.Applications 2.25.0, Microsoft.Entra.Authentication 1.3.0, and
  Microsoft.Entra.Applications 1.3.0 from exact official Gallery URLs, enforcing fixed byte size
  and SHA-512 before the provisioner independently enforces SHA-256 and package metadata
- WixStdBA presents the exact Microsoft Graph package names/versions and publisher LicenseUri;
  quiet/passive setup requires `RelayBridgeAcceptMicrosoftGraphTerms=1`, while a protected
  identity-bound machine record permits repair of only the same package identities
- the NativeAOT tooling provisioner uses protected random ProgramData staging, strict bounded ZIP
  extraction, exact dependency and file-closure verification, transactional Program Files module
  replacement, rollback, repair, release-bound servicing markers, and fail-closed removal
- deterministic installer pipeline publishes the framework-dependent Host and managed worker,
  publishes the self-contained NativeAOT launcher/provisioner, stages exact private PowerShell
  7.6.4 and ExchangeOnlineManagement 3.9.2, uses the acquired Graph/Entra closure only to generate
  its final file manifest, strips debug symbols, and generates complete WiX payload authoring
- exact M5.1 helper schema v1 and tooling schema v2 manifests are generated from staged trees;
  launcher/manifest SHA-256 trust anchors and fixed Program Files/ProgramData paths are written
  through the existing `NativeMicrosoftSetup` configuration contract before packaging
- pinned provenance records official source, version, package hash, signature expectation, and
  license status for every private tooling input; normal runtime never downloads from Gallery,
  searches PATH, or accepts an alternate module/runtime version
- machine-wide `HKLM\Software\Classes\relaybridge-setup` registration invokes only the quoted
  fixed NativeAOT launcher with one quoted URI argument; repair restores it and uninstall removes
  only the RelayBridge-owned handler
- LocalSystem `RelayBridge` service installation/start/stop/wait/delete uses `ServiceInstall`
  and `ServiceControl`; no firewall rule or unstable hard-coded management shortcut is created
- protected ProgramData SDDL grants SYSTEM/Administrators full control, grants Users only
  read/execute on the root and scratch root, and grants no ordinary-user Data access; permanent
  ProgramData components preserve queue/configuration/history on repair, upgrade, and uninstall
- stable major-upgrade family blocks downgrades and preserves ProgramData; ordinary uninstall is
  local-only and does not delete certificates/private keys or Microsoft tenant objects
- optional release-signing support covers RelayBridge executables, MSI, and bootstrapper when a
  real signing-certificate thumbprint is provided; it never creates a self-signed release identity

- responsive operational dashboard and device list with metadata-only readiness,
  queue, today, and recent-device summaries
- ordered dashboard actions: Microsoft setup first, then printer-connectivity
  preparation, then device creation only when the prerequisites are usable
- focused printer-connectivity guidance shows the configured listener, port,
  private active LAN candidates, authentication state, inbound-TLS absence, complete
  standalone deployment JSON only after explicit selection when multiple candidates exist,
  restart requirement, and trusted-LAN warning; startup-bound
  listener options remain unchanged at runtime
- five-step add-device flow that explains the inbound STARTTLS limitation, defaults
  to per-device authentication, and keeps Legacy mode constrained to a private/local
  source and the active authorized sender
- authenticated creation is gated by Microsoft readiness, LAN reachability, and
  cleartext SMTP AUTH availability; “I'm not sure” provides guidance and never
  silently selects Legacy mode
- unique device usernames and 192-bit one-time passwords; only the existing salted
  PBKDF2 verifier is persisted, reset invalidates the prior credential, and Test,
  Finish, reset-again, and controllable navigation require saved-password acknowledgement
- actual-listener endpoint advice that never tells a printer to use loopback and
  only suggests active private/ULA LAN addresses when the listener can accept them
- device detail/edit, password reset, disable/re-enable, setup-value copy/print, and
  a deliberate check-now workflow over normal durable queue metadata; queue history
  and stable device IDs are preserved
- device editing presents and preserves every allowed network rule, warns that
  address changes take effect immediately, and leaves existing queued mail untouched
- setup and detail screens share one server-address component and use explicit
  printer-side `Connection security: None` wording without weakening encrypted
  Exchange delivery
- inline reset/disable confirmations use truthful non-modal semantics with focus
  entry/restoration; no JavaScript dialog framework was added
- SQLite schema v6 retains the bounded optional description and one device revision;
  configuration edits, password resets, disable, and enable all compare-and-swap and
  advance that revision, while each narrow update changes only its intended columns
- management binding is code-owned loopback and startup rejects every effective
  non-loopback generic URL/Kestrel endpoint override; SMTP binding remains a separate
  surface and cleartext AUTH is rejected unless bound to one explicit RFC1918/ULA
  address; no discovery, live SMTP event system, inbound TLS, installer, remote
  administration, or Milestone 7 work was added
- configured Microsoft identifiers on the dashboard and completed setup page show
  Verification required after restart; readiness uses completed, attempt-scoped identity
  or Exchange evidence tied to the immutable configuration that originated the operation;
  one process-wide completion sequence deterministically selects the newest result for the
  current active configuration fingerprint
- device creation requires successful current-runtime Microsoft evidence, revalidates
  sender/listener/LAN state immediately before save, and verifies the reviewed Microsoft
  configuration fingerprint and sender inside the same SQLite device-insert transaction
- UI mutation handlers reject duplicate create/reset/enable/disable/save events at
  entry; one-time password/result objects and device records redact secret-bearing
  string representations, debugger views, and default JSON serialization
- queue summaries count Delivering as active work; NIC enumeration failures return a
  safe unavailable state; edit focus enters the inline panel and returns to its trigger
- loopback Blazor wizard with Welcome, new/existing application, certificate,
  Entra, Exchange permission, identity, SMTP/sender, test-message, completion,
  resume, back, cancel, and repair flows
- M3 certificate creation/selection/public-only export reused; selection shows only
  valid RSA signing certificates whose private key is usable by this process
- separate human-readable Entra and Exchange PowerShell 7 scripts remain available
  as Advanced recovery; the service does not execute those administrator-run scripts
- recommended new-application setup now uses the short-lived NativeAOT
  `RelayBridge.SetupLauncher` desktop boundary; only that launcher connects to the
  LocalSystem-owned named pipe and is authenticated by
  the kernel-reported client PID, that process token's SID, the impersonated pipe SID,
  interactive Windows session, absolute installed path, and exact executable hash;
  before the URI launch and again at connection, a separate pinned helper manifest
  authenticates the native launcher, managed worker apphost/assemblies, dependency/runtime configuration,
  and exact dedicated helper-directory contents; remote pipe clients are rejected
  and a native confirmation is required before provisioning; before showing that
  confirmation, `RelayBridge.Setup.exe` verifies its kernel-reported parent PID, exact
  launcher path/hash, Windows session and SID, so direct or copied-worker execution fails
- the authenticated launcher starts only the fixed sibling `RelayBridge.Setup.exe`
  worker over inherited standard handles, relays only bounded existing protocol frames,
  and owns a kill-on-close Job Object; the worker apphost publishes with
  `AppHostDotNetSearch=Global`, so `DOTNET_ROOT*` is not a runtime search source
- a strict versioned 4 KiB pipe protocol carries only public certificate material,
  sender and bounded sanitized identifiers/status; administrator credentials, tokens,
  authentication codes, private keys, PFX data, scripts, and arbitrary commands have
  no protocol fields and never cross into the service
- launcher-to-worker and worker-to-PowerShell process creation clears inherited
  environment state and reconstructs a reviewed allowlist from fixed Windows paths and
  current-user Windows known folders; raw proxy variables, low-value session variables,
  startup hooks, additional dependency/shared-store
  controls, `DOTNET_ROOT*`, CoreCLR/.NET/legacy profiler controls, and `COMPlus_*` are
  absent, while `DOTNET_EnableDiagnostics=0` is set for managed privileged children
- private PowerShell is launched by absolute manifest-approved path with `-NoProfile`,
  fixed stdin scripts, controlled module discovery, exact module path/version checks,
  bounded output, and Windows Job Object process-tree cleanup; Entra and Exchange run
  sequentially in separate processes with WAM and explicit disconnect/final process exit
- Entra provisioning failures retain an allowlisted substage code plus independently
  sanitized PowerShell exception type, fully-qualified error ID, category, and optional
  numeric HTTP status; raw Microsoft messages, request/response bodies, tokens, identifiers,
  stack traces, and invocation text do not cross the worker failure protocol
- Entra retains hidden process hosting; immediately before Exchange only, the interactive
  worker allocates or safely borrows a console, verifies a nonzero window and uses a small
  cancellation-aware bounded poll to require that the exact private PowerShell PID shares
  that console before writing the fixed Exchange script, and
  releases an owned console only after process exit and redirected-output drain; allocation,
  session/desktop, or child-attachment failure stops without an authentication fallback
- before WAM, a separate production-style import preflight explicitly loads exact private
  Microsoft.Graph.Authentication 2.25.0, Microsoft.Graph.Applications 2.25.0,
  Microsoft.Entra.Authentication 1.3.0, and Microsoft.Entra.Applications 1.3.0 manifests
  in dependency order, verifies canonical module bases and command sources, rejects stderr,
  disables autoload, and clears the reconstructed in-process `PSModulePath`
- after the Host authenticates the launcher, it creates one unpredictable session beneath
  the installer-owned `C:\ProgramData\RelayBridge\SetupScratch` root; only SYSTEM,
  Administrators, and the exact interactive SID receive session-directory access, while
  untrusted owners, broad mutation/delete-child rights, and reparse traversal fail closed;
  launcher, worker, and PowerShell use that directory for `TEMP`/`TMP`
- the exact private ExchangeOnlineManagement version is 3.9.2 GA; the Exchange stage
  supplies the protected scratch through `-EXOModuleBasePath` as well as `TEMP`/`TMP`
- release contracts require absolute non-reparse helper/tooling roots owned by
  SYSTEM, Administrators, or TrustedInstaller by SID, with no ordinary-user explicit
  mutation, raw `GENERIC_WRITE`/`GENERIC_ALL`, DACL-change, or delete-child replacement
  rights through either complete tree; pinned manifest hashes, deterministic exact trees,
  per-file SHA-256, and applicable Microsoft Authenticode identity are checked; integrity is
  rechecked immediately before each child process launch, and no
  PATH, user module, machine module, Gallery-latest, Device Code, or automatic
  `-DisableWAM` fallback exists
- schema v9 assigns every candidate/activation a collision-safe `ActivationId`, a
  candidate revision used for compare-and-swap updates, and an authoritative
  Active/Cancelled/Activated lifecycle;
  readiness evidence uses activation ID plus the non-secret configuration fingerprint,
  so A→B→identical-A cannot reuse A's prior process evidence; the candidate's same ID
  becomes active only after M3 token validation and actual M4 final SMTP acceptance
- administrator/user cancellation conditionally persists a Cancelled lifecycle and
  advances the candidate revision instead of creating a false Microsoft failure;
  later stage writes and activation fail compare-and-swap while the prior active
  configuration remains unchanged
- script values are Base64 JSON data rather than executable interpolation; Entra
  serializes both the public DER certificate and its thumbprint as Base64 strings. A narrow
  raw Graph v1.0 application read—not inconsistent Entra wrapper projections—authoritatively
  requires exactly one matching public-certificate credential, no password credentials,
  `AzureADMyOrg`, and zero API permission entries; zero service-principal app-role assignments
  remains separately enforced. Exchange requires the selected
  sender to be the only group member, an exact group-backed management-scope filter,
  and exactly one expected scoped `Application SMTP.SendAsApp` assignment discovered
  by the documented assignee-wide Exchange query; any extra assignment fails closed
- new candidates use deterministic certificate-scoped Entra and application-scoped
  Exchange object names, allowing safe side-by-side replacement while continuing
  to fail closed on an identity/configuration collision
- strict 4 KiB sanitized-result parsing rejects unknown/duplicate fields and invalid
  GUID/mailbox/injection values; pasted data is never interpreted as code
- SQLite schema v4 adds one safe resumable candidate row and authorized sender to
  the existing identity configuration; a v3 migration preserves active identity
- candidate certificate/token and actual M4 SMTP sender tests run before one atomic
  activation transaction, so failure/cancel leaves an existing connection intact
- the setup test uses the actual Exchange provider and reports “Accepted by
  Microsoft 365” only after final SMTP acceptance; no Graph/test transport exists
- M4 SMTP state machine, TLS, XOAUTH2, streaming, timeout, queue, retry, and final
  acceptance behavior were not changed
- all Milestone 1–3 SMTP intake, durable queue, recovery, capacity, bounded-worker,
  certificate, MSAL token-cache, and secret-handling invariants remain intact
- production `ExchangeSmtpOAuthProvider` uses one connection per attempt to the
  fixed `smtp.office365.com:587` endpoint; no Graph, pooling, pipelining, arbitrary
  SMTP endpoint, or TLS-validation bypass exists
- bounded RFC multiline response parser (2,048-byte line, 100 lines, 32 KiB total)
  with sanitized/bounded server diagnostics and stage-aware 2xx/3xx/4xx/5xx
  handling
- deterministic `EHLO relaybridge.local`, required STARTTLS, normal .NET/Windows
  chain/hostname/validity/SNI validation, OS TLS negotiation, and mandatory second
  EHLO with discarded pre-TLS capabilities
- Exchange token acquisition immediately before AUTH and documented challenge-form
  XOAUTH2; sensitive payload/encoded command byte arrays are zeroed and never
  logged, persisted, or returned in errors
- XOAUTH2 `user=` and `MAIL FROM` both use the durable envelope sender; MIME From
  is never used for routing and the sender must be inside the Exchange App RBAC
  resource scope
- RFC 1870 SIZE preflight and MAIL parameter, using the known spool size and
  server-advertised limit without a universal Microsoft limit
- multiple envelope recipients use V1 all-or-nothing semantics: every recipient
  must be accepted before DATA; rejection triggers best-effort RSET and no body
- authoritative raw spool streams through 64 KiB input/128 KiB transform buffers
  with outbound dot-stuffing and tested CRLF/DATA terminator semantics; no MIME
  parser or whole-message buffer was added
- final Exchange `250` after the terminator is the only success boundary; QUIT and
  connection-disposal failures after acceptance cannot trigger retry; failures
  proven before the terminator are ordinary transient failures, while uncertainty
  after terminator transmission begins is `AmbiguousAcceptance`
- connect/TLS/generic SMTP commands retain their 30-second bounds, DATA streaming
  retains its size-aware bound, and only the post-terminator final-response wait
  uses the RFC 5321-aligned 10-minute `DataTerminationTimeout`
- sanitized runtime telemetry records DATA start, payload bytes read, spool EOF,
  terminator write/flush, final-response wait/receipt, bounded final SMTP text,
  completion timestamps, exception type, and socket error without MIME or secrets
- the delivery snapshot also retains bounded numeric response codes for greeting,
  STARTTLS, AUTH, MAIL FROM, each RCPT TO, and DATA initiation
- SMTP/network/TLS/auth/authorization/sender/recipient/size/server/protocol/timeout
  results feed the existing queue policy; success persists Delivered before payload
  deletion, and no SQLite transaction spans network delivery
- composed host registers the Exchange provider, keeps workers disabled by
  default, and gates an explicitly enabled worker on local identity metadata plus
  a usable certificate without contacting Microsoft at startup
- Exchange delivery readiness remains distinct from identity and `/health`; a
  structured explicit test-message service exposes safe DNS/TCP/TLS/token/XOAUTH2/
  sender/final-acceptance checkpoints
- local intake rejects `MAIL FROM:<>`, and the outbound provider independently
  rejects an empty queued sender before token acquisition or network use; the
  freeze audit added regression coverage for both existing safeguards
- a concise sanitized real-tenant validation checklist records required tenant
  configuration, positive/negative RBAC tests, SMTP AUTH tests, size tests,
  leakage review, evidence fields, and the hard stop on denied-mailbox success
- ADR-0001/0002, architecture, queue durability, threat model, Microsoft transport,
  milestone notes, README, changelog, and status documentation updated
- loopback `/diagnostics` UI with explicit `Healthy`, `Needs attention`, `Unavailable`,
  `Not configured`, and `Unknown` states and visible Runtime, Configuration,
  Persisted state, Last verification, or Active probe provenance
- diagnostics project actual management/SMTP bindings, enabled devices, queue counts and
  ages, worker liveness, active Microsoft readiness and activation epoch, local certificate
  validity/signing access, sanitized setup result, schema/free disk, and security boundaries
- an explicit Exchange Online connectivity probe performs only DNS, TCP 587, greeting, EHLO,
  STARTTLS, and certificate-validated TLS within one 15-second budget; it requests no token,
  sends no AUTH, MAIL, RCPT, DATA, or message content, and stores only bounded stage evidence
- administrator-triggered bounded SQLite `PRAGMA quick_check` is kept distinct from ordinary
  storage availability and never runs automatically during page rendering
- local support-bundle generation uses a fixed schema-v1 entry list and a 1 MiB cap; its
  anonymous allowlisted DTOs cannot serialize runtime configuration objects, mailbox/device
  addresses, message content, credentials, certificate references, setup error dumps, or logs

## Verified

- Release build: 0 warnings, 0 errors
- Unit tests: 59/59; integration tests: 431/431; total: 490/490; failed: 0; skipped: 0
- owner personal-test defect regressions cover queue-enabled bounded deployment JSON, actionable
  administrator-assisted printer setup, direct active-configuration SMTP AUTH verification with no
  mail transaction, sanitized failure/readiness evidence, UI verify/repair separation, and
  same-configuration versus replacement service-principal preservation
- command-flood CI stabilization: focused test 25/25; CI-equivalent Release test configuration
  483/483; license and whitespace gates PASS
- M9 focused hardening: 12/12; hostile management: 12/12; hostile SMTP AUTH: 11/11;
  graceful shutdown: 2/2; M9.1 regression smoke: 16/16
- Installer 0.9.2 build/validation, CycloneDX SBOM, notices sidecar, payload scan, PowerShell
  parsing, NuGet vulnerability audit, secret/private-key scan, unsafe-TLS scan, and
  `git diff --check`: PASS
- Disposable Windows 11 Pro 10.0.26200 x64: 20/20 finite M9 acceptance cases PASS with no
  Microsoft authentication, tenant mutation, or Exchange SMTP delivery

- M9.1 Release build passes with 0 warnings and 0 errors; 59 unit and 418 integration tests pass
  (477 total, 0 failed, 0 skipped), including 20 focused acquisition, acceptance, package-metadata,
  unsafe-archive, and installer-contract tests
- WiX 6 remote-payload acquisition, exact cache-path handoff, silent/passive acceptance policy,
  protected cache reuse, repair, and rollback were exercised on Windows; the final Windows 11
  acceptance acquired four exact official packages, started one healthy service, returned HTTP 200
  for health/diagnostics, and left Microsoft state NotConfigured
- Windows 11 repair with an intact closure performed zero Gallery downloads; repair after deleting
  an owned Graph manifest restored the exact original hash from protected Burn cache. Uninstall
  removed service/URI/Program Files/downloaded module roots while preserving the durable database;
  accepted reinstall reacquired exactly four packages and reused that data
- a synthetic 0.9.1-to-0.9.2 major upgrade advanced the release-bound tooling marker, retained one
  exact 1.3.0/2.25.0 module closure, and remained healthy; downgrade returned 1638 before the
  provisioner ran. The WiX spike's intentional post-provisioning failure removed the new tooling
  and rolled back the chained install

- M8 release packaging passes: NativeAOT/framework-dependent publish, exact
  package-hash acquisition, helper/tooling manifest generation and closure verification,
  trust-anchor consistency, MSI ICE validation, MSI build, and prerequisite bundle build all
  complete with 0 warnings and 0 errors
- the verified staged release contains 1,224 files, including 6 helper-manifest entries and
  1,185 tooling-manifest entries; package validation excludes PFX/private-key/database/spool/
  message/test/local-development/debug-symbol artifacts and confirms the fixed service, URI,
  permanent ProgramData, and major-upgrade authoring
- the unsigned development MSI and prerequisite bootstrapper were exercised only on a disposable
  clean Windows 11 Pro x64 VM; neither package was installed on the developer workstation and
  external distribution remains prohibited pending signing and redistribution review. The final
  MSI is 116,914,729 bytes with SHA-256
  `C2970AB372DAB1DAE6B2FB67146E5E55B91F619AB99CCD3F435FC3629E1914BD`; the final
  158,431,665-byte bootstrapper SHA-256 is
  `CD24A0A525D602AC5D82149C5F20E5DF74268765AF71FC798CDB6A2AFC062E2A`
- current Release verification passes with 0 warnings and 0 errors; 59 unit and 403 integration
  tests pass (462 total, 0 failed, 0 skipped), including installer-contract and reciprocal
  SCM-service identity tests for fixed layout/service facilities, protected ProgramData/data
  preservation, URI and servicing policy, exact prerequisites, and pinned tooling/provenance inputs
- installed acceptance passes for the LocalSystem auto-start service, loopback health/diagnostics,
  exact Program Files/ProgramData owner-DACL and no-reparse policy, helper schema v1 (6 entries),
  tooling schema v2 (1,185 entries), real ordinary-user filesystem denial, elevated fail-closed
  tamper detection, private PowerShell 7.6.4 environment isolation, and exact module pins
- real Edge `relaybridge-setup://start/` launch displayed the genuine native confirmation; the
  observed interactive chain was installed launcher to installed worker to exact private pwsh,
  and cancellation removed the Job-contained process tree and protected per-session scratch while
  the LocalSystem service remained healthy. No Microsoft authentication or tenant operation ran
- direct MSI without the required runtimes failed with the actionable prerequisite message and no
  partial install; the bootstrapper then installed only the exact .NET and ASP.NET Core 10.0.11
  x64 prerequisites plus RelayBridge, requested no restart, and preserved ProgramData
- silent install/uninstall, same-version reinstall, standard repair, synthetic A-to-B upgrade,
  downgrade rejection, and intentional-failure rollback preserve ProgramData and return one exact,
  healthy execution closure with no mixed service/URI/tooling state
- focused hostile management passes 15/15, hostile cleartext SMTP AUTH passes 9/9, graceful
  shutdown passes 2/2, and isolated Production/Development hosts both return HTTP 200 for
  `/health` and `/diagnostics`
- targeted format/analyzer verification, installer PowerShell parsing, `git diff --check`, NuGet
  direct/transitive vulnerability audits for the solution and WiX projects, tracked secret/private-
  key and unsafe-TLS scans, installer log/payload privacy scans, and MSI/bootstrapper validation pass
- M7 Release build passes with 0 warnings and 0 errors; 59 unit and 398 integration
  tests pass (457 total, 0 failed, 0 skipped), including 17 new status-policy cases and
  10 new connectivity/page/bundle integration cases
- deterministic local SMTP fixtures prove the diagnostic probe's DNS/TCP stage separation,
  multiline EHLO and STARTTLS transition, trusted TLS success, untrusted-certificate rejection,
  missing STARTTLS, malformed/oversized reply, timeout, cancellation, and absence of AUTH or
  mail commands
- hostile support data containing sender/recipient addresses, subject/body/MIME markers,
  device plaintext/verifier values, token/password/auth-code-shaped setup details, and a
  private-key marker does not appear in any generated ZIP entry; the fixed schema-v1 bundle
  is path-safe and below its 1 MiB cap
- scoped M7 formatter/analyzer verification, `git diff --check`, NuGet direct/transitive
  vulnerability audit, tracked private-key/JWT-marker scan, and unsafe-TLS scan pass; the
  solution-wide formatter still reports only the two pre-existing line-ending items below
- published Production and Development hosts returned HTTP 200 for `/health` and
  `/diagnostics`; Production downloaded a 4,603-byte ZIP with the safe timestamped filename,
  and both modes shut down with the SMTP listener stopped
- the generated fresh-install support bundle was manually opened: all 11 fixed entries were
  present, all 10 JSON entries parsed, schema remained 9, connectivity/quick-check were
  honestly NotRun/Unknown, and inspection found no configuration, address, credential,
  certificate reference, message, token, private-key, database, or log data
- focused hostile management (10/10), cleartext SMTP-AUTH binding (9/9), and graceful
  shutdown (2/2) regression gates pass
- independent final verification returned `A. PASS — READY TO FREEZE M7`; manual browser
  review passed at 1280, 1024, and 768 pixels without horizontal overflow, and the
  diagnostics status text, focus treatment, STARTTLS limitation, and bundle explanation
  remained visible and understandable
- M6.1 remediation policy and integration coverage passes for hostile management
  and cleartext-AUTH bindings, attempt-scoped configuration-isolated readiness chronology, atomic
  creation/configuration TOCTOU checks, synchronized reset/reset/reset-disable/reset-edit
  interleavings, duplicate-mutation behavior, explicit multi-NIC selection, partial NIC
  failure salvage, redacted secret string/JSON output, Delivering queue state, normal-entry
  v4/v5 migrations, IPv6 loopback host filtering, and edit focus, in addition to ordered prerequisite actions,
  authenticated and Legacy fail-closed gates, undecided-mode handling, password-exit
  acknowledgement, all queue-state wording, multi-network preservation, shared
  address rendering, and corrected inline-confirmation focus semantics
- actual browser smoke rendered the fresh dashboard and printer-connectivity page;
  fresh state showed `Set up Microsoft 365`, local/printer readiness as needing
  attention, exact loopback deployment guidance, safe private candidates, and no
  false physical-printer ready claim
- M6 integration coverage passes for unique credential provisioning/no plaintext
  persistence, invalid Legacy atomic failure, reset/disable/re-enable, edit history
  preservation, dashboard activity metadata, loopback endpoint safety, and all new
  management routes
- a published Production-layout host returned HTTP 200 for dashboard, devices,
  add-device, settings, Microsoft setup, `/health`, `app.css`, `app.js`, and
  `blazor.web.js`; `/health` was Healthy and graceful shutdown completed
- the standard `dotnet run` path now uses an explicit Development launch profile;
  dashboard, CSS, JavaScript, and Blazor framework assets all returned HTTP 200,
  resolving the prior project-output Production static-asset 500 responses without
  weakening the published Production configuration
- Release build succeeds with zero warnings/errors. After cleanly stopping the installed
  `RelayBridgeNativeValidation` service and confirming its process had exited, the complete
  trusted suite passed all 42 unit and 388 integration tests (430 total), with zero failed or
  skipped. The previously intermittent real-process console test also passes 20/20 isolated runs.
- pre-freeze cleanup now revalidates the exact SYSTEM-owned, protected three-principal session
  DACL and session-bound interactive SID before recursive scratch deletion; invalid owner,
  broadened ACL, containment/name, active-lock, and reparse cases retain the directory fail safely
- the production Exchange script clears live `PSModulePath` after exact private EXO 3.9.2 import
  and again after `Connect-ExchangeOnline`, keeps module autoload disabled, revalidates the exact
  EXO `ModuleBase`, and rejects loaded `tmpEXO_*` modules outside protected session scratch
- M5.1 security tests execute strict pipe framing, malformed/duplicate/oversized and
  stalled-bootstrap rejection, actual launcher identity policy, remote-client pipe flags,
  launcher/worker/tool/manifest/ACL/reparse and between-stage tamper rejection, raw generic
  ACL-mask handling, exact execution-closure validation, exact module
  versions, executable Entra credential-set and Exchange scope/assignment policies,
  WAM-only fixed scripts, PowerShell injection resistance, strict output/noise/token-field
  rejection, session locking, helper disconnects, cancellation classification and
  host-verification cancellation, absolute PowerShell launch policy, normal schema-v7/v8/v9
  migration, candidate lifecycle/compare-and-swap isolation, activation-epoch isolation,
  deterministic cancel-vs-activate and late-result races, and conditional
  candidate-final-250 activation
- pre-CLR tests include a working positive-control startup hook, actual sanitized
  private PowerShell Entra/Exchange child processes, direct-worker confirmation rejection,
  worker parent/path/hash/session/SID policy, protected scratch ACL/containment/reparse/
  cleanup policy, inherited proxy removal and audit-listener non-contact, bounded
  launcher framing, profiler/runtime-variable exclusion, malicious `DOTNET_ROOT*`,
  and an actual NativeAOT publish/run with no managed runtime sidecars or probe marker
- the exact UI-generated standalone JSON round-trips through `System.Text.Json` and
  RelayBridge's normal configuration/options binding; a published Production host used
  that copied JSON to bind SMTP to the selected explicit private address while management
  remained loopback-only
- published Production and trusted Development hosts returned HTTP 200 for health,
  dashboard/static assets, IPv4 loopback, localhost, and direct IPv6 loopback; both
  completed graceful shutdown
- published loopback server-render timing over 20 requests measured a 1.81 ms median
  for the dashboard and 1.25 ms for the device list (maximums 3.56 ms and 1.85 ms);
  no automatic polling or message-content query was added
- M6 static/secret review found no endpoint/TLS weakening, management bind change,
  token/private-key material, plaintext persistence, browser storage, or new package;
  the NuGet direct/transitive vulnerability audit reports no vulnerable packages
- generated Entra and Exchange scripts parse in PowerShell 7 with zero parser errors
- focused real-PowerShell tests cover all six Entra provisioning failure substages,
  protocol/runtime retention, and rejection of secret-shaped optional diagnostic fields
- after the first real native Entra provisioning attempt stopped before Host acceptance,
  a separate read-only inventory classified the tenant as `A — NOTHING CREATED`: no
  matching application or service principal, certificate credential, API permission,
  or app-role assignment exists. Listener readiness now preserves the attempt's sanitized
  stage, failure category, safe code, and timestamp until a deliberate new launch starts.
- setup tests cover fresh flow, strict results/identifiers, injection attempts,
  least privilege, conflicts/idempotency, resume/back/cancel, v3 migration,
  candidate token/SMTP failure preservation, activation, and final test acceptance
- setup page server-render and public-only certificate regressions pass; interactive
  browser automation was unavailable because the app browser rejected the local URL
- isolated composed-host smoke returned HTTP 200 for `/health` and
  `/setup/microsoft` with Microsoft configuration absent, rendered the wizard, and
  shut down with the loopback listener closed
- dedicated-tenant new-application flow passed separate Entra and Exchange
  PowerShell 7 stages, zero API permissions, valid service-principal/group/scope/
  role objects, allowed sender in scope, and denied control mailbox out of scope
- the corrected production-native launcher/worker path created and exactly verified its
  separate production Entra application/service principal and Exchange RBAC objects. Its
  first immediate SMTP verification returned `AUTH 535` while the newly created Exchange
  authorization propagated; one deliberate retry after the propagation interval completed
  token acquisition, STARTTLS, XOAUTH2, sender authorization, and final SMTP `250`, then
  atomically activated the unchanged candidate with a fresh activation epoch
- post-activation validation confirmed the out-of-scope control sender acquired an
  application token and established STARTTLS but was denied at `AUTH 535`; no control message
  was accepted. After service restart, the active configuration and activation epoch persisted,
  an independent authorized-sender check again reached final SMTP `250`, and the Host correctly
  returned to process-local `Verification required` readiness
- no MFA challenge was observed during the production-native Entra or Exchange sign-ins;
  Microsoft broker SSO completed without one, so MFA enforcement is not claimed by this run
- a deliberately unregistered pre-activation candidate failed token acquisition and
  left the previously active identity unchanged
- the validated candidate acquired an Exchange token, completed normal STARTTLS and
  XOAUTH2, authorized the configured sender, received final SMTP `250`, and only then
  became active; the wizard test message received a separate final `250`
- after composed-host restart, `/health` and the completed setup page returned 200;
  a fresh process loaded the activated identity and again passed token, STARTTLS,
  XOAUTH2, sender authorization, and final SMTP `250`
- .NET SDK 10.0.400; Windows x64 runtime 10.0.11 used for build/tests
- Release build succeeds with zero warnings and zero errors
- 42 unit tests pass
- 146 integration tests pass (188 total with 42 unit tests, zero failed or skipped),
  including final-response delay/timeout, acceptance-boundary telemetry, immediate-
  resend prevention, and Exchange `554 5.2.270` size classification
- parser tests cover multiline capability replies, malformed/bare-LF/mixed-code
  replies, and line/count/total-size bounds
- TLS tests cover required capability, trusted test upgrade, normal validation
  rejecting the untrusted certificate, server close during handshake, mandatory
  post-TLS EHLO, and post-TLS XOAUTH2 absence
- OAuth tests prove the exact fake `user=<envelope sender>\x01auth=Bearer
  <token>\x01\x01` form, 334/235 success, 535 rejection, provider failure,
  cancellation, and token/Base64 log redaction
- envelope/SIZE tests cover MAIL rejection, SIZE larger/equal/smaller/omitted,
  single/multiple recipients, 4xx/5xx recipient outcomes, all-recipient acceptance,
  and no DATA on any recipient failure
- DATA/integrity tests cover normal and multipart MIME, quoted-printable and
  base64 PDF-like text, leading one/two dots, empty input, trailing/non-trailing
  CRLF, DATA rejection, final 4xx/5xx, cancellation, and disconnects before,
  during, and after DATA
- explicit final-boundary tests prove close before final response is ambiguous and
  final `250` followed by close before QUIT remains success
- real Exchange results persist through the existing worker as Delivered,
  RetryScheduled, or PermanentFailure; ambiguous results retry; prior tests retain
  result-update-failure recovery, missing-payload short-circuit, no DB lock during
  delivery, bounded concurrency, and payload-cleanup ordering
- local Release fake-server measurements (includes loopback TLS and test-server
  processing; managed values are post-forced-GC heap deltas, not peak memory):
  - 1 MiB: 28.88 ms, 34.63 MiB/s, +1,050,280 managed bytes
  - 10 MiB: 215.40 ms, 46.43 MiB/s, +13,688 managed bytes
  - 25 MiB: 1,014.53 ms, 24.64 MiB/s, +1,409,104 managed bytes
- fixed-size buffers and size-independent post-GC deltas confirm no whole-message
  allocation in the provider; existing three-worker 3 x 10 MiB queue test remains
  passing
- current Microsoft SMTP App RBAC, SMTP OAuth/XOAUTH2, SMTP AUTH, and authenticated-
  submission throttling guidance was rechecked; no architecture change was required
- a credential-free live check of the fixed production endpoint passed DNS,
  TCP/587, `220`, EHLO, STARTTLS advertisement and `220`, normal platform TLS
  chain/hostname validation, post-TLS EHLO, XOAUTH2 advertisement, SIZE
  advertisement, and QUIT; no AUTH command was sent and no token was acquired
- the Git-ignored tenant file's exact `CurrentUser\My` certificate reference was
  found and usable; certificate-backed Exchange token acquisition passed without
  requesting, exporting, or logging private-key material
- dedicated-tenant SMTP validation passed normal STARTTLS, post-TLS XOAUTH2,
  allowed-mailbox AUTH and MAIL authorization, final acceptance of a standards-valid
  small message, and final acceptance for two recipients in one transaction
- the same application, certificate, and token model failed closed for the mailbox
  outside the Exchange App RBAC resource scope: TLS and token acquisition passed,
  XOAUTH2 was rejected, and neither sender authorization nor DATA acceptance occurred
- a deterministic streamed 10 MiB MIME message reached final Exchange acceptance
- the original ambiguous 25 MiB message was not retried; its missing telemetry remains
  unrecoverable and no claim is made about its remote acceptance
- the first newly identified 26,214,400-byte message failed safely before acceptance:
  9,895,936 bytes were read in 230.570 seconds, spool EOF and terminator were not
  reached, no final wait began, the result was transient `Network`, and the payload
  remained present
- a second fresh 26,214,400-byte message over the earlier network path reached spool
  EOF after 377.515 seconds, flushed the terminator, received final `554 5.2.270`
  after 1.089 seconds, persisted PermanentFailure, and retained the payload
- that live response confirmed the provider's prior generic permanent classification
  was too broad; focused regression coverage now maps `552` and Exchange
  `554 5.2.270` to permanent `MessageTooLarge` without another real submission
- external standalone validation outside RelayBridge proved the same tenant identity,
  normal TLS, XOAUTH2, allowed/denied RBAC behavior, advertised SIZE 157,286,400,
  manual receipt of the small message, and IPv4-only acceptance of a fresh
  26,214,400-byte message in 3.07 seconds at 8.145 MiB/s
- that validator measured its default IPv6/vEthernet 10 MiB DATA path at 158.39
  seconds (0.063 MiB/s), versus 0.98 seconds (10.173 MiB/s) in an IPv4-only process;
  this is recorded as a developer-environment observation, not a product default
- the required final actual RelayBridge production-path run set
  `DOTNET_SYSTEM_NET_DISABLEIPV6=1` for its process and used the real certificate
  loader, MSAL token provider, Exchange provider, durable queue, a new Message-ID,
  and a new exact 26,214,400-byte MIME spool
- that run passed DNS/TCP, greeting `220`, STARTTLS `220`, normal TLS, token
  acquisition, XOAUTH2 `235`, MAIL `250`, RCPT `250`, and DATA `354`; all
  26,214,400 bytes reached EOF and the terminator flush completed in 2.752 seconds
  at 9.083 MiB/s
- Exchange returned final `554 5.2.270` after 0.490 seconds; RelayBridge returned
  permanent `MessageTooLarge`, persisted PermanentFailure, retained the payload,
  and completed the one queue attempt in 4.432 seconds with no exception/socket error
- a separately authorized fixture-matched production-path run used strict-CRLF
  `multipart/mixed` MIME with a small text part and a base64 synthetic binary
  attachment; the exact 26,214,400-byte spool represented 19,156,143 decoded
  attachment bytes and used a new Message-ID
- that run used `DOTNET_SYSTEM_NET_DISABLEIPV6=1`, the normal durable spool/queue,
  Windows-store certificate, MSAL token provider, and actual Exchange provider;
  MAIL `250`, RCPT `250`, and DATA `354` passed, all 26,214,400 bytes reached EOF,
  and the terminator flush completed after 4.102 seconds of DATA at 6.093 MiB/s
- Exchange returned final `250 2.0.0` after 0.719 seconds; RelayBridge returned
  Success, persisted Delivered, marked the payload absent, deleted the spool file,
  and completed the queue attempt in 6.039 seconds
- a new non-exportable temporary `CurrentUser\My` certificate was rejected by
  Microsoft token acquisition with the credential-rejected classification; cleanup
  removed only that temporary certificate/CNG key and preserved the working certificate
- the dedicated Entra application had zero API permission entries, Security Defaults
  was disabled, and the isolated SMTP-AUTH-disable experiment was inconclusive; tenant
  settings were restored and that experiment is not treated as a freeze blocker
- tracked-file identifier comparison, tracked secret scan, local private-key-file
  scan, and local-config ignore verification were clean
- the wrong-certificate runner emitted only sanitized PASS/FAIL fields; no real-test
  log/trace files were retained, and the final workspace scan found no token/JWT,
  credential assignment, tenant-identifier, private-key marker, or non-SDK key file
- after negative-test cleanup, `CurrentUser\My` returned to its two-certificate
  baseline with exactly one private-key-bearing match for the working reference
- targeted formatting/style/analyzer verification for every file changed by the low-risk closure,
  `git diff --check`, NuGet direct/transitive vulnerability audit, static security/secret scans, and composed
  host smoke verification pass; the isolated host returned health 200, durably
  queued a synthetic local SMTP message with Microsoft configuration absent, and
  shut down with both listeners closed
- solution-wide format verification still reports pre-existing mixed LF/CRLF lines in
  `ExchangeWamConsoleLease.cs` and `PowerShellProcessRunner.cs`; neither file belongs to this
  closure diff, so they were not mass-reformatted
- the final finite public-repository audit verified all reachable public commits use the expected GitHub
  noreply identity, checksum-verified Gitleaks 8.30.1 found no current-tree or reachable-history
  leaks, NuGet reported no vulnerable direct or transitive solution/WiX packages, the Release build
  passed with 0 warnings/errors, all 483 tests passed with no skips, and the unsigned installer plus
  SBOM/notices validation passed; no binary was published or signed

## Known issues

- WiX Toolset 6 OSMF/license compliance is closed for the current individual,
  non-revenue-generating use. It must be rechecked if use becomes revenue-generating. This is a
  release-compliance record, not legal advice.

- Connectivity diagnostics prove only DNS/TCP/SMTP/STARTTLS/TLS reachability at the time of
  the explicit probe. They do not authenticate, authorize a sender, submit a message, or prove
  inbox delivery. Microsoft runtime readiness remains the authoritative current-process
  identity/Exchange evidence.
- The support ZIP contains intentionally non-secret operational metadata such as versions,
  timestamps, listener/network metadata, counts, storage capacity, and safe status summaries.
  Administrators are told to review it before sharing. RelayBridge does not upload it.

- Native setup is initially supported only for Windows 10/11 with one locally
  interactive administrator. Windows Server, RDP, Server Core, and simultaneous-user
  native setup are not claimed.
- M8 now packages the installer-owned private PowerShell/tooling tree, sidecar-free NativeAOT
  launcher, framework-dependent managed-worker closure, complete exact Graph/Entra/Exchange
  trees, custom protocol, helper/tooling manifests, and Host trust anchors. It authors protected
  ProgramData/SetupScratch SDDL and relies on the standard protected Program Files hierarchy;
  effective installed owner/ACL/delete-child/reparse behavior, real non-admin write denial, and
  frozen verifier acceptance now pass on the disposable Windows 11 VM. There is no alternate-
  version, Gallery, PATH, or system/user module fallback. Self-contained single-file worker
  publishing was rejected for this boundary because native runtime extraction would
  introduce a user-writable `%TEMP%\.net` execution path unless separately hardened.
- Exact installed handle-table enumeration was not added to the disposable VM. Strongest
  behavioral evidence shows the worker has no bootstrap-pipe implementation, only the intended
  redirected launcher channels cross, killing the launcher closes its Job and removes worker/pwsh,
  and the Host pipe cannot be reused by direct worker execution. No concrete inheritable Host-pipe
  handle defect was observed.
- Current development MSI/bootstrapper artifacts are unsigned. Public release signing support is
  implemented, but `PUBLIC RELEASE SIGNING GATE — OPEN` remains until a trusted code-signing
  certificate and signed-package verification are available.
- The exact Microsoft Graph 2.25.0 Gallery packages declare `RequireLicenseAcceptance=true` and
  `https://aka.ms/devservicesagreement` but omit the `license.txt` described by Gallery publishing
  guidance. M9.1 does not invent that file and no longer redistributes Graph/Entra bytes; written
  Microsoft redistribution and missing-license-file clarification remain open. ExchangeOnlineManagement
  3.9.2 and PowerShell 7.6.4 retain their reviewed M8 treatment.
- Production-native setup has now run end-to-end against the dedicated test tenant, including
  exact assignee-wide role inventory, strict returned scope-filter representation, authorized
  final SMTP `250`, atomic activation, and native-path negative out-of-scope authorization.
  No MFA challenge was presented; representative Conditional Access and actually observed MFA
  remain environment-dependent validation evidence rather than claims of this run. The Advanced
  manual workflow remains available.
- Newly created Exchange application authorization can transiently return SMTP `535` before
  propagation completes. Setup remains fail closed, and its guidance now asks administrators to
  wait briefly and retry after fresh setup before diagnosing persistent tenant/mailbox SMTP AUTH.
- The parameter-free custom-URI launcher can still be invoked by another local page. It
  carries no authority, the service/launcher identity checks and native confirmation remain
  authoritative, and a launch-intent nonce is deferred as low-risk prompt-spam hardening.
- The standalone lab identified and proved the production Entra compatibility corrections:
  certificate hash/DER fields must be Base64 strings, and Microsoft.Entra 1.3.0 permission
  projections are not a stable authoritative read-back shape under StrictMode. Production
  applies those exact corrections, and the completed production-native run validated them.

- Inbound STARTTLS remains unavailable. The device wizard therefore refuses to
  describe authenticated setup as ready while cleartext AUTH is disabled; enabling
  LAN binding and cleartext AUTH remains an explicit trusted-network decision.
- Default SMTP loopback binding is intentionally unusable by a physical printer.
  The wizard reports this plainly, provides safe active private-address candidates,
  requires explicit choice when several exist before generating exact deployment
  configuration, and requires a restart. Runtime listener
  reconfiguration remains intentionally absent because options are startup-bound.
- Device deletion is intentionally absent because queue/history retention semantics
  require a later explicit product decision. Disabling a device rejects new sessions
  while retaining its configuration and history.
- The setup test observes durable queue metadata on explicit “Check now”; live SMTP
  session timelines and automatic event streaming belong to Milestone 7.
- The fresh M5 validation resources remain in the dedicated test tenant pending
  explicit administrator cleanup. RelayBridge never deletes cloud resources; the
  manual cleanup order is documented and identifiers remain only in ignored local
  validation files.
- Management remains code-enforced loopback-only without a production remote-
  management authentication model. Broader authenticated/remote management remains later
  roadmap work; the Windows installer and its local trust boundaries are already frozen.
- The normal setup test directly exercises the candidate M4 provider so a failed
  candidate cannot replace active settings. Durable device-queue provisioning is
  owned by M6; the wizard does not create a hidden device merely for onboarding.
- The original validator did not instrument or retain enough evidence to determine bytes
  read/written, spool EOF, terminator/flush completion, final-response wait entry, the
  exact exception/socket error, exact elapsed duration, or queue state. Its result was
  `AmbiguousAcceptance`; queue state is not applicable because it called the provider
  directly rather than delivering a durable queue item. That original message remains
  unretired and was never resent.
- Exchange rejected an exact 26,214,400-byte synthetic-text MIME fixture with final
  `554 5.2.270`, while it accepted an exact-size realistic multipart/base64 scan
  fixture through the same actual RelayBridge production path. This is consistent
  with documented content-conversion/encoding overhead; RelayBridge therefore does
  not infer a universal tenant limit from mailbox MaxSendSize. The rejection remains
  a permanent `MessageTooLarge` result, and neither Message-ID was resent.
- SMTP final acceptance passed for the small, multiple-recipient, and 10 MiB tests;
  manual receipt of the small real message is confirmed.
- The SMTP-AUTH-disable experiment was inconclusive after settings propagation and
  tenant settings were restored. The positive SMTP AUTH path and negative RBAC scope
  remain proven; this isolated experiment is recorded but is not a freeze blocker.
- Authentication/configuration errors use bounded queue retries with a five-minute
  provider hint; no separate suspended/configuration queue state was added.
- V1 is all-or-nothing before DATA and has no recipient-level delivery state.
- Inbound SMTP does not advertise SMTPUTF8 or 8BITMIME. Typical scan MIME should be
  transfer-encoded; unsupported international envelopes are rejected, while
  nonconforming unencoded 8-bit content is not upgraded or rewritten.
- Exchange documents per-mailbox throttles including three concurrent connections,
  30 messages/minute, and 10,000 recipients/day. The default worker concurrency is
  one and 4xx responses back off, but no proactive per-mailbox rate window exists.
- A crash after final Exchange acceptance but before local Delivered persistence,
  or an ambiguous lost final response, can duplicate mail. Exactly-once delivery
  remains impossible.
- Inbound STARTTLS, permanent-failure retention controls, and sustained authentication-abuse
  throttling remain earlier known work.

## Deferred

- live per-session SMTP timelines/history, printer discovery, remote management, inbound
  STARTTLS provisioning, and later M10 release-candidate work
- Windows Server/RDP installer support, Server Core, x86/Arm64, arbitrary installation paths,
  automatic application-data removal, tenant cleanup, and installer-managed firewall rules
- certificate renewal/rotation UI beyond reuse of the same repair wizard
- Graph delivery/fallback, generic SMTP hosts, alternative OAuth providers,
  multiple tenants, connection pooling, PIPELINING, recipient-level queue state,
  and distributed/high-availability architecture
- polished message-history/troubleshooting UI and safe device-deletion semantics

## Next step

M9 remains frozen. The public source repository, fresh history, GitHub noreply identity, main-branch
ruleset, and public-repository security controls are in place. Confirm GitHub MFA remains enabled
and send the narrow SignPath first-release inquiry. Do not begin M10 automatically. SignPath,
Authenticode, and written Microsoft redistribution/missing-license-file clarification remain open;
external Windows binary publication remains prohibited.
