# Milestone 6.1 — Focused Device UX Correction

## Scope

Milestone 6.1 corrects normal-use blockers found in the frozen Milestone 6 device
experience. It does not add SMTP history, live diagnostics, inbound STARTTLS, or
new listener/configuration architecture.

Independent adversarial audits found implementation gaps. The readiness-provenance
remediation addresses the final audit's overlapping-operation findings after the earlier
chronology, concurrency, configuration, serialization, NIC, IPv6 loopback, and test-quality
corrections. Implementation is complete, but Milestone 6.1 remains intentionally unfrozen
until a focused independent verification audit passes.

## Readiness flow

The dashboard now guides a fresh administrator in dependency order:

1. configure Microsoft 365;
2. prepare printer connectivity when the SMTP listener is not LAN-usable;
3. add a device only after Microsoft and printer prerequisites are ready.

Microsoft identity/delivery, LAN listener reachability, SMTP authentication, and
inbound TLS are separate states. Loopback-only SMTP never produces a physical-
printer ready claim. The dashboard and completed setup page show Verification required
after restart. Identity and Exchange operations retain an immutable attempt ID, captured
configuration, non-secret fingerprint, and start sequence. Only completed evidence can
establish readiness, and one process-wide completion sequence deterministically selects
the newest identity or Exchange outcome for the current active fingerprint. Overlapping
attempts cannot transfer outcomes between configurations; activating a replacement
configuration therefore leaves Verification required until that configuration completes
an authoritative operation.

The listener is composed from startup-bound options and starts once. Safe runtime
mutation would require persistence and restart coordination that does not exist.
M6.1 therefore provides the smaller deployment path: the current bind address and
port, all safe active private-address candidates, complete standalone configuration
after explicit selection, restart instruction, and an explicit trusted-private-LAN warning. It never
recommends a public address or wildcard binding, never chooses the first virtual/private
adapter, and fails safely when interface enumeration is unavailable.

Management HTTP is now bound to loopback in composition code. Non-loopback generic
URL or Kestrel endpoint configuration fails startup. The SMTP surface is separate;
cleartext AUTH requires one explicit RFC1918/ULA listener and rejects wildcard,
loopback, link-local, multicast, and public addresses.

## Device safety and clarity

Authenticated device creation requires usable Microsoft readiness, LAN listener
reachability, and authenticated SMTP intake. Legacy remains an explicit, fail-
closed choice with the existing private-source restrictions. Selecting “I'm not
sure” shows printer-setting guidance and does not choose Legacy.

Immediately before create, the wizard re-reads Microsoft sender/runtime readiness,
listener authentication, and LAN interface advice. The device insert then verifies
the reviewed active-configuration fingerprint and sender inside the same SQLite
transaction. A changed review fails closed and creates no device.

New and reset passwords remain transient. Before Test, Finish, reset-again, or a
controllable navigation that would discard plaintext, the administrator must
confirm that the password was saved. Refresh still loses plaintext by design; a
lost password must be reset. Printed instructions exclude the password and say so.

Printer-facing setup now says `Connection security: None`, tells the administrator
to disable SSL/TLS and STARTTLS only for the trusted Printer-to-RelayBridge LAN
connection, and keeps outbound Exchange TLS/OAuth clearly distinct. Setup and
details reuse the same usable-address display. If several private addresses exist,
both show the same list and ask the administrator to choose the one reachable by
the printer.

Device editing presents and preserves every allowed network rule and warns that
changes take effect immediately. Existing queued messages are not rewritten.
Schema v6 adds a minimal device revision. Configuration edit, password reset, disable,
and enable each compare-and-swap and advance that revision. A concurrent reset has
exactly one winner; its losing plaintext is never returned. Narrow updates modify only
their intended columns, so stale operations cannot restore old network, enabled, or
credential state. Mutation handlers reject duplicate queued events.
Queue-backed Check Now wording distinguishes local acceptance, waiting, retry,
permanent rejection, and Microsoft acceptance without introducing Milestone 7
diagnostic correlation.

Reset and disable confirmations remain inline. Incorrect modal/alertdialog claims
were removed, and focus moves into the confirmation then returns to its trigger.
Edit now follows the same enter/restore focus behavior. Secret-bearing result and
device output is redacted from `ToString()`, debugger display, and default JSON
serialization, including nested serialization. Queue Delivering is active work, not Clear.

## Verification

Focused tests cover hostile HTTP/SMTP configuration, deterministic completion chronology,
captured-configuration token acquisition, overlapping Exchange and identity attempts,
cross-source ordering, configuration replacement, transaction-bound creation TOCTOU, genuinely synchronized
reset/reset/reset-disable/reset-edit operations, duplicate-mutation behavior, virtual-
first and per-adapter failed NIC discovery, secret string/JSON redaction, active Delivering
state, edit focus, IPv6 loopback host filtering, and normal-initialization v4/v5 migrations,
plus the prior device UX cases. The full Release suite passes with 42 unit and 248
integration tests (290 total, zero failed or skipped).

Release build and formatting complete with zero warnings/errors. `git diff --check`,
NuGet direct/transitive vulnerability audit, tracked static/secret/private-key review,
hostile startup checks, and published composed-host health/static-asset/graceful-
shutdown smoke pass.

The administrator-facing SMTP configuration is a complete standalone JSON document.
Tests parse it, bind it through the same .NET configuration path as RelayBridge, and
prove it changes only the SMTP listener. A published Production smoke used the copied
document on an explicit developer-owned private address; management remained loopback.
Production and Development smoke accepted IPv4 loopback, localhost, and IPv6 loopback.

## Deferred

Inbound STARTTLS, runtime listener reconfiguration, device discovery, live SMTP
events/history, queue drill-down, log viewing, and diagnostic bundles remain out of
scope. Those constraints are not hidden by the UI.
