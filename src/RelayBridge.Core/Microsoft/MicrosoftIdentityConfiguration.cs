// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace RelayBridge.Core.Microsoft;

public enum CertificateStoreTarget
{
    LocalMachine,
    CurrentUser,
}

public sealed class MicrosoftCertificateReference
{
    private MicrosoftCertificateReference(string thumbprint, CertificateStoreTarget storeLocation)
    {
        Thumbprint = thumbprint;
        StoreLocation = storeLocation;
    }

    public string Thumbprint { get; }

    public string StoreName => "My";

    public CertificateStoreTarget StoreLocation { get; }

    public static MicrosoftCertificateReference Create(
        string thumbprint,
        CertificateStoreTarget storeLocation)
    {
        if (!Enum.IsDefined(storeLocation))
        {
            throw new ArgumentOutOfRangeException(nameof(storeLocation));
        }

        var normalized = NormalizeThumbprint(thumbprint);
        if (normalized.Length != 40 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "The certificate thumbprint must contain exactly 40 hexadecimal characters.",
                nameof(thumbprint));
        }

        return new MicrosoftCertificateReference(normalized, storeLocation);
    }

    private static string NormalizeThumbprint(string thumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);
        return string.Concat(thumbprint.Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();
    }
}

public sealed class MicrosoftIdentityConfiguration
{
    public const string ExchangeOnlineScope = "https://outlook.office365.com/.default";

    private MicrosoftIdentityConfiguration(
        Guid tenantId,
        Guid clientId,
        MicrosoftCertificateReference certificate)
    {
        TenantId = tenantId;
        ClientId = clientId;
        Certificate = certificate;
    }

    public Guid TenantId { get; }

    public Guid ClientId { get; }

    public MicrosoftCertificateReference Certificate { get; }

    public string Authority => $"https://login.microsoftonline.com/{TenantId:D}";

    public static MicrosoftIdentityConfiguration Create(
        string tenantId,
        string clientId,
        MicrosoftCertificateReference certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return new MicrosoftIdentityConfiguration(
            ParseRequiredGuid(tenantId, nameof(tenantId)),
            ParseRequiredGuid(clientId, nameof(clientId)),
            certificate);
    }

    public static MicrosoftIdentityConfiguration Create(
        Guid tenantId,
        Guid clientId,
        MicrosoftCertificateReference certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("The tenant ID cannot be empty.", nameof(tenantId));
        }

        if (clientId == Guid.Empty)
        {
            throw new ArgumentException("The client ID cannot be empty.", nameof(clientId));
        }

        return new MicrosoftIdentityConfiguration(tenantId, clientId, certificate);
    }

    private static Guid ParseRequiredGuid(string value, string parameterName)
    {
        if (!Guid.TryParse(value, out var parsed) || parsed == Guid.Empty)
        {
            throw new ArgumentException("A non-empty GUID is required.", parameterName);
        }

        return parsed;
    }
}

public static class MicrosoftConfigurationFingerprint
{
    public static string Create(MicrosoftIdentityConfiguration configuration, string? authorizedSender)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var canonical = string.Join(
            '\n',
            configuration.TenantId.ToString("D"),
            configuration.ClientId.ToString("D"),
            configuration.Certificate.Thumbprint,
            configuration.Certificate.StoreName,
            configuration.Certificate.StoreLocation.ToString(),
            authorizedSender?.Trim().ToLowerInvariant() ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string CreateRuntimeEvidenceKey(Guid activationId, string configurationFingerprint)
    {
        if (activationId == Guid.Empty)
        {
            throw new ArgumentException("The Microsoft activation ID cannot be empty.", nameof(activationId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(configurationFingerprint);
        return $"{activationId:D}:{configurationFingerprint}";
    }
}

public sealed record ActiveMicrosoftConfiguration(
    MicrosoftIdentityConfiguration Identity,
    string? AuthorizedSender,
    string ConfigurationFingerprint,
    Guid ActivationId)
{
    public string Fingerprint => MicrosoftConfigurationFingerprint.CreateRuntimeEvidenceKey(
        ActivationId,
        ConfigurationFingerprint);
}

[DebuggerDisplay("MicrosoftAccessToken: expires {ExpiresOn}")]
public sealed class MicrosoftAccessToken
{
    private readonly string _value;

    public MicrosoftAccessToken(string value, DateTimeOffset expiresOn, Guid tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
        ExpiresOn = expiresOn;
        TenantId = tenantId;
    }

    public DateTimeOffset ExpiresOn { get; }

    public Guid TenantId { get; }

    [JsonIgnore]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal string Value => _value;

    public override string ToString()
    {
        return $"MicrosoftAccessToken {{ ExpiresOn = {ExpiresOn:O}, TenantId = {TenantId:D} }}";
    }
}

public interface IMicrosoftTokenProvider
{
    Task<MicrosoftAccessToken> GetExchangeTokenAsync(CancellationToken cancellationToken);

    Task<MicrosoftAccessToken> GetExchangeTokenAsync(
        MicrosoftIdentityConfiguration configuration,
        CancellationToken cancellationToken);
}
