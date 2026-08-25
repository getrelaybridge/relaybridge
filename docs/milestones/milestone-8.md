# Milestone 8 — Windows Installer

## Status

**FROZEN.** The deterministic WiX MSI/bootstrapper pipeline builds and validates locally, and the
unsigned development package passed the authorized matrix on a disposable clean Windows 11 Pro
x64 VM plus final narrow verification. The package was not installed on the developer workstation.
Public signing and Graph/Entra redistribution review remain external release gates, and external
installer publication remains prohibited until both close.

## Installer architecture

WiX Toolset 6.0.2 produces an x64 per-machine MSI and a small prerequisite-only Burn bundle. The
MSI is authoritative. It uses standard Windows Installer authoring for files, directories,
MSI 5.0 SDDL ACLs, HKLM registry, `ServiceInstall`, `ServiceControl`, repair, and major upgrades.
There are no custom actions, firewall changes, Microsoft administration steps, or tenant calls.

The installation directory is fixed through `ProgramFiles64Folder`; users cannot redirect
launcher, worker, tooling, manifest, service, scratch, or URI paths through MSI properties.

## Installed paths

```text
C:\Program Files\RelayBridge\
    Host\       framework-dependent RelayBridge.Host publish closure
    Setup\      NativeAOT launcher plus managed worker closure and helper manifest
    Tooling\    private PowerShell, exact Microsoft modules, provenance and tooling manifest

C:\ProgramData\RelayBridge\
    Data\
    SetupScratch\
```

The Host's staged production configuration points storage to ProgramData and uses the exact frozen
M5.1 setting names for installation root, launcher path/hash, worker path, helper manifest
path/hash, tooling root, tooling manifest path/hash, and scratch behavior. The service name and
display name are `RelayBridge`; omitting an account in `ServiceInstall` preserves LocalSystem.

## ACL and ownership model

Program Files uses Windows' protected machine hierarchy and is never made user-writable. The MSI
authors protected SDDL on ProgramData:

- root and `SetupScratch`: SYSTEM and Builtin Administrators full control; Builtin Users only
  read/execute/traverse;
- `Data`: SYSTEM and Builtin Administrators full control; no ordinary-user grant.

The protected DACL is inheritable and specifies SYSTEM ownership. M5.1 creates each unpredictable
scratch session beneath `SetupScratch` with the exact interactive SID's narrowly required rights.
No per-user session directory is pre-created. Installed acceptance proved effective owner, DACL,
parent replacement denial, reparse absence, and frozen production-verifier acceptance. A real
non-admin process was denied every attempted trusted-tree mutation.

## Private tooling and manifests

`installer/tooling-lock.json` pins PowerShell 7.6.4, Graph Authentication/Applications 2.25.0,
Entra Authentication/Applications 1.3.0, ExchangeOnlineManagement 3.9.2, exact upstream HTTPS
sources, and package hashes. The build downloads only these inputs, verifies their hashes, checks
required package signatures and the Microsoft signature on `pwsh.exe`, extracts to a private
tree, removes debug symbols, and generates `package-provenance.json`.

The exact helper and tooling trees are hashed into the frozen M5.1 schema versions. The manifest
files do not authenticate themselves: their SHA-256 values and the launcher hash are placed in
protected Host configuration before MSI construction. Missing, unexpected, or changed files
therefore fail closed at product runtime. No Gallery, PATH, user module, system module, or
alternate-version fallback exists.

## Custom URI and management entry

The MSI owns `HKLM\Software\Classes\relaybridge-setup` and invokes exactly:

```text
"C:\Program Files\RelayBridge\Setup\RelayBridge.SetupLauncher.exe" "%1"
```

The URI remains untrusted input and the frozen launcher performs strict parsing and Host
authorization. Uninstall removes only this RelayBridge-owned key. No management shortcut is
created because the current management endpoint is not represented by a stable installer-owned
URL abstraction. No firewall rule is created.

## Data, certificate, and tenant preservation

The ProgramData directory components are permanent and never overwritten. Default uninstall
removes service registration, Program Files payload, URI registration, and MSI metadata while
preserving database, queue/spool, configuration, operational history, certificate references,
and any other ProgramData. There is no broad `REMOVE_DATA` action in M8, no secure-wipe claim,
and no certificate-store deletion. Uninstall is local-only and never removes or changes Microsoft
tenant objects.

