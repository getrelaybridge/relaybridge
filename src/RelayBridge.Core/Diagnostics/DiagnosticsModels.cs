// SPDX-License-Identifier: MPL-2.0

namespace RelayBridge.Core.Diagnostics;

using RelayBridge.Core.Microsoft;
using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<DiagnosticStatus>))]
public enum DiagnosticStatus
{
    Healthy,
    Attention,
    Unavailable,
    NotConfigured,
    Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter<DiagnosticEvidenceSource>))]
public enum DiagnosticEvidenceSource
{
    Runtime,
    Configuration,
    PersistedState,
    LastVerification,
    ActiveProbe,
}

public sealed record DiagnosticEvidence(
    DiagnosticStatus Status,
    DateTimeOffset ObservedUtc,
    DiagnosticEvidenceSource Source,
    string Summary);

public sealed record RuntimeDiagnosticSnapshot(
    DiagnosticEvidence Evidence,
    string Version,
    string InformationalVersion,
    TimeSpan Uptime,
    string OperatingSystem,
    string DotNetRuntime,
    string HostingMode,
    string ManagementBinding);

public sealed record SmtpIntakeDiagnosticSnapshot(
    DiagnosticEvidence Evidence,
    bool Enabled,
    bool Listening,
    string? BoundAddress,
    int? BoundPort,
    string IntakeMode,
    int EnabledDeviceCount,
    DateTimeOffset? LastAcceptedMessageUtc,
    string InboundStartTls,
    string CleartextAuthenticationBoundary);

public sealed record QueueDiagnosticSnapshot(
    DiagnosticEvidence Evidence,
    int ActiveCount,
    int ReadyCount,
    int RetryingCount,
    int DeliveringCount,
    int PermanentFailureCount,
    DateTimeOffset? OldestQueuedUtc,
    DateTimeOffset? NextRetryUtc,
    bool WorkerExpected,
    bool WorkerRunning);

public sealed record MicrosoftDiagnosticSnapshot(
    DiagnosticEvidence Evidence,
    bool Configured,
    bool ActiveConfigurationExists,
    string Readiness,
    DateTimeOffset? LastSuccessfulVerificationUtc,
    DateTimeOffset? LastCompletedVerificationUtc,
    DateTimeOffset? LastActivationUtc,
    bool ActivationIdPresent);

public sealed record CertificateDiagnosticSnapshot(
    DiagnosticEvidence Evidence,
    bool Configured,
    bool Present,
    bool PrivateKeyAccessible,
    DateTimeOffset? ValidFromUtc,
    DateTimeOffset? ExpiresUtc,
    double? RemainingDays,
    bool Expired,
    string? AbbreviatedThumbprint);

public sealed record SetupDiagnosticSnapshot(
    DiagnosticEvidence Evidence,
    string Stage,
    string Category,
    string? SafeCode,
    string? PowerShellExceptionType,
    string? FullyQualifiedErrorId,
    string? PowerShellCategory,
    int? HttpStatusCode);

[JsonConverter(typeof(JsonStringEnumConverter<ConnectivityProbeStage>))]
public enum ConnectivityProbeStage
{
    NotRun,
    Dns,
    Tcp,
    Greeting,
    Ehlo,
    StartTls,
    Tls,
    Complete,
}

public sealed record ConnectivityDiagnosticSnapshot(
    DiagnosticEvidence Evidence,
    ConnectivityProbeStage Stage,
    bool? Succeeded,
    TimeSpan? Elapsed);

public sealed record StorageDiagnosticSnapshot(
    DiagnosticEvidence Evidence,
    bool DatabaseAccessible,
    bool StorageDirectoryAccessible,
    int? SchemaVersion,
    long? FreeDiskBytes,
    DiagnosticEvidence QuickCheck);

public sealed record SecurityDiagnosticSnapshot(
    DiagnosticEvidence Evidence,
    bool ManagementLoopbackOnly,
    string AuthenticatedCleartextSmtp,
    string InboundStartTls,
    string PrivateMicrosoftTooling,
    string CertificatePrivateKey,
    string ProvisioningScratch);

public sealed record RelayDiagnosticsSnapshot(
    DiagnosticEvidence Overall,
    RuntimeDiagnosticSnapshot Runtime,
    SmtpIntakeDiagnosticSnapshot Smtp,
    QueueDiagnosticSnapshot Queue,
    MicrosoftDiagnosticSnapshot Microsoft,
    CertificateDiagnosticSnapshot Certificate,
    SetupDiagnosticSnapshot Setup,
    ConnectivityDiagnosticSnapshot Connectivity,
    StorageDiagnosticSnapshot Storage,
    SecurityDiagnosticSnapshot Security);

public static class DiagnosticsOverallStatusPolicy
{
    public static DiagnosticStatus Evaluate(
        bool microsoftConfigured,
        DiagnosticStatus runtime,
        DiagnosticStatus smtp,
        DiagnosticStatus queue,
        DiagnosticStatus microsoft,
        DiagnosticStatus certificate,
        DiagnosticStatus setup,
        DiagnosticStatus connectivity,
        DiagnosticStatus storage,
        DiagnosticStatus security)
    {
        var requiredLocal = new[] { runtime, smtp, queue, storage, security };
        if (requiredLocal.Contains(DiagnosticStatus.Unavailable) ||
            (microsoftConfigured &&
             (microsoft == DiagnosticStatus.Unavailable || certificate == DiagnosticStatus.Unavailable)))
        {
            return DiagnosticStatus.Unavailable;
        }

        if (!microsoftConfigured)
        {
            return requiredLocal.Contains(DiagnosticStatus.Attention)
                ? DiagnosticStatus.Attention
                : DiagnosticStatus.NotConfigured;
        }

        if (requiredLocal.Contains(DiagnosticStatus.Attention) ||
            microsoft is DiagnosticStatus.Attention or DiagnosticStatus.Unknown or DiagnosticStatus.NotConfigured ||
            certificate == DiagnosticStatus.Attention ||
            setup == DiagnosticStatus.Attention ||
            connectivity == DiagnosticStatus.Attention)
        {
            return DiagnosticStatus.Attention;
        }

        return DiagnosticStatus.Healthy;
    }
}

public static class DiagnosticsItemStatusPolicy
{
    public static DiagnosticStatus Listener(bool enabled, bool listening) => !enabled
        ? DiagnosticStatus.Attention
        : listening ? DiagnosticStatus.Healthy : DiagnosticStatus.Unavailable;

    public static DiagnosticStatus Queue(
        bool workerExpected,
        bool workerRunning,
        int permanentFailureCount) => workerExpected && !workerRunning
            ? DiagnosticStatus.Unavailable
            : permanentFailureCount > 0 ? DiagnosticStatus.Attention : DiagnosticStatus.Healthy;

    public static DiagnosticStatus Certificate(CertificateValidationStatus status) => status switch
    {
        CertificateValidationStatus.Valid => DiagnosticStatus.Healthy,
        CertificateValidationStatus.ExpiringSoon => DiagnosticStatus.Attention,
        CertificateValidationStatus.NotConfigured => DiagnosticStatus.NotConfigured,
        _ => DiagnosticStatus.Unavailable,
    };
}
