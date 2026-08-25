# Milestone 3 — Microsoft Identity Foundation

## Goal

Maintain one customer-owned Microsoft Entra application identity and acquire the
Exchange Online application token with a local certificate, without sending mail.

## Implemented

- strict tenant/client GUID and certificate-reference validation with fixed
  tenant-specific authority and Exchange resource scope
- SQLite schema version 3 for non-secret identity metadata only
- Windows `LocalMachine\My` production and explicit `CurrentUser\My` development
  certificate references
- administrator-provided certificate support plus RSA-2048/SHA-256, one-year,
  non-exportable CNG certificate generation
- exact lookup, date/algorithm/signing/private-key capability validation, and
  configurable expiry warning
- public-only `.cer` export under a fixed server directory
- MSAL.NET confidential-client certificate authentication, in-memory application
  token cache, cancellation/timeout, configuration-aware reuse, and safe retirement
- token type that redacts string/JSON/debug views and safe identity diagnostics
- runtime `NotConfigured`, `Checking`, `Healthy`, `Attention`, and `Failed` state;
  local `/health` semantics remain unchanged

## Security boundary

No PFX, private key, client secret, access token, client assertion, or serialized
MSAL cache is persisted or logged. The private key stays in the Windows key store;
only a public `.cer` is exported for Entra registration. `LocalMachine` private-key
ACL provisioning is an installer prerequisite. Self-signed generation is not the
preferred organizational production lifecycle.

## Explicitly deferred

- `smtp.office365.com`, STARTTLS, XOAUTH2, envelope commands, DATA, and delivery
- Exchange mailbox authorization and out-of-scope sender negative testing
- setup wizard, Entra/Exchange automation, certificate rotation/deletion UI
- inbound STARTTLS and installer service-identity/ACL configuration
- real-tenant validation when no dedicated test tenant is available

## Exit criteria

Configuration, certificate lifecycle, public export, token secrecy, fake-boundary
token acquisition, concurrency, cancellation, failure classification, schema
migration, host startup, queue preservation, Release build/test/format, dependency
audit, static secret scan, and composed-host smoke checks pass. No outbound SMTP
implementation exists.
