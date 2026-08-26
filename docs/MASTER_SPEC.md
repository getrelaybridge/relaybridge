# RelayBridge — Master Product Specification

## 0. Document Purpose and Current State

This public document records RelayBridge's product direction, architecture constraints, security
invariants, supported behavior, non-goals, and milestone structure. It is not a private engineering
prompt or a substitute for current implementation evidence.

`BUILD_STATUS.md` and the applicable documents under `docs/milestones/` are authoritative for what
is implemented and verified at the current revision. At present:

- Milestone 9 product hardening is frozen.
- Milestone 10 has not started.
- RelayBridge source is public under MPL-2.0.
- Official Windows binaries have not been released.

The milestone sections below preserve the incremental product plan. They do not authorize
re-running completed milestones, starting Milestone 10, or publishing binaries. Contributors and
coding agents must follow `AGENTS.md` and `CONTRIBUTING.md`.

RelayBridge should remain a **small, understandable, secure, reliable, high-performance production
application** that solves one problem extremely well.

The application must eventually be something an IT administrator or copier technician can confidently deploy at a customer site.

---

# 1. Product Mission

Build a completely local SMTP-to-Microsoft-365 relay for:

- multifunction printers
- scanners
- photocopiers
- NAS systems
- monitoring devices
- legacy software
- embedded equipment
- other applications that support traditional SMTP but cannot support Microsoft 365 OAuth directly

Legacy devices typically know only:

```text
SMTP server
Port
Username
Password
STARTTLS / SSL
```

RelayBridge accepts those SMTP messages locally and securely relays them to Microsoft 365 using modern OAuth authentication.

The primary Microsoft 365 path is:

```text
MFP / Legacy Device
        │
        │ SMTP
        ▼
┌─────────────────────┐
│     RelayBridge     │
│                     │
│ Local SMTP Server   │
│ Device Security     │
│ Durable Queue       │
│ OAuth Token Manager │
│ SMTP OAuth Client   │
│ Diagnostics         │
│ Local Web UI        │
└──────────┬──────────┘
           │
           │ STARTTLS
           │ AUTH XOAUTH2
           │ SMTP
           ▼
 smtp.office365.com
           │
           ▼
    Exchange Online
```

RelayBridge converts:

```text
Legacy SMTP authentication
```

into:

```text
Microsoft OAuth authenticated SMTP
```

without requiring the printer itself to understand OAuth.

---

# 2. Product Philosophy

The product must feel like an appliance.

The ideal experience is:

```text
Install
   ↓
Open RelayBridge
   ↓
Connect Microsoft 365
   ↓
Add Printer
   ↓
Copy SMTP Settings
   ↓
Scan
   ↓
✓ Delivered
```

A technician should not need to understand:

- OAuth
- XOAUTH2
- MSAL
- Entra ID
- access tokens
- RBAC
- service principals
- SMTP SASL
- certificate authentication

RelayBridge should understand those things for them.

---

# 3. Absolutely No Cloud Service

RelayBridge is open source and completely local.

There must be:

- no RelayBridge cloud account
- no subscription
- no licensing server
- no hosted dashboard
- no remote control plane
- no mandatory telemetry
- no tracking account
- no customer email flowing through infrastructure controlled by this project

Normal external communication should only occur where necessary for:

- Microsoft authentication
- Microsoft 365 SMTP delivery
- optional manually initiated update checks if implemented later

The application must continue functioning without any RelayBridge-operated infrastructure.

---

# 4. THE MOST IMPORTANT ENGINEERING RULE

## Do not over-engineer this product.

This instruction has very high priority.

Do not create architecture simply because it looks sophisticated.

Do not implement speculative abstractions for hypothetical future products.

Do not create ten micro-projects because “Clean Architecture” diagrams commonly show ten projects.

Do not introduce technologies merely because they are fashionable.

Before adding any abstraction, project, service, interface, package, caching layer, event bus, repository layer, or framework, ask:

> What concrete problem does this solve in RelayBridge today?

If there is no strong answer, do not add it.

Prefer:

```text
boring
clear
tested
predictable
```

over:

```text
clever
abstract
fashionable
complex
```

---

# 5. Simplicity Does NOT Mean Low Quality

Do not misunderstand the previous section.

We still require excellent:

- security
- reliability
- performance
- error handling
- tests
- logging
- recovery behavior
- user experience

The philosophy is:

> Minimum complexity necessary for maximum reliability.

Not:

> Minimum effort necessary to make a demo work.

The application should be boring internally and impressive externally.

---

# 6. Core Priorities

When requirements conflict, prioritize:

1. Security
2. Message integrity
3. Reliability
4. Recoverability
5. Simplicity
6. Compatibility
7. Troubleshooting
8. Performance
9. UX polish
10. Additional features

Performance matters, but correctness matters more.

UX matters, but security cannot be hidden to produce fewer clicks.

---

# 7. Technology Stack

Use:

- .NET 10 LTS
- C#
- ASP.NET Core
- Blazor Web App
- ASP.NET Core hosted services
- SQLite
- Entity Framework Core where it genuinely helps
- MSAL.NET
- Microsoft.Extensions.Logging
- Microsoft.Extensions.Options
- IHttpClientFactory where HTTP is required
- System.Threading.Channels if useful for bounded internal work queues
- mature SMTP/MIME libraries where appropriate

Primary platform:

```text
Windows x64
```

Future possibility:

```text
Linux x64
Linux ARM64
```

Do not compromise the Windows experience merely to achieve theoretical cross-platform purity.

But avoid unnecessary Windows-only dependencies inside the core mail engine.

---

# 8. One Process

RelayBridge V1 has one long-running product process: the **RelayBridge Windows Service**.

The service should host:

```text
ASP.NET Core web UI
SMTP listener
queue workers
health monitoring
Microsoft authentication
outbound SMTP
```

Do NOT create separate long-running processes for these responsibilities.

Short-lived setup executables are an intentional security boundary: a NativeAOT launcher and its
subordinate managed worker perform interactive Microsoft administration, then exit. They are not
mail-delivery services, listeners, or persistent administration processes.

Do NOT create:

```text
SMTP microservice
queue microservice
auth microservice
web microservice
worker microservice
```

That architecture is unnecessary for this product.

One process reduces:

- deployment complexity
- failure modes
- configuration complexity
- IPC complexity
- support burden

