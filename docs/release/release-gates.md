# RelayBridge release gates

| Gate | Status | Evidence / closure requirement |
|---|---|---|
| M10 unsigned RC1 publication | COMPLETED / PUBLIC | The immutable `v1.0.0-rc.1` source tag, exact unsigned nine-file release package, checksums/provenance, final gates, and owner Windows 11 x64 smoke passed before the public pre-release was published. |
| Product hardening (M9) | CLOSED / PASS | Frozen after the finite 20-case Windows 11/repository acceptance matrix passed. |
| Public source readiness (RP-1) | PUBLIC / PASS | The MPL-2.0 source repository is public from an intentional fresh-history snapshot. Governance, source-only CI, snapshot scanning, the GitHub noreply identity, and the protected `main` ruleset are in place. |
| Microsoft redistribution clarification | OPEN | Written terms/permission review for redistributed Microsoft components. |
| Microsoft Graph missing `license.txt` clarification | OPEN | Written clarification for the exact Graph packages that require acceptance but omit `license.txt`. |
| WiX Toolset 6 OSMF/license compliance | CLOSED FOR CURRENT NON-REVENUE STATUS | Legal user is an individual developer; current use is non-revenue-generating. WiX v6.0.2 notice, MS-RL text/source, and SBOM runtime classification are recorded. Recheck before revenue-generating use. |
| SignPath Foundation eligibility | ACTION REQUIRED | The public repository is established. MFA confirmation, the SignPath application, trusted GitHub origin configuration, manual approval policy, and final artifact configuration remain open. |
| SignPath released-project policy | REVIEW REQUIRED | A public unsigned RC1 Windows release now exists. SignPath Foundation still makes the eligibility decision; no application, approval, trusted-build configuration, or certificate is active. |
| Authenticode signing identity | OPEN | Target is SignPath Foundation OSS; no application or certificate is active. |
| Signed artifact verification | NOT RUN — WAITING FOR SIGNING IDENTITY | Run after a signing identity is available. |
| Unsigned RC1 evaluation publication | COMPLETED / PUBLIC | The exact tagged package passed final gates, checksum/provenance review, and owner smoke before publication as an unsigned pre-release. Open Microsoft clarification remains disclosed; RC1 publication adds no legal conclusion. |
| Signed stable production publication | PROHIBITED | Requires the applicable Microsoft clarification, trusted Authenticode identity, signed-package verification, and separate owner authorization. |

The installer build supports SHA-256 Authenticode signing of RelayBridge executables, the MSI, and
the bootstrapper with RFC 3161 timestamping. Development artifacts are not releases. RC1 is a
distinct, intentionally unsigned evaluation pre-release and must carry the published warning,
source provenance, SBOM/notices, and checksums.
