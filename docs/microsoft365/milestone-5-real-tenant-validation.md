# Milestone 5 real-tenant validation

Milestone 5 was validated on 2026-08-22 against the dedicated Microsoft 365 test
tenant. This document intentionally contains only sanitized PASS/FAIL evidence. No
tenant ID, client ID, service-principal ID, certificate thumbprint, mailbox address,
administrator identity, token, XOAUTH2 payload, password, PFX, or private key is
recorded here.

## Candidate and rollback boundary

| Check | Result |
|---|---|
| Existing working configuration stored as active baseline | **PASS** |
| Fresh non-exportable CurrentUser certificate usable | **PASS** |
| Unregistered candidate token acquisition fails before activation | **PASS** |
| Failed candidate leaves active baseline unchanged | **PASS** |
| Candidate state and identifiers remain in ignored local storage only | **PASS** |

The first generated Entra script discovered a real safe-replacement defect: its
single global application display name collided with the existing M4 application.
It failed closed and made no changes. The smallest M5-only correction assigns a
deterministic certificate-scoped Entra name and client-scoped Exchange object names.
This allows a candidate to coexist with the working identity while keeping reruns
idempotent and identity conflicts fail-closed. A regression test covers this rule.

## Administrator stages

The Entra and Exchange scripts were run manually by the administrator in separate,
fresh PowerShell 7 sessions.

| Check | Result |
|---|---|
| Fresh single-tenant application created | **PASS** |
| Fresh Entra service principal created | **PASS** |
| Public certificate registered | **PASS** |
| Entra API permission entries/app-role assignments | **PASS — zero** |
| Exchange service-principal reference valid | **PASS** |
| Dedicated marked sender group valid | **PASS** |
| Explicit management scope valid | **PASS** |
| Scoped `Application SMTP.SendAsApp` assignment valid | **PASS** |
| Configured sender in scope | **PASS** |
| Control mailbox outside scope | **PASS** |

The first Exchange run created valid objects but reached its final authorization
check before propagation completed. It produced no successful setup result and no
RelayBridge activation. A read-only audit later confirmed all six object/scope
checks, and the documented idempotent rerun then passed. No permission was broadened.

## Runtime and acceptance boundary

| Check | Result |
|---|---|
| Candidate Exchange token acquisition | **PASS** |
| DNS/TCP production endpoint | **PASS** |
| STARTTLS and normal TLS validation | **PASS** |
| Post-TLS XOAUTH2 | **PASS** |
| Configured sender authorization | **PASS** |
| Verification message final SMTP response | **PASS — 250** |
| Candidate activation occurred only after final acceptance | **PASS** |
| Wizard test-message final SMTP response | **PASS — 250** |
| Wizard completion state detected | **PASS** |

The test recipient was the second synthetic test mailbox. SMTP `250` proves
acceptance by Microsoft 365; RelayBridge does not claim inbox receipt without inbox
permissions.

## Restart validation

| Check | Result |
|---|---|
| Composed host starts with activated candidate | **PASS** |
| `/health` after restart | **PASS — HTTP 200** |
| Completed wizard page after restart | **PASS — HTTP 200** |
| Activated identity and sender persisted | **PASS** |
| Fresh-process token acquisition | **PASS** |
| Fresh-process STARTTLS/XOAUTH2/sender authorization | **PASS** |
| Fresh-process final SMTP response | **PASS — 250** |
| Graceful host shutdown | **PASS** |

## Leakage and cleanup

Tracked and ignored validation artifacts were scanned by filename/count-only rules.
No token/JWT-like content, OAuth/XOAUTH2 payload, credential assignment, private-key
marker, PFX, PEM, or key file was retained in the repository. Administrator sessions
were external to RelayBridge and were not persisted by the wizard.

The fresh cloud resources are intentionally not deleted by RelayBridge. After the
dedicated-tenant evidence is no longer needed, an administrator may manually remove,
in order, the scoped role assignment, management scope, dedicated sender group,
Exchange service-principal reference, Entra service principal/application, and the
temporary local certificate/key. Confirm each candidate-specific identifier from
the ignored local result before deletion; never infer a target from display name
alone. The original M4 resources and certificate must remain untouched.

All required M5 real-tenant gates passed. Milestone 5 is frozen.