---

# 9. Keep the Solution Small

The core solution remains approximately:

```text
RelayBridge.sln

src/
  RelayBridge.Core/
  RelayBridge.Infrastructure/
  RelayBridge.Host/

tests/
  RelayBridge.Tests/
  RelayBridge.IntegrationTests/

docs/

installer/
```

The current repository also contains narrow Setup, SetupLauncher, and ToolingProvisioner projects
plus small test probes. Their concrete security/process boundaries are documented in
`docs/architecture/overview.md`; they are not a basis for adding more layers or services.

Responsibilities:

## RelayBridge.Core

Contains:

- domain models
- queue states
- authorization rules
- interfaces at true external boundaries
- validation
- retry policies
- business logic

Must not depend on ASP.NET Core, SQLite, MSAL, or SMTP libraries where avoidable.

## RelayBridge.Infrastructure

Contains:

- SQLite persistence
- Microsoft authentication
- Exchange Online SMTP client
- queue storage
- certificates
- Windows secret protection
- SMTP server adapter
- filesystem operations

## RelayBridge.Host

Contains:

- ASP.NET Core host
- Blazor UI
- hosted services
- configuration
- dependency injection
- health endpoints
- Windows Service integration

That is enough.

Do not split projects further unless a real problem emerges.

---

# 10. Avoid Interface Explosion

Do not create an interface for every class.

Interfaces are appropriate at meaningful boundaries such as:

```text
IMailDeliveryProvider
IMessageStore
ITokenProvider
ICredentialProtector
IClock
```

Do not create things like:

```text
IDeviceNameFormatter
IQueueStatusMapper
IDashboardCardFactory
ISettingsReaderManager
```

unless there is a concrete reason.

A well-designed concrete class is perfectly acceptable.

---

# 11. Primary Microsoft 365 Architecture

## IMPORTANT ARCHITECTURAL DECISION

Microsoft Graph is NOT the primary mail-delivery provider.

Primary production transport:

```text
Exchange Online SMTP AUTH
+
OAuth 2.0 client credentials
+
XOAUTH2
+
STARTTLS
+
Exchange Application RBAC
```

Target SMTP service:

```text
smtp.office365.com
```

Typical submission port:

```text
587
```

Do not hardcode network assumptions that Microsoft documents as configurable or potentially subject to change.

Consult current official Microsoft documentation before implementation.

---

# 12. Why SMTP OAuth Is Primary

RelayBridge receives SMTP messages.

The natural transformation is therefore:

```text
SMTP
  ↓
SMTP
```

rather than:

```text
SMTP
  ↓
parse MIME
  ↓
create Graph message object
  ↓
extract attachments
  ↓
upload attachments
  ↓
reconstruct message
  ↓
send
```

Scan-to-email messages routinely include large PDFs.

SMTP OAuth avoids making Graph attachment APIs part of the core delivery path.

SMTP also allows us to preserve the received MIME representation much more faithfully.

---

# 13. Raw MIME Preservation

RelayBridge should preserve the original RFC 5322/MIME message wherever protocol semantics permit.

The design should be:

```text
SMTP envelope metadata
+
raw message content
```

Do not parse and reconstruct MIME merely to deliver it upstream.

Store something conceptually like:

```text
queue/
  01JABC.../
      envelope metadata
      message.eml
```

The `.eml` file should contain the message received during SMTP DATA after correctly processing SMTP transport framing.

Preserve:

- MIME boundaries
- headers
- attachment encoding
- body structure
- multipart relationships
- filename parameters
- character encoding

Do not promise final mailbox content will be literally byte-for-byte identical because SMTP/Exchange transport may legitimately add or transform transport metadata.

But RelayBridge itself should avoid unnecessary MIME rewriting.

---

# 14. MIME Parsing Philosophy

Use MIME parsing only when necessary for:

- validation
- extracting safe metadata
- diagnostics
- privacy-aware history
- optional message inspection

The raw spool file remains the delivery source of truth.

Do not serialize a parsed MIME object back into a replacement message unless absolutely necessary.

---

# 15. Outbound SMTP Library Decision

Do not blindly assume MailKit or another package can preserve raw MIME exactly as required.

Before implementing outbound SMTP:

1. Evaluate whether a mature .NET SMTP client allows streaming an existing RFC message without destructive reserialization.
2. Evaluate OAuth/XOAUTH2 support.
3. Evaluate STARTTLS validation.
4. Evaluate cancellation and timeouts.
5. Evaluate large-message streaming.
6. Evaluate connection reuse.
7. Evaluate error reporting.
8. Evaluate licensing.
9. Evaluate maintenance status.

If an existing library meets the requirements, use it.

If it does not, implement only the **small outbound SMTP client subset actually required**.

Do not build a general SMTP client library.

A custom implementation, if necessary, should only support:

```text
DNS/connect
server greeting
EHLO
STARTTLS
EHLO again
AUTH XOAUTH2
MAIL FROM
RCPT TO
DATA
QUIT
```

plus required error handling and standards compliance.

Document this decision in ADR-0002.

---

# 16. SMTP DATA Handling

Correctly implement SMTP DATA semantics.

Be careful about:

- CRLF normalization
- terminating dot
- dot-stuffing
- lines beginning with `.`
- server size responses
- premature disconnects

Do not accidentally corrupt:

```text
.
..text
.boundaries
```

inside MIME content.

Create specific integration tests for this.

---

# 17. Microsoft Authentication

Default authentication should use:

```text
customer-owned Entra application
+
certificate credentials
```

RelayBridge generates or manages a local authentication certificate.

The private key remains local.

The administrator configures the public certificate in Microsoft Entra.

Store:

```text
Tenant ID
Application / Client ID
certificate reference
sender mailbox
```

Never embed a project-wide app secret.

Never send private keys to any RelayBridge service.

---

# 18. Exchange Application RBAC

The preferred authorization model is Exchange Online **Application RBAC**.

The application should be scoped to only the mailbox or mailboxes that RelayBridge is expected to send as.

Desired security outcome:

```text
RelayBridge CAN send as:
scanner@company.com

RelayBridge CANNOT send as:
ceo@company.com
finance@company.com
random.user@company.com
```

Current Microsoft documentation must be consulted when implementing the actual setup.

Do not combine mutually incompatible Microsoft authorization paths.

