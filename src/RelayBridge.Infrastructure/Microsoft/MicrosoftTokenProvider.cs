// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography.X509Certificates;
using Microsoft.Identity.Client;
using RelayBridge.Core.Microsoft;
using RelayBridge.Infrastructure.Storage;

namespace RelayBridge.Infrastructure.Microsoft;

public interface IMicrosoftIdentityClient
{
    Task<MicrosoftAccessToken> AcquireTokenAsync(string scope, CancellationToken cancellationToken);
}

public interface IMicrosoftIdentityClientFactory
{
    IMicrosoftIdentityClient Create(
        MicrosoftIdentityConfiguration configuration,
        X509Certificate2 certificate);
}

public sealed class MsalMicrosoftIdentityClientFactory : IMicrosoftIdentityClientFactory
{
    private readonly MicrosoftIdentityOptions _options;
    private readonly TimeProvider _timeProvider;

    public MsalMicrosoftIdentityClientFactory(MicrosoftIdentityOptions options, TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
        _options.Validate();
    }

    public IMicrosoftIdentityClient Create(
        MicrosoftIdentityConfiguration configuration,
        X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(certificate);
        var application = ConfidentialClientApplicationBuilder
            .Create(configuration.ClientId.ToString("D"))
            .WithAuthority(configuration.Authority)
            .WithCertificate(certificate)
            .Build();
        return new MsalMicrosoftIdentityClient(
            application,
            configuration.TenantId,
            _options.AuthenticationTimeout,
            _timeProvider);
    }

    private sealed class MsalMicrosoftIdentityClient : IMicrosoftIdentityClient
    {
        private readonly IConfidentialClientApplication _application;
        private readonly Guid _configuredTenantId;
        private readonly TimeSpan _timeout;
        private readonly TimeProvider _timeProvider;

        public MsalMicrosoftIdentityClient(
            IConfidentialClientApplication application,
            Guid configuredTenantId,
            TimeSpan timeout,
            TimeProvider timeProvider)
        {
            _application = application;
            _configuredTenantId = configuredTenantId;
            _timeout = timeout;
            _timeProvider = timeProvider;
        }

        public async Task<MicrosoftAccessToken> AcquireTokenAsync(
            string scope,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(scope, MicrosoftIdentityConfiguration.ExchangeOnlineScope, StringComparison.Ordinal))
            {
                throw new ArgumentException("Only the Exchange Online application scope is supported.", nameof(scope));
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            try
            {
                var result = await _application
                    .AcquireTokenForClient([scope])
                    .ExecuteAsync(timeout.Token)
                    .ConfigureAwait(false);
                var tenantId = Guid.TryParse(result.TenantId, out var parsedTenantId) && parsedTenantId != Guid.Empty
                    ? parsedTenantId
                    : _configuredTenantId;
                return new MicrosoftAccessToken(result.AccessToken, result.ExpiresOn, tenantId);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new MicrosoftIdentityException(
                    MicrosoftIdentityErrorCategory.NetworkFailure,
                    "Microsoft authentication timed out before a response was received.",
                    technicalCode: "AuthenticationTimeout");
            }
            catch (MsalServiceException exception)
            {
                throw MapServiceException(exception, _timeProvider.GetUtcNow());
            }
            catch (MsalClientException exception)
            {
                throw MapClientException(exception, _timeProvider.GetUtcNow());
            }
        }

