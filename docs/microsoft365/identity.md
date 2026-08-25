# Microsoft identity foundation

RelayBridge uses one customer-owned, single-tenant Microsoft Entra application
with an X.509 certificate credential. The configured values are the Directory
(tenant) GUID, Application (client) GUID, and a reference to a certificate in the
Windows Personal (`My`) store. The authority is constructed from the tenant GUID;
the Exchange resource scope is fixed at
`https://outlook.office365.com/.default`.

## Certificate storage

The production target is `LocalMachine\My` so a dedicated Windows Service identity
can use the credential. The installer must later grant that identity private-key
access. `CurrentUser\My` exists only for explicit development and testing.

RelayBridge accepts an administrator-provided certificate. Its optional generator
creates an RSA-2048/SHA-256 signing certificate with a non-exportable Windows CNG
private key and a one-year default lifetime. Microsoft recommends organizationally
managed CA certificates and lifecycle controls for production; generated
self-signed certificates are not presented as equivalent to corporate PKI.

Lookup is exact and fails closed on missing or duplicate thumbprints, invalid
dates, unsupported algorithms or key sizes, missing private keys, and private keys
the current process cannot actually use to sign. The default expiry warning begins
60 days before expiration and is configurable as implementation policy.

The export operation writes a server-named `.cer` below RelayBridge's data
directory. It contains only the public certificate. RelayBridge does not create a
PFX or upload/transmit the private key.

## Tokens and configuration

MSAL.NET builds one confidential client for the current tenant, client, and
certificate. Normal calls use `AcquireTokenForClient` and MSAL's in-memory
application token cache; force refresh and persistent token caching are absent.
A restart simply acquires a new token. Tokens are excluded from `ToString()`, JSON
diagnostics, logs, SQLite, and application settings.

SQLite schema version 9 stores the runtime identity fields:

- tenant GUID
- client GUID
- certificate thumbprint
- store name (`My`)
- store location (`LocalMachine` or explicit `CurrentUser`)
- one authorized envelope sender mailbox
- one collision-safe activation ID

There is no client-secret or token schema. The M5 wizard adds a one-row safe
candidate-progress record containing only identifiers, certificate reference,
sender, step, validation flags, its reserved activation ID, a compare-and-swap revision,
and an authoritative Active/Cancelled/Activated lifecycle. Runtime evidence is
keyed by activation ID plus the non-secret configuration fingerprint, so reactivating
identical settings cannot reuse evidence from an older activation. Configuration
replacement is atomic only after candidate token and SMTP sender validation. A
conditional SQLite cancellation advances the revision, causing late administrator-stage
results and activation to fail compare-and-swap. Changing tenant, client, or certificate
causes the cached confidential client to be replaced without deleting either
certificate.

## Diagnostic meaning

The explicit authentication test validates local configuration and certificate
access, then attempts to acquire the Exchange Online resource token. It returns
plain-language categories and safe Microsoft technical/correlation identifiers.
Success means only that the application identity authenticated and obtained a
token. It does not test SMTP AUTH, Exchange Application RBAC mailbox scope, sender
authorization, or delivery. Milestone 4 provides a separate explicit Exchange
delivery diagnostic whose safe checkpoints cover DNS/TCP, STARTTLS, token
acquisition, XOAUTH2, sender authorization, and final test-message acceptance.

The service does not contact Microsoft during startup. Missing configuration,
certificate errors, Internet failure, and Entra failure do not stop local SMTP or
alter accepted queue data. `/health` remains local queue health. The M5 management
route remains loopback-only and does not broaden the binding; authenticated remote
management remains later hardening work.

For the preferred Exchange App RBAC path, follow Microsoft's dedicated SMTP App
RBAC onboarding guidance: do not add Entra API permission claims, create the
Exchange service-principal pointer, and assign resource-scoped
`Application SMTP.SendAsApp`. Real positive and negative SMTP authorization tests
were proven during the Milestone 4 freeze. See
[`exchange-smtp-delivery.md`](exchange-smtp-delivery.md) and the guided
[`setup-wizard.md`](setup-wizard.md).