In particular:

- distinguish classic `SMTP.SendAsApp` Entra permission onboarding
- from Exchange Application RBAC onboarding

Do not blindly configure both.

The secure RBAC path should be the recommended approach.

---

# 19. OAuth Token Scope

At implementation time verify current Microsoft documentation.

The currently expected application token scope for Exchange SMTP application authentication is conceptually:

```text
https://outlook.office365.com/.default
```

Do not blindly hardcode this based only on this prompt.

Verify against current official documentation and create an ADR describing the implementation.

---

# 20. SMTP AUTH Availability

RelayBridge must explicitly test whether SMTP AUTH is usable.

Possible conditions include:

- enabled for organization
- disabled for organization
- enabled/disabled for specific mailbox
- security policy preventing use
- firewall blocking outbound port 587
- TLS interception problems
- DNS problems

The setup wizard must detect these where practical.

Never display:

```text
Microsoft connected
```

merely because OAuth token acquisition succeeded.

A real SMTP OAuth test must succeed.

---

# 21. Microsoft Setup Wizard

The Microsoft setup should be guided.

Do not request a Microsoft administrator password.

Do not embed an administrator browser session.

Do not request unnecessarily broad Graph permissions merely to automate setup.

The wizard should guide:

```text
1. Generate RelayBridge certificate
2. Create/register Entra application
3. Register Exchange service principal
4. Create mailbox/resource scope
5. Assign Application SMTP.SendAsApp role
6. Enter Tenant ID
7. Enter Client ID
8. Verify OAuth token
9. Verify SMTP AUTH
10. Verify allowed sender
11. Send test message
```

Provide copyable PowerShell commands where appropriate.

The generated commands must be:

- explicit
- easy to audit
- least privilege
- documented

---

# 22. Microsoft Setup UX

Show something like:

```text
Microsoft 365 Setup

Application                ✓
Certificate                ✓
Exchange service principal ✓
Mailbox scope              ✓
OAuth authentication       ✓
SMTP AUTH                  ✓
Sender permission          ✓
Test delivery              ✓

Microsoft 365 is ready.
```

If something fails, identify exactly which layer failed.

---

# 23. Do Not Automatically Use Graph as Fallback

V1 should not silently switch from SMTP to Graph.

Reasons:

- authorization model differs
- Graph permissions differ
- large-message behavior differs
- MIME behavior differs
- troubleshooting becomes harder
- code complexity doubles

For V1:

```text
IMailDeliveryProvider
   │
   ├── ExchangeSmtpOAuthProvider
   └── LocalPreviewProvider
```

Graph may be implemented in a later version as an explicitly selected provider.

Do not build Graph delivery in V1.

---

# 24. Local SMTP Listener

RelayBridge accepts SMTP from printers.

Support only functionality needed for realistic MFP operation.

Current inbound command support:

```text
HELO
EHLO
MAIL FROM
RCPT TO
DATA
RSET
NOOP
QUIT
AUTH LOGIN
AUTH PLAIN
SIZE
```

Inbound STARTTLS is not implemented or advertised in the current frozen M9 source. A `STARTTLS`
command receives a not-available response. `8BITMIME` and `SMTPUTF8` are also not advertised.

Potential future extensions:

```text
8BITMIME
SMTPUTF8
```

only if implementation supports them correctly.

Do not advertise unsupported SMTP extensions.

---

# 25. Default SMTP Listener

Current default:

```text
Port: 2525
Binding: Loopback
Cleartext SMTP authentication: Disabled
Anonymous relay: Disabled
Internet exposure: Not supported/recommended
STARTTLS: Not available
```

Do not automatically use port 25.

Allow administrator configuration.

---

# 26. Printer Security Modes

The current device wizard exposes two understandable modes.

## Authenticated / Compatible

```text
SMTP authentication required
IP restriction
sender restriction
```

Because inbound STARTTLS is not yet available, cleartext SMTP authentication is disabled by
default and can be enabled only on one explicit trusted private interface with a prominent warning.

## Legacy

```text
No SMTP authentication
STRICT IP restriction required
sender restriction required
private LAN only
strong warning
```

Legacy mode must fail validation if no IP/subnet restriction is present.

---

# 27. Never Become an Open Relay

This is a hard security requirement.

Default configuration must NEVER allow:

```text
anonymous SMTP
+
any IP
+
any sender
+
any recipient
```

Relay authorization must evaluate:

```text
device enabled?
source IP allowed?
authentication valid?
TLS policy satisfied?
sender allowed?
recipient allowed?
message size allowed?
rate limit allowed?
```

Fail closed.

---

# 28. Device Model

Each printer/application should be a first-class device.

Store:

```text
ID
Name
Description
Enabled
Allowed IPs/subnets
SMTP username
Password verifier
TLS requirement
Allowed sender addresses
Optional recipient restrictions
Maximum message size
Rate limits
Created date
Last connection
Last successful delivery
Last error
```

Passwords should be generated using a cryptographically secure RNG.

Store password verifiers, not plaintext passwords.

Password rotation should generate a new secret.

Existing passwords generally should not be recoverable from storage.

---

# 29. Inbound Message Acceptance

Do not acknowledge SMTP DATA success until the message has been safely persisted.

Desired sequence:

```text
Receive DATA
     ↓
write spool file
     ↓
flush durable data
     ↓
write queue metadata transaction
     ↓
queue accepted
     ↓
250 OK
```

If durable persistence fails:

```text
do not tell the printer the message was accepted
```

This is critical for reliability.

---

# 30. Durable Queue

Messages must survive:

- service restart
- Windows reboot
- temporary Internet failure
- Microsoft outage
- application crash

Suggested states:

```text
Receiving
Queued
Delivering
RetryScheduled
Delivered
PermanentFailure
Expired
```

Avoid excessive queue states.

Do not build a workflow engine.

---

# 31. Queue Storage

Use:

```text
SQLite → metadata
filesystem → raw .eml payload
```

Avoid storing large scans as SQLite blobs unless real benchmarks demonstrate it is better.

Example:

```text
Data/
  relaybridge.db

  spool/
    pending/
    failed/
```

Use server-generated random IDs.

Never use:

- email subject
- attachment filename
- recipient
- sender

as filesystem paths.

---

# 32. Atomic Queue Operations

Design queue operations carefully.

