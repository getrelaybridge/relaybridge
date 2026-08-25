// SPDX-License-Identifier: MPL-2.0

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RelayBridge.Core.Microsoft;
using RelayBridge.Core.Queue;
using RelayBridge.Infrastructure.Microsoft;
using RelayBridge.Infrastructure.Storage;
using Xunit;

namespace RelayBridge.IntegrationTests;

[SupportedOSPlatform("windows")]
public sealed class MicrosoftIdentityIntegrationTests
{
    [Fact]
    public async Task Generated_certificate_is_non_exportable_usable_and_exports_public_only()
    {
        using var context = IdentityTestContext.Create();
        var metadata = context.Certificates.GenerateSelfSignedCertificate(CertificateStoreTarget.CurrentUser);
        var reference = MicrosoftCertificateReference.Create(
            metadata.Thumbprint,
            CertificateStoreTarget.CurrentUser);
        context.Track(reference);

        var validation = context.Certificates.Validate(reference);
        var export = await context.Certificates.ExportPublicCertificateAsync(reference);

        Assert.Equal(CertificateValidationStatus.Valid, validation.Status);
        Assert.True(validation.IsUsable);
        Assert.Equal(2048, validation.Certificate?.KeySizeBits);
        Assert.StartsWith("relaybridge-auth-", export.FileName, StringComparison.Ordinal);
        Assert.EndsWith(".cer", export.FileName, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(context.DataDirectory, "exports", export.FileName), export.FullPath);

        using var exported = X509CertificateLoader.LoadCertificateFromFile(export.FullPath);
        Assert.False(exported.HasPrivateKey);
        Assert.Equal(reference.Thumbprint, exported.Thumbprint);

        using var local = LoadCertificate(reference);
        Assert.True(local.HasPrivateKey);
        using var privateKey = local.GetRSAPrivateKey();
        Assert.NotNull(privateKey);
        Assert.ThrowsAny<CryptographicException>(() => privateKey.ExportPkcs8PrivateKey());
        Assert.True(context.Certificates.Validate(reference).IsUsable);
    }

    [Fact]
    public void Certificate_lookup_fails_closed_for_missing_no_key_expired_future_and_unsupported_certificates()
    {
        using var context = IdentityTestContext.Create();
        var missing = MicrosoftCertificateReference.Create(
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            CertificateStoreTarget.CurrentUser);
        var noKey = context.InstallRsaCertificate(
            context.Time.GetUtcNow().AddDays(-1),
            context.Time.GetUtcNow().AddDays(100),
            includePrivateKey: false);
        var expired = context.InstallRsaCertificate(
            context.Time.GetUtcNow().AddDays(-10),
            context.Time.GetUtcNow().AddDays(-1),
            includePrivateKey: true);
        var future = context.InstallRsaCertificate(
            context.Time.GetUtcNow().AddDays(1),
            context.Time.GetUtcNow().AddDays(100),
            includePrivateKey: true);
        var unsupported = context.InstallEcdsaCertificate(
            context.Time.GetUtcNow().AddDays(-1),
            context.Time.GetUtcNow().AddDays(100));

        Assert.Equal(CertificateValidationStatus.Missing, context.Certificates.Validate(missing).Status);
        Assert.Equal(CertificateValidationStatus.NoPrivateKey, context.Certificates.Validate(noKey).Status);
        Assert.Equal(CertificateValidationStatus.Expired, context.Certificates.Validate(expired).Status);
        Assert.Equal(CertificateValidationStatus.Invalid, context.Certificates.Validate(future).Status);
        Assert.Equal(CertificateValidationStatus.Unsupported, context.Certificates.Validate(unsupported).Status);
    }

    [Fact]
    public void Certificate_expiry_warning_uses_configured_threshold()
    {
        using var context = IdentityTestContext.Create(options => options.CertificateExpiryWarningDays = 30);
        var attention = context.InstallRsaCertificate(
            context.Time.GetUtcNow().AddDays(-1),
            context.Time.GetUtcNow().AddDays(20),
            includePrivateKey: true);
        var healthy = context.InstallRsaCertificate(
            context.Time.GetUtcNow().AddDays(-1),
            context.Time.GetUtcNow().AddDays(31),
            includePrivateKey: true);

        Assert.Equal(CertificateValidationStatus.ExpiringSoon, context.Certificates.Validate(attention).Status);
        Assert.Equal(CertificateValidationStatus.Valid, context.Certificates.Validate(healthy).Status);
    }

