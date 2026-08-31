<!-- SPDX-License-Identifier: MPL-2.0 -->

# RelayBridge — Getting Started

RelayBridge is a completely local SMTP-to-Microsoft-365 OAuth relay for printers, scanners, NAS
devices, monitoring systems, and legacy applications.

## Before installation

RelayBridge v1.0.0-rc.1 is an **unsigned pre-release for evaluation and community testing**. It is
not the final stable production release. Windows will identify the publisher as unknown, and
Microsoft Defender SmartScreen may warn. Verify the published SHA-256 checksum before running the
installer and proceed only if you explicitly accept the risks of testing an unsigned release
candidate.

The initial supported installation environment is:

- Windows 10 or Windows 11 x64;
- one locally interactive Windows administrator; and
- Internet access during installation for exact pinned Microsoft Graph and Entra package
  acquisition from Microsoft's official PowerShell Gallery.

Use `RelayBridge-Setup-1.0.0-rc.1-win-x64.exe`. The Burn bootstrapper is the supported
clean-machine entry point. Do not start with the MSI: the bootstrapper installs the required exact
Microsoft prerequisites, enforces Graph terms acceptance, provisions the private tooling closure,
and then installs RelayBridge.

## First run

After installation:

1. Open **RelayBridge** from the desktop shortcut or the browser page offered by the interactive
   installer.
2. Configure the customer-owned Microsoft 365 application and scoped Exchange permission.
3. Open **Settings**, select the trusted private LAN interface, and choose **Apply printer
   connectivity**.
4. Add the printer, scanner, NAS, or legacy application as a RelayBridge device.
5. Submit one synthetic SMTP message and confirm local acceptance, queue processing, and Exchange
   delivery.

The management interface remains available only on the RelayBridge machine through its protected
loopback endpoint. The printer-facing SMTP listener is a separate, explicitly configured private
LAN surface.

## Configure Microsoft 365

The recommended wizard creates or verifies a customer-owned, single-tenant Microsoft Entra
application with a certificate credential whose private key remains in the local Windows
certificate store. It then creates or verifies an Exchange service-principal reference and one
resource-scoped `Application SMTP.SendAsApp` assignment for the selected sender through Exchange
Application RBAC.

RelayBridge never asks for or retains the Microsoft administrator password. Authentication and
consent occur in Microsoft's WAM experience; administrator tokens do not enter the RelayBridge
Windows Service or its database.

The setup flow verifies:

1. the local certificate and private-key access;
2. the customer-owned Entra application identity;
3. the Exchange service-principal and scoped sender authorization;
4. certificate-based Exchange token acquisition;
5. normal certificate-validated STARTTLS and XOAUTH2; and
6. final SMTP acceptance for the selected sender.

New Exchange authorization can take time to become effective. After the brief inline retries, the
Step 5 page can check the same candidate automatically every five minutes for up to 12 attempts or
one hour. The checks are bounded and cancellable, do not mutate the tenant, and send no
`MAIL`/`RCPT`/`DATA` while authentication is rejected. An SMTP 535 is presented as possible
propagation, not proof of propagation.

A final upstream SMTP `250` proves Exchange Online accepted the message. It does not guarantee
inbox placement or final delivery by a downstream recipient system.

## Prepare printer connectivity

In **Settings**, choose one active trusted private interface and the configured SMTP listener port,
then select **Apply printer connectivity** and approve the Windows elevation prompt. The bounded
helper writes only the validated RelayBridge production configuration, enables the queue, restarts
only the RelayBridge service, waits for management readiness, and verifies the selected listener.

If the host firewall blocks the printer, follow the displayed narrow program/address/port guidance
as an administrator. RelayBridge does not create or broaden Windows Firewall rules automatically.

Inbound STARTTLS is unavailable in RC1. Authenticated cleartext SMTP can be enabled only on one
explicit RFC1918/ULA private interface. Keep the listener on a trusted private network and retain
device-specific source-IP and sender restrictions. Never expose the listener directly to the
Internet.

## Add and configure a device

Prefer **Authenticated / Compatible** mode when the device supports SMTP AUTH LOGIN or AUTH PLAIN.
RelayBridge generates a per-device username and one-time password; copy the password immediately
because RelayBridge stores only a verifier and cannot show it later. Configure the device with:

- the private RelayBridge server address displayed by the wizard;
- the displayed SMTP listener port (normally 2525);
- connection security **None** because inbound STARTTLS is unavailable;
- the generated username and password;
- the exact authorized sender; and
- the device's permitted source IP/subnet.

Use **Legacy** mode only when the device cannot authenticate. Legacy mode accepts no password and
therefore requires a strict private source IP/subnet plus the exact sender restriction. RelayBridge
rejects wildcard/public/unrestricted Legacy rules and must never become an open relay.

After saving the device, submit only synthetic/non-sensitive test content. A local SMTP success
means RelayBridge durably queued the message. Confirm the later queue result separately.

## Repair, upgrade, and uninstall

Use the supported Burn bootstrapper for repair and future manual upgrades. Repair restores
installer-owned application files, service registration, protocol handlers, and the desktop
shortcut without intentionally deleting durable ProgramData. A major upgrade preserves supported
configuration and database state and blocks downgrades.

RelayBridge has no automatic updater. **Check for updates** retrieves only bounded public GitHub
release metadata after an administrator selects it; it does not download or install anything.

Normal uninstall removes the RelayBridge service, Program Files application/tooling, custom URI
registrations, and desktop shortcut. It preserves `C:\ProgramData\RelayBridge` by default so
configuration, queued mail, and local history are not silently destroyed. Review and remove that
data separately only when retention is no longer required.

Uninstall does not delete Windows certificates/private keys or Microsoft Entra/Exchange tenant
objects. Those require a separate, deliberate administrator cleanup using Microsoft tools.

## Known RC1 limitations

- RC1 is unsigned; Unknown Publisher and SmartScreen warnings are expected.
- Inbound STARTTLS is unavailable.
- Windows Server, RDP, Server Core, and simultaneous-user native setup are not claimed.
- Exchange authorization propagation can delay initial SMTP authentication.
- SMTP `250` is acceptance by Exchange, not an inbox-delivery guarantee.
- Firewall changes remain manual and narrowly administrator-controlled.
- Update checking is manual and informational; there is no auto-download or auto-install.
- A signed stable `v1.0.0` release is not yet available.

## Software and licenses

RelayBridge-owned source is open-source software licensed under the Mozilla Public License 2.0.
The corresponding source is available at <https://github.com/getrelaybridge/relaybridge> and will
be linked to the exact public release tag for every published binary.

The installer includes certain unmodified upstream prerequisites and tooling under their own
licenses, including the exact Microsoft .NET and ASP.NET Core runtime installers, PowerShell,
ExchangeOnlineManagement, and incorporated WiX runtime/bootstrapper components. RelayBridge does
not modify or relicense those third-party components.

Microsoft Graph and Entra PowerShell packages are not bundled with RelayBridge. During
installation, the exact required versions are downloaded directly from Microsoft's official
PowerShell Gallery after the applicable Microsoft Graph terms are accepted. They remain subject
to Microsoft's terms and their package metadata.

RelayBridge is an independent project and is not affiliated with or endorsed by Microsoft.

Installed reference files:

- `LICENSE` — RelayBridge MPL-2.0 license;
- `THIRD-PARTY-NOTICES.md` — component/license/distribution inventory; and
- `GETTING-STARTED.md` — this guide.

Project website: <https://getrelaybridge.com>
