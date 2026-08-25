# Exchange SMTP OAuth delivery

RelayBridge's production delivery provider submits one queued message per TCP
connection to `smtp.office365.com:587`. The endpoint, Exchange token audience,
STARTTLS requirement, XOAUTH2 mechanism, and platform certificate validation are
security constants rather than administrator-selectable transport settings.
Tests replace the endpoint through an internal seam and trust one exact generated
test certificate; production has neither endpoint override nor trust bypass.

Milestone 5 does not duplicate or alter this state machine. Candidate sender
verification and the setup test message call this provider directly, and active
configuration is committed only after its final-acceptance result succeeds. See
[`setup-wizard.md`](setup-wizard.md).

## Protocol and identity rule

The implemented sequence is:

```text
220 -> EHLO -> STARTTLS -> validated TLS -> EHLO -> AUTH XOAUTH2
    -> MAIL FROM -> every RCPT TO -> DATA -> streamed spool -> final 250 -> QUIT
```

Pre-TLS capabilities are discarded. Post-TLS `AUTH XOAUTH2` is required. The
OAuth token is acquired immediately before AUTH for the fixed Exchange scope
`https://outlook.office365.com/.default` and is never persisted or logged.

The XOAUTH2 `user=` mailbox is the durable queue's SMTP envelope sender. The same
address is sent in `MAIL FROM`. RelayBridge never substitutes the MIME `From:`
header. Consequently every permitted envelope sender must be a real Exchange
mailbox in the application's `Application SMTP.SendAsApp` RBAC resource scope.
V1 does not support authenticating as one mailbox and sending a different
envelope sender through delegated SendAs behavior.

The local SMTP parser rejects the null reverse-path `MAIL FROM:<>`. The outbound
provider independently rejects an empty queued envelope sender before token or
network use. RelayBridge is an MFP/application submission relay and does not
invent an authentication mailbox for bounce submission.

