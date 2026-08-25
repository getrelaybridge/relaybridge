# ADR-0002: Use a narrow outbound SMTP client

- Status: Implemented in Milestone 4
- Date: 2026-08-22

## Context

Outbound delivery must stream an existing spool file as SMTP DATA without parsing
and reconstructing its MIME structure. It also needs required STARTTLS, XOAUTH2,
cancellation, timeouts, large-message support, clear SMTP responses, and modest
connection reuse.

The following options were investigated on 2026-08-22. The decision was
revalidated immediately before Milestone 4 implementation. MailKit 4.17.0
remained current, active, MIT-licensed, and .NET 10 compatible, but its public
SMTP send boundary still accepted `MimeMessage` rather than independent envelope
values plus an arbitrary raw RFC message stream. No maintained client with a
safer supported raw-stream boundary was identified.

### MailKit 4.17

[MailKit](https://github.com/jstedfast/MailKit) is active, MIT-licensed, supports
.NET 10, XOAUTH2, STARTTLS, certificate validation, cancellation, asynchronous I/O,
SMTP extensions, connection reuse, and useful protocol errors. It is the strongest
general-purpose .NET SMTP library evaluated.

Its public SMTP send API accepts a MimeKit `MimeMessage`. The send path calculates
and writes that message through MimeKit formatting/serialization. MimeKit is a
mature streaming parser and often preserves parsed content well, but delivery
would still require parsing the spool and serializing a message object. There is
no supported public MailKit API that accepts independent envelope addresses plus
an arbitrary raw RFC message stream as DATA. `MimeMessage.WriteToAsync` is not a
virtual raw-stream seam. Depending on MailKit internals or maintaining a fork would
be more fragile than the protocol subset RelayBridge needs.

### System.Net.Mail.SmtpClient

This API does not provide the required raw DATA streaming boundary or suitable
XOAUTH2 control. It is not selected.

### Narrow RelayBridge client

A client limited to Exchange SMTP submission can stream the spool directly and
keep the envelope independent from message headers. The cost is owning careful
SMTP state-machine, response, timeout, DATA framing, and test code.

## Decision

Implement only the narrow outbound SMTP subset needed by RelayBridge in Milestone
4, using .NET networking and TLS primitives rather than a general SMTP/MIME stack:

```text
DNS/connect -> greeting -> EHLO -> STARTTLS -> EHLO -> AUTH XOAUTH2
            -> MAIL FROM -> RCPT TO (one or more) -> DATA -> QUIT
```

The client will:

- require STARTTLS and retain platform certificate/hostname validation
- authenticate only with XOAUTH2 and never log the token or AUTH payload
- accept envelope sender/recipients separately from the raw spool stream
- stream with bounded buffers and cancellation
- perform correct CRLF, dot-stuffing, and DATA termination handling
- validate and classify multiline SMTP responses and 4xx/5xx outcomes
- apply explicit connect, TLS, command, and throughput-aware DATA timeouts
- use one connection per delivery initially; reuse requires measured justification
- advertise or use only extensions it implements correctly

This decision does not authorize SMTP implementation during Milestone 0.

## Consequences

- RelayBridge avoids MIME reserialization on delivery and avoids a production
  MailKit/MimeKit dependency for the outbound path.
- The implementation must not become a general SMTP client library.
- Milestone 4 needs scripted socket-level integration tests for multiline replies,
  STARTTLS, XOAUTH2, multiple recipients, dot-stuffing, large streams, disconnects,
  timeouts, cancellation, 4xx/5xx classification, and secret-safe diagnostics.
- TLS and cryptography remain delegated to maintained .NET platform primitives.
- Recheck maintained clients in future transport revisions; reverse this decision
  if a supported raw-stream API later meets every required boundary with less risk.

## Implementation findings

The Milestone 4 client stayed within the planned subset and added no generic SMTP
framework. One-byte exact response reads avoid buffering plaintext beyond the
STARTTLS boundary; response lines, count, and total bytes are bounded. DATA uses a
64 KiB input and 128 KiB transformation buffer. Microsoft documents both SMTP
XOAUTH2 initial-response syntax and a challenge example; RelayBridge uses the
documented `AUTH XOAUTH2` / `334` challenge form, sends the Base64 payload once,
then zeroes its byte buffers. These findings do not change the decision.
