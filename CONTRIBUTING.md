# Contributing to RelayBridge

RelayBridge is led by [@yuvaraj-builds-ai](https://github.com/yuvaraj-builds-ai). Bug reports,
focused fixes, tests, documentation corrections,
and narrowly scoped proposals are welcome. A submission is not a promise that it will be merged;
the maintainer decides project direction, scope, release timing, and whether a change fits the
current milestone and security model.

Before changing code, read `AGENTS.md`, `docs/MASTER_SPEC.md`, `BUILD_STATUS.md`, the relevant
architecture documents, and `docs/security/threat-model.md` for security-sensitive work. Open an
issue before a large feature, dependency, schema, installer, or security-boundary change.

## Development rules

- Keep changes within the current milestone and prefer the smallest production-quality design.
- Do not add dependencies without documenting their purpose, license, maintenance status, and
  security implications.
- Preserve the SMTP-to-SMTP architecture, durable-acceptance boundary, strict relay authorization,
  and fail-closed Microsoft setup model.
- Never commit customer mail, tenant identifiers, passwords, tokens, private keys, PFX files,
  credential caches, production configuration, or other secrets.
- Add the appropriate `SPDX-License-Identifier: MPL-2.0` comment to new RelayBridge-owned source
  files when that file format supports comments.
- Do not remove or rewrite third-party license notices.

Run before submitting:

```powershell
./eng/verify-license.ps1
dotnet build RelayBridge.sln --configuration Release
dotnet test RelayBridge.sln --configuration Release --no-build
git diff --check
```

## Developer Certificate of Origin

RelayBridge uses the [Developer Certificate of Origin 1.1](https://developercertificate.org/)
and does not require a contributor license agreement. Every commit must include a `Signed-off-by`
trailer certifying that you have the right to contribute the work under the project's license:

```text
Signed-off-by: Your Name <your-public-email@example.com>
```

Git can add this trailer with `git commit -s`. Use an identity and email address you intend to
publish in Git history. By contributing, you agree that your contribution and its history are
public and licensed under MPL-2.0 where applicable.

## Review and conduct

Pull requests should explain the problem, the bounded solution, tests run, security impact, and
documentation impact. Maintainers may request changes or decline work that expands scope or risk.
Participation is governed by [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).

Do not report suspected vulnerabilities in a public issue. Follow [SECURITY.md](SECURITY.md).
