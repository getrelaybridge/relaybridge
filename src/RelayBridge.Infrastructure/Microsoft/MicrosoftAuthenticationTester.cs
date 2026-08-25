// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;
using RelayBridge.Core.Microsoft;
using RelayBridge.Infrastructure.Storage;

namespace RelayBridge.Infrastructure.Microsoft;

public sealed class MicrosoftIdentityRuntimeState
{
    private readonly object _lock = new();
    private readonly MicrosoftRuntimeEvidenceSequence _sequence;
    private readonly Dictionary<Guid, MicrosoftIdentityHealthSnapshot> _attempts = [];
    private readonly Dictionary<string, MicrosoftIdentityHealthSnapshot> _completedByFingerprint =
        new(StringComparer.Ordinal);
    private MicrosoftIdentityHealthSnapshot _snapshot;

    public MicrosoftIdentityRuntimeState(
        RelayDatabase database,
        MicrosoftRuntimeEvidenceSequence sequence)
    {
        _sequence = sequence;
        var activeConfiguration = database.GetActiveMicrosoftConfiguration();
        _snapshot = new MicrosoftIdentityHealthSnapshot(
            activeConfiguration?.AuthorizedSender is null
                ? MicrosoftIdentityHealthStatus.NotConfigured
                : MicrosoftIdentityHealthStatus.Attention,
            null,
            null,
            null,
            null);
    }

    internal MicrosoftIdentityRuntimeState(RelayDatabase database)
        : this(database, new MicrosoftRuntimeEvidenceSequence())
    {
    }

    public MicrosoftIdentityHealthSnapshot Snapshot
    {
        get
        {
            lock (_lock)
            {
                return _snapshot;
            }
        }
    }

    public MicrosoftIdentityHealthSnapshot GetCompletedSnapshot(string? configurationFingerprint)
    {
        lock (_lock)
        {
            return configurationFingerprint is not null &&
                _completedByFingerprint.TryGetValue(configurationFingerprint, out var snapshot)
                    ? snapshot
                    : new MicrosoftIdentityHealthSnapshot(
                        MicrosoftIdentityHealthStatus.Attention,
                        null,
                        null,
                        null,
                        null);
        }
    }

    internal MicrosoftAttemptContext Begin(
        DateTimeOffset attemptedAt,
        DateTimeOffset? certificateExpiresOn,
        MicrosoftIdentityConfiguration capturedConfiguration,
        string configurationFingerprint)
    {
        var attempt = MicrosoftAttemptContext.Create(
            _sequence,
            attemptedAt,
            capturedConfiguration,
            configurationFingerprint);
        lock (_lock)
        {
            var snapshot = new MicrosoftIdentityHealthSnapshot(
                MicrosoftIdentityHealthStatus.Checking,
                attemptedAt,
                null,
                certificateExpiresOn,
                null)
            {
                AttemptId = attempt.AttemptId,
                ConfigurationFingerprint = configurationFingerprint,
            };
            _attempts.Add(attempt.AttemptId, snapshot);
            _snapshot = snapshot;
        }

        return attempt;
    }

    internal void MarkNotConfigured()
    {
        lock (_lock)
        {
            _snapshot = new MicrosoftIdentityHealthSnapshot(
                MicrosoftIdentityHealthStatus.NotConfigured,
                _snapshot.LastAttemptedAt,
                _snapshot.LastSuccessfulAt,
                null,
                null);
        }
    }

    internal void Succeed(
        MicrosoftAttemptContext attempt,
        DateTimeOffset completedAt,
        DateTimeOffset? certificateExpiresOn,
        bool certificateExpiringSoon)
    {
        lock (_lock)
        {
            var snapshot = new MicrosoftIdentityHealthSnapshot(
                certificateExpiringSoon
                    ? MicrosoftIdentityHealthStatus.Attention
                    : MicrosoftIdentityHealthStatus.Healthy,
                attempt.StartedAt,
                completedAt,
                certificateExpiresOn,
                null)
            {
                AttemptId = attempt.AttemptId,
                ConfigurationFingerprint = attempt.ConfigurationFingerprint,
                LastCompletedAt = completedAt,
                CompletionSequence = _sequence.Next(),
            };
            Complete(attempt, snapshot);
        }
    }

    internal void Fail(
        MicrosoftAttemptContext attempt,
        DateTimeOffset completedAt,
        DateTimeOffset? certificateExpiresOn,
        MicrosoftIdentityErrorCategory category)
    {
        lock (_lock)
        {
            var snapshot = new MicrosoftIdentityHealthSnapshot(
                MicrosoftIdentityHealthStatus.Failed,
                attempt.StartedAt,
                null,
                certificateExpiresOn,
                category)
            {
                AttemptId = attempt.AttemptId,
                ConfigurationFingerprint = attempt.ConfigurationFingerprint,
                LastCompletedAt = completedAt,
                CompletionSequence = _sequence.Next(),
            };
            Complete(attempt, snapshot);
        }
    }

    internal void Abandon(MicrosoftAttemptContext attempt)
    {
        lock (_lock)
        {
            _attempts.Remove(attempt.AttemptId);
            if (_snapshot.AttemptId == attempt.AttemptId)
            {
                _snapshot = attempt.ConfigurationFingerprint is not null &&
                    _completedByFingerprint.TryGetValue(attempt.ConfigurationFingerprint, out var completed)
                        ? completed
                        : new MicrosoftIdentityHealthSnapshot(
                            MicrosoftIdentityHealthStatus.Attention,
                            null,
                            null,
                            null,
                            null);
            }
        }
    }

