# RelayBridge — Codex Instructions

## Project

RelayBridge is an open-source, completely local SMTP-to-Microsoft-365 OAuth relay for legacy printers, MFPs, scanners, NAS devices, and applications.

The authoritative product specification is:

`docs/MASTER_SPEC.md`

Read it before making architectural or significant implementation decisions.

---

## Required Reading

At the beginning of every substantial coding task, read:

1. `docs/MASTER_SPEC.md`
2. `BUILD_STATUS.md`
3. relevant files under `docs/architecture/`
4. `docs/security/threat-model.md` when working on security-sensitive code
5. the current milestone document

Do not rely on previous chat context as the source of truth.

The repository is the source of truth.

---

## Core Engineering Rule

Do not over-engineer RelayBridge.

Prefer the smallest production-quality design that satisfies the current requirement.

Do not introduce unnecessary:

- projects
- layers
- interfaces
- abstractions
- patterns
- frameworks
- dependencies
- background services
- message buses
- CQRS
- MediatR
- microservices
- repositories wrapping repositories
- generic base classes

Before adding complexity, identify the concrete problem it solves.

If a straightforward C# implementation is sufficient, use it.

---

## Quality Standard

Simplicity must NOT reduce:

- security
- reliability
- message integrity
- recoverability
- performance
- diagnostics
- maintainability

RelayBridge should be internally boring and externally polished.

---

## Primary Architecture

Primary Microsoft 365 delivery is:

Exchange Online SMTP AUTH using:

- OAuth 2.0 client credentials
- certificate authentication
- XOAUTH2
- STARTTLS
- Exchange Application RBAC

Target transport is Exchange Online authenticated SMTP.

Microsoft Graph is NOT the V1 production delivery path.

Do not silently add Graph fallback.

See:

`docs/architecture/decisions/ADR-0001-primary-delivery-via-smtp-oauth.md`

---

## MIME Handling

RelayBridge is an SMTP-to-SMTP bridge.

Preserve the received RFC 5322/MIME message as faithfully as protocol semantics permit.

Do not parse and reconstruct MIME merely for delivery.

Use:

- SQLite for queue metadata
- filesystem spool files for raw `.eml`

Large messages must be streamed.

Do not load entire 10–30 MB scans into memory unnecessarily.

---

## Reliability Rule

Never return SMTP success after DATA until the message has been durably accepted into the local queue.

Never silently discard queued messages.

Queued mail must survive:

- service restart
- Windows reboot
- temporary Microsoft outage
- temporary Internet outage

Exactly-once delivery is not guaranteed.

Minimize duplicate risk and document recovery behavior honestly.

---

## Security Rules

RelayBridge must never become an open relay.

Never fix problems by:

- disabling TLS certificate validation
- allowing unrestricted anonymous relay
- storing plaintext secrets
- logging OAuth tokens
- logging passwords
- broadening Microsoft permissions unnecessarily
- disabling sender authorization
- disabling IP restrictions in Legacy mode

Legacy unauthenticated devices MUST have strict network restrictions.

Fail closed.

---

## Performance Rules

Optimize for realistic environments:

- approximately 1–100 devices
- hundreds to thousands of messages per day
- 10–30 MB scan messages
- multiple concurrent scans

Use bounded concurrency.

Prefer streaming.

Do not introduce performance complexity until measurements justify it.

---

## Dependencies

Minimize third-party dependencies.

Before adding a package verify:

- active maintenance
- license
- .NET 10 compatibility
- security history
- actual need

Do not reimplement mature cryptography, OAuth, TLS, or MIME functionality merely to avoid a dependency.

---

## Coding Rules

Use:

- .NET 10
- nullable reference types
- async/await
- cancellation tokens on I/O
- `DateTimeOffset`
- UTC persistence
- structured logging
- dependency injection where useful

Avoid:

- `.Result`
- `.Wait()`
- `Thread.Sleep`
- global mutable state
- service locator patterns
- empty catch blocks
- unnecessary reflection
- speculative abstractions

---

## Work Incrementally

Only implement the currently requested scope.

Do not advance to another milestone automatically.

Before implementing:

1. inspect existing code
2. read relevant documentation
3. understand what already works
4. identify the smallest coherent change
5. identify security implications

After implementing:

1. run relevant tests
2. run `dotnet build -c Release`
3. run `dotnet test -c Release`
4. fix regressions introduced by the change
5. update `BUILD_STATUS.md`

Never claim something works unless it was actually verified where locally possible.

---

## Public Contribution Workflow

The `main` branch is protected. Use this workflow:

1. create a focused branch
2. make the smallest coherent change
3. commit with a Developer Certificate of Origin `Signed-off-by` trailer
4. push the branch and let CI run
5. open a pull request to `main`
6. merge only after the required checks pass

Do not bypass required CI or push uncontrolled changes directly to `main`.

See `CONTRIBUTING.md` for the public DCO and pull-request requirements.

---

## Do Not Rewrite Working Code Without Cause

AI-assisted development can easily introduce unnecessary rewrites.

Do not restructure large working areas merely because you prefer another pattern.

Make the smallest production-quality change that completes the current milestone.

---

## Documentation

Record significant architecture decisions in:

`docs/architecture/decisions/`

Do not create ADRs for trivial choices.

Keep `BUILD_STATUS.md` current.

It must contain:

- Current milestone
- Implemented
- Verified
- Known issues
- Deferred
- Next step