        private static MicrosoftIdentityException MapServiceException(
            MsalServiceException exception,
            DateTimeOffset timestamp)
        {
            var aadCode = ExtractAadCode(exception.Message);
            var category = aadCode switch
            {
                "AADSTS90002" => MicrosoftIdentityErrorCategory.TenantNotFound,
                "AADSTS700016" => MicrosoftIdentityErrorCategory.ApplicationNotFound,
                "AADSTS7000215" or "AADSTS700027" => MicrosoftIdentityErrorCategory.CredentialRejected,
                _ when string.Equals(exception.ErrorCode, "invalid_client", StringComparison.OrdinalIgnoreCase) =>
                    MicrosoftIdentityErrorCategory.CredentialRejected,
                _ when exception.StatusCode >= 500 => MicrosoftIdentityErrorCategory.MicrosoftServiceFailure,
                _ when exception.InnerException is HttpRequestException ||
                    string.Equals(exception.ErrorCode, "request_failed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(exception.ErrorCode, "temporarily_unavailable", StringComparison.OrdinalIgnoreCase) =>
                    MicrosoftIdentityErrorCategory.NetworkFailure,
                _ => MicrosoftIdentityErrorCategory.Unknown,
            };
            var message = category switch
            {
                MicrosoftIdentityErrorCategory.TenantNotFound =>
                    "Microsoft could not find the configured tenant.",
                MicrosoftIdentityErrorCategory.ApplicationNotFound =>
                    "Microsoft could not find the configured application in this tenant.",
                MicrosoftIdentityErrorCategory.CredentialRejected =>
                    "Microsoft rejected the application certificate. The registered public certificate may not match this server.",
                MicrosoftIdentityErrorCategory.MicrosoftServiceFailure =>
                    "Microsoft identity service is temporarily unavailable.",
                MicrosoftIdentityErrorCategory.NetworkFailure =>
                    "RelayBridge could not reach Microsoft identity services.",
                _ => "Microsoft rejected the authentication request.",
            };
            return new MicrosoftIdentityException(
                category,
                message,
                aadCode ?? exception.ErrorCode,
                exception.CorrelationId,
                timestamp);
        }

        private static MicrosoftIdentityException MapClientException(
            MsalClientException exception,
            DateTimeOffset timestamp)
        {
            var networkFailure = exception.InnerException is HttpRequestException ||
                string.Equals(exception.ErrorCode, "request_failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(exception.ErrorCode, "temporarily_unavailable", StringComparison.OrdinalIgnoreCase);
            return new MicrosoftIdentityException(
                networkFailure
                    ? MicrosoftIdentityErrorCategory.NetworkFailure
                    : MicrosoftIdentityErrorCategory.Unknown,
                networkFailure
                    ? "RelayBridge could not reach Microsoft identity services."
                    : "RelayBridge could not complete Microsoft authentication.",
                exception.ErrorCode,
                exception.CorrelationId,
                timestamp);
        }

        private static string? ExtractAadCode(string message)
        {
            var start = message.IndexOf("AADSTS", StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            var end = start + 6;
            while (end < message.Length && char.IsAsciiDigit(message[end]))
            {
                end++;
            }

            return end == start + 6 ? null : message[start..end];
        }
    }
}

public sealed class MicrosoftTokenProvider : IMicrosoftTokenProvider, IDisposable
{
    private readonly object _clientLock = new();
    private readonly RelayDatabase _database;
    private readonly MicrosoftCertificateService _certificates;
    private readonly IMicrosoftIdentityClientFactory _clientFactory;
    private CachedClient? _cachedClient;
    private bool _disposed;

    public MicrosoftTokenProvider(
        RelayDatabase database,
        MicrosoftCertificateService certificates,
        IMicrosoftIdentityClientFactory clientFactory)
    {
        _database = database;
        _certificates = certificates;
        _clientFactory = clientFactory;
    }

    public async Task<MicrosoftAccessToken> GetExchangeTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = _database.GetMicrosoftIdentityConfiguration(cancellationToken)
            ?? throw new MicrosoftIdentityException(
                MicrosoftIdentityErrorCategory.InvalidConfiguration,
                "Microsoft identity has not been configured.");
        return await GetExchangeTokenAsync(configuration, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MicrosoftAccessToken> GetExchangeTokenAsync(
        MicrosoftIdentityConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();
        var client = AcquireClient(configuration, cancellationToken);
        try
        {
            return await client.Client
                .AcquireTokenAsync(MicrosoftIdentityConfiguration.ExchangeOnlineScope, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ReleaseClient(client);
        }
    }

    public void Dispose()
    {
        lock (_clientLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_cachedClient is not null)
            {
                RetireClient(_cachedClient);
            }

            _cachedClient = null;
        }
    }

    private CachedClient AcquireClient(
        MicrosoftIdentityConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var key = new ConfigurationKey(
            configuration.TenantId,
            configuration.ClientId,
            configuration.Certificate.Thumbprint,
            configuration.Certificate.StoreLocation);
        lock (_clientLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cachedClient?.Key == key)
            {
                _cachedClient.ActiveCalls++;
                return _cachedClient;
            }

            var certificate = _certificates.LoadForAuthentication(
                configuration.Certificate,
                cancellationToken);
            try
            {
                var client = _clientFactory.Create(configuration, certificate);
                var replacement = new CachedClient(key, client, certificate) { ActiveCalls = 1 };
                if (_cachedClient is not null)
                {
                    RetireClient(_cachedClient);
                }

                _cachedClient = replacement;
                return replacement;
            }
            catch
            {
                certificate.Dispose();
                throw;
            }
        }
    }

    private void ReleaseClient(CachedClient client)
    {
        lock (_clientLock)
        {
            client.ActiveCalls--;
            if (client.Retired && client.ActiveCalls == 0)
            {
                client.Certificate.Dispose();
            }
        }
    }

    private static void RetireClient(CachedClient client)
    {
        client.Retired = true;
        if (client.ActiveCalls == 0)
        {
            client.Certificate.Dispose();
        }
    }

    private sealed class CachedClient
    {
        public CachedClient(
            ConfigurationKey key,
            IMicrosoftIdentityClient client,
            X509Certificate2 certificate)
        {
            Key = key;
            Client = client;
            Certificate = certificate;
        }

        public ConfigurationKey Key { get; }

        public IMicrosoftIdentityClient Client { get; }

        public X509Certificate2 Certificate { get; }

        public int ActiveCalls { get; set; }

        public bool Retired { get; set; }
    }

    private sealed record ConfigurationKey(
        Guid TenantId,
        Guid ClientId,
        string Thumbprint,
        CertificateStoreTarget StoreLocation);
}
