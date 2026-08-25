// SPDX-License-Identifier: MPL-2.0

using System.Text.Json.Serialization;

namespace RelayBridge.Core.Microsoft;

public static class NativeMicrosoftSetupProtocol
{
    public const int Version = 1;
    public const int MaximumMessageBytes = 4096;
    public const string BootstrapPipeName = "RelayBridge.MicrosoftSetup.Bootstrap.v1";
}

public enum NativeSetupMessageKind
{
    Hello,
    Start,
    Confirmed,
    Cancelled,
    Stage,
    EntraResult,
    ExchangeResult,
    Completed,
    Failed,
}

public enum NativeSetupStage
{
    WaitingForHelper,
    Confirming,
    VerifyingTools,
    WaitingForEntraSignIn,
    ConfiguringApplication,
    RegisteringCertificate,
    WaitingForExchangeSignIn,
    ConfiguringExchange,
    RestrictingSender,
    VerifyingAuthorization,
    VerifyingIdentity,
    VerifyingSmtp,
    Complete,
}

public enum NativeSetupFailureCategory
{
    None,
    Cancelled,
    Busy,
    InvalidHelper,
    InvalidSession,
    ToolIntegrity,
    AuthenticationRejected,
    InsufficientPermission,
    ConditionalAccess,
    Conflict,
    MicrosoftService,
    Timeout,
    InvalidResult,
    HelperFailed,
}

public sealed record NativeSetupSafeFailureDetails(
    string? PowerShellExceptionType,
    string? FullyQualifiedErrorId,
    string? PowerShellCategory,
    int? HttpStatusCode);

public sealed record NativeSetupEnvelope(
    int Version,
    NativeSetupMessageKind Kind,
    Guid? SessionId = null,
    int? ProcessId = null,
    int? WindowsSessionId = null,
    NativeSetupStage? Stage = null,
    NativeSetupFailureCategory FailureCategory = NativeSetupFailureCategory.None,
    string? SafeCode = null,
    string? SafeCorrelationId = null,
    NativeSetupSafeFailureDetails? SafeFailureDetails = null,
    EntraSetupResult? Entra = null,
    ExchangeSetupResult? Exchange = null);

public sealed record NativeSetupStartRequest(
    int Version,
    Guid SessionId,
    string SenderMailbox,
    string PublicCertificateBase64,
    string ApplicationDisplayName,
    bool IsRepair,
    string InstallationRoot,
    string ToolingRoot,
    string ToolingManifestPath,
    string ToolingManifestSha256,
    string ScratchDirectory,
    int LauncherProcessId,
    int LauncherWindowsSessionId,
    string LauncherUserSid,
    string LauncherPath,
    string LauncherSha256,
    Guid CandidateActivationId,
    long CandidateRevision,
    string ConfigurationFingerprint,
    MicrosoftSetupMode SetupMode);

public sealed record NativeSetupRuntimeSnapshot(
    bool Available,
    bool Running,
    NativeSetupStage Stage,
    string Message,
    NativeSetupFailureCategory FailureCategory,
    string? SafeCode,
    string? SafeCorrelationId,
    DateTimeOffset UpdatedUtc,
    NativeSetupSafeFailureDetails? SafeFailureDetails = null)
{
    [JsonIgnore]
    public bool Failed => FailureCategory != NativeSetupFailureCategory.None &&
        FailureCategory != NativeSetupFailureCategory.Cancelled;
}
