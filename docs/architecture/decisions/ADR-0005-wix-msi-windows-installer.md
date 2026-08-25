# ADR-0005: Package RelayBridge with WiX MSI and a prerequisite-only bundle

- Status: Accepted for Milestone 8
- Date: 2026-08-24

## Context

RelayBridge needs a per-machine Windows installation with a LocalSystem service, a fixed
protected Program Files execution hierarchy, protected ProgramData state, machine-wide custom
URI registration, normal Windows repair/upgrade/uninstall behavior, and exact private Microsoft
provisioning tooling. The M5.1 execution-trust boundary requires security-critical paths to be
installer-owned and must not accept an arbitrary install directory.

The Host and managed setup worker are framework-dependent .NET 10 applications. The Host uses
ASP.NET Core, while the worker requires the .NET runtime. A normal release therefore also needs
an actionable prerequisite path without mixing Microsoft tenant setup into elevation.

## Decision

Use WiX Toolset 6.0.2 to produce:

1. an x64, per-machine MSI as the authoritative RelayBridge package; and
2. a minimal Burn bootstrapper that installs exact Microsoft .NET Runtime 10.0.11 and ASP.NET
   Core Runtime 10.0.11 packages when absent, then runs the MSI.

The MSI uses standard Windows Installer tables for files, directories, MSI 5.0 SDDL ACLs,
registry, service installation/control, repair, and major upgrades. It has no custom actions,
shell execution, PowerShell provisioning, firewall rule, or user-selectable security path.
Program Files contains only published runtime closures and pinned private tooling. ProgramData
is represented by permanent empty-directory components so ordinary uninstall preserves durable
state. Microsoft tenant objects and certificates are never installer uninstall targets.

Build acquisition is controlled by `installer/tooling-lock.json`. Exact HTTPS sources and
package hashes are checked before extraction. Generated helper/tooling manifests use the frozen
M5.1 schemas and their hashes are written into staged Host configuration before packaging.
Normal product runtime never downloads modules or discovers ambient PowerShell installations.

## Consequences

- Windows 10/11 x64 is the initial supported installer target. Windows Server/RDP is not claimed.
- MSI repair and major upgrade can service the protected execution closure while preserving
  ProgramData. Downgrades are blocked.
- The bootstrapper is larger because it embeds both exact .NET prerequisites, but the MSI remains
  independently deployable where prerequisites are already managed.
- Release signing is supported by the build script but requires a real trusted signing
  certificate; development artifacts can remain unsigned.
- Microsoft Graph/Entra package redistribution remains subject to legal/license approval. Until
  that review is complete, locally produced packages must not be published externally.
- Package-level validation does not prove installed ACL ownership, service lifecycle, URI browser
  behavior, rollback, repair, or upgrade. Those remain mandatory disposable-machine acceptance
  gates before M8 can freeze.
