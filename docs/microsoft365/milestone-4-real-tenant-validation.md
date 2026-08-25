# Milestone 4 real-tenant validation

This is the maintainer checklist for validating RelayBridge against a dedicated
Microsoft 365 development tenant. It is not customer setup UX and does not replace
the Milestone 5 wizard. Do not run negative tests against a production customer
tenant.

Milestone 4 is **FROZEN**. The security/RBAC boundary and the fixture-matched actual
RelayBridge 25 MiB production path are proven. Never copy a token, XOAUTH2 value,
private key, PFX, client assertion, or password into test output or repository files.

Read tenant identifiers only from the intentionally Git-ignored
`.local/m4-test-tenant.json`. Never copy its values into source code, commands
stored in the repository, or committed evidence. Obtain the certificate private
key only through that file's exact Windows certificate store reference.

## Required isolated tenant state

Prepare four synthetic test mailboxes:

- `relaybridge-allowed@<test-tenant>` inside the RelayBridge Exchange resource scope
- `relaybridge-denied@<test-tenant>` outside that scope
- `recipient1@<test-tenant>`
- `recipient2@<test-tenant>`

Use one single-tenant Entra application and a test certificate whose private key
is accessible only to the RelayBridge test process. Record sanitized tenant type,
test date, certificate thumbprint suffix, client ID suffix, and Exchange service
principal identity. Do not publish complete tenant or mailbox identifiers.