Avoid situations where:

```text
database says message exists
but .eml does not
```

or:

```text
.eml exists
but queue row disappeared
```

Use:

- temporary spool files
- atomic rename/move
- database transaction
- startup reconciliation

Document the chosen ordering.

---

# 33. Crash Recovery

At startup:

- detect messages left in Delivering state
- safely return abandoned items to queue
- detect orphan spool files
- detect missing payload files
- log inconsistencies
- recover automatically where safe
- avoid duplicate delivery as much as realistically possible

Do not claim exactly-once delivery.

SMTP fundamentally cannot guarantee perfect exactly-once behavior across every network failure.

Design for:

> at-least-once delivery with duplicate risk minimized.

Document this honestly.

---

# 34. Retry Strategy

Use bounded exponential backoff with jitter.

Classify errors.

## Retry

Examples:

```text
network unavailable
SMTP 421
temporary Exchange failure
throttling
DNS temporary failure
```

## Do Not Retry Indefinitely

Examples:

```text
invalid sender authorization
bad Microsoft configuration
permanent SMTP rejection
invalid mailbox
message permanently too large
```

Respect server-provided retry information when available.

---

# 35. Queue Limits

Prevent disk exhaustion.

Configure:

```text
Maximum message size
Maximum queued messages
Maximum spool bytes
Minimum free disk space
Maximum retry age
```

When capacity is reached:

```text
reject new SMTP submissions cleanly
```

Do not fill the Windows disk.

---

# 36. Message Size

Treat size as:

```text
total SMTP message size
```

not:

```text
PDF attachment size
```

A 20 MB PDF can become significantly larger after MIME/base64 encoding.

Default message size should be conservative and configurable.

Do not blindly assume a universal Microsoft 365 size limit.

During setup, document that:

- Exchange limits may vary
- tenant administrators can change message size limits
- recipient systems may impose smaller limits

RelayBridge should clearly report an upstream “message too large” rejection.

---

# 37. Streaming

Large scans must not require loading the entire message into memory.

Design:

```text
SMTP DATA
   ↓ stream
spool file
   ↓ stream
SMTP DATA outbound
```

Do not repeatedly copy entire MIME messages into:

```text
string
byte[]
MemoryStream
```

unless required for a small bounded operation.

Target stable memory usage even with multiple 20–30 MB messages.

---

# 38. Performance Requirements

RelayBridge is not hyperscale infrastructure.

Target realistic environments:

```text
1–100 printers
hundreds to several thousand messages/day
multiple simultaneous scans
10–30 MB messages common
```

Performance goals:

- low idle CPU
- bounded memory use
- no whole-message memory buffering
- responsive UI during mail delivery
- bounded concurrency
- no thread-per-connection architecture
- asynchronous network I/O
- asynchronous disk I/O where beneficial
- efficient SQLite access

Do not optimize imaginary million-message-per-second scenarios.

---

# 39. Performance Benchmarks

Create lightweight repeatable benchmarks or integration tests for:

```text
1 MB message
10 MB message
25 MB message
50 MB message
```

where upstream configuration permits.

Measure:

```text
peak memory
receive throughput
spool write time
outbound throughput
concurrent sessions
CPU utilization
```

Tests should catch catastrophic regressions.

Do not build a giant benchmarking framework.

---

# 40. Concurrency

Use bounded concurrency.

Example concepts:

```text
maximum simultaneous inbound SMTP connections
maximum simultaneous outbound deliveries
```

Defaults should be safe.

Do not spawn unlimited tasks.

Use semaphores, channels, or equivalent simple primitives.

---

# 41. SMTP Connection Reuse

Evaluate whether reusing authenticated outbound SMTP sessions improves reliability/performance.

Do not prematurely implement complicated connection pooling.

Initial implementation may use:

```text
one connection per delivery
```

if performance is adequate.

Only add pooling if measurements justify it.

This is an example of the anti-overengineering rule.

---

# 42. Timeouts Everywhere

Network operations must never hang forever.

Configure sensible timeouts for:

- inbound SMTP idle timeout
- command timeout
- DATA timeout
- DNS
- TCP connect
- TLS negotiation
- OAuth token request
- outbound SMTP command
- outbound DATA transfer

Long uploads need throughput-aware timeouts.

Do not kill a slow but valid 25 MB scan because of an unrealistically short fixed timeout.

---

# 43. TLS

Inbound STARTTLS is not implemented in the current frozen M9 source and is not advertised. Until a
certificate-backed inbound TLS design is implemented, cleartext SMTP authentication remains
disabled by default and may be enabled only on an explicit trusted private interface.

Outbound Microsoft SMTP:

```text
STARTTLS required
certificate validation required
```

Never provide:

```text
IgnoreCertificateErrors=true
```

as a troubleshooting shortcut.

Never disable TLS verification to “fix” corporate proxy issues.

Provide understandable diagnostics instead.

---

# 44. Authentication Certificate

Prefer certificate authentication over client secrets.

Manage:

- certificate creation
- storage
- expiry monitoring
- rotation
- verification

Do not delete the old certificate before the new one has been tested successfully.

Show certificate expiry prominently in advance.

---

# 45. Secret Storage

On Windows use appropriate OS security primitives.

Consider:

- Windows Certificate Store
- DPAPI
- restrictive filesystem ACLs

Never store secrets in plaintext:

```text
appsettings.json
database columns
logs
diagnostics
```

Create a small `ICredentialProtector` boundary for future Linux support.

Do not build a generic secret-management framework.

---

# 46. Local Management UI

The product UI should be:

```text
clean
fast
calm
professional
obvious
```

Not:

```text
generic admin template
enterprise ERP
developer dashboard
```

Use Blazor and simple components.

Do not introduce React/Vue/Angular unless there is a genuinely compelling reason.

Avoid a Node.js build dependency.

---

# 47. Dashboard

Main page should answer:

```text
Is RelayBridge running?
Is Microsoft 365 working?
Are my printers healthy?
Are messages queued?
Did anything fail?
```

Example:

```text
RelayBridge

● Relay Running

Microsoft 365
● Connected
scanner@company.com

Devices
5 configured
5 healthy

Queue
0 waiting

Today
214 delivered
1 failed

Recent Activity
────────────────────────────────────
11:42 Ricoh Reception    Delivered
11:39 Canon Accounts     Delivered
11:34 Xerox Finance      Failed
```

