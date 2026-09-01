# SignPath Foundation readiness

This is a technical pre-application assessment, not approval by SignPath Foundation and not legal
advice. The authoritative conditions are maintained by SignPath Foundation.

## Eligibility matrix

| Requirement | Status | RelayBridge evidence / remaining action |
|---|---|---|
| No malware or unwanted behavior | PASS | The finite hardening/installed-machine matrix and release security gates passed; RelayBridge is a local SMTP relay, not an exploitation or evasion tool. |
| OSI-approved license without commercial dual license | PASS | RelayBridge-owned source is MPL-2.0 only. Third-party OSS components retain their OSI-approved licenses. |
| No proprietary project code | PASS | The signed candidate is built from RelayBridge MPL source plus reviewed OSS/runtime components; Graph/Entra package bytes are acquired Microsoft-to-customer and are not embedded. See component review below. |
| Maintained | PASS | The repository has current milestones, tests, architecture, security, and release documentation. |
| Already released in the form to sign | PUBLIC UNSIGNED RC1 / REVIEW REQUIRED | The public `v1.0.0-rc.1` Windows release is unsigned. SignPath Foundation still decides eligibility and whether that release satisfies its policy before the first signed release. |
| Functionality documented | PASS | README, master specification, architecture, setup, security, and operational diagnostics are documented. |
| Development and signing team controls source/build | PASS | The maintainer controls the public RelayBridge source repository and repository build scripts. |
| Sign only project-owned binaries | PASS | The signing plan targets RelayBridge-owned PE/MSI/bootstrapper layers and explicitly excludes upstream binaries from RelayBridge signatures. |
| Privacy/security behavior disclosed | PASS | The code-signing policy describes Microsoft/official-source network communication and states there is no RelayBridge-operated cloud service. |
| System changes announced | PASS | Installer documentation identifies the service, URI handler, Program Files tooling, and preserved ProgramData. |
| Uninstallation available | PASS | M8 installed acceptance verified uninstall and data-preservation behavior. |
| MFA for source host and SignPath | ACTION REQUIRED | Owner confirmation is required for GitHub MFA; SignPath MFA must be enabled when the account is created. |
| Team roles documented | PASS | Committer, reviewer, and approver roles are declared without inventing extra people. |
| Code signing policy published | PASS (pre-application) | `CODE-SIGNING-POLICY.md` exists; the required SignPath attribution remains explicitly pending approval. |
| Manual approval for each release | ACTION REQUIRED | Configure in SignPath after application approval. |
| GitHub trusted build/origin verification | ACTION REQUIRED | Release workflow is repository-controlled and GitHub-hosted; install the SignPath GitHub App and configure repository, branch, commit, workflow, and artifact checks after SignPath approval. |
| Artifact metadata restrictions | PASS (source ready) | Repository metadata sets product `RelayBridge`, consistent version input, company `RelayBridge contributors`, and an accurate description; SignPath artifact configuration remains to be created. |

## Proprietary-component review

| Future candidate component | License / distribution conclusion |
|---|---|
| RelayBridge-owned executables and libraries | MPL-2.0; project-owned and eligible for RelayBridge signing. |
| .NET / ASP.NET Core runtime and managed runtime libraries | Microsoft-maintained open-source runtime under MIT and applicable bundled third-party notices; upstream binaries are included/acquired but not re-signed as RelayBridge code. |
| PowerShell 7.6.4 | MIT upstream release; exact hash-pinned Microsoft-signed binary, not re-signed by RelayBridge. |
| ExchangeOnlineManagement 3.9.2 | Package `license.txt` states MIT License 2.0; exact hash-pinned upstream package, not re-signed by RelayBridge. |
| WiX 6.0.2 Burn/WixStdBA/NetFx/Util runtime | MS-RL, an OSI-approved license; exact source and full license are preserved in third-party notices. |
| Microsoft Graph/Entra modules | Not embedded in the MSI/bootstrapper. Exact packages are acquired directly from Microsoft only after explicit terms acceptance and independently verified before protected installation. |

No incorporated proprietary RelayBridge project code was identified. SignPath Foundation makes
the final eligibility decision, including its treatment of upstream runtime binaries.

## Published RC1 and future signing

SignPath Foundation currently requires a project to be already released in the form that should be
signed. RelayBridge has now published the intentionally unsigned `v1.0.0-rc.1` Windows pre-release
for evaluation/community testing. That publication does not represent SignPath approval, a signing
application, an issued certificate, or configured trusted signing. A later signed stable release
remains subject to SignPath Foundation's eligibility decision and every open release gate.

## Owner actions

- Confirm GitHub MFA remains enabled before applying to SignPath.
- Review the current SignPath eligibility requirements against the public RC1 before applying.
- After eligibility confirmation, install the SignPath GitHub App and configure origin verification,
  a manually approved release policy, and the artifact configuration.
