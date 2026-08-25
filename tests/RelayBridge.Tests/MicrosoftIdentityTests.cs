// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using RelayBridge.Core.Microsoft;
using Xunit;

namespace RelayBridge.Tests;

public sealed class MicrosoftIdentityTests
{
    private const string Thumbprint = "0123456789ABCDEF0123456789ABCDEF01234567";

    [Fact]
    public void Valid_identity_configuration_is_normalized_and_uses_fixed_Microsoft_endpoints()
    {
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var reference = MicrosoftCertificateReference.Create(
            "01 23 45 67 89 ab cd ef 01 23 45 67 89 ab cd ef 01 23 45 67",
            CertificateStoreTarget.LocalMachine);

        var configuration = MicrosoftIdentityConfiguration.Create(
            tenantId.ToString("B"),
            clientId.ToString("D"),
            reference);

        Assert.Equal(tenantId, configuration.TenantId);
        Assert.Equal(clientId, configuration.ClientId);
        Assert.Equal(Thumbprint, configuration.Certificate.Thumbprint);
        Assert.Equal("My", configuration.Certificate.StoreName);
        Assert.Equal($"https://login.microsoftonline.com/{tenantId:D}", configuration.Authority);
        Assert.Equal("https://outlook.office365.com/.default", MicrosoftIdentityConfiguration.ExchangeOnlineScope);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Invalid_tenant_ID_is_rejected(string tenantId)
    {
        var reference = MicrosoftCertificateReference.Create(Thumbprint, CertificateStoreTarget.LocalMachine);

        Assert.Throws<ArgumentException>(() => MicrosoftIdentityConfiguration.Create(
            tenantId,
            Guid.NewGuid().ToString("D"),
            reference));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Invalid_client_ID_is_rejected(string clientId)
    {
        var reference = MicrosoftCertificateReference.Create(Thumbprint, CertificateStoreTarget.LocalMachine);

        Assert.Throws<ArgumentException>(() => MicrosoftIdentityConfiguration.Create(
            Guid.NewGuid().ToString("D"),
            clientId,
            reference));
    }

    [Theory]
    [InlineData("")]
    [InlineData("xyz")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF012345678")]
    public void Invalid_certificate_reference_is_rejected(string thumbprint)
    {
        Assert.Throws<ArgumentException>(() => MicrosoftCertificateReference.Create(
            thumbprint,
            CertificateStoreTarget.LocalMachine));
    }

    [Fact]
    public void Token_value_is_excluded_from_string_and_json_diagnostics()
    {
        const string secretToken = "test-token-value-that-must-not-leak";
        var token = new MicrosoftAccessToken(
            secretToken,
            new DateTimeOffset(2026, 8, 22, 18, 0, 0, TimeSpan.Zero),
            Guid.NewGuid());

        var text = token.ToString();
        var json = JsonSerializer.Serialize(token);

        Assert.DoesNotContain(secretToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain(secretToken, json, StringComparison.Ordinal);
        Assert.DoesNotContain("AccessToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ExpiresOn", json, StringComparison.Ordinal);
    }
}
