# Milestone 9 — Product hardening

Status: **FROZEN**.

## Scope

M9 validates the assembled product under service crashes, hostile SMTP input, queue/storage
failure, degraded Microsoft/network state, diagnostics/privacy, servicing, and bounded endurance.
It does not add product features or redesign the frozen SMTP, queue, Microsoft setup, installer,
or acquisition boundaries.

## Implemented corrections

- The Windows service has bounded first/second-failure restart actions and no third restart.
- Production SMTP rejects port zero instead of silently selecting an ephemeral listener.
- Failed SQLite opens dispose their connection before returning the fail-closed error.
- Focused SMTP and SQLite hostile/failure regression coverage was added.
- Windows distributions receive a CycloneDX 1.6 SBOM and categorized third-party notices.
- WiX symbol databases are separated from the public-candidate package directory, whose
  validation now rejects build/debug symbols.

## Release boundaries

M9 does not close public-release gates. Microsoft redistribution clarification, the Graph package
missing-`license.txt` clarification, WiX Toolset 6 OSMF compliance, and a trusted Authenticode
identity remain open. External publication is prohibited.

## Acceptance

The final acceptance is a fixed 20-case Windows 11 Pro x64 matrix plus repository build, test,
installer, vulnerability, privacy, and hostile-boundary gates. Microsoft authentication, tenant
mutation, and Exchange delivery are excluded.

All 20 Windows cases passed on disposable Windows 11 Pro 10.0.26200 x64. Evidence included a
realistic 0.9.1-to-0.9.2 preserved-state upgrade, LocalSystem service crash recovery and reboot,
durable and hostile SMTP intake, queue retry/permanent-failure fixtures, corruption/access-denied/
low-disk storage failures, missing/public-only certificates, controlled DNS/TCP/untrusted-TLS
failures, degraded diagnostics and support-bundle privacy, installer acquisition, direct-MSI
refusal, ProgramData-preserving uninstall, and healthy clean reinstall.

The repository baseline is 59/59 unit and 424/424 integration tests (483/483 total), with zero
failures or skips. The Release build and installer validation passed with zero warnings/errors,
and vulnerability, secret/private-key, unsafe-TLS, payload, PowerShell-parser, hostile-management,
hostile-SMTP-AUTH, graceful-shutdown, and whitespace gates passed.

## Closed findings

1. Service crash recovery absent — MEDIUM — FIXED.
2. Production SMTP accepted port zero — LOW — FIXED.
3. Failed SQLite initialization did not deterministically dispose its connection — LOW — FIXED.
4. WiX symbols appeared beside public-candidate artifacts — LOW — FIXED.

Open findings: BLOCKER 0, HIGH 0, MEDIUM 0, LOW 0.

## Release inventory

- CycloneDX 1.6 SBOM: 23 components.
- Third-party notices distinguish redistributed, Microsoft-direct-acquired, and build/development-
  only components.
- The public-candidate package directory contains only the MSI, bootstrapper, SBOM, and notices.
  WiX symbols remain in the internal symbols directory; Graph/Entra package bytes remain absent.
- Development artifacts remain unsigned and must not be published.
