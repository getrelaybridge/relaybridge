# Source publication and licensing

## RelayBridge-owned source

RelayBridge-owned source code is licensed under the Mozilla Public License 2.0. The root
`LICENSE` file is the unmodified official MPL-2.0 text. Human-authored source formats that permit
comments carry an `SPDX-License-Identifier: MPL-2.0` marker. Commentless configuration and data
formats are covered by the root license and repository context.

The current tracked source was authored for RelayBridge. The repository does not vendor
Microsoft, WiX, or other third-party source code as RelayBridge-owned source. Third-party binary,
module, and build-tool identities remain under their own terms and are inventoried in
`docs/release/THIRD-PARTY-NOTICES.md` and the generated release SBOM.

## Binary-to-source invariant

For every public RelayBridge binary release, the project must publish the exact corresponding
source at the same immutable release tag. That source must include the build and installer
authoring needed to reproduce the RelayBridge-owned executable source forms, subject to the
documented third-party acquisition process. Release notes and artifact metadata must identify
that exact tag; a moving default branch is not a substitute.

Generated installer authoring, manifests, SBOMs, and build outputs under `artifacts/` are not
hand-maintained source and are not committed. Their RelayBridge-owned generator scripts and
inputs are licensed source. Generated outputs must preserve required third-party notices and
must not be mislabeled as independently authored RelayBridge source.

MPL-2.0 is file-level copyleft. It does not transfer ownership of contributions, third-party
components, project names, or trademarks. Contributions are accepted under the DCO process in
`CONTRIBUTING.md`; no CLA or copyright assignment is required.

## Project identity

The MPL permits forks and modified distributions. RelayBridge is also the name of the official
project maintained by [@yuvaraj-builds-ai](https://github.com/yuvaraj-builds-ai). Modified
distributions should identify their changes and must not imply that they are an official or
endorsed RelayBridge release. No registered-trademark status is claimed, and this project-identity
guidance does not limit the rights granted by MPL-2.0.

## Publication boundary

Public source readiness does not by itself authorize installer publication. M10 is preparing one
explicitly unsigned public RC1 for evaluation/community testing, but publication still requires
the exact merged source commit/tag, passing package gates, owner smoke approval, and a separate
release task. Authenticode signing, Microsoft licensing clarification, and SignPath eligibility
remain separate gates for the later signed stable release in `docs/release/release-gates.md`. WiX
Toolset 6 OSMF/license compliance is closed for the current individual, non-revenue-generating use
and must be reviewed again if that status changes.

## Public snapshot inventory

The public repository was created from Git-tracked source only. It includes the root governance and
build files plus `.github/`, `src/`, `tests/`, `installer/`, `eng/`, and `docs/`. This includes
installer authoring, SBOM/notices generators, tooling locks, architecture/security documentation,
and contribution templates.

The snapshot excludes private Git history/tags and every untracked, ignored, generated, runtime,
or machine-specific item, including `.git/`, `.local/`, `artifacts/`, `bin/`, `obj/`, installer
caches, Graph/Entra package bytes, MSI/EXE/WiXPDB/PDB files, databases, mail/spool data, setup
scratch, credentials, and test output. `eng/export-public-source.ps1` uses `git archive` against a
clean committed revision; it does not recursively copy the workspace.

The public repository began with one fresh root commit and did not import private engineering
commits or milestone tags. All reachable public commits use the GitHub noreply identity for
`yuvaraj-builds-ai`; no private email is used or inferred.

Public source may be published before Windows binaries. Each later binary release must refer to its
exact corresponding public source tag. The unsigned RC1 preparation does not create that tag or
publish a binary; the signed stable production release remains prohibited until its applicable
external release gates close.
