# Milestone 5 — Microsoft 365 Setup Wizard

## Goal

Provide a small, resumable Blazor setup experience for the frozen M3 certificate
identity and M4 Exchange SMTP OAuth transport without embedding Microsoft
administration credentials or creating a second delivery path.

## Implemented

- Welcome, new/existing application choice, certificate create/select/export,
  separate Entra and Exchange administrator stages, candidate validation, test
  message, completion, back, cancel, resume, and repair behavior
- certificate enumeration limited to currently valid RSA signing certificates with
  a private key usable by the current process; existing M3 generation and public-
  only export are reused
- separate PowerShell 7 scripts generated from encoded JSON data; RelayBridge never
  launches them or accepts executable pasted content
- least-privilege Entra script with zero permission checks and an idempotent,
  fail-closed Exchange App RBAC script using one dedicated sender group and explicit
  management scope
- deterministic certificate/client-scoped administrative object names so a candidate
  can coexist with the active identity during safe replacement
- strict 4 KiB JSON result parsing with unknown/duplicate property rejection,
  non-empty GUID checks, and ASCII mailbox validation
- schema-v4 resumable candidate state and atomic activation that preserves an
  existing working identity until token and SMTP sender checks succeed
- identity checks through the M3 token provider and Exchange/sender/test-message
  checks through the actual M4 provider; no SMTP state-machine, TLS, XOAUTH2, queue,
  or acceptance-boundary changes
- accessible loopback Blazor UI with semantic headings, labels, keyboard focus,
  status text, explicit copy/download actions, plain-language failures, and safe
  expandable technical/security details

## Automated verification

Tests cover fresh setup, certificate blocking/advance, strict identifiers and
result parsing, injection inputs, script privilege and conflict checks, restart
resume, back/cancel behavior, v3-to-v4 migration, candidate token/SMTP failure
preserving active configuration, atomic activation after final SMTP acceptance,
test-message success, public-only export regression, and setup-page rendering.

All pre-existing M1–M4 tests remain part of the full Release suite. The generated
scripts also pass the PowerShell parser independently.

## Real-tenant validation and freeze

The dedicated-tenant new-application path passed both administrator scripts, zero
Entra permissions, positive and negative Exchange scope, candidate token acquisition,
real STARTTLS/XOAUTH2, sender authorization, final SMTP `250` before activation, a
second final `250` for the wizard test message, and post-restart health. A deliberate
pre-activation token failure preserved the active baseline. See
[`milestone-5-real-tenant-validation.md`](../microsoft365/milestone-5-real-tenant-validation.md).

Milestone 5 is frozen. No test-tenant identifier or secret is present in tracked
documentation.
