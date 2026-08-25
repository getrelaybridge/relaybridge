// SPDX-License-Identifier: MPL-2.0

using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelayBridge.Core.Microsoft;

namespace RelayBridge.Setup;

internal sealed class SetupOrchestrator
{
    private const string ResultPrefix = "RELAYBRIDGE_RESULT:";
    private readonly Stream _output;
    private readonly NativeSetupStartRequest _start;

    internal SetupOrchestrator(Stream output, NativeSetupStartRequest start)
    {
        _output = output;
        _start = start;
    }

    [SupportedOSPlatform("windows")]
    internal async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await StageAsync(NativeSetupStage.VerifyingTools, cancellationToken).ConfigureAwait(false);
            _ = ToolingIntegrityVerifier.Verify(
                _start.InstallationRoot,
                _start.ToolingRoot,
                _start.ToolingManifestPath,
                _start.ToolingManifestSha256);
            var certificateBytes = Convert.FromBase64String(_start.PublicCertificateBase64);
            if (certificateBytes.Length is <= 0 or > 3072)
            {
                throw new InvalidDataException("The public certificate payload is invalid.");
            }

            var entraPayload = new EntraExecutorPayload(
                _start.ApplicationDisplayName,
                _start.PublicCertificateBase64);
            await StageAsync(NativeSetupStage.WaitingForEntraSignIn, cancellationToken).ConfigureAwait(false);
            var entra = await ExecuteEntraAsync(entraPayload, cancellationToken).ConfigureAwait(false);
            await NativeSetupPipeProtocol.WriteAsync(
                _output,
                new NativeSetupEnvelope(
                    NativeMicrosoftSetupProtocol.Version,
                    NativeSetupMessageKind.EntraResult,
                    _start.SessionId,
                    Entra: entra),
                cancellationToken).ConfigureAwait(false);

