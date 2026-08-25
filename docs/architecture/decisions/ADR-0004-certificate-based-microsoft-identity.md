# ADR-0004: Certificate-based Microsoft application identity

- Status: Accepted
- Date: 2026-08-22

## Context

RelayBridge needs one customer-owned, tenant-specific Microsoft Entra application
identity before Exchange SMTP delivery can be implemented. The credential must be
usable by a Windows Service without placing a reusable client secret, private key,
or bearer token in SQLite, application settings, logs, or diagnostics.

Current Microsoft guidance was reviewed on 2026-08-22:

- [MSAL.NET client credential flows](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/web-apps-apis/client-credential-flows)
- [Add and manage application credentials](https://learn.microsoft.com/en-us/entra/identity-platform/how-to-add-credentials)
- [Create a self-signed certificate](https://learn.microsoft.com/en-us/entra/identity-platform/howto-create-self-signed-certificate)
- [SMTP OAuth](https://learn.microsoft.com/en-us/exchange/client-developer/legacy-protocols/how-to-authenticate-an-imap-pop-smtp-application-by-using-oauth)
- [SMTP onboarding to App RBAC](https://learn.microsoft.com/en-us/exchange/client-developer/legacy-protocols/smtp-app-rbac-onboarding)
- [RBAC for Applications](https://learn.microsoft.com/en-us/exchange/permissions-exo/application-rbac)

Microsoft recommends certificates rather than client secrets for production,
documents RSA 2048/SHA-256 for self-signed test credentials, and recommends a
trusted CA and managed lifecycle for production. MSAL documents a long-lived
confidential client and its in-memory application token cache. The dedicated SMTP
App RBAC guide says not to add Entra API permission claims for the RBAC path and
requires `Application SMTP.SendAsApp` through a resource-scoped Exchange role.
The SMTP resource remains `https://outlook.office365.com/.default`.

## Decision

- Use `Microsoft.Identity.Client` 4.88.0 and `AcquireTokenForClient`; do not build
  OAuth requests or client assertions.
- Accept one tenant GUID, one client GUID, and one certificate reference. Build a
  tenant-specific public-cloud authority; do not accept arbitrary authority URLs.
- Use `LocalMachine\My` for the eventual Windows Service production credential.
  `CurrentUser\My` is an explicit development/test location, not a fallback.
- Support administrator-provided certificates. RelayBridge generation creates an
  RSA-2048, SHA-256, signature-capable one-year certificate with a named,
  non-exportable Windows CNG private key. Microsoft currently characterizes
  self-signed credentials as testing-oriented; a customer-managed CA certificate
  and lifecycle are the production recommendation.
- Validate exact thumbprint selection, validity, RSA/key size/signing usage,
  private-key presence, and actual signing capability under the process identity.
- Export only an X.509 public `.cer` into a fixed RelayBridge export directory.
- Persist only tenant ID, client ID, thumbprint, store name, and store location in
  SQLite schema version 3. Never persist a token, client assertion, client secret,
  PFX, private key, or serialized MSAL cache.
- Reuse one MSAL confidential client for an unchanged configuration and rely on
  MSAL's application cache. Rebuild it when tenant, client, or certificate changes;
  retire old certificate handles after in-flight calls complete.
- Do not acquire a token during service startup. Manual diagnostics distinguish
  application authentication from SMTP authorization and mail delivery.

## Consequences

The installer must later grant the chosen Windows Service identity read/sign
access to the `LocalMachine\My` private key without making it exportable. A local
administrator can still use or steal any credential accessible on the host; this
is an unavoidable trust boundary. Certificate rotation can register and test a
candidate before switching the stored reference, and RelayBridge never deletes an
existing configured certificate automatically.

Milestone 3 proves only Entra application authentication and Exchange-resource
token acquisition. Exchange SMTP, XOAUTH2, mailbox RBAC, and delivery remain
Milestone 4.
