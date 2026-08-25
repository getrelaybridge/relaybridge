# RelayBridge

RelayBridge is a free and open-source local SMTP-to-Microsoft-365 OAuth relay for printers, scanners,
NAS devices, monitoring appliances, and legacy applications. It is designed to
run as one Windows service and preserve accepted mail in a durable local queue
before relaying it to Exchange Online.

## Project status

Milestones 5.1, 7, 8, 9.1, and 9 are frozen. Milestone 10 Release Candidate is next and has not
started.
The installed Windows product uses a sidecar-free NativeAOT setup launcher, subordinate
managed worker, protected per-session ProgramData scratch, private PowerShell 7.6.4, and
ExchangeOnlineManagement 3.9.2. Graph/Entra packages are not embedded in the public-candidate
MSI or bootstrapper: the installer acquires exact pinned packages directly from Microsoft only
after the explicit Graph terms boundary, verifies them twice, and commits them into protected
Program Files. Normal runtime never contacts the Gallery or falls back to user/system modules.

Development installers are not public-release artifacts. External publication remains prohibited
while Microsoft redistribution/missing-license clarification and the trusted Authenticode signing
identity remain open. WiX Toolset 6 compliance is closed for the current individual,
non-revenue-generating use and must be reassessed if that status changes. See
[docs/release/release-gates.md](docs/release/release-gates.md).

RelayBridge is ready for public source review and contribution, but it has not yet issued a
public production release. It was created and is maintained by
[@yuvaraj-builds-ai](https://github.com/yuvaraj-builds-ai), with
contributions reviewed through the maintainer-led process in [CONTRIBUTING.md](CONTRIBUTING.md).

Public source may precede the first official Windows binary release. The future public repository
will begin from a clean tracked-source snapshot with fresh Git history; the private engineering
history and milestone tags are not publication inputs. The planned SignPath Foundation integration
and current pre-application rules are documented in the
[code signing policy](docs/release/CODE-SIGNING-POLICY.md).

The existing runtime provides a bounded local SMTP listener, per-device authentication and
authorization, durable intake, startup reconciliation, queue capacity protection,
atomic worker claiming, persisted retry scheduling, and the certificate-based
Microsoft application identity foundation, plus production Exchange Online SMTP
OAuth delivery with mandatory STARTTLS, XOAUTH2, raw MIME streaming, and queue
retry integration. The default listener is loopback-only, no devices are
provisioned, and delivery workers remain disabled until explicitly configured.

See [BUILD_STATUS.md](BUILD_STATUS.md) for verified repository state and
[docs/MASTER_SPEC.md](docs/MASTER_SPEC.md) for the authoritative product scope.

## Architecture

RelayBridge runtime uses one service process with three primary projects, plus two
short-lived setup executables:

- `RelayBridge.Core` contains business rules and true external-boundary contracts.
- `RelayBridge.Infrastructure` contains persistence, Microsoft, SMTP, certificate,
  and operating-system integrations as those capabilities are added.
- `RelayBridge.Host` is the ASP.NET Core/Blazor application and Windows Service host.
- `RelayBridge.SetupLauncher` is the NativeAOT interactive trust boundary authenticated
  by the Host. It launches only the fixed managed worker with a clean environment.
- `RelayBridge.Setup` is the subordinate managed Microsoft provisioning worker. It has
  no direct Host authority and owns no runtime delivery function.

The dependency direction is `Host -> Infrastructure -> Core`; Host may also use
Core directly. See [docs/architecture/overview.md](docs/architecture/overview.md)
and the ADRs under `docs/architecture/decisions/`.

## Development

Prerequisites:

- .NET 10 SDK
- Windows x64 for the primary runtime experience; build and test remain portable
  where the referenced framework APIs permit

```powershell
dotnet restore RelayBridge.sln
dotnet build RelayBridge.sln --no-restore
dotnet test RelayBridge.sln --no-build
dotnet run --project src/RelayBridge.Host
```

The management host is code-owned loopback with a configurable `Management:Port`;
non-loopback generic URL or Kestrel endpoint overrides fail startup. The development
SMTP listener also defaults to loopback. `/health`
reports local database, spool, disk-capacity, and worker state; Microsoft identity
and Exchange delivery readiness remain separate. The SMTP listener defaults to port
2525. Device management is at `/devices` and `/devices/add`; the Microsoft setup
wizard is at `/setup/microsoft`. See
[docs/microsoft365/setup-wizard.md](docs/microsoft365/setup-wizard.md) for its
administrator and security model,
[docs/microsoft365/identity.md](docs/microsoft365/identity.md) for the
identity and certificate security model, and
[docs/microsoft365/exchange-smtp-delivery.md](docs/microsoft365/exchange-smtp-delivery.md)
for the Milestone 4 transport and RBAC validation gate.

## What RelayBridge does not do

- It does not use Microsoft Graph as a mail-delivery fallback.
- It does not retain Microsoft administrator credentials or delegated provisioning tokens.
- It does not weaken TLS validation, sender authorization, or the local durable-queue boundary.
- It does not make unsigned development installers suitable for external deployment.
- It does not claim Windows Server/RDP native-setup support in the current release candidate.

The recommended new-application wizard uses the installer-owned NativeAOT launcher and
managed worker, exact release manifests, private Microsoft setup tooling, the machine-owned
custom URI, and protected ProgramData scratch on Windows 10/11 x64. Missing or altered
prerequisites fail closed; the existing manual PowerShell workflow remains available only
under Advanced recovery. Windows Server/RDP native setup is not claimed.

## Security

RelayBridge must never become an open relay. Every device must have source-IP/CIDR
and sender allow-lists; authenticated devices also use a
generated secret whose verifier, never plaintext, is stored. Inbound STARTTLS is
not yet available, so cleartext AUTH is disabled by default. It can be enabled only
with one explicit RFC1918 IPv4 or IPv6 ULA listener address; wildcard, loopback,
link-local, multicast, and public bindings fail startup. Per-device source and sender
restrictions remain mandatory.
See [SECURITY.md](SECURITY.md) and [docs/security/threat-model.md](docs/security/threat-model.md).

## License

RelayBridge-owned source code is licensed under the
[Mozilla Public License 2.0](LICENSE). Third-party components retain their own licenses and
notices; see [docs/release/THIRD-PARTY-NOTICES.md](docs/release/THIRD-PARTY-NOTICES.md).

MPL-2.0 source obligations are fulfilled by publishing the corresponding source for each
released binary at the exact release tag. Development binaries are not public releases. See
[docs/release/source-publication.md](docs/release/source-publication.md) for the release-source
invariant and generated-file treatment.

RelayBridge is the project name, not a statement of affiliation with or endorsement by
Microsoft. Microsoft 365, Exchange Online, Windows, PowerShell, and related marks belong to
their respective owners.