---

# 48. Device Page

Example:

```text
Ricoh Reception

● Healthy

IP Address
192.168.10.31

Authentication
Username + Password

TLS
STARTTLS

Sender
scanner@company.com

Last Connection
2 minutes ago

Last Delivery
2 minutes ago

Messages Today
37

[ Test ]
[ View Activity ]
[ Reset Password ]
```

---

# 49. Add Printer Wizard

Ask only what matters.

Example:

```text
Device name
[ Ricoh Reception ]

Does this printer support SMTP authentication?
● Yes
○ No
○ Not sure

Inbound TLS
Not available in this release
```

RelayBridge chooses the recommended security profile.

Final screen:

```text
Configure your printer with:

SMTP Server
192.168.10.20     [ Copy ]

Port
2525             [ Copy ]

Security
No TLS — trusted private LAN only

Username
ricoh-reception  [ Copy ]

Password
••••••••••••     [ Copy ]

Sender
scanner@company.com

[ Test Connection ]
```

---

# 50. Printer Password UX

Newly generated passwords may be shown once.

Provide:

```text
Copy Password
Download Setup Instructions
Print Setup Instructions
```

After leaving the setup screen:

```text
password cannot be viewed
```

Admin can:

```text
Reset Password
```

instead.

---

# 51. Live Troubleshooting

The following is target UX for a later live per-session timeline. Current M9 provides privacy-safe
diagnostic summaries and persisted message metadata; polished live SMTP timelines remain deferred.

Success example:

```text
Ricoh Reception

11:42:03 Connected from 192.168.10.31       ✓
11:42:03 STARTTLS                           ✓
11:42:04 Authentication                     ✓
11:42:04 Sender authorization               ✓
11:42:05 Message received — 18.4 MB         ✓
11:42:05 Message safely queued              ✓
11:42:06 Microsoft OAuth                    ✓
11:42:06 Exchange SMTP connection           ✓
11:42:08 Message submitted                  ✓

Delivered in 5.1 seconds
```

Failure example:

```text
11:47:10 Connected from 192.168.10.31       ✓
11:47:11 Authentication                     ✕

The password configured on this printer
does not match RelayBridge.

[ Reset Password ]
[ Show Printer Settings ]

Technical details >
```

---

# 52. Plain-English Errors

Never primarily show:

```text
535 5.7.3 Authentication unsuccessful
```

Instead:

```text
Microsoft 365 rejected RelayBridge authentication.

Possible causes:
• SMTP AUTH is disabled
• The application is not authorized for this mailbox
• The certificate configuration is incorrect

[ Run Microsoft Diagnostics ]

Technical details
535 5.7.3 ...
```

Technical details remain available.

---

# 53. Message History

Store minimal metadata needed for troubleshooting.

Suggested:

```text
time
device
sender
recipient count
message size
status
attempt count
duration
last error category
correlation ID
```

Subject logging should be optional and disabled by default or privacy-conscious.

Never retain body content merely for convenience.

---

# 54. Message Retention

After successful delivery:

```text
raw .eml should normally be deleted
```

Metadata may be retained for a configurable period.

Failed messages may be retained for troubleshooting according to policy.

Provide configuration:

```text
Delivered metadata retention
Failed message retention
```

Safe defaults should minimize sensitive-data retention.

---

# 55. Logging

Use structured logging.

Important event types:

```text
SmtpConnectionAccepted
DeviceAuthenticated
DeviceAuthenticationFailed
MessageAccepted
MessageQueued
DeliveryStarted
DeliverySucceeded
DeliveryFailed
RetryScheduled
MicrosoftAuthenticationFailed
ConfigurationChanged
CertificateExpiring
```

Every message should get a correlation ID.

Never log:

- device plaintext password
- certificate private key
- OAuth access token
- authorization header
- full message body
- attachment content

---

# 56. Diagnostics Bundle

Provide:

```text
Download Diagnostic Bundle
```

Include:

- RelayBridge version
- .NET version
- OS version
- sanitized configuration
- health state
- recent logs
- queue statistics
- database schema version
- SMTP listener settings
- Microsoft certificate metadata
- recent error categories

Exclude:

- private keys
- access tokens
- secrets
- message content
- password hashes

Aggressively redact sensitive values.

---

# 57. Health Checks

Monitor:

```text
SMTP listener
queue database
spool filesystem
free disk
Microsoft authentication
Microsoft SMTP connectivity
configured sender authorization
certificate expiry
queue backlog
service uptime
```

Overall states:

```text
Healthy
Attention
Critical
```

Do not report Healthy merely because the process is alive.

---

# 58. Startup Behavior

RelayBridge should start even when:

```text
Internet is down
Microsoft 365 is unavailable
DNS is temporarily failing
```

Local SMTP should continue operating while queue capacity permits.

Microsoft connectivity must NOT be a hard service startup dependency.

---

# 59. Graceful Shutdown

When Windows stops RelayBridge:

1. stop accepting new SMTP sessions
2. finish or safely terminate active inbound transactions
3. stop claiming new queue messages
4. allow currently running deliveries a bounded grace period
5. safely return interrupted deliveries to recoverable state
6. close database/resources

Do not lose mail during routine service restart.

---

# 60. Security Threat Model

Maintain:

```text
docs/security/threat-model.md
```

Cover at least:

```text
compromised printer
hostile LAN device
open relay abuse
stolen SMTP password
sender spoofing
malicious MIME
huge message attack
connection exhaustion
authentication brute force
RelayBridge server theft
database theft
certificate theft
Microsoft identity compromise
local administrator compromise
log leakage
diagnostic bundle leakage
dependency compromise
```

For each:

```text
Threat
Impact
Mitigation
Residual Risk
```

---

# 61. Security Rate Limiting

Protect:

- login attempts
- connections per source IP
- concurrent sessions
- messages per device
- queue consumption

Do not implement an elaborate distributed rate-limiter.

This is one server.

Use simple in-process mechanisms.

---

# 62. SMTP Fuzz / Abuse Testing

Test:

```text
very long commands
invalid UTF-8
invalid AUTH payloads
connection disconnect during DATA
missing terminator
many recipients
many commands
huge headers
malformed MIME
dot-stuffing
slow sender
oversized message
```

RelayBridge should reject or safely handle them without crashing.

