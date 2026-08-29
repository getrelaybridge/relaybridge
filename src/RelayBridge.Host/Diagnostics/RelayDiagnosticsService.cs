// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using RelayBridge.Core.Diagnostics;
using RelayBridge.Core.Microsoft;
using RelayBridge.Host.Services;
using RelayBridge.Infrastructure.Diagnostics;
using RelayBridge.Infrastructure.Microsoft;
using RelayBridge.Infrastructure.Queue;
using RelayBridge.Infrastructure.Smtp;
using RelayBridge.Infrastructure.Storage;

namespace RelayBridge.Host.Diagnostics;

public sealed class RelayDiagnosticsService
{
    private readonly RelayDatabase _database;
    private readonly LocalDiagnosticDataReader _localData;
    private readonly DiagnosticsActionState _actions;
    private readonly SmtpListener _listener;
    private readonly SmtpListenerOptions _smtpOptions;
    private readonly QueueWorker _queueWorker;
    private readonly QueueOptions _queueOptions;
    private readonly QueueDeliveryActivation _queueDeliveryActivation;
    private readonly MicrosoftCertificateService _certificates;
    private readonly MicrosoftIdentityRuntimeState _identity;
    private readonly ExchangeDeliveryRuntimeState _exchange;
    private readonly NativeMicrosoftSetupRuntime _nativeSetup;
    private readonly NativeMicrosoftSetupOptions _nativeOptions;
    private readonly TimeProvider _timeProvider;
    private readonly IHostEnvironment _environment;
    private readonly IServer _server;

    public RelayDiagnosticsService(
        RelayDatabase database,
        LocalDiagnosticDataReader localData,
        DiagnosticsActionState actions,
        SmtpListener listener,
        IOptions<SmtpListenerOptions> smtpOptions,
        QueueWorker queueWorker,
        QueueOptions queueOptions,
        QueueDeliveryActivation queueDeliveryActivation,
        MicrosoftCertificateService certificates,
        MicrosoftIdentityRuntimeState identity,
        ExchangeDeliveryRuntimeState exchange,
        NativeMicrosoftSetupRuntime nativeSetup,
        NativeMicrosoftSetupOptions nativeOptions,
        TimeProvider timeProvider,
        IHostEnvironment environment,
        IServer server)
    {
        _database = database;
        _localData = localData;
        _actions = actions;
        _listener = listener;
        _smtpOptions = smtpOptions.Value;
        _queueWorker = queueWorker;
        _queueOptions = queueOptions;
        _queueDeliveryActivation = queueDeliveryActivation;
        _certificates = certificates;
        _identity = identity;
        _exchange = exchange;
        _nativeSetup = nativeSetup;
        _nativeOptions = nativeOptions;
        _timeProvider = timeProvider;
        _environment = environment;
        _server = server;
    }

    public RelayDiagnosticsSnapshot GetSnapshot(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var management = ReadManagementBoundary(now);
        var runtime = ReadRuntime(now, management);
        var storageFacts = _localData.ReadStorage(cancellationToken);
        var storage = ReadStorage(now, storageFacts);
        var active = storageFacts.DatabaseAccessible
            ? SafeRead<ActiveMicrosoftConfiguration>(() => _database.GetActiveMicrosoftConfiguration(cancellationToken))
            : null;
        var setupState = storageFacts.DatabaseAccessible
            ? SafeRead<MicrosoftSetupState>(() => _database.GetMicrosoftSetupState(cancellationToken))
            : null;
        var queueFacts = storageFacts.DatabaseAccessible
            ? SafeRead(() => _localData.ReadQueue(cancellationToken))
            : null;
        var devices = storageFacts.DatabaseAccessible
            ? SafeRead(() => _database.GetDevices(cancellationToken))
            : null;

        var smtp = ReadSmtp(now, devices, queueFacts?.LastAcceptedUtc);
        var queue = ReadQueue(now, queueFacts, active is not null);
        var microsoft = ReadMicrosoft(now, active, setupState);
        var certificate = ReadCertificate(now, active);
        var setup = ReadSetup(now, active is not null, setupState);
        var scratch = _localData.ReadProvisioningScratch(_nativeOptions.Enabled);
        var security = ReadSecurity(now, management, smtp, certificate, scratch);
        var connectivity = _actions.Connectivity;
        var overallStatus = DiagnosticsOverallStatusPolicy.Evaluate(
            microsoft.Configured,
            runtime.Evidence.Status,
            smtp.Evidence.Status,
            queue.Evidence.Status,
            microsoft.Evidence.Status,
            certificate.Evidence.Status,
            setup.Evidence.Status,
            connectivity.Evidence.Status,
            storage.Evidence.Status,
            security.Evidence.Status);
        var overall = new DiagnosticEvidence(
            overallStatus,
            now,
            DiagnosticEvidenceSource.Runtime,
            OverallSummary(overallStatus));

        return new RelayDiagnosticsSnapshot(
            overall,
            runtime,
            smtp,
            queue,
            microsoft,
            certificate,
            setup,
            connectivity,
            storage,
            security);
    }

