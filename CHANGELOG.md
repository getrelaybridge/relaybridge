# Changelog

All notable changes will be documented here.

## Unreleased

- M10 prepares the unsigned `v1.0.0-rc.1` public evaluation candidate without creating the tag or
  release. It adds the RC warning and administrator guides, versioned release notes, a selectable
  external artifact root, an exact public release allowlist, path-free source/package provenance,
  and deterministic SHA-256 checksums. Product, protocol, Microsoft, queue, and security behavior
  are unchanged.

- RP-1 prepares a clean fresh-history MPL-2.0 public source snapshot, adds a GitHub-hosted unsigned
  release-candidate workflow and SignPath Foundation pre-application policy, closes WiX v6.0.2
  compliance for the current non-revenue status, preserves the full MS-RL notice/source reference,
  and distinguishes incorporated WiX runtime components from excluded build tooling in CycloneDX.

- Milestone 5.1 adds a sidecar-free NativeAOT `RelayBridge.SetupLauncher` as the only
  desktop process authenticated by the Host, plus a subordinate managed
  `RelayBridge.Setup` worker over inherited standard handles. The launcher creates an
  explicit child environment that excludes .NET startup-hook, profiler, diagnostics,
  dependency-store, and runtime-redirection controls; the worker apphost searches only
  the trusted global .NET installation and applies the same policy to separate private
  Entra/Exchange
  PowerShell processes using WAM, fixed idempotent provisioning operations, deterministic
  raw-generic-aware ACL/reparse/tree/hash/version validation, complete pinned helper
  execution-closure verification before launch and connection, tooling revalidation
  before every stage, Job Object cleanup,
  strict bounded sanitized results, schema-v9 authoritative candidate lifecycle and
  compare-and-swap plus activation epochs, exact Entra certificate/secret policy,
  exact Exchange group/scope/assignee-wide role policy with wrong-assignee rejection,
  and automatic M3/M4 verification before conditional atomic activation. The hardened
  first-instance pipe rejects remote clients and binds the native launcher's
  kernel-reported process-token SID to the impersonated pipe SID. A kill-on-close Job
  Object owns the managed worker and private PowerShell descendants. The Host now creates
  protected per-session ProgramData scratch after launcher authentication; worker and both
  PowerShell stages replace inherited `TEMP`/`TMP`, EXO is pinned to 3.9.2 and receives
  `-EXOModuleBasePath`, and raw proxy variables are removed. The worker also verifies its
  actual launcher parent/path/hash/session/SID before confirmation. The manual workflow
  remains Advanced recovery. The Entra stage now explicitly pins and imports only
  Microsoft.Graph.Authentication and Microsoft.Graph.Applications 2.25.0 before the
  Entra 1.3.0 wrappers, verifies canonical module and command identity, locks reconstructed
  module discovery, and passes an import-only private-PowerShell preflight before WAM.
  Exchange now uses a short-lived interactive console lease for Microsoft's default WAM:
  Entra remains hidden, Exchange private PowerShell inherits the verified console while
  all standard streams stay redirected, child attachment is proven before the fixed
  script is sent, and cancellation/exit drains output before an owned console is freed.
  No `-DisableWAM`, Device Code, or administrator-token handoff was added.
  Real WAM validation, real-tenant validation, and a focused independent adversarial audit remain pending.

- Milestone 0 foundation established.
- Milestone 1 durable, authorized, streaming SMTP intake implemented.
- Milestone 2 restart-safe queue state, reconciliation, retry scheduling,
  capacity admission, bounded workers, health, and graceful shutdown implemented.
- Milestone 2 correctness audit hardened fail-safe orphan ownership checks,
  reservation cleanup, result-update recovery, retry-age boundaries, and schema
  migration versioning/rollback verification.
- Milestone 3 certificate-based Microsoft Entra application identity added with
  MSAL.NET, Windows certificate-store references, public-only certificate export,
  safe diagnostics, and non-secret SQLite configuration. Outbound Exchange SMTP
  remained intentionally absent at that milestone.
- Milestone 4 Exchange SMTP OAuth delivery added with fixed production endpoint,
  mandatory validated STARTTLS, XOAUTH2, RFC 1870 SIZE, all-or-nothing recipients,
  raw MIME streaming/dot transparency, bounded response parsing/timeouts,
  stage-aware queue outcomes, ambiguous final-acceptance handling, readiness
  diagnostics, and a scripted STARTTLS integration server. Real-tenant RBAC
  validation remains pending.
