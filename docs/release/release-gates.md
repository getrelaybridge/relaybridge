# RelayBridge release gates

| Gate | Status | Evidence / closure requirement |
|---|---|---|
| Product hardening (M9) | CLOSED / PASS | Frozen after the finite 20-case Windows 11/repository acceptance matrix passed. |
| Public source readiness (RP-1) | PUBLIC / PASS | The MPL-2.0 source repository is public from an intentional fresh-history snapshot. Governance, source-only CI, snapshot scanning, the GitHub noreply identity, and the protected `main` ruleset are in place. |
| Microsoft redistribution clarification | OPEN | Written terms/permission review for redistributed Microsoft components. |
| Microsoft Graph missing `license.txt` clarification | OPEN | Written clarification for the exact Graph packages that require acceptance but omit `license.txt`. |
| WiX Toolset 6 OSMF/license compliance | CLOSED FOR CURRENT NON-REVENUE STATUS | Legal user is an individual developer; current use is non-revenue-generating. WiX v6.0.2 notice, MS-RL text/source, and SBOM runtime classification are recorded. Recheck before revenue-generating use. |
| SignPath Foundation eligibility | ACTION REQUIRED | The public repository is established. MFA confirmation, the SignPath application, trusted GitHub origin configuration, manual approval policy, and final artifact configuration remain open. |
| SignPath released-project policy | CLARIFICATION REQUIRED | Official Windows binaries have not been released; ask whether a public source repository plus reproducible unsigned candidate workflow is sufficient before a first signed binary release. |
| Authenticode signing identity | OPEN | Target is SignPath Foundation OSS; no application or certificate is active. |
| Signed artifact verification | NOT RUN — WAITING FOR SIGNING IDENTITY | Run after a signing identity is available. |
| External Windows binary publication | PROHIBITED | Allowed only after all applicable binary-release gates close. Public source publication is a separate gate. |

The installer build supports SHA-256 Authenticode signing of RelayBridge executables, the MSI, and the bootstrapper with RFC 3161 timestamping. Development artifacts are unsigned and are not public-release candidates.