            await StageAsync(NativeSetupStage.WaitingForExchangeSignIn, cancellationToken).ConfigureAwait(false);
            var exchange = await ExecuteExchangeAsync(
                new ExchangeExecutorPayload(
                    entra.ClientId,
                    entra.ServicePrincipalObjectId,
                    _start.SenderMailbox),
                cancellationToken).ConfigureAwait(false);
            await NativeSetupPipeProtocol.WriteAsync(
                _output,
                new NativeSetupEnvelope(
                    NativeMicrosoftSetupProtocol.Version,
                    NativeSetupMessageKind.ExchangeResult,
                    _start.SessionId,
                    Exchange: exchange),
                cancellationToken).ConfigureAwait(false);
            await NativeSetupPipeProtocol.WriteAsync(
                _output,
                new NativeSetupEnvelope(
                    NativeMicrosoftSetupProtocol.Version,
                    NativeSetupMessageKind.Completed,
                    _start.SessionId,
                    Stage: NativeSetupStage.Complete),
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            await TrySendFailureAsync(NativeSetupFailureCategory.Cancelled, "Cancelled").ConfigureAwait(false);
            return false;
        }
        catch (ToolIntegrityException)
        {
            await TrySendFailureAsync(NativeSetupFailureCategory.ToolIntegrity, "ToolIntegrity").ConfigureAwait(false);
            NativeConfirmation.ShowFailure("RelayBridge's Microsoft setup tools are not installed securely. Repair the RelayBridge installation.");
            return false;
        }
        catch (ProvisioningException exception)
        {
            await TrySendFailureAsync(
                exception.Category,
                exception.SafeCode,
                exception.SafeFailureDetails).ConfigureAwait(false);
            return false;
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or IOException)
        {
            await TrySendFailureAsync(NativeSetupFailureCategory.InvalidResult, exception.GetType().Name).ConfigureAwait(false);
            return false;
        }
    }

    internal static async Task ListenForCancellationAsync(
        Stream pipe,
        Guid sessionId,
        CancellationTokenSource cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var message = await NativeSetupPipeProtocol.ReadAsync<NativeSetupEnvelope>(
                    pipe,
                    cancellation.Token).ConfigureAwait(false);
                if (message.Version != NativeMicrosoftSetupProtocol.Version || message.SessionId != sessionId ||
                    message.Kind != NativeSetupMessageKind.Cancelled)
                {
                    cancellation.Cancel();
                    return;
                }

                cancellation.Cancel();
                return;
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidDataException)
        {
            cancellation.Cancel();
        }
    }

    internal static async Task IgnoreCancellationListenerAsync(Task listener)
    {
        try
        {
            await listener.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task<EntraSetupResult> ExecuteEntraAsync(
        EntraExecutorPayload payload,
        CancellationToken cancellationToken)
    {
        await StageAsync(NativeSetupStage.ConfiguringApplication, cancellationToken).ConfigureAwait(false);
        var tooling = ToolingIntegrityVerifier.Verify(
            _start.InstallationRoot,
            _start.ToolingRoot,
            _start.ToolingManifestPath,
            _start.ToolingManifestSha256);
        using var runner = new PowerShellProcessRunner();
        VerifyScratchDirectory();
        var preflight = await runner.RunAsync(
            tooling.PowerShellPath,
            _start.ToolingRoot,
            _start.ScratchDirectory,
            ProvisioningScripts.CreateEntraImportPreflightScript(tooling),
            cancellationToken).ConfigureAwait(false);
        var preflightEvidence = ParseResult<EntraImportPreflightResult>(preflight);
        if (string.IsNullOrWhiteSpace(preflightEvidence.PowerShellVersion) ||
            !string.Equals(
                preflightEvidence.GraphAuthenticationVersion,
                ToolingIntegrityVerifier.RequiredGraphAuthenticationModuleVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                preflightEvidence.GraphApplicationsVersion,
                ToolingIntegrityVerifier.RequiredGraphApplicationsModuleVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                preflightEvidence.EntraAuthenticationVersion,
                ToolingIntegrityVerifier.RequiredEntraAuthenticationModuleVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                preflightEvidence.EntraApplicationsVersion,
                ToolingIntegrityVerifier.RequiredEntraApplicationsModuleVersion,
                StringComparison.Ordinal) ||
            !preflightEvidence.GraphAuthenticationPathMatches ||
            !preflightEvidence.GraphApplicationsPathMatches ||
            !preflightEvidence.EntraAuthenticationPathMatches ||
            !preflightEvidence.EntraApplicationsPathMatches ||
            !preflightEvidence.ConnectMgGraphAvailable ||
            !preflightEvidence.ConnectEntraAvailable ||
            !preflightEvidence.GetMgApplicationAvailable ||
            !preflightEvidence.PSModulePathLocked ||
            preflightEvidence.UnexpectedModuleDiscovery)
        {
            throw new ToolIntegrityException();
        }
        var result = await runner.RunAsync(
            tooling.PowerShellPath,
            _start.ToolingRoot,
            _start.ScratchDirectory,
            ProvisioningScripts.CreateEntraScript(tooling, Encode(payload)),
            cancellationToken).ConfigureAwait(false);
        return ParseResult<EntraSetupResult>(result);
    }

    [SupportedOSPlatform("windows")]
    private async Task<ExchangeSetupResult> ExecuteExchangeAsync(
        ExchangeExecutorPayload payload,
        CancellationToken cancellationToken)
    {
        await StageAsync(NativeSetupStage.ConfiguringExchange, cancellationToken).ConfigureAwait(false);
        var tooling = ToolingIntegrityVerifier.Verify(
            _start.InstallationRoot,
            _start.ToolingRoot,
            _start.ToolingManifestPath,
            _start.ToolingManifestSha256);
        using var runner = new PowerShellProcessRunner();
        VerifyScratchDirectory();
        var result = await runner.RunAsync(
            tooling.PowerShellPath,
            _start.ToolingRoot,
            _start.ScratchDirectory,
            ProvisioningScripts.CreateExchangeScript(tooling, Encode(payload), _start.ScratchDirectory),
            cancellationToken,
            PowerShellHostingMode.InteractiveWamConsole,
            _start.LauncherWindowsSessionId).ConfigureAwait(false);
        return ParseResult<ExchangeSetupResult>(result);
    }

    private async Task StageAsync(NativeSetupStage stage, CancellationToken cancellationToken)
    {
        await NativeSetupPipeProtocol.WriteAsync(
            _output,
            new NativeSetupEnvelope(
                NativeMicrosoftSetupProtocol.Version,
                NativeSetupMessageKind.Stage,
                _start.SessionId,
                Stage: stage),
            cancellationToken).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private void VerifyScratchDirectory()
    {
        ProvisioningScratchDirectory.VerifySession(
            ProvisioningScratchDirectory.DefaultRoot,
            _start.ScratchDirectory,
            new System.Security.Principal.SecurityIdentifier(_start.LauncherUserSid));
    }

    private async Task TrySendFailureAsync(
        NativeSetupFailureCategory category,
        string safeCode,
        NativeSetupSafeFailureDetails? safeFailureDetails = null)
    {
        try
        {
            await NativeSetupPipeProtocol.WriteAsync(
                _output,
                new NativeSetupEnvelope(
                    NativeMicrosoftSetupProtocol.Version,
                    category == NativeSetupFailureCategory.Cancelled
                        ? NativeSetupMessageKind.Cancelled
                        : NativeSetupMessageKind.Failed,
                    _start.SessionId,
                    FailureCategory: category,
                    SafeCode: safeCode,
                    SafeFailureDetails: safeFailureDetails),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
    }

    private static string Encode<T>(T payload)
    {
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload));
    }

    internal static T ParseResult<T>(PowerShellExecutionResult result)
    {
        if (result.ExitCode != 0)
        {
            throw ProvisioningException.FromPowerShellFailure(result.StandardError);
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            throw new InvalidDataException("Microsoft setup returned unexpected diagnostic output.");
        }

        var lines = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 1 || !lines[0].StartsWith(ResultPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Microsoft setup did not return exactly one structured result.");
        }

        var json = lines[0][ResultPrefix.Length..];
        if (Encoding.UTF8.GetByteCount(json) > NativeMicrosoftSetupProtocol.MaximumMessageBytes)
        {
            throw new InvalidDataException("Microsoft setup returned an oversized result.");
        }

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
        };
        try
        {
            return JsonSerializer.Deserialize<T>(json, options)
                ?? throw new InvalidDataException("Microsoft setup returned an empty result.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Microsoft setup returned an invalid structured result.", exception);
        }
    }

    private sealed record EntraExecutorPayload(string ApplicationDisplayName, string PublicCertificateBase64);

    internal sealed record EntraImportPreflightResult(
        string PowerShellVersion,
        string GraphAuthenticationVersion,
        bool GraphAuthenticationPathMatches,
        string GraphApplicationsVersion,
        bool GraphApplicationsPathMatches,
        string EntraAuthenticationVersion,
        bool EntraAuthenticationPathMatches,
        string EntraApplicationsVersion,
        bool EntraApplicationsPathMatches,
        bool ConnectMgGraphAvailable,
        bool ConnectEntraAvailable,
        bool GetMgApplicationAvailable,
        bool PSModulePathLocked,
        bool UnexpectedModuleDiscovery);

    private sealed record ExchangeExecutorPayload(Guid ClientId, Guid ServicePrincipalObjectId, string SenderMailbox);
}

internal sealed class ProvisioningException : Exception
{
    private const string EntraFailurePrefix = "RELAYBRIDGE_ENTRA_FAILURE:";
    private static readonly HashSet<string> EntraFailureCodes = new(StringComparer.Ordinal)
    {
        "EntraConnectionFailed",
        "EntraApplicationDiscoveryFailed",
        "EntraApplicationCreateFailed",
        "EntraServicePrincipalCreateFailed",
        "EntraCertificateCredentialFailed",
        "EntraApplicationVerificationFailed",
    };

    internal ProvisioningException(
        NativeSetupFailureCategory category,
        string safeCode,
        NativeSetupSafeFailureDetails? safeFailureDetails = null)
        : base("Microsoft setup did not complete.")
    {
        Category = category;
        SafeCode = safeCode;
        SafeFailureDetails = safeFailureDetails;
    }

    internal NativeSetupFailureCategory Category { get; }

    internal string SafeCode { get; }

    internal NativeSetupSafeFailureDetails? SafeFailureDetails { get; }

    internal static ProvisioningException FromPowerShellFailure(string standardError)
    {
        var bounded = standardError.Length > 8192 ? standardError[..8192] : standardError;
        var entraFailure = ParseEntraFailure(bounded);
        if (entraFailure is not null)
        {
            return entraFailure;
        }

        if (bounded.Contains("RELAYBRIDGE_CANCELLED", StringComparison.Ordinal))
        {
            return new ProvisioningException(NativeSetupFailureCategory.Cancelled, "UserCancelled");
        }

        if (bounded.Contains("RELAYBRIDGE_TOOL_INTEGRITY", StringComparison.Ordinal))
        {
            return new ProvisioningException(NativeSetupFailureCategory.ToolIntegrity, "ToolIntegrity");
        }

        if (bounded.Contains("RELAYBRIDGE_PERMISSION", StringComparison.Ordinal))
        {
            return new ProvisioningException(NativeSetupFailureCategory.InsufficientPermission, "InsufficientPermission");
        }

        if (bounded.Contains("RELAYBRIDGE_EXCHANGE_ASSIGNMENT_CONFLICT", StringComparison.Ordinal))
        {
            return new ProvisioningException(NativeSetupFailureCategory.Conflict, "UnexpectedExchangeAssignments");
        }

        if (bounded.Contains("RELAYBRIDGE_CONFLICT", StringComparison.Ordinal))
        {
            return new ProvisioningException(NativeSetupFailureCategory.Conflict, "CloudObjectConflict");
        }

        if (bounded.Contains("RELAYBRIDGE_CA", StringComparison.Ordinal))
        {
            return new ProvisioningException(NativeSetupFailureCategory.ConditionalAccess, "ConditionalAccess");
        }

        return new ProvisioningException(NativeSetupFailureCategory.MicrosoftService, "MicrosoftProvisioningFailed");
    }

    private static ProvisioningException? ParseEntraFailure(string standardError)
    {
        var marker = standardError.IndexOf(EntraFailurePrefix, StringComparison.Ordinal);
        if (marker < 0)
        {
            return null;
        }

        var encoded = standardError[(marker + EntraFailurePrefix.Length)..].Trim();
        if (encoded.Length is <= 0 or > 2048 || encoded.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '+' and not '/' and not '='))
        {
            return GenericMicrosoftFailure();
        }

        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = false,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                AllowDuplicateProperties = false,
            };
            var payload = JsonSerializer.Deserialize<EntraSafeFailurePayload>(
                Convert.FromBase64String(encoded),
                options);
            if (payload is null || !EntraFailureCodes.Contains(payload.Code))
            {
                return GenericMicrosoftFailure();
            }

            return new ProvisioningException(
                NativeSetupFailureCategory.MicrosoftService,
                payload.Code,
                new NativeSetupSafeFailureDetails(
                    SafeOrNull(payload.ExceptionType, 160),
                    SafeOrNull(payload.FullyQualifiedErrorId, 256),
                    SafeOrNull(payload.PowerShellCategory, 80),
                    payload.HttpStatusCode is >= 100 and <= 599 ? payload.HttpStatusCode : null));
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return GenericMicrosoftFailure();
        }
    }

    private static ProvisioningException GenericMicrosoftFailure() =>
        new(NativeSetupFailureCategory.MicrosoftService, "MicrosoftProvisioningFailed");

    private static string? SafeOrNull(string? value, int maximumLength) =>
        value is not null && value.Length <= maximumLength && !LooksSecretShaped(value) &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or ',' or ' ' or '+' or '`' or '-')
            ? value
            : null;

    private static bool LooksSecretShaped(string value) =>
        value.Contains("Bearer ", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("access_token", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("refresh_token", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("client_secret", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("password=", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("authorization:", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("eyJ", StringComparison.Ordinal) && value.Count(character => character == '.') >= 2 ||
        System.Text.RegularExpressions.Regex.IsMatch(
            value,
            @"(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b");

    private sealed record EntraSafeFailurePayload(
        string Code,
        string? ExceptionType,
        string? FullyQualifiedErrorId,
        string? PowerShellCategory,
        int? HttpStatusCode);
}
