# Code signing policy

## Status

RelayBridge targets the SignPath Foundation open-source code-signing service. The application has
not been submitted and no SignPath certificate is currently authorized for RelayBridge.
Development MSI/bootstrapper artifacts are not releases. The separately reviewed RC1 is intended
to be an explicitly unsigned public pre-release for evaluation/community testing, with prominent
Unknown Publisher/SmartScreen warnings; it is not a signed stable production release.

The attribution required by SignPath Foundation is therefore pending, not an active claim:

> Pending approval: Free code signing provided by SignPath.io, certificate by SignPath Foundation.

After approval, release pages and this policy must use the approved attribution without the
"Pending approval" qualifier.

## Team roles

- Committer / maintainer: [@yuvaraj-builds-ai](https://github.com/yuvaraj-builds-ai)
- Reviewer: [@yuvaraj-builds-ai](https://github.com/yuvaraj-builds-ai) initially
- Approver: [@yuvaraj-builds-ai](https://github.com/yuvaraj-builds-ai) initially

RelayBridge currently has one maintainer. No additional person or role separation is invented.
Changes from other contributors require maintainer review. Every release-signing request requires
an explicit approval after its source revision, workflow, SBOM, notices, and candidate artifacts
have been reviewed. If SignPath requires separation that a one-person project cannot satisfy, the
application must stop and record that requirement.

## Privacy and network behavior

RelayBridge does not transfer user information to RelayBridge-operated networked services.
Network communication occurs only for explicitly requested or required product functions with
Microsoft services and official dependency sources, including Microsoft administrator setup,
Exchange Online SMTP delivery, connectivity diagnostics requested by an administrator, and
installer acquisition of exact pinned Microsoft prerequisites/modules. RelayBridge does not run a
cloud mail relay and does not retain Microsoft administrator passwords or delegated provisioning
tokens.

The installer discloses that it installs a per-machine Windows service, a machine-owned custom URI
handler, protected Program Files tooling, and protected ProgramData state. Windows uninstall
removes executable, service, and URI state while preserving ProgramData by default so queued mail
and configuration are not silently destroyed.

## Source, build, and origin controls

- RelayBridge-owned source and build scripts are maintained in the public repository under
  MPL-2.0.
- Release candidates are built by a repository-controlled GitHub Actions workflow on a
  GitHub-hosted Windows runner from the exact triggering commit.
- Build dependencies and Microsoft tooling identities are version/hash pinned; the workflow does
  not accept manually substituted binaries.
- The SignPath GitHub App, trusted-build-system verification, repository URL, allowed branch, and
  origin verification must be configured before signing is enabled.
- Release signing must remain a separate, manually approved step. Interactive uploads and local
  developer builds are not eligible for release signing.

## Signing scope and order

The intended nested order follows the existing installer pipeline:

1. Sign RelayBridge-owned PE payloads: `RelayBridge.Host.exe`,
   `RelayBridge.SetupLauncher.exe`, `RelayBridge.Setup.exe`,
   `RelayBridge.Setup.dll`, `RelayBridge.Core.dll`, and
   `RelayBridge.ToolingProvisioner.exe`.
2. Build and sign the RelayBridge MSI containing the signed RelayBridge payloads.
3. Build and sign the final Burn bootstrapper containing the signed MSI.

Artifact configuration must enforce product name `RelayBridge` and one consistent product version.
Upstream third-party binaries, including PowerShell, ExchangeOnlineManagement, .NET prerequisites,
and WiX-originated runtime components, must not be signed with RelayBridge's future certificate.

The current build's thumbprint-based local signing option is development plumbing only. It does not
represent SignPath approval and must not be used to bypass origin verification.
