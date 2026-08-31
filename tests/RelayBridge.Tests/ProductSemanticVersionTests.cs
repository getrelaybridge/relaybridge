// SPDX-License-Identifier: MPL-2.0

using RelayBridge.Core.Release;
using Xunit;

namespace RelayBridge.Tests;

public sealed class ProductSemanticVersionTests
{
    [Theory]
    [InlineData("1.0.0", false, "v1.0.0")]
    [InlineData("1.0.0-rc.1", true, "v1.0.0-rc.1")]
    [InlineData("1.1.0-rc.12", true, "v1.1.0-rc.12")]
    public void Supported_versions_round_trip(string value, bool prerelease, string tag)
    {
        var version = ProductSemanticVersion.Parse(value);

        Assert.Equal(value, version.ToString());
        Assert.Equal(prerelease, version.IsPrerelease);
        Assert.Equal(tag, version.Tag);
        Assert.True(ProductSemanticVersion.TryParseTag(tag, out var fromTag));
        Assert.Equal(version, fromTag);
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("v1.0")]
    [InlineData("1.0.0")]
    [InlineData("v1.0.0-beta")]
    [InlineData("v1.0.0-rc")]
    [InlineData("v1.0.0-rc.-1")]
    [InlineData("v1.0.0-rc.0")]
    [InlineData("v1.0.0-rc.255")]
    [InlineData("v1.0.0-rc.foo")]
    [InlineData("v01.0.0")]
    [InlineData("v999999999999.0.0")]
    [InlineData("unexpected text")]
    public void Malformed_or_unsupported_tags_are_rejected(string tag)
    {
        Assert.False(ProductSemanticVersion.TryParseTag(tag, out _));
    }

    [Theory]
    [InlineData("1.0.0-rc.1", "1.0.0-rc.2")]
    [InlineData("1.0.0-rc.12", "1.0.0")]
    [InlineData("1.0.0", "1.0.1-rc.1")]
    [InlineData("1.0.1-rc.1", "1.0.1")]
    [InlineData("1.0.1", "1.1.0-rc.1")]
    [InlineData("1.9.9", "2.0.0-rc.1")]
    public void Supported_versions_have_expected_order(string lower, string higher)
    {
        Assert.True(ProductSemanticVersion.Parse(lower).CompareTo(ProductSemanticVersion.Parse(higher)) < 0);
    }

    [Fact]
    public void Same_version_compares_equal_and_stable_defaults_to_stable_channel()
    {
        var version = ProductSemanticVersion.Parse("1.0.0");

        Assert.Equal(0, version.CompareTo(ProductSemanticVersion.Parse("1.0.0")));
        Assert.Equal(ReleaseChannel.Stable, version.DefaultChannel);
        Assert.Equal(ReleaseChannel.Preview, ProductSemanticVersion.Parse("1.0.0-rc.1").DefaultChannel);
    }

    [Theory]
    [InlineData("1.0.0", "https://github.com/getrelaybridge/relaybridge/releases/tag/v1.0.0")]
    [InlineData("1.0.0-rc.1", "https://github.com/getrelaybridge/relaybridge/releases/tag/v1.0.0-rc.1")]
    public void Release_uri_is_constructed_only_from_the_validated_tag(string value, string expectedUri)
    {
        var version = ProductSemanticVersion.Parse(value);

        Assert.Equal(expectedUri, version.OfficialReleaseUri.AbsoluteUri);
    }
}
