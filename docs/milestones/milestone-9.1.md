# Milestone 9.1 — Installer-Time Microsoft Module Acquisition

## Status

Frozen after finite acceptance and release-hygiene review.

Milestones 5.1, 7, and 8 remain frozen historical boundaries. M9.1 changes only how four
Graph/Entra PowerShell packages reach the existing private tooling tree; Microsoft setup, WAM,
Exchange RBAC, SMTP OAuth, launcher/worker authority, and runtime module loading are unchanged.

The accepted freeze baseline is 59 unit and 418 integration tests (477 total, 0 failed, 0 skipped),
20/20 focused acquisition/packaging tests, a zero-warning/zero-error Release build, installer and
payload validation, zero known NuGet vulnerabilities, clean secret/private-key and unsafe-TLS
scans, PowerShell parser validation, and `git diff --check`.

## Supported installer

The supported path is `RelayBridge-Setup-<version>-win-x64.exe`. A clean direct MSI install is
rejected before service or URI registration because the MSI is an internal Burn chain package.

Burn retains the existing exact .NET 10.0.11 prerequisites. It remotely acquires these packages
from fixed official PowerShell Gallery HTTPS URLs:

| Package | Version | Bytes | SHA-256 | License acceptance |
|---|---:|---:|---|---|
| Microsoft.Graph.Authentication | 2.25.0 | 7,281,967 | `C9596CD06539EA898D8F0BC3569BDD2FDBAB931390FC5747548CFED75B10CC9D` | required |
| Microsoft.Graph.Applications | 2.25.0 | 4,201,875 | `03D59FAAC37E1B3D77464FC8F58F5F09FF1A02622BD998240F18DE5B0CD3862E` | required |
| Microsoft.Entra.Authentication | 1.3.0 | 51,989 | `4EA5E5C080D87C4E5E9E99AAF18CA4F089F1E12D1E606BC10093691FDE3FE036` | not required by metadata |
| Microsoft.Entra.Applications | 1.3.0 | 138,505 | `374F9245E0D4DAF3EA74C55E2C4C2FCBCA37116E1ABCC66112317A364D5A9A29` | not required by metadata |

No floating version, search, dependency resolver, mirror, system module, user module, or runtime
Gallery fallback exists. Private PowerShell 7.6.4 and ExchangeOnlineManagement 3.9.2 retain the
reviewed M8 distribution model.

## License identity and acceptance

The two Graph packages declare `RequireLicenseAcceptance=true` and the publisher-supplied
`https://aka.ms/devservicesagreement`, but contain no `license.txt`. This is recorded as upstream
package-metadata behavior; RelayBridge does not invent or hash a substitute document.

Acceptance identity schema 1 binds exact package ID, version, package SHA-256,
`RequireLicenseAcceptance`, and publisher LicenseUri. WixStdBA shows the exact package names and
versions and links the official URI. Interactive continuation requires affirmative acceptance.
Quiet/passive setup requires `RelayBridgeAcceptMicrosoftGraphTerms=1`; neither mode implies consent.
A minimal protected machine record contains only technical identity, timestamp, and release data—no
account, tenant, mailbox, token, password, credential, or browser state. Exact-identity repair may
reuse acceptance; any ID/version/hash/acceptance-state/LicenseUri change requires fresh acceptance.

## Acquisition and provisioning

Burn verifies the fixed payload size and WiX-required SHA-512 and passes only its protected package
cache to `RelayBridge.ToolingProvisioner.exe`. The NativeAOT provisioner has no networking or
Microsoft authentication capability. Its compiled lock independently validates SHA-256, package
ID/version, dependency declarations, declared license metadata, and the package signature payload.

Extraction occurs beneath a new protected `C:\ProgramData\RelayBridge\InstallerStaging\session-*`
directory. Traversal, rooted/drive/UNC/ADS paths, dot ambiguity, case collisions, links/reparse
points, excessive counts/sizes, and closure differences are rejected. Complete extracted packages
are copied into a protected incoming tree, verified against tooling manifest schema 2, then replace
only the four owned Program Files module roots with backup/rollback support. The RelayBridge service
starts only after the exact final closure and acceptance state pass.

## Servicing

Burn keeps the verified `.nupkg` payloads in its protected package cache. Exact-identity repair
revalidates them without a Gallery download and restores missing/corrupt installed files. A release-
bound marker forces the provisioner to participate in each RelayBridge release upgrade even when
package identities are unchanged. Uninstall removes the acquired Graph/Entra module roots and
technical acceptance record while preserving `C:\ProgramData\RelayBridge\Data`. Reinstall reacquires
and verifies the packages. Downgrades remain blocked and rollback never adopts ambient modules.

## Validation

- actual WiX 6 remote download, protected cache handoff, quiet/passive policy, repair, and rollback
  spike: pass
- disposable Windows 11 clean accepted install: four exact packages, service Running, health and
  diagnostics 200, Microsoft NotConfigured
- quiet/passive without explicit acceptance: rejected with zero Graph/Entra acquisition and no
  service/URI/Program Files state
- intact repair: pass with zero Gallery downloads
- deleted owned Graph manifest: exact hash restored from protected cache
- malformed acceptance record: rejected; explicit renewed acceptance restored service
- uninstall/data preservation/reinstall: pass; reinstall reacquired exactly four packages
- synthetic 0.9.1 → 0.9.2: pass with one exact module closure and release marker 0.9.2
- downgrade: rejected with 1638 before the provisioner ran; installed service remained healthy
- direct MSI on clean machine: rejected with 1603 and no partial service/URI/tree
- repository tests: 59 unit + 418 integration = 477, no failures or skips

No Microsoft authentication, tenant mutation, Exchange operation, or SMTP delivery occurred.

## Accepted low-risk observation

A redundant secondary-user staging probe was inconclusive. This does not block the freeze: actual
installed ordinary-user ACL denial and deterministic trust-verifier coverage passed, and no
reproducible bypass was demonstrated. The observation is recorded without reopening acceptance.

## Publication and legal gates

- Graph/Entra embedded redistribution: not used by the M9.1 public-candidate installer
- Microsoft redistribution clarification: open
- Microsoft Graph missing `license.txt` clarification: open
- public release signing gate: open
- offline Microsoft-module acquisition: future/not implemented

These gates prohibit external Windows binary publication. This document is technical evidence, not
legal advice.

## Freeze

M9.1 Installer-Time Microsoft Module Acquisition is frozen. The next workstream is M9 Hardening.
