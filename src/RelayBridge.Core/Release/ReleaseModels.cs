// SPDX-License-Identifier: MPL-2.0

namespace RelayBridge.Core.Release;

public enum ReleaseCheckStatus
{
    NotChecked,
    Checking,
    UpToDate,
    UpdateAvailable,
    CouldNotCheck,
}

public sealed record ReleaseCheckResult(
    ReleaseCheckStatus Status,
    DateTimeOffset CheckedUtc,
    ProductSemanticVersion CurrentVersion,
    ReleaseChannel Channel,
    ProductSemanticVersion? AvailableVersion = null,
    DateTimeOffset? PublishedUtc = null,
    string? SafeFailureCategory = null)
{
    public Uri? ReleaseUri => Status == ReleaseCheckStatus.UpdateAvailable
        ? AvailableVersion?.OfficialReleaseUri
        : null;
}