Follow Microsoft's dedicated
[SMTP App RBAC onboarding](https://learn.microsoft.com/en-us/exchange/client-developer/legacy-protocols/smtp-app-rbac-onboarding):

- the Entra application has no API permission claims for the RBAC-only path
- the Exchange service-principal pointer uses the Application/Client ID and the
  **service principal object ID**, not the application object ID
- the role assignment is `Application SMTP.SendAsApp`
- the assignment has the intended custom resource scope
- the allowed mailbox is in scope and the denied mailbox is out of scope

Inspect the Entra application's API permissions before testing. Any broad
Exchange or Graph application permission, including classic `SMTP.SendAsApp`,
`Mail.Send`, or mailbox read/write grants, invalidates the negative RBAC proof.

Useful read-only Exchange checks include:

```powershell
Get-ServicePrincipal -Identity <application-client-id>
Get-ManagementRoleAssignment -Role 'Application SMTP.SendAsApp'
Test-ServicePrincipalAuthorization -Identity <application-client-id> -Resource <allowed-mailbox>
Test-ServicePrincipalAuthorization -Identity <application-client-id> -Resource <denied-mailbox>
Get-TransportConfig | Format-List SmtpClientAuthenticationDisabled
Get-CASMailbox -Identity <mailbox> | Format-List SmtpClientAuthenticationDisabled
```

Expected authorization results are allowed `InScope=True` and denied
`InScope=False`. Review Security Defaults and relevant tenant policy separately;
token acquisition does not prove SMTP AUTH availability.

## Validation order

1. Configure RelayBridge with the test tenant ID, client ID, and valid certificate
   reference. Run the Microsoft identity test. Record only success, token expiry,
   safe correlation ID, and sanitized identity metadata.
2. Select an unregistered certificate, confirm token acquisition fails with a
   credential-oriented classification, then restore the valid certificate.
3. With the production endpoint, record DNS, TCP 587, greeting, pre-TLS EHLO,
   STARTTLS, normal TLS chain/hostname validation, post-TLS EHLO, advertised SIZE,
   and XOAUTH2 capability.
4. Confirm organization and allowed-mailbox SMTP AUTH settings. Send a small
   deterministic message from the allowed mailbox through the durable queue.
   Require final SMTP `250`, Delivered state, payload cleanup, and recipient receipt.
5. Disable SMTP AUTH only for an isolated test mailbox. Confirm RelayBridge does
   not authenticate, reports an authentication/configuration category, retains the
   queued message according to retry policy, and continues local intake. Restore
   the mailbox setting afterward.
6. With the same application, certificate, and token model, attempt the denied
   out-of-scope mailbox. It must not successfully send. Record the actual rejection
   stage and bounded SMTP code/text.
7. Send one message to both synthetic recipients. Confirm all RCPT commands are
   accepted before one DATA transaction, both recipients receive one copy, and one
   queue item becomes Delivered.
8. Send deterministic synthetic MIME messages of approximately 10 MiB and 25 MiB
   total `.eml` size. Record size, duration, advertised SIZE, final SMTP status,
   queue state, and receipt. RelayBridge must avoid DATA when the advertised numeric
   SIZE limit proves the message is oversized; a separate mailbox limit revealed
   only by the final response must be classified correctly.
9. Inspect logs, console/test output, SQLite, spool/diagnostic artifacts, and the
   repository for token, XOAUTH2, private-key, PFX, assertion, or secret leakage.
10. Run the full Release build/test/format/audit/scan/composed-host gate.

RelayBridge rejects `MAIL FROM:<>` at local intake and independently rejects an
empty queued envelope sender before token acquisition or network use. Null
reverse-path delivery is intentionally unsupported for this MFP submission relay.

## Required evidence matrix

| Test | Required result | Recorded result |
|---|---|---|
| Correct certificate token | Pass | **PASS** |
| Wrong certificate token | Fail safely | **PASS — credential rejected; temporary certificate/key removed** |
| DNS / TCP 587 | Pass | **PASS** |
| STARTTLS / normal TLS validation | Pass | **PASS** |
| Post-TLS XOAUTH2 | Pass | **PASS** |
| SMTP AUTH enabled | Pass | **PASS** |
| SMTP AUTH disabled | Fail clearly | Inconclusive; settings restored; not a freeze blocker |
| Allowed RBAC mailbox | Pass | **PASS** |
| Denied RBAC mailbox | **Fail** | **PASS — rejected before sender/DATA** |
| No broad Entra permission bypass | Verified | **PASS — zero Entra API permission entries** |
| Small message | Pass | **PASS — final acceptance** |
| Multiple recipients | Pass | **PASS — final acceptance** |
| Approximately 10 MiB MIME | Pass or documented limit | **PASS — final acceptance** |
| Standalone IPv4 approximately 25 MiB MIME | Pass | **PASS — final `250` acceptance** |
| RelayBridge IPv4 approximately 25 MiB synthetic text MIME | Documented policy result | **PASS — final `554 5.2.270`; correctly classified and retained** |
| RelayBridge IPv4 approximately 25 MiB multipart/base64 scan | Pass | **PASS — final `250`; Delivered and payload cleaned up** |
| Credential leakage scan | Clean | **PASS** |

The recorded results above contain only sanitized PASS/FAIL evidence from the
2026-08-22 dedicated-tenant run. No prior ambiguous, rejected, or accepted Message-ID
was retried. Manual receipt of the small real message is confirmed.

## Existing 25 MiB ambiguity diagnosis

The original message was **not resent**. Its temporary spool and validator output
were deleted after the earlier run, and the direct-provider validator did not emit
the requested progress telemetry. The surviving evidence is:

| Field | Retained evidence |
|---|---|
| Exact `.eml` size | 26,214,400 bytes (the generator's exact target) |
| DATA timeout | 00:12:30 |
| Command/final-response timeout | 00:00:30 |
| Bytes read from spool | Unavailable |
| Bytes successfully written | Unavailable |
| Spool EOF reached | Unavailable |
| SMTP `.` terminator written | Unavailable |
| Terminator flush completed | Unavailable |
| Waiting for final response began | Unavailable |
| Exact timeout/exception/socket error | Unavailable |
| `DeliveryResult` category | `AmbiguousAcceptance` |
| Resulting queue state | Not applicable; the validator invoked the provider directly and created no queue item |
| Exact elapsed duration | Unavailable; external polling observed approximately 344 seconds for the 25 MiB phase |

The historical provider entered its ambiguous region before streaming started and
retained that classification through terminator transmission and final-response wait.
Consequently the retained category cannot distinguish a DATA write failure from a
disconnect after Exchange accepted the terminator. This does not prove failure before
remote acceptance, so the original message remains unretired and was never resent.

## Corrected timeout and fresh 25 MiB evidence

The provider now keeps the generic command timeout at 30 seconds, retains the
size-aware 12-minute-30-second DATA streaming timeout for a 25 MiB spool, and uses a
separate 10-minute post-terminator final-response timeout aligned with
[RFC 5321 section 4.5.3.2.6](https://www.rfc-editor.org/rfc/rfc5321#section-4.5.3.2.6).
Sanitized stage telemetry records payload progress and the exact
EOF, terminator, flush, final-wait, and final-response boundaries.

The first fresh message used a new Message-ID and failed definitively before remote
acceptance. It was not reused:

| Field | Recorded result |
|---|---|
| Exact `.eml` size | 26,214,400 bytes |
| Bytes read | 9,895,936 |
| Spool EOF | No |
| Terminator/flush | Not reached |
| Final-response wait | Not reached |
| Duration | 230.570 seconds |
| Delivery result | Transient `Network` |
| Queue/payload | PermanentFailure in the one-shot validation queue; payload retained |

A second fresh message used another new Message-ID and produced the conclusive size
result. It was not resent:

| Field | Recorded result |
|---|---|
| Exact `.eml` size / bytes read | 26,214,400 / 26,214,400 |
| DATA streaming time | 377.515 seconds |
| Spool EOF | Yes |
| Terminator flush | Completed |
| Final-response wait | 1.089 seconds |
| Final SMTP response | `554 5.2.270` — message exceeds the mailbox submission-size limit |
| Total provider duration | 380.506 seconds |
| Live delivery result | PermanentFailure / `PermanentServerFailure` (confirmed classification defect) |
| Queue/payload | PermanentFailure; payload retained and spool still present |

The earlier live response confirmed a narrow classification defect. The corrected provider
maps SMTP `552` and Exchange enhanced status `5.2.270` to permanent
`MessageTooLarge`; a focused fake-server regression replays the exact status. A
permanent final rejection does not create an ambiguous acceptance risk.

## External IPv4 evidence and production-path attempts

A completely standalone PowerShell 7 validator, outside RelayBridge and Codex,
proved certificate signing, Exchange token acquisition, normal STARTTLS/TLS,
XOAUTH2, allowed-mailbox `235`, denied-mailbox `535`, and an advertised SIZE of
157,286,400. The Entra application still had zero API permission entries. Its
IPv4-only fresh 26,214,400-byte message was accepted with final `250` after 3.07
seconds of DATA at 8.145 MiB/s; final-response wait was 0.58 seconds. Its 10 MiB
DATA comparison was 158.39 seconds (0.063 MiB/s) over the default IPv6/vEthernet
path and 0.98 seconds (10.173 MiB/s) in the IPv4-only process.

The required final RelayBridge run used a fresh Message-ID and the real durable
queue, certificate loader, MSAL token provider, and production Exchange SMTP
provider. IPv6 was disabled only for the process through
`DOTNET_SYSTEM_NET_DISABLEIPV6=1`; no product or system network default was changed.
The message was attempted exactly once and was not resent.

| Field | Sanitized production-path evidence |
|---|---|
| Exact `.eml` size | 26,214,400 bytes |
| DNS / TCP | PASS / PASS |
| Greeting / STARTTLS | `220` / `220`; normal TLS established |
| Token / XOAUTH2 | PASS / `235` |
| MAIL FROM / RCPT TO | `250` / `250` |
| DATA initiation | `354` |
| Spool EOF / bytes streamed | Yes / 26,214,400 |
| DATA duration / throughput | 2.752 seconds / 9.083 MiB/s |
| Terminator write / flush | Started / completed |
| Final-response wait | 0.490 seconds |
| Final SMTP response | `554 5.2.270` — message exceeds maximum submission size |
| Delivery result | PermanentFailure / `MessageTooLarge` |
| Queue / payload | PermanentFailure; payload retained; spool present |
| Total queue attempt | 4.432 seconds |
| Exception / socket error | None / none |

The fast upload confirms that the earlier multi-minute behavior was associated with
the developer IPv6/vEthernet path. The rejected RelayBridge fixture was synthetic
text MIME, while the accepted standalone fixture was realistic multipart/base64.
The `554` is not treated as a universal tenant limit, and that Message-ID was not
resent.

## Fixture-matched 25 MiB production-path evidence

A separately authorized, actual RelayBridge run used a new Message-ID and a new
normal durable spool. The runner created strict-CRLF `multipart/mixed` MIME with a
small `text/plain` part and a synthetic random `application/octet-stream` attachment
encoded as base64. The real Windows-store certificate loader, MSAL token provider,
`ExchangeSmtpOAuthProvider`, and queue worker were used. IPv6 was disabled only for
the developer process through `DOTNET_SYSTEM_NET_DISABLEIPV6=1`.

| Field | Sanitized fixture-matched evidence |
|---|---|
| Exact `.eml` size | 26,214,400 bytes |
| MIME fixture | `multipart/mixed`; small `text/plain` + base64 `application/octet-stream` attachment |
| Decoded synthetic attachment | 19,156,143 bytes |
| MAIL FROM response | `250` |
| RCPT TO response | `250` |
| DATA response | `354` |
| Payload EOF / bytes streamed | Yes / 26,214,400 |
| DATA duration / throughput | 4.102 seconds / 6.093 MiB/s |
| Terminator write / flush | Started / completed |
| Final-response wait | 0.719 seconds |
| Final SMTP response | `250 2.0.0 OK` |
| Delivery result | Success |
| Queue state | Delivered |
| Payload cleanup | Payload marked absent; spool file deleted |
| Total queue attempt | 6.039 seconds |

The matched fixture closes the production-path large-message gate. Exchange accepted
the realistic scan representation at the same exact wire size that it rejected for
the synthetic-text representation. This is consistent with Microsoft-documented
content-conversion/encoding overhead, not a RelayBridge transport failure. The
existing automated regression continues to map upstream `554 5.2.270` to permanent
`MessageTooLarge`.

## Wrong-certificate negative test

A new non-exportable temporary certificate was generated in `CurrentUser\My` using
the production certificate service. The existing tenant/application identity was
tested through the production MSAL token provider. Microsoft rejected token
acquisition with the credential-rejected classification. Cleanup removed the exact
temporary certificate and CNG key; the configured working certificate remained
present and usable. No PFX or private-key file was created.

The Entra application had zero API permission entries. Security Defaults was
disabled. The isolated SMTP-AUTH-disable experiment was inconclusive, its tenant
settings were restored, and it is not treated as a freeze blocker because the
positive SMTP AUTH and negative RBAC paths are independently proven.

The negative-test console emitted sanitized PASS/FAIL fields only and retained no
real-test log or trace file. The final workspace scan found no access-token/JWT-like
value, credential assignment, tenant identifier outside the ignored configuration,
private-key marker, PFX, or non-SDK key file. The Windows store returned to its
two-certificate baseline with exactly one private-key-bearing match for the working
certificate reference.

If the denied mailbox sends successfully, stop immediately. Do not change
RelayBridge or broaden permissions. Recheck scope membership, Exchange service
principal IDs, the scoped role assignment, additive Entra permissions, and the
actual application being tested. Do not create the `milestone-4` tag.

The fixture-matched production-path 25 MiB row passes, so Milestone 4 is frozen. Do
not resend any validation Message-ID. Do not begin Milestone 5 automatically.