---

# 63. Tests

Do not chase 100% code coverage.

Focus tests on areas where bugs matter.

High-value unit tests:

```text
IP/CIDR authorization
password verification
sender policy
recipient policy
retry classification
queue transitions
redaction
certificate expiry
message-size limits
rate limits
```

High-value integration tests:

```text
SMTP AUTH LOGIN
SMTP AUTH PLAIN
STARTTLS
anonymous legacy mode
invalid password
wrong IP
DATA reception
large messages
dot-stuffing
restart recovery
queue retry
Microsoft SMTP mock server interactions
```

---

# 64. Microsoft Tests

Normal CI must NOT require a real Microsoft tenant.

Create:

```text
fake OAuth token provider
local SMTP test server
scripted SMTP responses
```

Optional real-tenant tests should be manually enabled.

Never put real tenant credentials in CI repository secrets unless intentionally configured by maintainers.

---

# 65. Do Not Mock Everything

Use mocks at actual external boundaries.

Do not mock ordinary domain classes.

Prefer integration tests with:

```text
temporary SQLite
temporary spool directory
real SMTP socket
local test SMTP server
```

for core flows.

---

# 66. Reliability Tests

Specifically test:

```text
service restarted after DATA acceptance
service killed during delivery
Internet failure
DNS failure
SMTP connection reset
temporary 4xx response
permanent 5xx response
SQLite temporarily locked
disk nearly full
spool missing
duplicate retry
```

Reliability is a feature.

---

# 67. Installer

Current unsigned candidate name:

```text
RelayBridge-Setup-<version>-win-x64.exe
```

User should not manually install:

- .NET runtime
- IIS
- Python
- Docker
- SQL Server
- Node.js

The current WiX 6 MSI/Burn pipeline:

```text
install files
create data directories
apply secure ACLs
install Windows Service
configure automatic startup
start service
register the parameter-free local setup URI
```

Upgrade must preserve configuration.

No firewall rule or unstable management shortcut is created. Uninstall preserves ProgramData and
does not alter certificates or Microsoft tenant objects.

---

# 68. Installer Technology

WiX Toolset 6.0.2 is the selected Windows installer technology. The decision, licensing boundary,
service/upgrade behavior, and non-goals are recorded in
`docs/architecture/decisions/ADR-0005-wix-msi-windows-installer.md` and the M8 milestone document.

---

# 69. Code Signing

Design release pipeline so binaries/installers can eventually be signed.

Do not require a signing certificate for local development.

Unsigned development builds should remain easy.

---

# 70. Local Web Security

The current management UI is deliberately loopback-only and has no production remote-management
authentication model. Code-owned binding validation rejects non-loopback effective listeners at
startup. Authenticated remote management remains deferred; do not expose the current UI beyond
loopback or weaken the binding checks.

---

# 71. Secure Defaults

First launch must never result in:

```text
open SMTP relay
non-loopback anonymous admin panel
unrestricted Internet listener
```

Secure defaults should be automatic.

Advanced administrators may loosen specific settings intentionally after seeing warnings.

---

# 72. Configuration Validation

At startup validate configuration.

Fail loudly for dangerous or impossible settings.

Examples:

```text
Legacy mode with no IP restriction
invalid subnet
duplicate SMTP username
invalid listening port
missing Microsoft certificate
spool directory inaccessible
```

Configuration errors should not produce mysterious runtime exceptions.

---

# 73. Dependency Policy

Minimize dependencies.

Every package must have a reason.

Before adding one verify:

```text
current maintenance
license
security
.NET 10 compatibility
transitive dependency burden
```

Avoid pulling a giant framework to solve a 20-line problem.

But equally:

Do not reimplement cryptography, MIME parsing, TLS, OAuth, or mature protocol functionality merely to avoid one dependency.

---

# 74. Coding Style

Use:

- nullable reference types
- async/await
- cancellation tokens
- `DateTimeOffset`
- UTC storage
- records where useful
- immutable value objects where useful
- explicit error/result types where they improve clarity
- structured logging
- async streaming where useful

Avoid:

```text
.Result
.Wait()
Thread.Sleep
static service locator
global mutable state
catch(Exception) { }
dynamic
reflection-heavy magic
```

---

# 75. Don't Invent Frameworks

Do not build custom:

```text
Mediator framework
EventBus framework
CQRS framework
Result framework
validation framework
dependency injection container
ORM
logging framework
RPC system
workflow engine
```

Use straightforward C#.

If a simple method call works, use a method call.

---

# 76. Do Not Automatically Use CQRS/MediatR

There is no requirement for:

```text
MediatR
CQRS
event sourcing
domain events
message buses
```

Add such patterns only if a concrete problem demonstrates their value.

V1 likely does not need them.

---

# 77. Do Not Create Repository Pattern Over EF Core by Default

If EF Core is already providing the required abstraction, don't wrap every DbSet in boilerplate repositories.

Create explicit persistence abstractions only where they provide genuine domain/testing value.

---

# 78. Avoid Generic Base Classes

Avoid constructions like:

```text
BaseEntity<T>
GenericRepository<T>
BaseService<T>
AbstractManagerFactory<T>
```

unless they remove real duplication without hiding behavior.

Explicit code is usually easier to debug.

---

# 79. UI Performance

Dashboard should not poll the entire database every second.

Use sensible refresh intervals or SignalR only if needed.

Do not introduce realtime infrastructure merely for visual effects.

Live troubleshooting may justify realtime updates.

If so, use built-in ASP.NET Core capabilities.

---

# 80. Accessibility

Include:

- keyboard navigation
- proper labels
- visible focus
- semantic HTML
- adequate contrast
- status text in addition to color
- responsive layouts

Do not make accessibility an afterthought.

---

# 81. UX Rule

Hide unnecessary complexity.

Do not hide important security consequences.

Example:

Good:

```text
Legacy Mode

This printer cannot authenticate.

RelayBridge will only accept email from:
192.168.10.31

[ Enable Legacy Mode ]
```

Bad:

```text
Allow Anonymous
☑
```

---

# 82. Product Appearance

Avoid:

- excessive gradients
- giant hero banners inside admin UI
- glassmorphism
- excessive animation
- meaningless charts
- six different status colors
- generic Bootstrap admin theme

Prefer calm appliance UI.

---

# 83. Configuration Backup

