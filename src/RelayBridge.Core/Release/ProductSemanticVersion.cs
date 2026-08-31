// SPDX-License-Identifier: MPL-2.0

using System.Globalization;

namespace RelayBridge.Core.Release;

public enum ReleaseChannel
{
    Stable,
    Preview,
}

public readonly record struct ProductSemanticVersion : IComparable<ProductSemanticVersion>
{
    private const int MaximumNumericComponent = 255;
    private const int MaximumReleaseCandidate = 254;

    private ProductSemanticVersion(int major, int minor, int patch, int? releaseCandidate)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        ReleaseCandidate = releaseCandidate;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public int? ReleaseCandidate { get; }

    public bool IsPrerelease => ReleaseCandidate is not null;

    public ReleaseChannel DefaultChannel => IsPrerelease ? ReleaseChannel.Preview : ReleaseChannel.Stable;

    public string Tag => $"v{this}";

    public Uri OfficialReleaseUri => new(
        $"https://github.com/getrelaybridge/relaybridge/releases/tag/{Tag}",
        UriKind.Absolute);

    public static ProductSemanticVersion Parse(string value)
    {
        if (!TryParse(value, out var version))
        {
            throw new FormatException("The RelayBridge product version is not supported.");
        }

        return version;
    }

    public static bool TryParse(string? value, out ProductSemanticVersion version) =>
        TryParseCore(value, requireTagPrefix: false, out version);

    public static bool TryParseTag(string? value, out ProductSemanticVersion version) =>
        TryParseCore(value, requireTagPrefix: true, out version);

    public int CompareTo(ProductSemanticVersion other)
    {
        var comparison = Major.CompareTo(other.Major);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Minor.CompareTo(other.Minor);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Patch.CompareTo(other.Patch);
        if (comparison != 0)
        {
            return comparison;
        }

        if (ReleaseCandidate is null)
        {
            return other.ReleaseCandidate is null ? 0 : 1;
        }

        return other.ReleaseCandidate is null
            ? -1
            : ReleaseCandidate.Value.CompareTo(other.ReleaseCandidate.Value);
    }

    public override string ToString() => ReleaseCandidate is null
        ? $"{Major}.{Minor}.{Patch}"
        : $"{Major}.{Minor}.{Patch}-rc.{ReleaseCandidate.Value}";

    private static bool TryParseCore(
        string? value,
        bool requireTagPrefix,
        out ProductSemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
        {
            return false;
        }

        var text = value.AsSpan();
        if (requireTagPrefix)
        {
            if (text[0] != 'v')
            {
                return false;
            }

            text = text[1..];
        }
        else if (text[0] == 'v')
        {
            return false;
        }

        var prereleaseSeparator = text.IndexOf('-');
        var numeric = prereleaseSeparator < 0 ? text : text[..prereleaseSeparator];
        var prerelease = prereleaseSeparator < 0 ? ReadOnlySpan<char>.Empty : text[(prereleaseSeparator + 1)..];
        Span<Range> numericParts = stackalloc Range[3];
        var numericCount = numeric.Split(numericParts, '.', StringSplitOptions.None);
        if (numericCount != 3 ||
            !TryParseComponent(numeric[numericParts[0]], MaximumNumericComponent, out var major) ||
            !TryParseComponent(numeric[numericParts[1]], MaximumNumericComponent, out var minor) ||
            !TryParseComponent(numeric[numericParts[2]], MaximumNumericComponent, out var patch))
        {
            return false;
        }

        int? releaseCandidate = null;
        if (!prerelease.IsEmpty)
        {
            const string prefix = "rc.";
            if (!prerelease.StartsWith(prefix, StringComparison.Ordinal) ||
                !TryParseComponent(prerelease[prefix.Length..], MaximumReleaseCandidate, out var rc) ||
                rc == 0)
            {
                return false;
            }

            releaseCandidate = rc;
        }
        else if (prereleaseSeparator >= 0)
        {
            return false;
        }

        version = new ProductSemanticVersion(major, minor, patch, releaseCandidate);
        return true;
    }

    private static bool TryParseComponent(ReadOnlySpan<char> value, int maximum, out int parsed)
    {
        parsed = 0;
        return !value.IsEmpty &&
               (value.Length == 1 || value[0] != '0') &&
               int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) &&
               parsed <= maximum;
    }
}
