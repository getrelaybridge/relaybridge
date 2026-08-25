# ADR-0003: Stream inbound SMTP to filesystem spool with SQLite metadata

- Status: Accepted
- Date: 2026-08-22

## Context

Milestone 1 must accept SMTP DATA without holding a 10–30 MB scan in memory and
must not return `250` until local persistence is durable. The listener also needs
AUTH LOGIN/PLAIN, per-device authorization, bounded inputs, and a storage callback
that can fail the SMTP transaction.

The current embeddable .NET server options were reviewed on 2026-08-22:

- [SmtpServer 11.1.0](https://www.nuget.org/packages/SmtpServer) is maintained,
  MIT-licensed, compatible with .NET 10, and supports STARTTLS, SIZE, AUTH LOGIN,
  AUTH PLAIN, and configurable message limits. Its public `IMessageStore` hook
  receives a completed `ReadOnlySequence<byte>` after `ReadDotBlockAsync` has
  accumulated DATA. It does not expose a supported stream-as-received persistence
  hook. This retains the complete message until storage starts and cannot provide
  RelayBridge's required streaming/durability boundary.
- `Rnwood.SmtpServer` is marked deprecated on NuGet. Its containing smtp4dev
  project remains active, but embedding a deprecated component or the full test
  mail application is not appropriate for production relay intake.

The protocol behavior is constrained by
[RFC 5321](https://www.rfc-editor.org/rfc/rfc5321),
[RFC 4954](https://www.rfc-editor.org/rfc/rfc4954), and
[RFC 4616](https://www.rfc-editor.org/rfc/rfc4616). Microsoft documents that
`Microsoft.Data.Sqlite` async ADO.NET calls execute synchronously because SQLite
does not provide asynchronous I/O; WAL is recommended for concurrency.
[OWASP password-storage guidance](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html)
recommends salted adaptive hashing and specifies 600,000 PBKDF2-HMAC-SHA256
iterations when PBKDF2 is selected. RelayBridge uses that built-in .NET mechanism
for its high-entropy generated device secrets, avoiding another cryptography
package.

## Decision

Use a narrow RelayBridge inbound SMTP state machine built on .NET `TcpListener`
and streaming `FileStream` I/O. It implements only HELO/EHLO, AUTH LOGIN, AUTH
PLAIN, MAIL, RCPT, DATA, RSET, NOOP, and QUIT, plus the SIZE advertisement. It
does not attempt to become a general mail server.

Store:

- raw transport-decoded RFC 5322/MIME content in random `.eml` files
- device definitions, password verifiers, envelope metadata, recipients, size,
  timestamps, spool filename, and queue state in SQLite

The acceptance order is:

1. create a random file in `spool/incoming`
2. stream CRLF-delimited DATA while removing exactly one transparency dot
3. flush the file and intermediate OS buffers with `FileStream.Flush(true)`
4. close it and atomically move it on the same volume to `spool/pending`
5. insert message and recipient metadata in one SQLite transaction using WAL and
   `synchronous=FULL`
6. return SMTP `250`

If the SQLite insert fails, RelayBridge best-effort deletes the promoted file and
returns `451`. It never creates a queue row before the final spool path exists.
SQLite metadata calls remain short synchronous calls, reflecting the provider's
documented behavior; network and large-file operations are asynchronous.

STARTTLS is not advertised in Milestone 1. Implementing it safely requires
inbound certificate provisioning, private-key protection, renewal, and explicit
TLS policy that do not exist yet. The default listener is therefore loopback-only
and does not advertise or accept cleartext AUTH. AUTH LOGIN and PLAIN expose
reusable credentials on a non-TLS connection; enabling
`AllowCleartextAuthentication` is an explicit temporary trusted-network
compatibility choice and is accepted only with one explicit RFC1918 IPv4 or IPv6
ULA listener address. Wildcard, loopback, link-local, multicast, public, and
Internet authenticated bindings fail startup.

## Consequences

- Message memory is bounded by socket, command, and one DATA-line buffers rather
  than total message size.
- RelayBridge owns a small security-sensitive SMTP parser; socket-level positive,
  negative, abuse, durability, restart, and large-message tests are mandatory.
- A crash after file promotion but before metadata commit can leave an orphan
  spool file. Milestone 2 startup reconciliation must recover or quarantine it.
- A crash or disconnect after metadata commit but before the client receives
  `250` can cause an upstream retry and duplicate message. Delivery is at-least-
  once, not exactly-once.
- Power-loss durability relies on the platform filesystem honoring the durable
  file flush and same-volume atomic rename. Abrupt-power testing remains a
  hardening requirement.
- Re-evaluate maintained server packages if one adds a supported streaming store
  callback that preserves RelayBridge's authorization and acceptance semantics.