Safe configuration export is not implemented in the current frozen M9 source and remains deferred.

Any future default export must exclude secrets.

An encrypted full backup may come later.

Do not delay V1 for sophisticated backup encryption.

This is an example of intentionally controlling scope.

---

# 84. Updates

Do not implement a RelayBridge update cloud service.

V1 supports:

```text
download new installer
run installer
configuration preserved
database migrated
service restarted
```

Simple.

Reliable.

---

# 85. Open-Source Repository

Maintain:

```text
README.md
LICENSE
SECURITY.md
CONTRIBUTING.md
CODE_OF_CONDUCT.md
CHANGELOG.md
docs/
```

README should clearly explain:

- purpose
- screenshots later
- architecture
- installation
- Microsoft configuration
- printer setup
- security model
- limitations
- development setup
- contribution process

---

# 86. ADRs

Keep architecture decision records short.

Create only for meaningful decisions.

Initial ADRs:

```text
ADR-0001-primary-delivery-via-exchange-smtp-oauth.md

ADR-0002-outbound-smtp-library-or-narrow-client.md

ADR-0003-sqlite-metadata-filesystem-spool.md

ADR-0004-certificate-authentication.md

ADR-0005-installer-technology.md
```

Do not create ADRs for trivial implementation details.

---

# 87. ADR-0001 Required Decision

ADR-0001 should record:

## Decision

Use Exchange Online authenticated SMTP submission with:

```text
OAuth client credentials
certificate authentication
XOAUTH2
STARTTLS
Exchange Application RBAC
```

as RelayBridge's primary Microsoft 365 delivery transport.

## Reasons

- natural SMTP-to-SMTP bridge
- works well for large scan messages
- preserves original MIME structure
- avoids Graph large-attachment reconstruction
- simpler queue model
- fewer message transformations
- easier protocol-level troubleshooting
- mailbox scope can be constrained through Exchange RBAC

## Consequences

- SMTP AUTH must be available
- outbound TCP/587 must be allowed
- Exchange-specific onboarding required
- OAuth/XOAUTH2 implementation required

## Graph

Graph is not part of V1 production delivery.

Future optional provider only.

---

# 88. No Premature Features

Before adding any feature ask:

```text
Does this improve the experience of:
install → configure → scan → deliver → troubleshoot?
```

If not, probably defer it.

Examples to defer:

```text
themes beyond basic dark/light
multi-user RBAC for local admins
plugin marketplace
cloud sync
mobile app
Prometheus exporter
Grafana dashboard
Kubernetes deployment
REST API for everything
webhooks
multi-node HA
distributed queue
```

---

# 89. Definition of Production Quality

Production quality does NOT mean:

> maximum code

It means:

```text
predictable behavior
secure defaults
clear failure modes
recoverable data
bounded resource usage
useful diagnostics
documented configuration
tests around critical paths
clean upgrades
```

---

# 90. Build Incrementally

Never attempt to generate the entire application in one pass.

Every milestone must leave the repository:

```text
building
tested
understandable
runnable
```

Milestones 0 through 9 are implemented historical boundaries; M9 is frozen and M10 has not
started. The individual milestone documents and `BUILD_STATUS.md` supersede the original planning
language below when recording actual implementation or verification status.

---

# 91. Milestone 0 — Foundation Only

Implement:

```text
solution structure
Core project
Infrastructure project
Host project
tests
basic Blazor host
Windows Service capable host
health skeleton
architecture documentation
threat model
ADR-0001
ADR-0002 investigation
GitHub Actions build/test
```

Do NOT implement real SMTP delivery yet.

At end:

```text
dotnet build
dotnet test
```

must succeed.

Then STOP.

---

# 92. Milestone 1 — Durable SMTP Intake

Implement:

```text
SMTP listener
device definition
AUTH LOGIN
AUTH PLAIN
IP restrictions
legacy mode
message size limits
stream DATA to spool
SQLite queue metadata
250 only after durable acceptance
```

Use LocalPreviewDeliveryProvider.

Tests first for dangerous behavior.

STOP after milestone.

---

# 93. Milestone 2 — Queue Reliability

Implement:

```text
queue worker
state transitions
retry scheduling
crash recovery
queue limits
disk limits
startup reconciliation
graceful shutdown
```

Run restart/failure integration tests.

STOP.

---

# 94. Milestone 3 — Microsoft OAuth

Implement:

```text
certificate credentials
MSAL
token acquisition
token cache
Microsoft configuration validation
certificate expiry monitoring
```

No real mail delivery yet if separation improves testing.

STOP.

---

# 95. Milestone 4 — Exchange SMTP OAuth Delivery

Implement:

```text
smtp.office365.com connectivity
STARTTLS
XOAUTH2
MAIL FROM
RCPT TO
stream raw DATA
SMTP response handling
error classification
retry integration
```

Run:

```text
small message test
10 MB test
25 MB test
dot-stuffing test
multiple recipient test
network interruption test
```

STOP.

---

# 96. Milestone 5 — Microsoft Setup Wizard

Implement:

```text
certificate setup
Tenant ID
Client ID
Exchange RBAC guidance
PowerShell helper commands
OAuth verification
SMTP verification
sender authorization test
test message
```

STOP.

---

# 97. Milestone 6 — Device UX

Implement:

```text
dashboard
device list
add device
edit device
reset password
printer configuration screen
test SMTP
```

STOP.

---

# 98. Milestone 7 — Diagnostics

Implement:

```text
diagnostic status and bounded probes
privacy-conscious message metadata/history
plain-English errors
health status
diagnostic bundle
privacy-safe logging
```

STOP.

---

# 99. Milestone 8 — Installer

Implement:

```text
framework-dependent Host/worker and self-contained NativeAOT helper publishing
WiX MSI/Burn installer
Windows Service registration
secure ACLs
parameter-free local setup URI
upgrade
uninstall
```

STOP.

---

# 100. Milestone 9 — Hardening

Perform:

```text
SMTP fuzz tests
large message tests
resource exhaustion tests
kill/restart tests
secret audit
authorization audit
dependency audit
TLS review
queue consistency testing
installer upgrade testing
```

Fix issues.

Do not add new features.

---

# 101. Milestone 10 — Release Candidate

Finish:

```text
documentation
README
screenshots
SECURITY.md
installation guide
Microsoft guide
printer guide
troubleshooting guide
checksums
release package
```

