# Security Policy

RelayBridge handles authentication material and email content. Do not disclose a suspected
vulnerability in a public issue, pull request, discussion, or support bundle.

## Supported versions

RelayBridge has not yet issued a public production release. Security fixes currently target the
latest revision on the default branch. This policy will be updated with a version-support table
before the first public production release.

## Reporting a vulnerability

Use the repository host's private vulnerability-reporting feature. If that feature is not
available, do not open a public issue; use the maintainer's private contact mechanism shown on
the [public maintainer profile](https://github.com/yuvaraj-builds-ai) and clearly mark the report
as security-sensitive.

Include the affected revision or version, impact, minimal reproduction steps, and any proposed
mitigation. Use synthetic data. Do not send customer messages, passwords, OAuth tokens,
authorization codes, tenant identifiers, private keys, PFX files, or reusable credential caches.

The maintainer will acknowledge a usable report, assess severity and affected versions, and
coordinate remediation and disclosure. No response-time or bounty commitment is made.

## Public discussion

After a fix is available, the maintainer may publish a sanitized advisory and credit the reporter
if requested. Do not test against systems or Microsoft tenants you do not own or have explicit
permission to use.