    [Fact]
    public void Identity_configuration_persists_only_non_secret_normalized_metadata()
    {
        using var context = IdentityTestContext.Create();
        var configuration = CreateConfiguration(
            MicrosoftCertificateReference.Create(
                "0123456789abcdef0123456789abcdef01234567",
                CertificateStoreTarget.LocalMachine));

        context.Database.SaveMicrosoftIdentityConfiguration(configuration);

        var restarted = new RelayDatabase(
            new RelayStorageOptions { DataDirectory = context.DataDirectory },
            AppContext.BaseDirectory);
        var loaded = restarted.GetMicrosoftIdentityConfiguration();
        Assert.NotNull(loaded);
        Assert.Equal(configuration.TenantId, loaded.TenantId);
        Assert.Equal(configuration.ClientId, loaded.ClientId);
        Assert.Equal(configuration.Certificate.Thumbprint, loaded.Certificate.Thumbprint);
        Assert.Equal(CertificateStoreTarget.LocalMachine, loaded.Certificate.StoreLocation);

        using var connection = restarted.OpenConnectionForDiagnostics();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(MicrosoftIdentityConfiguration);";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        Assert.Equal(
            ["Id", "TenantId", "ClientId", "CertificateThumbprint", "CertificateStoreName", "CertificateStoreLocation", "AuthorizedSender", "ActivationId"],
            columns);
        Assert.DoesNotContain(columns, column => column.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, column => column.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, column => column.Contains("Private", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, column => column.Contains("Pfx", StringComparison.OrdinalIgnoreCase));

        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        Assert.Equal(9L, (long)versionCommand.ExecuteScalar()!);
    }

