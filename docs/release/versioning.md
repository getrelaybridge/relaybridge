# Product versioning and release awareness

## Canonical product version

`Directory.Build.props` is the single authoritative source for the RelayBridge semantic product
version. The `RelayBridgeVersion` property supplies the .NET package and informational version,
installer artifact names, Burn bundle version, SBOM version, and release-awareness comparison
version. Build scripts must read that property instead of maintaining an independent default.

RelayBridge accepts only these public version forms:

- stable: `MAJOR.MINOR.PATCH`
- release candidate: `MAJOR.MINOR.PATCH-rc.N`
- Git tag: the corresponding version prefixed with `v`

Components do not contain leading zeroes. The current version is `1.0.0-rc.1`; no public tag or
official Windows binary release is created by setting this source value.

## Windows Installer mapping

MSI requires a three-part numeric product version, so `eng/versioning.ps1` deterministically maps
the semantic version to `MAJOR.MINOR.BUILD`. `BUILD` is `PATCH * 256 + STAGE`, where release
candidate `N` uses stage `N` and the stable release uses stage `255`. Major, minor, and patch are
limited to 0–255; release-candidate numbers are limited to 1–254. The derived build value must fit
the MSI 0–65535 range.

This preserves servicing order, including:

```text
1.0.0-rc.1 < 1.0.0-rc.2 < 1.0.0 < 1.0.1-rc.1 < 1.0.1
```

The numeric MSI value is an internal servicing value. Administrators, filenames, the management
UI, Burn, release tags, and SBOM metadata use the semantic product version.

## Release channels and checks

A stable build follows the Stable channel. A release-candidate build follows the Preview channel.
Stable checks consider only non-draft, non-prerelease GitHub Releases. Preview checks consider
both stable and matching `-rc.N` releases and select the highest valid semantic version rather
than trusting publication order.

The initial implementation is manual and informational only. Selecting **Check for updates** in
Settings makes one bounded unauthenticated HTTPS request to the official GitHub Releases API for
`getrelaybridge/relaybridge`. RelayBridge sends only normal connection metadata and the
`RelayBridge/<version>` user agent. It sends no tenant, device, message, configuration, machine
identifier, credential, or telemetry data. It does not follow release-supplied URLs; the displayed
release link is constructed from a strictly validated tag and the fixed canonical repository.

RelayBridge does not check automatically, download an update, install an update, or execute release
metadata. A GitHub timeout, rate limit, malformed response, or outage is reported only as
**Could not check** and cannot affect service health, SMTP intake, queue processing, Microsoft
configuration, or delivery.