---

# 102. Rules for AI-Assisted Contributions

Because this project is being heavily AI-assisted, follow these rules carefully.

## Never blindly rewrite large working areas.

Before changing code:

1. inspect it
2. understand why it exists
3. identify exact issue
4. make smallest coherent change

## Never solve one bug by restructuring the entire application.

## Never change architecture merely because another architecture is fashionable.

## Never add a dependency without explaining why.

## Never claim a test passed without running it.

## Never claim the application builds without building it.

## Never leave fake TODO implementations in security-critical paths.

---

# 103. Before Every Coding Session

Read:

```text
README.md
docs/architecture/*
docs/security/threat-model.md
current milestone
BUILD_STATUS.md
```

Inspect repository state.

Then state:

```text
Current milestone
What already works
What will change
Security implications
Tests that will be run
```

Keep this short.

Then implement.

---

# 104. After Every Meaningful Change

Run relevant:

```text
dotnet build
dotnet test
```

and targeted tests.

Fix:

- compilation errors
- test failures
- warnings introduced by your change

Do not accumulate dozens of known failures.

---

# 105. Build Status File

Maintain:

```text
BUILD_STATUS.md
```

Keep it concise.

Include:

```text
Current milestone
Implemented
Verified
Known issues
Deferred
Next step
```

This prevents future coding sessions from hallucinating project state.

---

# 106. No Fake Security

Never “fix” a security problem by:

```text
disabling TLS validation
accepting every sender
accepting every IP
storing plaintext passwords
granting tenant-wide access
logging tokens
allowing anonymous Internet relay
```

If the requested convenience conflicts with security:

Explain the conflict and implement the safest usable alternative.

---

# 107. No Fake Reliability

Never acknowledge successful SMTP acceptance before durable persistence.

Never delete a queued message before successful upstream acceptance.

Never hide permanent delivery failure by continuously retrying.

Never silently discard messages.

---

# 108. No Fake Performance Optimization

Do not optimize based on intuition.

Measure.

Only introduce additional complexity such as:

```text
connection pooling
custom memory pools
zero-copy abstractions
parallel chunk processing
advanced caching
```

when profiling shows a meaningful benefit.

---

# 109. Performance Acceptance Criteria

Before V1 release demonstrate:

### 25 MB message

RelayBridge can:

```text
receive
persist
queue
stream
deliver
```

without holding multiple full copies in memory.

### Concurrent scans

At least several simultaneous printer submissions should work reliably.

### Idle operation

CPU usage should remain negligible.

### Memory

Memory should remain reasonably bounded as message sizes increase.

Record actual observed measurements rather than inventing thresholds before testing.

---

# 110. Reliability Acceptance Criteria

Before release verify:

```text
Windows reboot with queued mail
service restart with queued mail
Internet outage
Microsoft outage simulation
DNS failure
SMTP 4xx
SMTP 5xx
disk-space limit
bad device password
unauthorized sender
unauthorized IP
expired certificate
Microsoft configuration failure
```

Each should produce predictable behavior and useful diagnostics.

---

# 111. Security Acceptance Criteria

Before release verify:

```text
not an open relay
device passwords hashed
private keys protected
OAuth tokens never logged
sender spoofing policy enforced
IP restrictions enforced
legacy mode cannot be unrestricted
TLS certificate validation enforced outbound
diagnostic bundle redacts secrets
Microsoft mailbox scope documented/tested
```

---

# 112. End-to-End Definition of Done

V1 is ready when this works:

```text
Fresh Windows machine
      ↓
Download RelayBridge installer
      ↓
Install
      ↓
No manual .NET installation
      ↓
Local web UI opens
      ↓
Configure Microsoft 365
      ↓
Generate/use certificate
      ↓
Configure Exchange Application RBAC
      ↓
Verify SMTP OAuth
      ↓
Add Ricoh/Canon/Xerox/etc.
      ↓
Receive generated SMTP settings
      ↓
Configure printer
      ↓
Scan 15 MB PDF
      ↓
RelayBridge accepts SMTP
      ↓
Message safely spooled
      ↓
OAuth token acquired
      ↓
STARTTLS to Exchange Online
      ↓
AUTH XOAUTH2
      ↓
Original MIME streamed
      ↓
Microsoft accepts message
      ↓
Recipient receives scan
      ↓
Dashboard shows success
```

Then:

```text
disconnect Internet
scan again
message queues
restore Internet
message delivers automatically
```

Then:

```text
restart Windows while mail is queued
message still delivers afterward
```

Then:

```text
unauthorized LAN device attempts relay
RelayBridge rejects it
```

That is V1.

---

# 113. The Wow Test

Before calling RelayBridge finished, give it to someone who understands printers but does not understand OAuth.

They should be able to see:

```text
RelayBridge

Microsoft 365
● Connected

Printers
● Ricoh Reception
● Canon Accounts
● Xerox Warehouse

Queue
0

Today
148 messages delivered

Everything is working.
```

When something breaks, the product should tell them what happened.

Example:

```text
Xerox Warehouse needs attention

The printer reached RelayBridge,
but its SMTP password was incorrect.

Last attempt:
11:42 AM from 192.168.10.51

[ Reset Password ]
[ Show Printer Settings ]
```

That experience is more valuable than showing them an impressive architecture diagram.

---

# 114. Final Engineering Principle

Whenever you have two technically valid approaches, prefer the one with:

```text
fewer moving parts
fewer dependencies
less hidden behavior
easier testing
easier recovery
easier debugging
lower privilege
lower memory usage
clearer code
```

provided security and reliability are not reduced.

The goal is not:

> Build the most sophisticated SMTP relay.

The goal is:

> Build the SMTP relay administrators trust because it is simple, fast, secure, reliable, understandable, and extremely easy to use.

---

# 115. Current Work Boundary

Milestone 9 is frozen. Do not reopen completed milestone architecture without a concrete reproduced
defect, do not begin Milestone 10 automatically, and do not publish or describe development
artifacts as official Windows releases.

For every requested change, inspect the current repository, use the smallest coherent scope, run
the relevant Release build/tests and repository verifiers, update `BUILD_STATUS.md` when project
state changes, and follow the protected-main contribution workflow in `AGENTS.md` and
`CONTRIBUTING.md`.
