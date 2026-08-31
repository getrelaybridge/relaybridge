# RelayBridge third-party component inventory

This inventory accompanies the Windows distribution. It is an engineering inventory, not legal
advice. Exact versions are recorded in the release SBOM and installer tooling lock.

RelayBridge-owned source is licensed under MPL-2.0. Nothing in that license changes the license
or ownership of the third-party components below. Third-party source and binaries remain under
their own terms.

The RelayBridge visual mark used by the Windows installer is project-owned material imported from
the separately MIT-licensed RelayBridge website repository at public commit
`0c47c2b88cada345ebad06b7d26b08d2a6dcb9ac`. Its provenance is recorded in
`installer/branding/README.md`. This does not change the MPL-2.0 license of RelayBridge application
source or the licenses of the third-party components below.

## A. Components redistributed by RelayBridge

| Component | Version | License / role |
|---|---:|---|
| .NET application NuGet dependencies | SBOM | Runtime libraries published with the Host and setup helper; package license metadata remains authoritative. |
| PowerShell | 7.6.4 | MIT; private Microsoft PowerShell runtime acquired from the pinned official release URI and packaged in the installer. |
| ExchangeOnlineManagement | 3.9.2 | MIT License 2.0 in the package `license.txt`; private Microsoft module acquired from its exact pinned PowerShell Gallery package and packaged in the installer. |
| .NET Runtime and ASP.NET Core Runtime installers | 10.0.11 | Microsoft prerequisite installers acquired from exact hash-pinned official sources and embedded in the bootstrapper. |

## B. WiX Toolset 6.0.2 incorporated installer components

RelayBridge's MSI/bootstrapper incorporates the following WiX Toolset 6.0.2 runtime output:

- Burn engine;
- WixStandardBootstrapperApplication;
- NetFx extension runtime/custom-action components; and
- Util extension runtime/custom-action components.

The WiX SDK/compiler and its build-time packages are build tooling; they are not represented as
RelayBridge runtime components. The generated runtime components above are redistributed and are
classified as required components in the CycloneDX SBOM.

Copyright (c) .NET Foundation and contributors.

License: Microsoft Reciprocal License (MS-RL). RelayBridge does not relicense WiX under MPL-2.0.
The corresponding exact-version source is available from the upstream
[WiX Toolset v6.0.2 source tag](https://github.com/wixtoolset/wix/tree/v6.0.2) and
[v6.0.2 release](https://github.com/wixtoolset/wix/releases/tag/v6.0.2).

The legal WiX user for the current RelayBridge build is an individual developer, and the current
use is non-revenue-generating. Under the WiX v6.0.2 OSMF agreement, the maintenance fee is not
required for this current status. This classification must be reviewed again before any use that
generates revenue. It does not impose an OSMF payment obligation on an end user merely for running
the RelayBridge installer.

### Microsoft Reciprocal License (MS-RL)

This license governs use of the accompanying software. If you use the software, you accept this
license. If you do not accept the license, do not use the software.

1. Definitions

The terms "reproduce," "reproduction," "derivative works," and "distribution" have the same
meaning here as under U.S. copyright law.

A "contribution" is the original software, or any additions or changes to the software.

A "contributor" is any person that distributes its contribution under this license.

"Licensed patents" are a contributor's patent claims that read directly on its contribution.

2. Grant of Rights

(A) Copyright Grant- Subject to the terms of this license, including the license conditions and
limitations in section 3, each contributor grants you a non-exclusive, worldwide, royalty-free
copyright license to reproduce its contribution, prepare derivative works of its contribution,
and distribute its contribution or any derivative works that you create.

(B) Patent Grant- Subject to the terms of this license, including the license conditions and
limitations in section 3, each contributor grants you a non-exclusive, worldwide, royalty-free
license under its licensed patents to make, have made, use, sell, offer for sale, import, and/or
otherwise dispose of its contribution in the software or derivative works of the contribution in
the software.

3. Conditions and Limitations

(A) Reciprocal Grants- For any file you distribute that contains code from the software (in source
code or binary format), you must provide recipients the source code to that file along with a copy
of this license, which license will govern that file. You may license other files that are entirely
your own work and do not contain code from the software under any terms you choose.

(B) No Trademark License- This license does not grant you rights to use any contributors' name,
logo, or trademarks.

(C) If you bring a patent claim against any contributor over patents that you claim are infringed
by the software, your patent license from such contributor to the software ends automatically.

(D) If you distribute any portion of the software, you must retain all copyright, patent,
trademark, and attribution notices that are present in the software.

(E) If you distribute any portion of the software in source code form, you may do so only under
this license by including a complete copy of this license with your distribution. If you distribute
any portion of the software in compiled or object code form, you may only do so under a license
that complies with this license.

(F) The software is licensed "as-is." You bear the risk of using it. The contributors give no
express warranties, guarantees or conditions. You may have additional consumer rights under your
local laws which this license cannot change. To the extent permitted under your local laws, the
contributors exclude the implied warranties of merchantability, fitness for a particular purpose
and non-infringement.

## C. Components acquired directly from Microsoft during installation

The public-candidate MSI and bootstrapper do not embed these package bytes. WiX Burn downloads the
exact locked package, verifies size and SHA-512, and the native provisioner verifies SHA-256 and
package metadata before protected extraction.

| Component | Version | License-acceptance behavior |
|---|---:|---|
| Microsoft.Graph.Authentication | 2.25.0 | Explicit Microsoft terms acceptance required. |
| Microsoft.Graph.Applications | 2.25.0 | Explicit Microsoft terms acceptance required. |
| Microsoft.Entra.Authentication | 1.3.0 | Exact package identity locked. |
| Microsoft.Entra.Applications | 1.3.0 | Exact package identity locked. |

## D. Development and build-only dependencies

| Component | Version | Role |
|---|---:|---|
| WiX Toolset SDK/extensions | 6.0.2 | Builds and validates the MSI/bootstrapper; runtime portions incorporated into the distribution are separately identified above. |
| Microsoft.NET.Test.Sdk | 18.9.0 | Test execution only. |
| xUnit / Visual Studio runner | 2.9.3 / 3.1.5 | Test execution only. |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.11 | Integration tests only. |

## Open external-release questions

- MICROSOFT REDISTRIBUTION CLARIFICATION — OPEN.
- MICROSOFT GRAPH MISSING `license.txt` CLARIFICATION — OPEN.
- PUBLIC RELEASE SIGNING GATE — OPEN.

M10 is preparing one explicitly unsigned public RC1 for evaluation/community testing; it does not
close or reinterpret these external questions. RC1 publication still requires the exact source
tag, completed package gates, owner smoke approval, and a separate release task. The later signed
stable production release remains prohibited until its applicable release gates close with
evidence.
