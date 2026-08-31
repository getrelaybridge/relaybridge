<!-- SPDX-License-Identifier: MPL-2.0 -->

# RelayBridge v1.0.0-rc.1

First public release candidate.

## Status

This is an **unsigned public pre-release for evaluation and community testing**.
It is not the final stable production release.

Windows will identify the publisher as unknown, and Microsoft Defender SmartScreen may warn when
the installer starts. Administrators should verify the SHA-256 checksums published with the
release and proceed only if they explicitly accept the risks of testing an unsigned release
candidate. Organizations that require a signed stable build should wait for the later signed
stable release.

## Highlights

- completely local SMTP compatibility bridge for printers, scanners, NAS devices, monitoring
  systems, and legacy applications;
- customer-owned Microsoft Entra application with a local certificate credential;
- sender-restricted Exchange Online Application RBAC and SMTP OAuth/XOAUTH2 delivery;
- durable SQLite/filesystem queue that acknowledges DATA only after local persistence;
- guided Microsoft 365 setup with bounded Step 5 authorization-propagation checks;
- one-click, administrator-approved private-LAN printer-connectivity Apply workflow;
- device-specific authentication or tightly restricted Legacy mode;
- local diagnostics and a sanitized support bundle;
- WiX 6 Windows installer with first-run onboarding and a desktop management shortcut; and
- installed version display plus manual, informational GitHub release awareness.

## Supported installation

Use `RelayBridge-Setup-1.0.0-rc.1-win-x64.exe`. The Burn bootstrapper is the supported
clean-machine entry point and installs the required exact Microsoft prerequisites before the
RelayBridge MSI. Direct MSI installation is not the supported clean-machine flow.

The initial supported environment is Windows 10 or Windows 11 x64 with one locally interactive
administrator and Internet access during installation. The installer obtains the exact pinned
Microsoft Graph and Entra PowerShell packages directly from Microsoft's official PowerShell
Gallery after the administrator accepts the applicable Graph terms. Those package bytes are not
embedded in the RelayBridge MSI or bootstrapper.

See `GETTING-STARTED.md` for installation, Microsoft setup, printer/device configuration,
repair/upgrade/uninstall, and troubleshooting guidance.

## Known limitations

- RC1 is unsigned; Unknown Publisher and SmartScreen warnings are expected.
- Inbound STARTTLS is unavailable. Authenticated cleartext SMTP is permitted only on one explicit
  trusted private interface under the existing device IP/sender restrictions.
- Native Microsoft setup is supported on Windows 10/11 x64 with one local interactive
  administrator. Windows Server, RDP, Server Core, and simultaneous-user setup are not claimed.
- Newly created Exchange authorization can take time to become effective. RelayBridge performs
  bounded, cancellable Step 5 verification checks but does not treat every SMTP 535 as propagation.
- A final upstream SMTP `250` proves Exchange Online accepted the message; it does not guarantee
  inbox placement or delivery by a downstream recipient system.
- Windows Firewall changes remain a deliberate manual administrator action.
- Updates are informational and administrator-initiated. RelayBridge does not download or install
  updates automatically.
- A signed stable `v1.0.0` release is not yet available.

## Verification

- Release build: 0 warnings and 0 errors.
- Unit tests: 86/86.
- Integration tests: 497/497.
- Total tests: 583/583, with 0 failed and 0 skipped.
- Owner-installed Windows 11 x64 validation covered install, first-run onboarding, Microsoft setup,
  Exchange SMTP OAuth acceptance, printer connectivity Apply/restart, local SMTP queue delivery,
  restart/reboot behavior, diagnostics, support-bundle privacy, repair, reinstall, and uninstall.
- The release package includes a CycloneDX SBOM, third-party notices, source provenance, and
  SHA-256 checksums.

The release provenance records the exact source revision, and the final release-preparation report
records the current verification totals. Corresponding MPL-2.0 source will be available at the exact
`v1.0.0-rc.1` release tag when RC1 is published.

RelayBridge is an independent project and is not affiliated with or endorsed by Microsoft.