    private void Complete(
        MicrosoftAttemptContext attempt,
        MicrosoftIdentityHealthSnapshot snapshot)
    {
        if (!_attempts.Remove(attempt.AttemptId))
        {
            return;
        }

        if (attempt.ConfigurationFingerprint is not null)
        {
            _completedByFingerprint[attempt.ConfigurationFingerprint] = snapshot;
        }

        _snapshot = snapshot;
    }
}

public sealed class MicrosoftAuthenticationTester
{
    private readonly RelayDatabase _database;
    private readonly MicrosoftCertificateService _certificates;
    private readonly IMicrosoftTokenProvider _tokenProvider;
    private readonly MicrosoftIdentityRuntimeState _runtimeState;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MicrosoftAuthenticationTester> _logger;

    public MicrosoftAuthenticationTester(
        RelayDatabase database,
        MicrosoftCertificateService certificates,
        IMicrosoftTokenProvider tokenProvider,
        MicrosoftIdentityRuntimeState runtimeState,
        TimeProvider timeProvider,
        ILogger<MicrosoftAuthenticationTester> logger)
    {
        _database = database;
        _certificates = certificates;
        _tokenProvider = tokenProvider;
        _runtimeState = runtimeState;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<MicrosoftAuthenticationTestResult> TestAsync(
        CancellationToken cancellationToken = default)
    {
        var attemptedAt = _timeProvider.GetUtcNow();
        var activeConfiguration = _database.GetActiveMicrosoftConfiguration(cancellationToken);
        if (activeConfiguration is null)
        {
            _runtimeState.MarkNotConfigured();
            var notConfigured = _certificates.Validate(null, cancellationToken);
            return new MicrosoftAuthenticationTestResult(
                false,
                "Microsoft identity has not been configured.",
                MicrosoftIdentityErrorCategory.InvalidConfiguration,
                notConfigured,
                attemptedAt,
                null,
                null,
                null);
        }

        var configuration = activeConfiguration.Identity;

        var certificate = _certificates.Validate(configuration.Certificate, cancellationToken);
        var attempt = _runtimeState.Begin(
            attemptedAt,
            certificate.Certificate?.NotAfter,
            configuration,
            activeConfiguration.Fingerprint);
        if (!certificate.IsUsable)
        {
            var category = MapCertificateCategory(certificate.Status);
            _runtimeState.Fail(
                attempt,
                _timeProvider.GetUtcNow(),
                certificate.Certificate?.NotAfter,
                category);
            _logger.LogWarning(
                "Microsoft authentication test stopped during certificate validation. Category: {Category}",
                category);
            return Failure(attemptedAt, certificate, category, certificate.Message, null, null);
        }

        try
        {
            var token = await _tokenProvider.GetExchangeTokenAsync(configuration, cancellationToken)
                .ConfigureAwait(false);
            _runtimeState.Succeed(
                attempt,
                _timeProvider.GetUtcNow(),
                certificate.Certificate?.NotAfter,
                certificate.Status == CertificateValidationStatus.ExpiringSoon);
            _logger.LogInformation(
                "Microsoft application authentication succeeded. Token expires at {ExpiresOn}.",
                token.ExpiresOn);
            return new MicrosoftAuthenticationTestResult(
                true,
                "RelayBridge authenticated as the configured Microsoft application and acquired an Exchange Online resource token. SMTP authorization and mail delivery were not tested.",
                null,
                certificate,
                attemptedAt,
                token.ExpiresOn,
                null,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _runtimeState.Abandon(attempt);
            return Failure(
                attemptedAt,
                certificate,
                MicrosoftIdentityErrorCategory.Cancelled,
                "Microsoft authentication was cancelled.",
                null,
                null);
        }
        catch (MicrosoftIdentityException exception)
        {
            _runtimeState.Fail(
                attempt,
                _timeProvider.GetUtcNow(),
                certificate.Certificate?.NotAfter,
                exception.Category);
            _logger.LogWarning(
                "Microsoft authentication test failed. Category: {Category}; technical code: {TechnicalCode}; correlation ID: {CorrelationId}",
                exception.Category,
                exception.TechnicalCode,
                exception.CorrelationId);
            return Failure(
                attemptedAt,
                certificate,
                exception.Category,
                exception.Message,
                exception.TechnicalCode,
                exception.CorrelationId);
        }
        finally
        {
            _runtimeState.Abandon(attempt);
        }
    }

    private static MicrosoftAuthenticationTestResult Failure(
        DateTimeOffset attemptedAt,
        CertificateValidationResult certificate,
        MicrosoftIdentityErrorCategory category,
        string message,
        string? technicalCode,
        string? correlationId)
    {
        return new MicrosoftAuthenticationTestResult(
            false,
            message,
            category,
            certificate,
            attemptedAt,
            null,
            technicalCode,
            correlationId);
    }

    private static MicrosoftIdentityErrorCategory MapCertificateCategory(CertificateValidationStatus status)
    {
        return status switch
        {
            CertificateValidationStatus.Missing => MicrosoftIdentityErrorCategory.CertificateMissing,
            CertificateValidationStatus.Expired => MicrosoftIdentityErrorCategory.CertificateExpired,
            CertificateValidationStatus.NoPrivateKey or CertificateValidationStatus.PrivateKeyInaccessible =>
                MicrosoftIdentityErrorCategory.PrivateKeyUnavailable,
            CertificateValidationStatus.NotConfigured => MicrosoftIdentityErrorCategory.InvalidConfiguration,
            _ => MicrosoftIdentityErrorCategory.CertificateInvalid,
        };
    }
}
