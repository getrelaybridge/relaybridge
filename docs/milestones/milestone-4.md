# Milestone 4 — Exchange SMTP OAuth Delivery

## Goal

Deliver the authoritative raw queue spool through Exchange Online authenticated
SMTP with certificate-backed application tokens, mandatory STARTTLS, XOAUTH2,
and Exchange Application RBAC.

## Implemented

- one fixed-production-endpoint connection per attempt; no pooling or Graph path
- bounded multiline SMTP response parser and explicit protocol stages
- required STARTTLS with normal platform chain, hostname, validity, and SNI
  behavior; capabilities are reacquired after TLS
- Exchange token acquisition immediately before challenge-form XOAUTH2, with
  credential byte buffers zeroed and all logging redacted
- XOAUTH2 mailbox and MAIL FROM both use the durable envelope sender
- RFC 1870 SIZE advertisement use and preflight rejection without a hardcoded
  tenant limit
- multiple recipients with V1 all-or-nothing pre-DATA semantics
- 64 KiB raw-spool streaming, outbound dot-stuffing, and exact DATA termination
- final `250` acceptance boundary, best-effort QUIT, and explicit ambiguous
  post-DATA retry classification
- separate 30-second generic command and 10-minute post-DATA final-response
  timeouts, with sanitized byte/EOF/terminator/final-response telemetry
- stage-aware network/TLS/auth/sender/recipient/size/server/protocol outcomes wired
  into the existing bounded queue worker
- safe delivery readiness snapshot and explicit test-message diagnostic service;
  `/health` remains local queue health
- worker startup gate for missing/unusable local Microsoft identity while local
  intake and reconciliation remain available

## Automated validation

The local fake Exchange server exercises greeting, multiline EHLO, STARTTLS,
certificate validation, post-TLS EHLO, XOAUTH2, envelope commands, DATA, final
acceptance, RSET, QUIT, scripted failures, and interruption points. Tests cover
response bounds/malformed input, secret redaction, size equal/above/below/omitted,
multiple-recipient rejection, raw multipart/base64/quoted-printable preservation,
dot transparency, empty/final-line framing, cancellation, ambiguous acceptance,
queue state persistence, and final-250/QUIT ordering.

Release streaming sanity results from the local fake server are recorded in
`BUILD_STATUS.md` for 1, 10, and 25 MiB deterministic messages.

## Security boundary and freeze gate

Production cannot redirect the Exchange token to an arbitrary SMTP host or
disable TLS validation. The token, decoded/encoded XOAUTH2 payload, MIME body, and
recipient values are not logged. The RBAC-only onboarding path must not receive a
broad Entra `SMTP.SendAsApp` grant.

Dedicated-tenant Exchange Online testing proved certificate-backed token
acquisition, allowed-mailbox final acceptance, and failure of the same application
and certificate when used with a mailbox outside the RBAC resource scope. A final
fixture-matched actual RelayBridge IPv4-only submission delivered an exact 25 MiB
multipart/base64 scan fixture, closing the large-message production-path gate.

## Real-tenant freeze audit

The 2026-08-22 freeze audit rechecked current Microsoft App RBAC, SMTP OAuth,
SMTP AUTH, and authenticated-submission throttling documentation. No architectural
change was required. A credential-free check against the fixed Exchange Online
endpoint passed DNS, TCP/587, greeting, EHLO, STARTTLS, normal TLS validation,
post-TLS EHLO, XOAUTH2 advertisement, SIZE advertisement, and QUIT without sending
AUTH or acquiring a token.

Tenant identifiers were supplied only through the Git-ignored local configuration.
Its exact `CurrentUser\My` certificate reference resolved and the private key was
used only through the Windows certificate store; no key material was requested,
exported, or logged. Token acquisition, normal STARTTLS, allowed-mailbox XOAUTH2,
MAIL authorization, small-message final acceptance, and multiple-recipient final
acceptance passed. The out-of-scope mailbox was rejected at XOAUTH2 before sender
authorization or DATA, proving the tested RBAC boundary failed closed.

The audit added regression coverage proving `MAIL FROM:<>` is rejected during
local intake and that an empty queued sender is rejected before token acquisition
or network use. The manual matrix and tenant prerequisites are documented in
[`../microsoft365/milestone-4-real-tenant-validation.md`](../microsoft365/milestone-4-real-tenant-validation.md).

Sanitized evidence: live network/TLS/capability stages **PASS**; correct-certificate
token **PASS**; SMTP AUTH enabled **PASS**; allowed RBAC mailbox **PASS**; denied
RBAC mailbox rejection **PASS**; small message **PASS**; multiple recipients **PASS**;
streamed 10 MiB **PASS**; standalone IPv4 25 MiB **PASS** with final `250`; actual
RelayBridge IPv4-only fixture-matched 25 MiB multipart/base64 scan **PASS** with
final `250`, Delivered persistence, and spool cleanup. Wrong-certificate rejection **PASS**;
zero Entra API permission entries **PASS**; the SMTP-AUTH-disable experiment was
inconclusive with settings restored and is not a freeze blocker. Manual receipt of
the small message is confirmed. Milestone 4 is frozen.

The original 25 MiB message remains unretired because its historical validator did
not retain acceptance-boundary telemetry. It was never resent. The corrected provider
keeps generic commands at 30 seconds, uses a separate RFC-aligned 10-minute DATA-
termination response wait, and records sanitized progress and protocol response codes.

External validation identified the developer IPv6/vEthernet path as the source of
the extreme upload slowdown: a standalone IPv4-only 26,214,400-byte submission took
3.07 seconds and received final `250`. The one authorized actual RelayBridge
production-path test also used IPv4 only and confirmed fast streaming—2.752 seconds
at 9.083 MiB/s—with `220/220/235/250/250/354` through DATA, EOF, and terminator flush.
Exchange returned final `554 5.2.270` after 0.490 seconds. RelayBridge correctly
persisted PermanentFailure/`MessageTooLarge` and retained the spool.

A separately authorized fixture-matched run then used a new Message-ID, strict-CRLF
`multipart/mixed` MIME, a small text part, and a base64 synthetic binary attachment.
The exact 26,214,400-byte spool contained 19,156,143 decoded attachment bytes. MAIL
`250`, RCPT `250`, DATA `354`, complete EOF streaming, and terminator flush passed;
DATA took 4.102 seconds, final `250 2.0.0` arrived after 0.719 seconds, the queue
persisted Delivered, and the spool payload was removed. This demonstrates that the
earlier rejection was fixture/conversion-size dependent, not a RelayBridge transport
failure or universal tenant limit. Neither tested Message-ID was resent.

## Explicitly deferred

- Microsoft setup/RBAC wizard and PowerShell guidance UI (Milestone 5)
- recipient-level partial delivery, connection pooling, pipelining, Graph, and
  arbitrary SMTP providers
- inbound STARTTLS provisioning, installer/service ACLs, and device UX