    private RuntimeDiagnosticSnapshot ReadRuntime(
        DateTimeOffset now,
        DiagnosticEvidence management)
    {
        var assembly = typeof(Program).Assembly;
        var version = assembly.GetName().Version?.ToString() ?? "Unknown";
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? version;
        DateTimeOffset processStartedUtc;
        try
        {
            processStartedUtc = new DateTimeOffset(Process.GetCurrentProcess().StartTime).ToUniversalTime();
        }
        catch
        {
            processStartedUtc = now;
        }

        return new RuntimeDiagnosticSnapshot(
            management,
            version,
            informational,
            now - processStartedUtc,
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            WindowsServiceHelpers.IsWindowsService()
                ? "Windows Service"
                : _environment.IsDevelopment() ? "Development" : "Interactive host",
            "Loopback only");
    }

    private DiagnosticEvidence ReadManagementBoundary(DateTimeOffset now)
    {
        var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is { Count: > 0 })
        {
            var loopbackOnly = addresses.All(ManagementBindingPolicy.IsLoopbackUrl);
            return new DiagnosticEvidence(
                loopbackOnly ? DiagnosticStatus.Healthy : DiagnosticStatus.Unavailable,
                now,
                DiagnosticEvidenceSource.Runtime,
                loopbackOnly
                    ? "The active management listener addresses are loopback-only."
                    : "A management listener is not loopback-only.");
        }

