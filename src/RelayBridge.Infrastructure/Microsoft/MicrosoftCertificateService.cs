// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RelayBridge.Core.Microsoft;
using RelayBridge.Infrastructure.Storage;

namespace RelayBridge.Infrastructure.Microsoft;

public sealed record PublicCertificateExport(string FileName, string FullPath, MicrosoftCertificateMetadata Certificate);

public sealed class MicrosoftCertificateService
{
    private static readonly byte[] PrivateKeyProbe = SHA256.HashData("RelayBridge private-key access check"u8);
    private readonly RelayDatabase _database;
    private readonly MicrosoftIdentityOptions _options;
    private readonly TimeProvider _timeProvider;

    public MicrosoftCertificateService(
        RelayDatabase database,
        MicrosoftIdentityOptions options,
        TimeProvider timeProvider)
    {
        _database = database;
        _options = options;
        _timeProvider = timeProvider;
        _options.Validate();
    }

    public CertificateValidationResult Validate(
        MicrosoftCertificateReference? reference,
        CancellationToken cancellationToken = default)
    {
        if (reference is null)
        {
            return new CertificateValidationResult(
                CertificateValidationStatus.NotConfigured,
                "No Microsoft authentication certificate is configured.",
                null);
        }

        var resolution = Resolve(reference, cancellationToken);
        resolution.Certificate?.Dispose();
        return resolution.Result;
    }