    [Fact]
    public void Milestone_two_schema_adds_identity_configuration_without_touching_queue_data()
    {
        using var context = IdentityTestContext.Create();
        using (var connection = context.Database.OpenConnectionForDiagnostics())
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                DROP TABLE MicrosoftIdentityConfiguration;
                PRAGMA user_version = 2;
                """;
            command.ExecuteNonQuery();
        }

        var upgraded = new RelayDatabase(
            new RelayStorageOptions { DataDirectory = context.DataDirectory },
            AppContext.BaseDirectory);
        upgraded.Initialize();

        using var verification = upgraded.OpenConnectionForDiagnostics();
        using var tableCommand = verification.CreateCommand();
        tableCommand.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'MicrosoftIdentityConfiguration';";
        Assert.Equal(1L, (long)tableCommand.ExecuteScalar()!);
        using var versionCommand = verification.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        Assert.Equal(9L, (long)versionCommand.ExecuteScalar()!);
        Assert.Empty(upgraded.GetQueuedMessages());
    }

    [Fact]
    public async Task Token_provider_reuses_one_client_requests_Exchange_scope_and_rebuilds_after_configuration_change()
    {
        using var context = IdentityTestContext.Create();
        var reference = context.InstallRsaCertificate(
            context.Time.GetUtcNow().AddDays(-1),
            context.Time.GetUtcNow().AddDays(100),
            includePrivateKey: true);
        var firstConfiguration = CreateConfiguration(reference);
        context.Database.SaveMicrosoftIdentityConfiguration(firstConfiguration);
        var factory = new FakeMicrosoftIdentityClientFactory(context.Time);
        using var provider = new MicrosoftTokenProvider(context.Database, context.Certificates, factory);

        var tokens = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => provider.GetExchangeTokenAsync(CancellationToken.None)));

        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(16, factory.Scopes.Count);
        Assert.All(factory.Scopes, scope => Assert.Equal(MicrosoftIdentityConfiguration.ExchangeOnlineScope, scope));
        Assert.All(factory.Configurations, configuration =>
        {
            Assert.Equal(firstConfiguration.Authority, configuration.Authority);
            Assert.Equal(firstConfiguration.ClientId, configuration.ClientId);
        });
        Assert.All(factory.CertificateThumbprints, thumbprint => Assert.Equal(reference.Thumbprint, thumbprint));
        Assert.All(tokens, token => Assert.Equal(firstConfiguration.TenantId, token.TenantId));

        var changed = MicrosoftIdentityConfiguration.Create(
            firstConfiguration.TenantId,
            Guid.NewGuid(),
            reference);
        context.Database.SaveMicrosoftIdentityConfiguration(changed);
        await provider.GetExchangeTokenAsync(CancellationToken.None);

        Assert.Equal(2, factory.CreateCount);
        Assert.Equal(changed.ClientId, factory.Configurations.Last().ClientId);
    }

    [Fact]
    public async Task Token_provider_propagates_cancellation_without_rebuilding_or_deadlocking()
    {
        using var context = IdentityTestContext.Create();
        var reference = context.InstallRsaCertificate(
            context.Time.GetUtcNow().AddDays(-1),
            context.Time.GetUtcNow().AddDays(100),
            includePrivateKey: true);
        context.Database.SaveMicrosoftIdentityConfiguration(CreateConfiguration(reference));
        var factory = new FakeMicrosoftIdentityClientFactory(context.Time)
        {
            Handler = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            },
        };
        using var provider = new MicrosoftTokenProvider(context.Database, context.Certificates, factory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetExchangeTokenAsync(cancellation.Token));

        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task Authentication_test_returns_safe_state_and_never_logs_or_serializes_token()
    {
        const string accessToken = "test-token-value-that-must-not-leak";
        using var context = IdentityTestContext.Create();
        var reference = context.InstallRsaCertificate(
            context.Time.GetUtcNow().AddDays(-1),
            context.Time.GetUtcNow().AddDays(100),
            includePrivateKey: true);
        context.Database.SaveMicrosoftIdentityConfiguration(CreateConfiguration(reference));
        var factory = new FakeMicrosoftIdentityClientFactory(context.Time)
        {
            Handler = (_, _) => Task.FromResult(new MicrosoftAccessToken(
                accessToken,
                context.Time.GetUtcNow().AddHours(1),
                context.Database.GetMicrosoftIdentityConfiguration()!.TenantId)),
        };
        using var provider = new MicrosoftTokenProvider(context.Database, context.Certificates, factory);
        var state = new MicrosoftIdentityRuntimeState(context.Database);
        var logger = new CapturingLogger<MicrosoftAuthenticationTester>();
        var tester = new MicrosoftAuthenticationTester(
            context.Database,
            context.Certificates,
            provider,
            state,
            context.Time,
            logger);

        var result = await tester.TestAsync();
        var serialized = JsonSerializer.Serialize(result);

        Assert.True(result.Succeeded);
        Assert.False(result.SmtpAuthorizationTested);
        Assert.False(result.MailDeliveryTested);
        Assert.Equal(MicrosoftIdentityHealthStatus.Healthy, state.Snapshot.Status);
        Assert.DoesNotContain(accessToken, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(accessToken, string.Join(Environment.NewLine, logger.Messages), StringComparison.Ordinal);
        Assert.Contains("SMTP authorization and mail delivery were not tested", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authentication_failure_preserves_safe_correlation_metadata_and_queued_mail()
    {
        await using var queue = QueueTestContext.Create();
        var queued = await queue.EnqueueAsync();
        using var identity = IdentityTestContext.Create(dataDirectory: queue.DataDirectory);
        var reference = identity.InstallRsaCertificate(
            identity.Time.GetUtcNow().AddDays(-1),
            identity.Time.GetUtcNow().AddDays(100),
            includePrivateKey: true);
        identity.Database.SaveMicrosoftIdentityConfiguration(CreateConfiguration(reference));
        var factory = new FakeMicrosoftIdentityClientFactory(identity.Time)
        {
            Handler = (_, _) => throw new MicrosoftIdentityException(
                MicrosoftIdentityErrorCategory.CredentialRejected,
                "Microsoft rejected the application certificate.",
                "AADSTS700027",
                "safe-correlation-id",
                identity.Time.GetUtcNow()),
        };
        using var provider = new MicrosoftTokenProvider(identity.Database, identity.Certificates, factory);
        var state = new MicrosoftIdentityRuntimeState(identity.Database);
        var logger = new CapturingLogger<MicrosoftAuthenticationTester>();
        var tester = new MicrosoftAuthenticationTester(
            identity.Database,
            identity.Certificates,
            provider,
            state,
            identity.Time,
            logger);

        var result = await tester.TestAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(MicrosoftIdentityErrorCategory.CredentialRejected, result.ErrorCategory);
        Assert.Equal("AADSTS700027", result.TechnicalCode);
        Assert.Equal("safe-correlation-id", result.CorrelationId);
        Assert.Equal(MicrosoftIdentityHealthStatus.Failed, state.Snapshot.Status);
        var unchanged = Assert.Single(queue.Database.GetQueuedMessages());
        Assert.Equal(queued.Id, unchanged.Id);
        Assert.Equal(QueueState.Queued, unchanged.State);
    }

    [Theory]
    [InlineData(MicrosoftIdentityErrorCategory.NetworkFailure, "NetworkUnavailable")]
    [InlineData(MicrosoftIdentityErrorCategory.MicrosoftServiceFailure, "ServiceUnavailable")]
    public async Task Authentication_test_preserves_safe_network_and_service_failure_classification(
        MicrosoftIdentityErrorCategory category,
        string technicalCode)
    {
        using var context = IdentityTestContext.Create();
        var reference = context.InstallRsaCertificate(
            context.Time.GetUtcNow().AddDays(-1),
            context.Time.GetUtcNow().AddDays(100),
            includePrivateKey: true);
        context.Database.SaveMicrosoftIdentityConfiguration(CreateConfiguration(reference));
        var factory = new FakeMicrosoftIdentityClientFactory(context.Time)
        {
            Handler = (_, _) => throw new MicrosoftIdentityException(
                category,
                "Microsoft identity is temporarily unavailable.",
                technicalCode,
                "safe-correlation-id",
                context.Time.GetUtcNow()),
        };
        using var provider = new MicrosoftTokenProvider(context.Database, context.Certificates, factory);
        var tester = new MicrosoftAuthenticationTester(
            context.Database,
            context.Certificates,
            provider,
            new MicrosoftIdentityRuntimeState(context.Database),
            context.Time,
            new CapturingLogger<MicrosoftAuthenticationTester>());

        var result = await tester.TestAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(category, result.ErrorCategory);
        Assert.Equal(technicalCode, result.TechnicalCode);
        Assert.Equal("safe-correlation-id", result.CorrelationId);
    }

    [Fact]
    public async Task Cancelled_identity_verification_is_abandoned_without_authoritative_failure_evidence()
    {
        using var context = IdentityTestContext.Create();
        var reference = context.InstallRsaCertificate(
            context.Time.GetUtcNow().AddDays(-1),
            context.Time.GetUtcNow().AddDays(100),
            includePrivateKey: true);
        context.Database.SaveMicrosoftIdentityConfiguration(CreateConfiguration(reference));
        var active = context.Database.GetActiveMicrosoftConfiguration()!;
        var factory = new FakeMicrosoftIdentityClientFactory(context.Time)
        {
            Handler = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable");
            },
        };
        using var provider = new MicrosoftTokenProvider(context.Database, context.Certificates, factory);
        var sequence = new MicrosoftRuntimeEvidenceSequence();
        var state = new MicrosoftIdentityRuntimeState(context.Database, sequence);
        var exchange = new ExchangeDeliveryRuntimeState(sequence);
        var tester = new MicrosoftAuthenticationTester(
            context.Database,
            context.Certificates,
            provider,
            state,
            context.Time,
            new CapturingLogger<MicrosoftAuthenticationTester>());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var result = await tester.TestAsync(cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(MicrosoftIdentityErrorCategory.Cancelled, result.ErrorCategory);
        Assert.Null(state.GetCompletedSnapshot(active.Fingerprint).CompletionSequence);
        Assert.Equal(MicrosoftIdentityHealthStatus.Attention, state.Snapshot.Status);
        Assert.Equal(
            MicrosoftRuntimeReadiness.VerificationRequired,
            MicrosoftRuntimeReadinessPolicy.Evaluate(true, active.Fingerprint, state, exchange));
    }

    [Fact]
    public async Task Missing_certificate_stops_before_network_and_configuration_can_be_removed_without_deleting_mail()
    {
        await using var queue = QueueTestContext.Create();
        var queued = await queue.EnqueueAsync();
        using var identity = IdentityTestContext.Create(dataDirectory: queue.DataDirectory);
        var missing = MicrosoftCertificateReference.Create(
            "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
            CertificateStoreTarget.CurrentUser);
        identity.Database.SaveMicrosoftIdentityConfiguration(CreateConfiguration(missing));
        var factory = new FakeMicrosoftIdentityClientFactory(identity.Time);
        using var provider = new MicrosoftTokenProvider(identity.Database, identity.Certificates, factory);
        var state = new MicrosoftIdentityRuntimeState(identity.Database);
        var tester = new MicrosoftAuthenticationTester(
            identity.Database,
            identity.Certificates,
            provider,
            state,
            identity.Time,
            new CapturingLogger<MicrosoftAuthenticationTester>());

        var result = await tester.TestAsync();
        identity.Database.ClearMicrosoftIdentityConfiguration();
        var removedResult = await tester.TestAsync();

        Assert.Equal(MicrosoftIdentityErrorCategory.CertificateMissing, result.ErrorCategory);
        Assert.Equal(0, factory.CreateCount);
        Assert.Null(identity.Database.GetMicrosoftIdentityConfiguration());
        Assert.Equal(MicrosoftIdentityErrorCategory.InvalidConfiguration, removedResult.ErrorCategory);
        Assert.Equal(MicrosoftIdentityHealthStatus.NotConfigured, state.Snapshot.Status);
        var unchanged = Assert.Single(queue.Database.GetQueuedMessages());
        Assert.Equal(queued.Id, unchanged.Id);
        Assert.Equal(QueueState.Queued, unchanged.State);
    }

    [Fact]
    public async Task Identity_verification_uses_captured_configuration_after_active_configuration_changes()
    {
        using var context = IdentityTestContext.Create();
        var reference = context.InstallRsaCertificate(
            context.Time.GetUtcNow().AddDays(-1),
            context.Time.GetUtcNow().AddDays(100),
            includePrivateKey: true);
        var configurationA = CreateConfiguration(reference);
        var configurationB = CreateConfiguration(reference);
        context.Database.SaveMicrosoftIdentityConfiguration(configurationA);
        var activeA = context.Database.GetActiveMicrosoftConfiguration()!;
        var sequence = new MicrosoftRuntimeEvidenceSequence();
        var identityState = new MicrosoftIdentityRuntimeState(context.Database, sequence);
        var exchangeState = new ExchangeDeliveryRuntimeState(sequence);
        var tokens = new CapturingConfigurationTokenProvider();
        var tester = new MicrosoftAuthenticationTester(
            context.Database,
            context.Certificates,
            tokens,
            identityState,
            context.Time,
            new CapturingLogger<MicrosoftAuthenticationTester>());

        var verificationA = tester.TestAsync();
        await tokens.FirstCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        context.Database.SaveMicrosoftIdentityConfiguration(configurationB);
        var activeB = context.Database.GetActiveMicrosoftConfiguration()!;
        tokens.ReleaseFirstCall.SetResult();

        Assert.True((await verificationA).Succeeded);
        Assert.Equal(configurationA.ClientId, Assert.Single(tokens.Configurations).ClientId);
        Assert.Equal(
            MicrosoftRuntimeReadiness.VerificationRequired,
            MicrosoftRuntimeReadinessPolicy.Evaluate(true, activeB.Fingerprint, identityState, exchangeState));
        Assert.Equal(MicrosoftIdentityHealthStatus.Healthy, identityState.GetCompletedSnapshot(activeA.Fingerprint).Status);

        Assert.True((await tester.TestAsync()).Succeeded);
        Assert.Equal(
            MicrosoftRuntimeReadiness.Ready,
            MicrosoftRuntimeReadinessPolicy.Evaluate(true, activeB.Fingerprint, identityState, exchangeState));
        Assert.Equal(configurationB.ClientId, tokens.Configurations.Last().ClientId);
    }

    [Fact]
    public async Task Overlapping_identity_checks_use_completion_order_not_start_order()
    {
        using var context = IdentityTestContext.Create();
        var reference = context.InstallRsaCertificate(
            context.Time.GetUtcNow().AddDays(-1),
            context.Time.GetUtcNow().AddDays(100),
            includePrivateKey: true);
        var configuration = CreateConfiguration(reference);
        context.Database.SaveMicrosoftIdentityConfiguration(configuration);
        var active = context.Database.GetActiveMicrosoftConfiguration()!;
        var sequence = new MicrosoftRuntimeEvidenceSequence();
        var identityState = new MicrosoftIdentityRuntimeState(context.Database, sequence);
        var exchangeState = new ExchangeDeliveryRuntimeState(sequence);
        var tokens = new OrderedIdentityTokenProvider();
        var tester = new MicrosoftAuthenticationTester(
            context.Database,
            context.Certificates,
            tokens,
            identityState,
            context.Time,
            new CapturingLogger<MicrosoftAuthenticationTester>());

        var first = tester.TestAsync();
        await tokens.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = tester.TestAsync();
        await tokens.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        tokens.ReleaseSecond.SetResult();
        Assert.False((await second).Succeeded);
        tokens.ReleaseFirst.SetResult();
        Assert.True((await first).Succeeded);

        Assert.Equal(
            MicrosoftRuntimeReadiness.Ready,
            MicrosoftRuntimeReadinessPolicy.Evaluate(true, active.Fingerprint, identityState, exchangeState));
        Assert.Equal(MicrosoftIdentityHealthStatus.Healthy, identityState.GetCompletedSnapshot(active.Fingerprint).Status);
    }

    [Fact]
    public Task Identity_failure_then_exchange_success_uses_later_completion()
    {
        return AssertCrossSourceCompletionAsync(
            identitySucceeds: false,
            identityCompletesFirst: true,
            exchangeSucceeds: true,
            MicrosoftRuntimeReadiness.Ready);
    }

    [Fact]
    public Task Exchange_success_then_identity_failure_uses_later_completion()
    {
        return AssertCrossSourceCompletionAsync(
            identitySucceeds: false,
            identityCompletesFirst: false,
            exchangeSucceeds: true,
            MicrosoftRuntimeReadiness.NeedsAttention);
    }

    [Fact]
    public Task Identity_success_then_exchange_failure_uses_later_completion()
    {
        return AssertCrossSourceCompletionAsync(
            identitySucceeds: true,
            identityCompletesFirst: true,
            exchangeSucceeds: false,
            MicrosoftRuntimeReadiness.NeedsAttention);
    }

    [Fact]
    public Task Exchange_failure_then_identity_success_uses_later_completion()
    {
        return AssertCrossSourceCompletionAsync(
            identitySucceeds: true,
            identityCompletesFirst: false,
            exchangeSucceeds: false,
            MicrosoftRuntimeReadiness.Ready);
    }

    private static async Task AssertCrossSourceCompletionAsync(
        bool identitySucceeds,
        bool identityCompletesFirst,
        bool exchangeSucceeds,
        MicrosoftRuntimeReadiness expected)
    {
        await using var context = QueueTestContext.Create();
        var sequence = new MicrosoftRuntimeEvidenceSequence();
        var identityState = new MicrosoftIdentityRuntimeState(context.Database, sequence);
        var exchangeState = new ExchangeDeliveryRuntimeState(sequence);
        var configuration = MicrosoftIdentityConfiguration.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            MicrosoftCertificateReference.Create(
                "0123456789ABCDEF0123456789ABCDEF01234567",
                CertificateStoreTarget.CurrentUser));
        var fingerprint = MicrosoftConfigurationFingerprint.Create(configuration, "scanner@example.com");
        var now = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        var identityAttempt = identityState.Begin(now, null, configuration, fingerprint);
        var exchangeAttempt = exchangeState.BeginAttempt(now, configuration, fingerprint);

        void CompleteIdentity()
        {
            if (identitySucceeds)
            {
                identityState.Succeed(identityAttempt, now, null, certificateExpiringSoon: false);
            }
            else
            {
                identityState.Fail(
                    identityAttempt,
                    now,
                    null,
                    MicrosoftIdentityErrorCategory.CredentialRejected);
            }
        }

        void CompleteExchange()
        {
            exchangeState.RecordResult(
                exchangeAttempt,
                now,
                exchangeSucceeds
                    ? DeliveryResult.Succeeded()
                    : DeliveryResult.TransientFailure("Authentication", "Failed."));
        }

        if (identityCompletesFirst)
        {
            CompleteIdentity();
            CompleteExchange();
        }
        else
        {
            CompleteExchange();
            CompleteIdentity();
        }

        Assert.Equal(
            expected,
            MicrosoftRuntimeReadinessPolicy.Evaluate(true, fingerprint, identityState, exchangeState));
    }

    private static MicrosoftIdentityConfiguration CreateConfiguration(MicrosoftCertificateReference reference)
    {
        return MicrosoftIdentityConfiguration.Create(Guid.NewGuid(), Guid.NewGuid(), reference);
    }

    private static X509Certificate2 LoadCertificate(MicrosoftCertificateReference reference)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(
            X509FindType.FindByThumbprint,
            reference.Thumbprint,
            validOnly: false);
        return Assert.Single(matches.Cast<X509Certificate2>());
    }

    private sealed class IdentityTestContext : IDisposable
    {
        private readonly bool _ownsDataDirectory;
        private readonly List<MicrosoftCertificateReference> _certificates = [];

        private IdentityTestContext(
            string dataDirectory,
            bool ownsDataDirectory,
            RelayDatabase database,
            ManualTimeProvider time,
            MicrosoftCertificateService certificates)
        {
            DataDirectory = dataDirectory;
            _ownsDataDirectory = ownsDataDirectory;
            Database = database;
            Time = time;
            Certificates = certificates;
        }

        public string DataDirectory { get; }

        public RelayDatabase Database { get; }

        public ManualTimeProvider Time { get; }

        public MicrosoftCertificateService Certificates { get; }

        public static IdentityTestContext Create(
            Action<MicrosoftIdentityOptions>? configure = null,
            string? dataDirectory = null)
        {
            var ownsDataDirectory = dataDirectory is null;
            dataDirectory ??= Path.Combine(Path.GetTempPath(), "RelayBridge.Tests", Guid.NewGuid().ToString("N"));
            var database = new RelayDatabase(
                new RelayStorageOptions { DataDirectory = dataDirectory },
                AppContext.BaseDirectory);
            database.Initialize();
            var time = new ManualTimeProvider(
                new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero));
            var options = new MicrosoftIdentityOptions();
            configure?.Invoke(options);
            options.Validate();
            var certificateService = new MicrosoftCertificateService(database, options, time);
            return new IdentityTestContext(
                dataDirectory,
                ownsDataDirectory,
                database,
                time,
                certificateService);
        }

        public void Track(MicrosoftCertificateReference reference)
        {
            _certificates.Add(reference);
        }

        public MicrosoftCertificateReference InstallRsaCertificate(
            DateTimeOffset notBefore,
            DateTimeOffset notAfter,
            bool includePrivateKey)
        {
            var keyName = $"RelayBridge-Test-{Guid.NewGuid():N}";
            if (includePrivateKey)
            {
                var parameters = new CngKeyCreationParameters
                {
                    ExportPolicy = CngExportPolicies.None,
                    KeyUsage = CngKeyUsages.Signing,
                    Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                };
                parameters.Parameters.Add(new CngProperty(
                    "Length",
                    BitConverter.GetBytes(2048),
                    CngPropertyOptions.None));
                using var key = CngKey.Create(CngAlgorithm.Rsa, keyName, parameters);
                using var rsa = new RSACng(key);
                var request = CreateRsaRequest(rsa);
                using var certificate = request.CreateSelfSigned(notBefore, notAfter);
                return AddToStore(certificate);
            }

            using (var rsa = RSA.Create(2048))
            {
                var request = CreateRsaRequest(rsa);
                using var withPrivateKey = request.CreateSelfSigned(notBefore, notAfter);
                using var publicOnly = X509CertificateLoader.LoadCertificate(
                    withPrivateKey.Export(X509ContentType.Cert));
                return AddToStore(publicOnly);
            }
        }

        public MicrosoftCertificateReference InstallEcdsaCertificate(
            DateTimeOffset notBefore,
            DateTimeOffset notAfter)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest(
                "CN=RelayBridge ECDSA Test",
                key,
                HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature,
                true));
            using var withPrivateKey = request.CreateSelfSigned(notBefore, notAfter);
            using var publicOnly = X509CertificateLoader.LoadCertificate(withPrivateKey.Export(X509ContentType.Cert));
            return AddToStore(publicOnly);
        }

        public void Dispose()
        {
            foreach (var reference in _certificates)
            {
                RemoveCertificateAndKey(reference);
            }

            SqliteConnection.ClearAllPools();
            if (_ownsDataDirectory && Directory.Exists(DataDirectory))
            {
                Directory.Delete(DataDirectory, recursive: true);
            }
        }

        private static CertificateRequest CreateRsaRequest(RSA rsa)
        {
            var request = new CertificateRequest(
                "CN=RelayBridge RSA Test",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature,
                true));
            return request;
        }

        private MicrosoftCertificateReference AddToStore(X509Certificate2 certificate)
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Add(certificate);
            var reference = MicrosoftCertificateReference.Create(
                certificate.Thumbprint,
                CertificateStoreTarget.CurrentUser);
            Track(reference);
            return reference;
        }

        private static void RemoveCertificateAndKey(MicrosoftCertificateReference reference)
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            var matches = store.Certificates.Find(
                X509FindType.FindByThumbprint,
                reference.Thumbprint,
                validOnly: false);
            foreach (var certificate in matches.Cast<X509Certificate2>())
            {
                string? keyName = null;
                try
                {
                    using var rsa = certificate.GetRSAPrivateKey();
                    if (rsa is RSACng cng)
                    {
                        keyName = cng.Key.KeyName;
                    }
                }
                catch (CryptographicException)
                {
                    // The certificate is still removed; inaccessible test keys are not expected here.
                }

                store.Remove(certificate);
                certificate.Dispose();
                if (keyName is not null && CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider))
                {
                    using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider);
                    key.Delete();
                }
            }
        }
    }

    private sealed class CapturingConfigurationTokenProvider : IMicrosoftTokenProvider
    {
        private int _calls;

        public TaskCompletionSource FirstCallEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstCall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<MicrosoftIdentityConfiguration> Configurations { get; } = new();

        public Task<MicrosoftAccessToken> GetExchangeTokenAsync(CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Identity verification must supply its captured configuration.");
        }

        public async Task<MicrosoftAccessToken> GetExchangeTokenAsync(
            MicrosoftIdentityConfiguration configuration,
            CancellationToken cancellationToken)
        {
            Configurations.Enqueue(configuration);
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstCallEntered.SetResult();
                await ReleaseFirstCall.Task.WaitAsync(cancellationToken);
            }

            return new MicrosoftAccessToken(
                "captured-configuration-token",
                DateTimeOffset.UtcNow.AddMinutes(30),
                configuration.TenantId);
        }
    }

    private sealed class OrderedIdentityTokenProvider : IMicrosoftTokenProvider
    {
        private int _calls;

        public TaskCompletionSource FirstEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSecond { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MicrosoftAccessToken> GetExchangeTokenAsync(CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Identity verification must supply its captured configuration.");
        }

        public async Task<MicrosoftAccessToken> GetExchangeTokenAsync(
            MicrosoftIdentityConfiguration configuration,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                FirstEntered.SetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
                return new MicrosoftAccessToken(
                    "first-identity-token",
                    DateTimeOffset.UtcNow.AddMinutes(30),
                    configuration.TenantId);
            }

            SecondEntered.SetResult();
            await ReleaseSecond.Task.WaitAsync(cancellationToken);
            throw new MicrosoftIdentityException(
                MicrosoftIdentityErrorCategory.CredentialRejected,
                "The controlled second identity check failed.");
        }
    }

    private sealed class FakeMicrosoftIdentityClientFactory : IMicrosoftIdentityClientFactory
    {
        private readonly TimeProvider _timeProvider;
        private int _createCount;

        public FakeMicrosoftIdentityClientFactory(TimeProvider timeProvider)
        {
            _timeProvider = timeProvider;
            Handler = (_, _) => Task.FromResult(new MicrosoftAccessToken(
                "fake-access-token",
                _timeProvider.GetUtcNow().AddHours(1),
                Configurations.Last().TenantId));
        }

        public Func<string, CancellationToken, Task<MicrosoftAccessToken>> Handler { get; set; }

        public int CreateCount => Volatile.Read(ref _createCount);

        public ConcurrentQueue<MicrosoftIdentityConfiguration> Configurations { get; } = new();

        public ConcurrentQueue<string> CertificateThumbprints { get; } = new();

        public ConcurrentQueue<string> Scopes { get; } = new();

        public IMicrosoftIdentityClient Create(
            MicrosoftIdentityConfiguration configuration,
            X509Certificate2 certificate)
        {
            Interlocked.Increment(ref _createCount);
            Configurations.Enqueue(configuration);
            CertificateThumbprints.Enqueue(certificate.Thumbprint);
            return new FakeMicrosoftIdentityClient(this);
        }

        private sealed class FakeMicrosoftIdentityClient : IMicrosoftIdentityClient
        {
            private readonly FakeMicrosoftIdentityClientFactory _owner;

            public FakeMicrosoftIdentityClient(FakeMicrosoftIdentityClientFactory owner)
            {
                _owner = owner;
            }

            public Task<MicrosoftAccessToken> AcquireTokenAsync(
                string scope,
                CancellationToken cancellationToken)
            {
                _owner.Scopes.Enqueue(scope);
                return _owner.Handler(scope, cancellationToken);
            }
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Enqueue(formatter(state, exception));
        }
    }
}
