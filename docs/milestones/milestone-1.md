# Milestone 1 — Durable SMTP Intake

## Goal

Accept authorized local SMTP messages into a durable queue without outbound
delivery, whole-message buffering, or an open-relay configuration.

## Implemented

- configurable async SMTP listener with loopback port 2525 defaults
- HELO, EHLO, AUTH LOGIN, AUTH PLAIN, MAIL FROM, RCPT TO, DATA, RSET, NOOP,
  QUIT, and SIZE advertisement
- authenticated and Legacy device modes persisted in SQLite
- mandatory source IP/CIDR and sender allow-lists for every device
- private/local-only source ranges for Legacy mode and rejection of catch-all
  networks
- 192-bit CSPRNG-generated device passwords and versioned salted
  PBKDF2-HMAC-SHA256 verifiers at 600,000 iterations
- global/per-source connection bounds, command/authentication-attempt/recipient/
  line/message limits, and per-read idle timeouts
- streamed DATA with strict CRLF framing, dot unstuffing, premature-disconnect
  cleanup, durable spool flush, atomic promotion, and transactional queue metadata
- local queue preview service for non-Microsoft inspection without publishing
  message content through the unauthenticated status UI

## Durable acceptance contract

The listener sends final `250` only after the spool file is durably flushed,
promoted into `spool/pending`, and the SQLite message/recipient transaction commits.
A local persistence failure returns `451`; an oversize message returns `552` and
the connection closes rather than consuming unbounded input.

## Security boundary

A fresh installation has no devices and listens only on loopback, so it cannot
relay mail. Every accepted transaction maps to one enabled device, an allowed
source, valid authentication where required, and an allowed envelope sender.
Legacy mode cannot use public or catch-all networks.

STARTTLS is explicitly deferred because safe certificate provisioning and key
protection do not yet exist. Cleartext AUTH is disabled by default because its
credentials are exposed at the SMTP layer without TLS. Enabling it or binding to
a non-loopback address is therefore a deliberate trusted-LAN compatibility
choice, not a safe Internet-facing configuration.

## Explicitly deferred

- inbound STARTTLS certificate lifecycle and Secure/Compatible profile UX
- startup orphan/missing-file reconciliation, capacity/free-disk limits, retry
  state transitions, and queue workers (Milestone 2)
- Microsoft authentication and all outbound SMTP delivery
- device management UI and one-time password presentation workflow
- installer-created service identity and restrictive data-directory ACLs

## Exit criteria

- real TCP tests prove authentication and Legacy happy paths
- anonymous, wrong-password, wrong-IP, disabled-device, and unauthorized-sender
  paths fail closed
- malformed/long commands, invalid AUTH, excessive recipients/connections,
  premature DATA disconnect, and oversize DATA are bounded
- dot transparency and raw MIME spool bytes are correct
- injected SQLite failure does not receive `250`
- accepted messages remain after listener/database restart
- a 10 MB-class message is streamed with bounded managed-memory growth
- Release build, tests, format verification, and dependency vulnerability audit
  pass