- Milestone 4 freeze audit added a sanitized real-tenant validation checklist and
  regression coverage for rejecting null/empty envelope senders before Microsoft
  authentication or delivery. Dedicated-tenant validation passed certificate-backed
  token acquisition, Exchange STARTTLS/XOAUTH2, allowed-mailbox and multiple-recipient
  final acceptance, negative RBAC mailbox rejection, and streamed 10 MiB acceptance.
  An unregistered temporary certificate was safely rejected and removed, and zero
  Entra API permission entries were confirmed. The original ambiguous 25 MiB message
  was not retried. A separate 10-minute DATA-termination response timeout and
  sanitized progress telemetry were added; a fresh 25 MiB run reached final Exchange
  `554 5.2.270`, and that response now maps to `MessageTooLarge`. External IPv4-only
  validation accepted 25 MiB and identified the developer IPv6/vEthernet path as the
  upload slowdown. A final fixture-matched actual RelayBridge IPv4-only attempt sent
  an exact 25 MiB strict-CRLF multipart/base64 synthetic scan, received Exchange final
  `250`, persisted Delivered, and removed the spool payload. The earlier synthetic-
  text `554` is retained as conversion/size-policy evidence and regression coverage;
  Milestone 4 is frozen.
- Milestone 5 adds a loopback Blazor Microsoft 365 setup wizard that reuses the
  frozen certificate/token/Exchange transport. It generates separate Entra and
  Exchange PowerShell 7 stages, enforces zero Entra mailbox permissions and scoped
  `Application SMTP.SendAsApp`, strictly parses sanitized results, preserves active
  configuration while validating candidates, resumes safe progress, and sends its
  test message through the real M4 provider. RelayBridge neither receives Microsoft
  administrator credentials nor executes the scripts. Automated implementation is
  complete. Dedicated-tenant validation created a fresh side-by-side candidate,
  proved zero Entra API permissions and positive/negative Exchange scope, required
  final SMTP `250` before activation, sent the wizard test message, and revalidated
  the active configuration after restart. Candidate-scoped object names were added
  after the original global name correctly exposed a safe-replacement collision;
  Milestone 5 is frozen.
- Milestone 6 adds a calm, responsive device dashboard, searchable device list,
  guided compatible/legacy setup, actual-listener setup values, one-time password
  handling, device edit/reset/disable controls, and a simple queue-backed setup test.
  SQLite schema v5 adds an optional device description while preserving queue and
  delivery history. Device matching, inbound SMTP, Microsoft identity, Exchange
  transport, and queue semantics remain unchanged. A standard Development launch
  profile also makes the documented local `dotnet run` path serve Blazor static
  assets correctly; published execution remains Production.
- Milestone 6.1 corrects the device setup dependency flow, adds exact trusted-LAN
  printer-connectivity preparation without runtime listener reconfiguration, gates
  authenticated creation on real readiness, makes Legacy an explicit choice,
  protects one-time passwords before exit, preserves multiple network rules, aligns
  setup/detail server addresses and queue-state wording, and fixes inline
  confirmation focus semantics. No Milestone 7 diagnostics or inbound TLS were added.
- Milestone 6.1 adversarial remediation enforces code-owned loopback management,
  rejects wildcard/public cleartext SMTP AUTH, makes restart readiness truthful,
  revalidates creation prerequisites at save, adds schema-v6 optimistic concurrency
  plus narrow security updates, guards duplicate UI mutations, requires explicit
  multi-interface selection, redacts secret-bearing string output, counts active
  Delivering work, handles NIC discovery failure safely, and completes edit focus.
  Implementation is complete but remains unfrozen pending an independent re-audit.
- Milestone 6.1 final audit remediation makes Microsoft readiness chronological and
  scoped to the active configuration, makes every device security mutation revision-
  guarded (including concurrent resets), verifies sender/configuration inside the
  device-insert transaction, emits complete round-tripped deployment JSON, excludes
  one-time secrets and verifiers from default JSON/debugger output, salvages valid NICs
  when another adapter fails, permits IPv6 loopback host filtering, and replaces weak
  concurrency/migration/listener assertions with production-path tests. It remains
  unfrozen pending a final independent audit.
