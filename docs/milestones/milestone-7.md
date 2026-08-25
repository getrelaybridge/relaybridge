# Milestone 7 — Diagnostics and Supportability

## Status

**FROZEN.** Independent final verification returned `A. PASS — READY TO FREEZE M7`.
Schema remains v9. This milestone does not add telemetry, remote management, tenant
mutation, test-message delivery, or installer work.

## Objective

Give a local administrator an honest, privacy-preserving view of RelayBridge's current
operational state and a small support artifact that can be reviewed before sharing. A
diagnostic claim must identify what evidence produced it and must not turn configured intent
or historical success into a live-health assertion.

## Diagnostics page

`/diagnostics` is part of the existing code-owned loopback management application. It reports
the explicit states Healthy, Needs attention, Unavailable, Not configured, and Unknown. Every
card identifies one of these evidence sources:

- Runtime — current-process state such as listener and worker liveness.
- Configuration — validated configured intent or a documented fixed boundary.
- Persisted state — current SQLite aggregate/state.
- Last verification — completed current-process Microsoft evidence.
- Active probe — the most recent explicit administrator-triggered check.

The page includes service/runtime, actual management and SMTP bindings, intake limitations,
enabled-device and queue aggregates, worker state, Microsoft configuration/readiness,
certificate validity and signing access, sanitized native-setup state, database/storage,
Exchange connectivity, and security-boundary summaries. It does not show message content,
recipients, senders, device credentials, tokens, private keys, tenant/client/object IDs, or
full certificate references.

## Explicit checks

The Exchange Online connectivity action has a single 15-second budget. It resolves
`smtp.office365.com`, connects to TCP 587, requires an SMTP 220 greeting, sends EHLO, requires
STARTTLS advertisement, sends STARTTLS, and completes a hostname/certificate-validated TLS
handshake. It does not acquire an OAuth token and cannot send AUTH, MAIL FROM, RCPT TO, DATA,
or message content. Stage-specific bounded results distinguish DNS, TCP, greeting, EHLO,
STARTTLS, TLS, timeout, cancellation, and success.

The database integrity action runs bounded SQLite `PRAGMA quick_check` only after an explicit
administrator request. Normal page rendering performs lightweight read-only state queries.

## Support bundle

RelayBridge builds the ZIP locally in memory and does not upload it. Bundle schema v1 has a
fixed filename list:

- `README.txt`
- `manifest.json`
- `runtime.json`
- `smtp.json`
- `queue.json`
- `microsoft.json`
- `certificate.json`
- `setup.json`
- `storage.json`
- `connectivity.json`
- `security.json`

The compressed bundle is capped at 1 MiB. Each JSON document is an anonymous allowlisted
projection; runtime configuration/domain objects are not serialized. The bundle excludes raw
configuration, SQLite files, logs, spool/message data, subjects, bodies, attachments, sender
and recipient addresses, device names/usernames/passwords/verifiers, OAuth/admin tokens,
authorization codes, client secrets, private keys/PFX/PEM, raw certificates,
tenant/client/service-principal/mailbox identifiers, full certificate references, environment
or command-line dumps, and PowerShell output. It retains versions, timestamps,
status/provenance, counts, capacity, listener/network metadata, and safe bounded summaries,
which administrators must review before sharing.

## Status policy

Required local runtime, SMTP, queue, storage, and security failures determine unavailable or
attention states. An unconfigured Microsoft identity is Not configured rather than a false
runtime failure. Once configured, certificate usability and current activation-scoped
Microsoft readiness participate in overall health. A connectivity check that has never run
is Unknown and does not make otherwise healthy configured runtime unavailable; a failed
explicit check is attention. Historical Microsoft success after restart is not treated as
current readiness.

## Verification

Automated coverage exercises status policy, actual host-route rendering, action endpoints,
database quick check, listener/worker/configuration separation, deterministic SMTP probe
success and stage failures, STARTTLS/TLS validation, timeout/cancellation and protocol bounds,
fixed ZIP entries and maximum size, hostile support-data fixtures, and absence of disallowed
content. Release build, full tests, formatting/analyzers, diff hygiene, dependency/security
scans, production/development smoke, hostile management and SMTP-AUTH startup checks, graceful
shutdown, and manual browser acceptance all passed. The verified totals are 59/59 unit tests,
398/398 integration tests, and 457/457 overall, with zero failures and zero skips. The Release
build completed with zero warnings and zero errors; the NuGet vulnerability audit reported
zero vulnerable packages. Hostile management passed 10/10, hostile cleartext SMTP AUTH passed
9/9, graceful shutdown passed 2/2, Production and Development diagnostics smoke passed, and
manual browser review passed at 1280, 1024, and 768 pixels. Full release evidence is recorded
in `BUILD_STATUS.md`.

## Deferred

Live per-session SMTP timelines, remote support/upload, telemetry, message-history tooling,
printer discovery, and installer packaging are not part of Milestone 7.