        return new DiagnosticEvidence(
            DiagnosticStatus.Healthy,
            now,
            DiagnosticEvidenceSource.Configuration,
            "Management is enforced by the code-owned loopback binding policy.");
    }

    private SmtpIntakeDiagnosticSnapshot ReadSmtp(
        DateTimeOffset now,
        IReadOnlyList<RelayBridge.Core.Devices.DeviceDefinition>? devices,
        DateTimeOffset? lastAcceptedUtc)
    {
        var endpoint = _listener.BoundEndpoint;
        var listening = endpoint is not null;
        var status = DiagnosticsItemStatusPolicy.Listener(_smtpOptions.Enabled, listening);
        var summary = !_smtpOptions.Enabled
            ? "The SMTP intake listener is disabled by configuration."
            : listening
                ? "The SMTP intake listener is actively bound and listening."
                : "The SMTP intake listener is enabled but is not currently bound.";
        var intakeMode = _smtpOptions.AllowCleartextAuthentication
            ? "Authenticated cleartext on an explicit private listener"
            : "Legacy/trusted-LAN only; SMTP AUTH disabled";
        return new SmtpIntakeDiagnosticSnapshot(
            new DiagnosticEvidence(status, now, DiagnosticEvidenceSource.Runtime, summary),
            _smtpOptions.Enabled,
            listening,
            endpoint?.Address.ToString(),
            endpoint?.Port,
            intakeMode,
            devices?.Count(device => device.Enabled) ?? 0,
            lastAcceptedUtc,
            "Not currently available",
            _smtpOptions.AllowCleartextAuthentication
                ? "Allowed only on an explicitly configured private listener"
                : "Not configured");
    }

    private QueueDiagnosticSnapshot ReadQueue(
        DateTimeOffset now,
        LocalQueueDiagnosticFacts? facts,
        bool microsoftConfigured)
    {
        if (facts is null)
        {
            return new QueueDiagnosticSnapshot(
                new DiagnosticEvidence(
                    DiagnosticStatus.Unavailable,
                    now,
                    DiagnosticEvidenceSource.Runtime,
                    "Queue metrics are unavailable."),
                0, 0, 0, 0, 0, null, null, _queueOptions.Enabled, _queueWorker.IsRunning);
        }

        var metrics = facts.Metrics;
        var workerHealthy = !_queueOptions.Enabled || _queueWorker.IsRunning;
        var status = DiagnosticsItemStatusPolicy.Queue(
            _queueOptions.Enabled,
            _queueWorker.IsRunning,
            microsoftConfigured,
            _queueDeliveryActivation.IsActivated,
            metrics.PermanentFailureCount);
        var active = metrics.QueuedCount + metrics.RetryScheduledCount + metrics.DeliveringCount;
        var summary = !workerHealthy
            ? "Queue delivery is enabled, but the worker is not running."
            : metrics.PermanentFailureCount > 0
                ? $"{metrics.PermanentFailureCount} message(s) have permanently failed."
                : _queueOptions.Enabled && !microsoftConfigured
                    ? "Queue delivery is waiting for Microsoft configuration."
                    : _queueOptions.Enabled && !_queueDeliveryActivation.IsActivated
                        ? "Queue delivery cannot use the active Microsoft configuration."
                        : active == 0
                            ? "No messages are waiting or being delivered."
                            : $"{active} message(s) are active in the queue.";
        return new QueueDiagnosticSnapshot(
            new DiagnosticEvidence(status, now, DiagnosticEvidenceSource.PersistedState, summary),
            active,
            metrics.QueuedCount,
            metrics.RetryScheduledCount,
            metrics.DeliveringCount,
            metrics.PermanentFailureCount,
            metrics.OldestQueuedUtc,
            facts.NextRetryUtc,
            _queueOptions.Enabled,
            _queueWorker.IsRunning);
    }

    private MicrosoftDiagnosticSnapshot ReadMicrosoft(
        DateTimeOffset now,
        ActiveMicrosoftConfiguration? active,
        MicrosoftSetupState? setupState)
    {
        if (active is null)
        {
            return new MicrosoftDiagnosticSnapshot(
                new DiagnosticEvidence(
                    DiagnosticStatus.NotConfigured,
                    now,
                    DiagnosticEvidenceSource.PersistedState,
                    "No active Microsoft configuration exists."),
                false, false, "Not configured", null, null, null, false);
        }

        var identity = _identity.GetCompletedSnapshot(active.Fingerprint);
        var exchange = _exchange.GetCompletedSnapshot(active.Fingerprint);
        var readiness = MicrosoftRuntimeReadinessPolicy.Evaluate(
            active.AuthorizedSender is not null,
            active.Fingerprint,
            identity,
            exchange);
        var status = readiness switch
        {
            MicrosoftRuntimeReadiness.Ready => DiagnosticStatus.Healthy,
            MicrosoftRuntimeReadiness.NeedsAttention or MicrosoftRuntimeReadiness.VerificationRequired =>
                DiagnosticStatus.Attention,
            _ => DiagnosticStatus.NotConfigured,
        };
        var source = readiness == MicrosoftRuntimeReadiness.VerificationRequired
            ? DiagnosticEvidenceSource.Runtime
            : DiagnosticEvidenceSource.LastVerification;
        var successful = new[] { identity.LastSuccessfulAt, exchange.LastSuccessfulAt }
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty()
            .Max();
        var completed = new[] { identity.LastCompletedAt, exchange.LastCompletedAt }
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty()
            .Max();
        DateTimeOffset? activationUtc = setupState?.Lifecycle == MicrosoftSetupCandidateLifecycle.Activated &&
            setupState.ActivationId == active.ActivationId
                ? setupState.UpdatedUtc
                : null;
        return new MicrosoftDiagnosticSnapshot(
            new DiagnosticEvidence(status, now, source, readiness switch
            {
                MicrosoftRuntimeReadiness.Ready => "Current-process Microsoft evidence is ready for the active configuration.",
                MicrosoftRuntimeReadiness.NeedsAttention => "The newest current-process Microsoft evidence needs attention.",
                _ => "The active configuration requires verification during this service start.",
            }),
            true,
            true,
            readiness switch
            {
                MicrosoftRuntimeReadiness.Ready => "Ready",
                MicrosoftRuntimeReadiness.NeedsAttention => "Needs attention",
                MicrosoftRuntimeReadiness.VerificationRequired => "Verification required",
                _ => "Not configured",
            },
            successful == default ? null : successful,
            completed == default ? null : completed,
            activationUtc,
            active.ActivationId != Guid.Empty);
    }

    private CertificateDiagnosticSnapshot ReadCertificate(
        DateTimeOffset now,
        ActiveMicrosoftConfiguration? active)
    {
        CertificateValidationResult validation;
        try
        {
            validation = _certificates.Validate(active?.Identity.Certificate);
        }
        catch
        {
            return new CertificateDiagnosticSnapshot(
                new DiagnosticEvidence(
                    DiagnosticStatus.Unavailable,
                    now,
                    DiagnosticEvidenceSource.Runtime,
                    "Certificate inspection failed unexpectedly."),
                active is not null, false, false, null, null, null, false, null);
        }

        var status = DiagnosticsItemStatusPolicy.Certificate(validation.Status);
        var metadata = validation.Certificate;
        return new CertificateDiagnosticSnapshot(
            new DiagnosticEvidence(status, now, DiagnosticEvidenceSource.Runtime, validation.Message),
            active is not null,
            metadata is not null,
            validation.IsUsable,
            metadata?.NotBefore,
            metadata?.NotAfter,
            metadata is null ? null : Math.Max(0, (metadata.NotAfter - now).TotalDays),
            validation.Status == CertificateValidationStatus.Expired,
            metadata?.Thumbprint is { Length: >= 12 } thumbprint ? thumbprint[..12] : null);
    }

    private SetupDiagnosticSnapshot ReadSetup(
        DateTimeOffset now,
        bool microsoftConfigured,
        MicrosoftSetupState? setupState)
    {
        var native = _nativeSetup.Snapshot;
        var nativeFailed = native.FailureCategory != NativeSetupFailureCategory.None &&
            native.FailureCategory != NativeSetupFailureCategory.Cancelled;
        if (nativeFailed || native.Stage == NativeSetupStage.Complete)
        {
            var details = native.SafeFailureDetails;
            return new SetupDiagnosticSnapshot(
                new DiagnosticEvidence(
                    nativeFailed ? DiagnosticStatus.Attention : DiagnosticStatus.Healthy,
                    native.UpdatedUtc,
                    DiagnosticEvidenceSource.Runtime,
                    nativeFailed
                        ? "The last native Microsoft setup attempt failed safely."
                        : "The last native Microsoft setup attempt completed."),
                native.Stage.ToString(),
                native.FailureSubstage.ToString(),
                native.FailureCategory.ToString(),
                native.SafeCode,
                details?.PowerShellExceptionType,
                details?.FullyQualifiedErrorId,
                details?.PowerShellCategory,
                details?.HttpStatusCode);
        }

        if (setupState is not null)
        {
            var completed = setupState.Lifecycle == MicrosoftSetupCandidateLifecycle.Activated &&
                setupState.Step == MicrosoftSetupStep.Complete;
            return new SetupDiagnosticSnapshot(
                new DiagnosticEvidence(
                    completed ? DiagnosticStatus.Healthy : DiagnosticStatus.Unknown,
                    setupState.UpdatedUtc,
                    DiagnosticEvidenceSource.PersistedState,
                    completed
                        ? "Persisted setup state records a completed activation."
                        : "Persisted setup progress is available, but it is not a live result."),
                setupState.Step.ToString(),
                NativeSetupFailureSubstage.None.ToString(),
                setupState.Lifecycle.ToString(),
                null, null, null, null, null);
        }

        return new SetupDiagnosticSnapshot(
            new DiagnosticEvidence(
                microsoftConfigured ? DiagnosticStatus.Unknown : DiagnosticStatus.NotConfigured,
                now,
                DiagnosticEvidenceSource.Runtime,
                "No retained Microsoft setup result is available."),
            "Not run", NativeSetupFailureSubstage.None.ToString(), "None", null, null, null, null, null);
    }

    private StorageDiagnosticSnapshot ReadStorage(
        DateTimeOffset now,
        LocalStorageDiagnosticFacts facts)
    {
        var status = facts.DatabaseAccessible && facts.StorageDirectoryAccessible
            ? DiagnosticStatus.Healthy
            : DiagnosticStatus.Unavailable;
        return new StorageDiagnosticSnapshot(
            new DiagnosticEvidence(
                status,
                now,
                DiagnosticEvidenceSource.Runtime,
                status == DiagnosticStatus.Healthy
                    ? "SQLite and the RelayBridge storage directory are accessible."
                    : "SQLite or the RelayBridge storage directory is unavailable."),
            facts.DatabaseAccessible,
            facts.StorageDirectoryAccessible,
            facts.SchemaVersion,
            facts.FreeDiskBytes,
            _actions.DatabaseQuickCheck);
    }

    private SecurityDiagnosticSnapshot ReadSecurity(
        DateTimeOffset now,
        DiagnosticEvidence management,
        SmtpIntakeDiagnosticSnapshot smtp,
        CertificateDiagnosticSnapshot certificate,
        DiagnosticEvidence scratch)
    {
        var configuredAddress = _smtpOptions.GetListenAddress();
        var privateAuthSafe = !_smtpOptions.AllowCleartextAuthentication ||
            (TrustedLanAddress.IsPrivateUnicast(configuredAddress) &&
             (_listener.BoundEndpoint is null || _listener.BoundEndpoint.Address.Equals(configuredAddress)));
        var tooling = !_nativeOptions.Enabled
            ? "Not configured"
            : _nativeSetup.Snapshot.Available ? "Configured; verified before privileged use" : "Configured but unavailable";
        var status = management.Status == DiagnosticStatus.Unavailable || !privateAuthSafe ||
            (_nativeOptions.Enabled && scratch.Status == DiagnosticStatus.Unavailable) ||
            (certificate.Configured && !certificate.PrivateKeyAccessible)
                ? DiagnosticStatus.Unavailable
                : smtp.Evidence.Status == DiagnosticStatus.Unavailable
                    ? DiagnosticStatus.Attention
                    : DiagnosticStatus.Healthy;
        return new SecurityDiagnosticSnapshot(
            new DiagnosticEvidence(
                status,
                now,
                DiagnosticEvidenceSource.Runtime,
                status == DiagnosticStatus.Healthy
                    ? "Observable RelayBridge security boundaries are satisfied."
                    : "One or more observable RelayBridge security boundaries require attention."),
            management.Status == DiagnosticStatus.Healthy,
            !_smtpOptions.AllowCleartextAuthentication
                ? "Not configured"
                : privateAuthSafe ? "Explicit private binding" : "Boundary failure",
            "Unavailable by design",
            tooling,
            !certificate.Configured
                ? "Not configured"
                : certificate.PrivateKeyAccessible ? "Accessible locally" : "Unavailable",
            scratch.Summary);
    }

    private static T? SafeRead<T>(Func<T?> action) where T : class
    {
        try
        {
            return action();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string OverallSummary(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Healthy => "Configured required runtime components are healthy.",
        DiagnosticStatus.Attention => "RelayBridge is running, but one or more diagnostic items need attention.",
        DiagnosticStatus.Unavailable => "A required configured runtime component is unavailable.",
        DiagnosticStatus.NotConfigured => "RelayBridge is running, but Microsoft 365 setup is not complete.",
        _ => "RelayBridge diagnostic state is incomplete.",
    };
}