## Repair, upgrade, rollback, and uninstall

Repair uses MSI source/cache to restore protected binaries, tooling, manifests, registry, and
service authoring without changing ProgramData or tenant state. A stable UpgradeCode and WiX
major-upgrade authoring stop/remove the old service, replace the Program Files closure, retain
permanent ProgramData components, and start the installed service after standard installation
actions. Downgrades are blocked. `ServiceControl` stops and waits for the service, deletes its
registration on uninstall, and starts it on install. No MSI code edits or migrates SQLite;
application startup retains schema ownership.

The authored design uses native MSI rollback. An intentional internal failure fixture returned
failure and restored the prior service, URI, exact trusted execution closure, health, and
ProgramData without leaving the fixture registered.

## Prerequisites

The Host requires both Microsoft.NETCore.App and Microsoft.AspNetCore.App; the managed worker
requires Microsoft.NETCore.App. The bundle embeds exact Microsoft .NET Runtime 10.0.11 x64 and
ASP.NET Core Runtime 10.0.11 x64 installers acquired from pinned official URLs with recorded
SHA-512 values. It detects compatible installed 10.x runtime versions and otherwise installs the
fixed packages silently without uninstalling them when RelayBridge is removed. The MSI itself
performs .NET compatibility checks and gives an actionable prerequisite error.

## Build and signing

Run:

```powershell
./installer/build-installer.ps1 -Version 0.8.0 -Configuration Release
```

The script publishes Host and worker framework-dependent, publishes the launcher self-contained
NativeAOT, stages exact tooling, verifies acquisitions, generates manifests/trust anchors and WiX
file authoring, builds/ICE-validates MSI and bundle, and runs payload/manifest validation. Optional
`-SigningCertificateThumbprint` support signs RelayBridge executables, MSI, and bootstrapper with
`signtool`; it does not create or embed a test certificate.

Current development artifacts are unsigned. **PUBLIC RELEASE SIGNING GATE — OPEN.** Graph/Entra
package metadata points to Microsoft Developer Services terms rather than explicit redistribution
permission, so **EXTERNAL REDISTRIBUTION LEGAL REVIEW — OPEN**. Bundle/package artifacts must not
be published until both gates are resolved.

## Supported scope and non-goals

M8 initially supports Windows 10/11 x64, per-machine installation, a local interactive
administrator, LocalSystem service operation, Add/Remove Programs, standard silent MSI commands,
repair, major upgrade, and uninstall. Windows Server/RDP, Server Core, x86/Arm64, arbitrary
installation paths, remote management, broad firewall rules, data erasure, cloud cleanup,
Microsoft sign-in/provisioning during MSI, and M9/M10 work are outside this milestone.

## Verification status

Package and installed-machine verification pass: exact publish/tooling closures,
helper/tooling-manifest verification, trust-anchor consistency, payload exclusion scan, MSI ICE,
bootstrapper prerequisite planning, service/URI/ACL/reparse checks, real Edge URI launch,
launcher/worker parent-chain evidence, private PowerShell probes, ordinary-user and elevated
tamper denial, repair, uninstall/data preservation, reinstall, synthetic A-to-B major upgrade,
downgrade rejection, intentional-failure rollback, and silent servicing. The generated stage has
1,224 files: 6 helper-manifest entries and 1,185 tooling-manifest entries.

Acceptance exposed one concrete compatibility defect: Windows denied process/token/session
inspection from the medium-integrity launcher to the LocalSystem Host. The final launcher instead
requires the kernel-reported pipe PID to equal the running SCM-owned RelayBridge service PID and
requires SCM to report the exact own-process LocalSystem service and the completely quoted,
parameter-free protected Host binary path.
The real installed browser path then displayed the genuine native confirmation, created the
expected launcher/worker/private-PowerShell process chain, and cleaned the Job-contained tree and
protected scratch on cancellation. No Microsoft authentication, tenant mutation, or SMTP delivery
was performed.

Final Release verification completed with 0 warnings and 0 errors. All 59 unit and 403 integration
tests pass (462 total, 0 failed, 0 skipped). Installed trust closure and installer servicing pass.
`PUBLIC RELEASE SIGNING GATE — OPEN` and `EXTERNAL REDISTRIBUTION LEGAL REVIEW — OPEN` remain
external gates and do not authorize public distribution of the development packages.
