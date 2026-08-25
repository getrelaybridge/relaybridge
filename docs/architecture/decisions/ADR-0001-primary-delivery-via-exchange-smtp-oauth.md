# ADR-0001: Primary delivery via Exchange SMTP OAuth

- Status: Accepted
- Date: 2026-08-22

## Context

RelayBridge receives RFC 5322/MIME messages over SMTP from local devices. The V1
delivery path should preserve that representation, stream large scan messages,
minimize transformations, and authorize the application to send only from intended
Exchange Online mailboxes.

Current Microsoft documentation was reviewed on 2026-08-22:

- [OAuth for IMAP, POP, and SMTP](https://learn.microsoft.com/en-us/exchange/client-developer/legacy-protocols/how-to-authenticate-an-imap-pop-smtp-application-by-using-oauth)
- [RBAC for Applications in Exchange Online](https://learn.microsoft.com/en-us/exchange/permissions-exo/application-rbac)
- [SMTP onboarding to App RBAC](https://learn.microsoft.com/en-us/exchange/client-developer/legacy-protocols/smtp-app-rbac-onboarding)
- [Enable or disable SMTP AUTH](https://learn.microsoft.com/en-us/exchange/clients-and-mobile-in-exchange-online/authenticated-client-smtp-submission)
- [Microsoft 365 client SMTP submission](https://learn.microsoft.com/en-us/exchange/mail-flow-best-practices/how-to-set-up-a-multifunction-device-or-application-to-send-email-using-microsoft-365-or-office-365)

Microsoft documents OAuth client-credentials access for SMTP, SASL XOAUTH2, the
application token scope `https://outlook.office365.com/.default`, and the
`SMTP.SendAsApp` permission. It documents `smtp.office365.com`, port 587 as the
recommended client-submission port, and TLS 1.2 or later. SMTP AUTH availability
is controlled at organization and mailbox levels.

Exchange Application RBAC lists `Application SMTP.SendAsApp`, allowing a role
assignment to be restricted to an Exchange resource scope. Its documentation also
states that Entra API permissions and Exchange RBAC grants are additive. An
unscoped Entra `SMTP.SendAsApp` grant would therefore defeat an intended RBAC-only
mailbox scope.

The dedicated SMTP App RBAC onboarding page now explicitly says that this path
does not require mailbox API permission claims and that adding the Entra
`SMTP.SendAsApp` claim triggers an unnecessary mailbox-permission check. It
confirms the Exchange scope `https://outlook.office365.com/.default` and the
resource-scoped `Application SMTP.SendAsApp` role assignment.

One Microsoft page currently lists only Microsoft Graph and EWS in its “Supported
Protocols” bullets while the same page lists `Application SMTP.SendAsApp` and
describes SMTP client submission. This inconsistency requires tenant-level
validation before production delivery is implemented.

## Decision

Use Exchange Online authenticated SMTP client submission as the primary V1
Microsoft 365 transport with:

- OAuth 2.0 client credentials using a customer-owned Entra application
- certificate authentication; no project-wide client secret
- `https://outlook.office365.com/.default` as the application token scope
- SASL XOAUTH2
- STARTTLS with normal certificate and hostname validation
- Exchange Application RBAC role `Application SMTP.SendAsApp`, restricted to the
  intended sender mailbox scope
- `smtp.office365.com` by DNS name and port 587 by default, both represented as
  validated configuration rather than scattered constants

The recommended RBAC onboarding path must not also grant the organization-wide
Entra `SMTP.SendAsApp` application permission. Setup diagnostics must test OAuth,
SMTP AUTH availability, TLS connectivity, RBAC scope, sender authorization, and a
real SMTP submission before reporting Microsoft 365 as connected.

Microsoft Graph is not a V1 production delivery provider and will not be used as a
silent fallback.

## Reasons

- SMTP-to-SMTP is the smallest natural bridge.
- Raw MIME can remain the spool and delivery source of truth.
- Large messages can be streamed without Graph attachment reconstruction.
- Queue and troubleshooting semantics stay at the SMTP protocol boundary.
- Exchange Application RBAC offers least-privilege mailbox scoping.

## Consequences

- Customer tenants must permit SMTP AUTH for the designated mailbox and allow
  outbound TCP 587.
- Exchange-specific service-principal and RBAC onboarding is required.
- OAuth/XOAUTH2, SMTP response classification, and STARTTLS must be implemented and
  tested.
- Exchange limits and policy can reject mail even after token acquisition.
- A real test tenant must validate the RBAC-only SMTP path before Milestone 4 is
  considered complete, including a negative test against an out-of-scope mailbox.
- Microsoft documentation must be rechecked when Microsoft integration work starts.

Milestone 4 revalidated these sources on 2026-08-22. The dedicated SMTP App RBAC
page (last updated 2025-10-17) still specifies no Entra permission claims, the
resource-scoped `Application SMTP.SendAsApp` role, the Exchange `.default` scope,
and XOAUTH2. The architecture therefore did not change. Real-tenant positive and
negative RBAC validation remains the freeze gate.