    public IReadOnlyList<MicrosoftCertificateMetadata> ListUsableCertificates(
        CertificateStoreTarget storeLocation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        using var store = new X509Store(StoreName.My, MapStoreLocation(storeLocation));
        try
        {
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        }
        catch (CryptographicException)
        {
            return [];
        }

        var results = new List<MicrosoftCertificateMetadata>();
        foreach (var certificate in store.Certificates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(certificate.Thumbprint))
                {
                    continue;
                }

                var reference = MicrosoftCertificateReference.Create(certificate.Thumbprint, storeLocation);
                var validation = ValidateSelected(reference, certificate);
                if (validation.IsUsable && validation.Certificate is not null)
                {
                    results.Add(validation.Certificate);
                }
            }
            finally
            {
                certificate.Dispose();
            }
        }

        return results
            .OrderByDescending(certificate => certificate.NotAfter)
            .ThenBy(certificate => certificate.Subject, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public MicrosoftCertificateMetadata GenerateSelfSignedCertificate(
        CertificateStoreTarget storeLocation = CertificateStoreTarget.LocalMachine,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "RelayBridge certificate generation currently requires the Windows certificate store.");
        }

        if (!Enum.IsDefined(storeLocation))
        {
            throw new ArgumentOutOfRangeException(nameof(storeLocation));
        }

        var keyName = $"RelayBridge-{Guid.NewGuid():N}";
        var creationParameters = new CngKeyCreationParameters
        {
            ExportPolicy = CngExportPolicies.None,
            KeyCreationOptions = storeLocation == CertificateStoreTarget.LocalMachine
                ? CngKeyCreationOptions.MachineKey
                : CngKeyCreationOptions.None,
            KeyUsage = CngKeyUsages.Signing,
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
        };
        creationParameters.Parameters.Add(new CngProperty(
            "Length",
            BitConverter.GetBytes(2048),
            CngPropertyOptions.None));

        using var key = CngKey.Create(CngAlgorithm.Rsa, keyName, creationParameters);
        try
        {
            using var rsa = new RSACng(key);
            var request = new CertificateRequest(
                "CN=RelayBridge Microsoft Authentication",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature,
                true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.2", "Client Authentication") },
                false));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

            var now = _timeProvider.GetUtcNow();
            using var certificate = request.CreateSelfSigned(
                now.AddMinutes(-5),
                now.AddDays(_options.GeneratedCertificateValidityDays));
            certificate.FriendlyName = "RelayBridge Microsoft Authentication";

            var reference = MicrosoftCertificateReference.Create(certificate.Thumbprint, storeLocation);
            var validation = ValidateSelected(reference, certificate);
            if (!validation.IsUsable || validation.Certificate is null)
            {
                throw new CryptographicException(
                    $"The generated certificate could not be used: {validation.Message}");
            }

            using var store = new X509Store(StoreName.My, MapStoreLocation(storeLocation));
            store.Open(OpenFlags.ReadWrite);
            store.Add(certificate);

            return validation.Certificate;
        }
        catch
        {
            key.Delete();
            throw;
        }
    }

    public async Task<PublicCertificateExport> ExportPublicCertificateAsync(
        MicrosoftCertificateReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var resolution = Resolve(reference, cancellationToken);
        using var certificate = resolution.Certificate;
        if (!resolution.Result.IsUsable || certificate is null || resolution.Result.Certificate is null)
        {
            throw CreateCertificateException(resolution.Result);
        }

        var exportDirectory = Path.Combine(_database.DataDirectory, "exports");
        Directory.CreateDirectory(exportDirectory);
        var fileName = $"relaybridge-auth-{reference.Thumbprint[..16].ToLowerInvariant()}.cer";
        var destinationPath = Path.Combine(exportDirectory, fileName);
        var temporaryPath = Path.Combine(exportDirectory, $".{Guid.NewGuid():N}.tmp");
        var publicBytes = certificate.Export(X509ContentType.Cert);
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, publicBytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return new PublicCertificateExport(fileName, destinationPath, resolution.Result.Certificate);
    }

    internal X509Certificate2 LoadForAuthentication(
        MicrosoftCertificateReference reference,
        CancellationToken cancellationToken)
    {
        var resolution = Resolve(reference, cancellationToken);
        if (resolution.Result.IsUsable && resolution.Certificate is not null)
        {
            return resolution.Certificate;
        }

        resolution.Certificate?.Dispose();
        throw CreateCertificateException(resolution.Result);
    }

    private CertificateResolution Resolve(
        MicrosoftCertificateReference reference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        X509Certificate2? selected = null;
        try
        {
            using var store = new X509Store(StoreName.My, MapStoreLocation(reference.StoreLocation));
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var certificates = store.Certificates;
            var matches = certificates
                .Where(certificate => string.Equals(
                    certificate.Thumbprint,
                    reference.Thumbprint,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (matches.Length == 0)
            {
                DisposeCertificates(certificates);
                return Missing();
            }

            if (matches.Length != 1)
            {
                DisposeCertificates(certificates);
                return new CertificateResolution(
                    new CertificateValidationResult(
                        CertificateValidationStatus.Invalid,
                        "More than one certificate matched the configured thumbprint.",
                        null),
                    null);
            }

            selected = matches[0];
            foreach (var certificate in certificates)
            {
                if (!ReferenceEquals(certificate, selected))
                {
                    certificate.Dispose();
                }
            }
        }
        catch (CryptographicException)
        {
            selected?.Dispose();
            return new CertificateResolution(
                new CertificateValidationResult(
                    CertificateValidationStatus.Invalid,
                    "RelayBridge could not open the configured Windows certificate store.",
                    null),
                null);
        }

        var result = ValidateSelected(reference, selected);
        return new CertificateResolution(result, selected);
    }

    private CertificateValidationResult ValidateSelected(
        MicrosoftCertificateReference reference,
        X509Certificate2 certificate)
    {
        var notBefore = new DateTimeOffset(certificate.NotBefore).ToUniversalTime();
        var notAfter = new DateTimeOffset(certificate.NotAfter).ToUniversalTime();
        var now = _timeProvider.GetUtcNow();
        var publicKey = certificate.GetRSAPublicKey();
        var keySize = publicKey?.KeySize ?? 0;
        var metadata = new MicrosoftCertificateMetadata(
            reference.Thumbprint,
            certificate.Subject,
            reference.StoreName,
            reference.StoreLocation,
            notBefore,
            notAfter,
            keySize);

        if (now < notBefore)
        {
            publicKey?.Dispose();
            return new CertificateValidationResult(
                CertificateValidationStatus.Invalid,
                "The authentication certificate is not valid yet.",
                metadata);
        }

        if (now >= notAfter)
        {
            publicKey?.Dispose();
            return new CertificateValidationResult(
                CertificateValidationStatus.Expired,
                "The authentication certificate has expired.",
                metadata);
        }

        if (publicKey is null || keySize < 2048 || !AllowsDigitalSignature(certificate))
        {
            publicKey?.Dispose();
            return new CertificateValidationResult(
                CertificateValidationStatus.Unsupported,
                "The authentication certificate must use an RSA signing key of at least 2048 bits.",
                metadata);
        }

        using (publicKey)
        {
            if (!certificate.HasPrivateKey)
            {
                return new CertificateValidationResult(
                    CertificateValidationStatus.NoPrivateKey,
                    "The authentication certificate does not contain a local private key.",
                    metadata);
            }

            try
            {
                using var privateKey = certificate.GetRSAPrivateKey();
                if (privateKey is null)
                {
                    return new CertificateValidationResult(
                        CertificateValidationStatus.PrivateKeyInaccessible,
                        "RelayBridge found the authentication certificate, but the current service account cannot access its private key.",
                        metadata);
                }

                var signature = privateKey.SignHash(
                    PrivateKeyProbe,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                if (!publicKey.VerifyHash(
                    PrivateKeyProbe,
                    signature,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1))
                {
                    return new CertificateValidationResult(
                        CertificateValidationStatus.Invalid,
                        "The authentication certificate private key did not match its public key.",
                        metadata);
                }
            }
            catch (Exception exception) when (exception is CryptographicException or UnauthorizedAccessException)
            {
                return new CertificateValidationResult(
                    CertificateValidationStatus.PrivateKeyInaccessible,
                    "RelayBridge found the authentication certificate, but the current service account cannot access its private key.",
                    metadata);
            }
        }

        var expiringSoon = notAfter - now <= TimeSpan.FromDays(_options.CertificateExpiryWarningDays);
        return new CertificateValidationResult(
            expiringSoon ? CertificateValidationStatus.ExpiringSoon : CertificateValidationStatus.Valid,
            expiringSoon
                ? "The authentication certificate is usable but will expire soon."
                : "The authentication certificate is valid and its private key is accessible.",
            metadata);
    }

    private static bool AllowsDigitalSignature(X509Certificate2 certificate)
    {
        var keyUsage = certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
        return keyUsage is null || keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature);
    }

    private static StoreLocation MapStoreLocation(CertificateStoreTarget target)
    {
        return target switch
        {
            CertificateStoreTarget.LocalMachine => StoreLocation.LocalMachine,
            CertificateStoreTarget.CurrentUser => StoreLocation.CurrentUser,
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
    }

    private static void DisposeCertificates(IEnumerable<X509Certificate2> certificates)
    {
        foreach (var certificate in certificates)
        {
            certificate.Dispose();
        }
    }

    private static CertificateResolution Missing()
    {
        return new CertificateResolution(
            new CertificateValidationResult(
                CertificateValidationStatus.Missing,
                "The configured authentication certificate was not found in the Windows certificate store.",
                null),
            null);
    }

    private static MicrosoftIdentityException CreateCertificateException(CertificateValidationResult result)
    {
        var category = result.Status switch
        {
            CertificateValidationStatus.Missing => MicrosoftIdentityErrorCategory.CertificateMissing,
            CertificateValidationStatus.Expired => MicrosoftIdentityErrorCategory.CertificateExpired,
            CertificateValidationStatus.NoPrivateKey or CertificateValidationStatus.PrivateKeyInaccessible =>
                MicrosoftIdentityErrorCategory.PrivateKeyUnavailable,
            CertificateValidationStatus.NotConfigured => MicrosoftIdentityErrorCategory.InvalidConfiguration,
            _ => MicrosoftIdentityErrorCategory.CertificateInvalid,
        };
        return new MicrosoftIdentityException(category, result.Message);
    }

    private sealed record CertificateResolution(
        CertificateValidationResult Result,
        X509Certificate2? Certificate);
}
