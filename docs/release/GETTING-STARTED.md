<!-- SPDX-License-Identifier: MPL-2.0 -->

# RelayBridge — Getting Started

RelayBridge is a completely local SMTP-to-Microsoft-365 OAuth relay. After installation:

1. Open **RelayBridge** from the desktop shortcut.
2. Configure the customer-owned Microsoft 365 application and scoped Exchange permission.
3. Open **Settings**, select the trusted private LAN interface, and choose **Apply printer connectivity**.
4. Add the printer, scanner, NAS, or legacy application as a RelayBridge device.
5. Submit one synthetic SMTP message and confirm normal queue processing and delivery.

## Software and licenses

RelayBridge-owned source is open-source software licensed under the Mozilla Public License 2.0.
The corresponding source is available at <https://github.com/getrelaybridge/relaybridge>.

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

- `LICENSE` — RelayBridge MPL-2.0 license
- `THIRD-PARTY-NOTICES.md` — complete component/license/distribution inventory
- `GETTING-STARTED.md` — this guide

Project website: <https://getrelaybridge.com>

This development candidate is unsigned and is not an official Windows binary release.