Microsoft's dedicated [SMTP App RBAC onboarding](https://learn.microsoft.com/en-us/exchange/client-developer/legacy-protocols/smtp-app-rbac-onboarding)
continues to say that this path needs no Entra API permission claim and that
adding the broad `SMTP.SendAsApp` claim triggers a separate, unnecessary mailbox
permission check. Do not add that claim to an RBAC-only deployment. The broader
[SMTP OAuth article](https://learn.microsoft.com/en-us/exchange/client-developer/legacy-protocols/how-to-authenticate-an-imap-pop-smtp-application-by-using-oauth)
also documents a classic permission path; it is not combined with RelayBridge's
preferred RBAC onboarding.

## Envelope, SIZE, and DATA

Envelope recipients come only from durable queue metadata. V1 requires every
recipient to be accepted before DATA. If any recipient is rejected, RelayBridge
sends best-effort RSET and does not send the body. Any permanent recipient
rejection makes the whole message permanent; otherwise a temporary rejection
retries the whole message. Recipient-level partial delivery is deliberately not
implemented because retrying already-accepted recipients would cause duplicates.

When Exchange advertises SIZE, RelayBridge includes the known spool byte count in
MAIL FROM. A message larger than an advertised numeric maximum is rejected locally
before token acquisition, AUTH, or payload streaming. No universal Microsoft
message limit is hardcoded; tenant/mailbox limits and actual SMTP responses remain
authoritative.

The raw `.eml` spool is read through a 64 KiB buffer. RelayBridge performs only
SMTP transparency: a leading dot on each CRLF-delimited line is doubled. A spool
ending in CRLF is followed directly by `.<CRLF>`; a nonempty final line without
CRLF is completed before the terminator; an empty stream sends only the
terminator. Normal inbound spool files already use CRLF framing. MIME headers,
boundaries, encodings, and attachments are not parsed or reconstructed.

## Acceptance and failure behavior

Only the final Exchange `250` after the DATA terminator is success. QUIT is best
effort and cannot reverse acceptance. A failure proven before terminator transmission
is an ordinary transient failure. Once terminator transmission begins, a disconnect,
timeout, malformed response, or cancellation before the final response is ambiguous.
The queue retries an ambiguous result with normal backoff, which can duplicate a
message Exchange actually accepted. RelayBridge does not claim exactly-once delivery.

SMTP 4xx responses are generally transient. Stage-specific 5xx responses map to
authentication/authorization, sender, recipient, size, protocol, or permanent
server categories. SMTP `552` and Exchange enhanced status `5.2.270` are permanent
`MessageTooLarge` results. Authentication/configuration failures retry through the
existing bounded queue policy with a slower provider hint. DNS, connect, TLS, and
ordinary commands retain short bounds; DATA streaming is size-aware, and the wait
for final acceptance after the terminator has a separate
[RFC 5321 section 4.5.3.2.6](https://www.rfc-editor.org/rfc/rfc5321#section-4.5.3.2.6)-aligned
10-minute default. No SQLite transaction is open during Microsoft network work.

The delivery snapshot records only sanitized stage data: DATA start, bytes read,
spool EOF, terminator write/flush, final-response wait/receipt, bounded response
text, completion times, exception type, and socket error. It never records MIME
content or OAuth/certificate material.

Microsoft documents organization and mailbox controls for [SMTP AUTH availability](https://learn.microsoft.com/en-us/exchange/clients-and-mobile-in-exchange-online/authenticated-client-smtp-submission).
Token acquisition alone is therefore not delivery readiness. Microsoft also
documents SMTP authenticated-submission throttles of three concurrent connections,
30 messages per minute, and 10,000 recipients per day; RelayBridge keeps bounded
workers and starts at one connection per attempt without pooling.

## Real-tenant validation gate

Automated tests use a local scripted STARTTLS server and fake token. Before
Milestone 4 can be frozen, a dedicated Microsoft 365 tenant must prove:

- token acquisition and validated `smtp.office365.com:587` STARTTLS
- XOAUTH2 and SMTP AUTH availability for an allowed mailbox
- successful small, multiple-recipient, and synthetic large-message delivery
- rejection of the same application using a mailbox outside its RBAC scope
- absence of a broad Entra permission that could invalidate the negative test
- expected behavior with SMTP AUTH disabled and with an unregistered certificate

A dedicated tenant subsequently proved the positive and negative RBAC transport
paths, wrong-certificate failure, 10 MiB acceptance, manual small-message receipt,
and a fixture-matched actual RelayBridge 25 MiB multipart/base64 delivery. The
production-path large-message gate is closed.

Use the sanitized maintainer checklist in
[`milestone-4-real-tenant-validation.md`](milestone-4-real-tenant-validation.md)
to perform and record the freeze audit. On 2026-08-22, the fixed production endpoint
passed DNS/TCP/STARTTLS/normal-TLS/post-TLS-capability stages, the exact Windows
certificate-store reference acquired an Exchange token, the allowed mailbox reached
final acceptance, and the out-of-scope mailbox was rejected at XOAUTH2. Small,
multiple-recipient, and streamed 10 MiB submissions reached final acceptance. The
an earlier 25 MiB attempt reached final `554 5.2.270` and was correctly classified.
A temporary unregistered certificate was correctly rejected during
token acquisition and removed without affecting the working certificate, and the
Entra application had zero API permission entries. The SMTP-AUTH-disable experiment
was inconclusive with settings restored and is not a freeze blocker. Manual receipt
of the small message is confirmed; Milestone 4 is frozen.

The original 25 MiB validator targeted exactly 26,214,400 bytes but retained no
progress/terminator/exception telemetry or queue record. Its `AmbiguousAcceptance`
cannot prove whether the terminator was accepted, so that message was never resent.
The corrected provider separates the 10-minute final-response wait from the 30-second
generic command timeout. A newly identified 26,214,400-byte spool reached EOF after
377.515 seconds, flushed its terminator, received `554 5.2.270` after 1.089 seconds,
and completed in 380.506 seconds. The payload was retained with the permanent queue
result; no accepted message was deleted or retried.

Subsequent standalone PowerShell validation showed that the developer machine's
default IPv6/vEthernet path caused the extreme upload slowdown. With IPv6 disabled
for that standalone process, a fresh exact 26,214,400-byte message streamed in 3.07
seconds at 8.145 MiB/s and received final `250`. Its 10 MiB comparison improved from
158.39 seconds over the default IPv6/vEthernet path to 0.98 seconds over IPv4.

The one authorized actual RelayBridge production-path validation also disabled IPv6
only for its process. It used the real durable queue, Windows-store certificate
loader, MSAL token provider, and production Exchange provider. DNS/TCP, greeting
`220`, STARTTLS `220`, TLS, token acquisition, XOAUTH2 `235`, MAIL `250`, RCPT `250`,
and DATA `354` all passed. The full 26,214,400-byte spool reached EOF and the
terminator flush completed in 2.752 seconds at 9.083 MiB/s. Exchange then returned
final `554 5.2.270` after 0.490 seconds. RelayBridge correctly returned permanent
`MessageTooLarge`, persisted PermanentFailure, and retained the payload.

The failed RelayBridge fixture was synthetic text MIME, while the accepted standalone
fixture was a realistic multipart/base64 attachment. A separately authorized, actual
RelayBridge fixture-matched run therefore created a new strict-CRLF `multipart/mixed`
message with a small text part and a base64 synthetic binary attachment. Its exact
26,214,400-byte spool represented 19,156,143 decoded attachment bytes. With IPv6
disabled only for the developer process, MAIL `250`, RCPT `250`, DATA `354`, spool
EOF, and terminator flush all passed; DATA streamed in 4.102 seconds at 6.093 MiB/s.
Exchange returned final `250 2.0.0` after 0.719 seconds. RelayBridge persisted
Delivered, marked the payload absent, and deleted the spool file in a 6.039-second
queue attempt.

The paired outcomes are consistent with Exchange content-conversion/encoding size
overhead: the exact-size synthetic text fixture was rejected, while the realistic
multipart/base64 scan fixture was accepted. No IPv4 product default or universal
tenant limit is inferred. No prior Message-ID was resent, and the upstream
`554 5.2.270` remains correctly classified as permanent `MessageTooLarge`.
