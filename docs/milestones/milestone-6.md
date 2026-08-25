# Milestone 6 — Device UX

## Goal

Provide a simple, accurate device-management experience around the frozen M1-M5
runtime without changing SMTP intake, queue, Microsoft identity, or Exchange
delivery behavior.

## Implemented

- operational dashboard with Microsoft readiness, device, queue, today, and recent
  device summaries from metadata-only queries
- searchable responsive device list with Ready, Needs attention, and Disabled text
  states
- guided add-device flow for compatible authenticated devices and explicitly
  restricted Legacy devices
- generated unique usernames and 192-bit passwords, with plaintext shown only once
  in the interactive component; SQLite stores only the existing salted verifier
- setup instructions based on the configured listener and active private LAN
  candidates; loopback-only configuration is reported as not reachable instead of
  suggesting `127.0.0.1` to a printer
- device details with bounded activity metadata, editable name/description/network,
  explicit password reset and disable/re-enable confirmations, and preserved queue
  history
- a deliberate “wait for printer / check now” workflow that observes normal durable
  queue activity; it does not create a second SMTP path or a live-event framework
- read-only settings page that makes loopback management, listener binding, inbound
  STARTTLS absence, and cleartext AUTH state visible
- SQLite schema v5 migration adding only an optional bounded device description

## Security behavior

Legacy provisioning requires a private/local source restriction and the active
authorized sender. Compatible setup does not claim readiness while cleartext AUTH
is disabled, because inbound STARTTLS is not yet implemented. Passwords are never
placed in URLs, browser storage, logs, or persistence. Device and activity screens
do not expose message bodies, attachments, password verifiers, OAuth material, or
private-key data.

Management remains bound to loopback. Milestone 6 does not solve remote management
authentication, inbound TLS certificate provisioning, printer discovery, deletion
semantics, or live SMTP troubleshooting; those remain later work.

## Verification

Automated tests cover credential uniqueness and non-persistence, invalid Legacy
atomic failure, reset/disable/re-enable, edit history preservation, dashboard
metadata, loopback endpoint safety, and all new UI routes. The complete Release
suite retains every M1-M5 regression. A published Production-layout smoke verifies
the dashboard, device routes, settings, static assets, health endpoint, and graceful
shutdown without changing production static-asset policy. The local `dotnet run`
Development profile separately verifies the dashboard, CSS, JavaScript, and Blazor
framework assets, preventing project-output Production static-asset failures.
