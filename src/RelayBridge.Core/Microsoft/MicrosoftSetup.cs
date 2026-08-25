// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using System.Text;

namespace RelayBridge.Core.Microsoft;

public enum MicrosoftSetupMode
{
    NewApplication,
    ExistingApplication,
}

public enum MicrosoftSetupStep
{
    Welcome,
    Certificate,
    MicrosoftApplication,
    ExchangePermission,
    VerifyIdentity,
    VerifyExchange,
    TestMessage,
    Complete,
}

public enum MicrosoftSetupCandidateLifecycle
{
    Active,
    Cancelled,
    Activated,
}

public sealed record MicrosoftSetupState(
    MicrosoftSetupStep Step,
    MicrosoftSetupMode Mode,
    MicrosoftCertificateReference? Certificate,
    Guid? TenantId,
    Guid? ClientId,
    Guid? ServicePrincipalObjectId,
    string? SenderMailbox,
    bool EntraResultValidated,
    bool ExchangeResultValidated,
    bool IdentityValidated,
    bool ExchangeValidated,
    bool TestMessageAccepted,
    DateTimeOffset UpdatedUtc,
    Guid? ActivationId = null,
    long Revision = 0,
    MicrosoftSetupCandidateLifecycle Lifecycle = MicrosoftSetupCandidateLifecycle.Active)
{
    public static MicrosoftSetupState Fresh(DateTimeOffset nowUtc) => new(
        MicrosoftSetupStep.Welcome,
        MicrosoftSetupMode.NewApplication,
        null,
        null,
        null,
        null,
        null,
        false,
        false,
        false,
        false,
        false,
        nowUtc,
        Guid.NewGuid(),
        0);
}

public sealed record NativeMicrosoftCandidateIdentity(
    Guid ActivationId,
    long Revision,
    string ConfigurationFingerprint,
    string SenderMailbox,
    MicrosoftSetupMode Mode,
    MicrosoftSetupState CapturedState);

public sealed record NativeMicrosoftActivationEvidence(
    Guid ActivationId,
    string CandidateFingerprint,
    string ConfigurationFingerprint,
    string SenderMailbox,
    bool IdentityVerified,
    bool FinalSmtpAcceptanceReceived);

public enum MicrosoftSetupCancellationOutcome
{
    Cancelled,
    AlreadyCancelled,
    AlreadyActivated,
    Replaced,
    Changed,
}

public sealed record MicrosoftSetupCancellationResult(
    MicrosoftSetupCancellationOutcome Outcome,
    MicrosoftSetupState? State);

public static class MicrosoftSetupCandidateFingerprint
{
    public static string Create(MicrosoftSetupState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.ActivationId is null || state.ActivationId == Guid.Empty)
        {
            throw new InvalidOperationException("The Microsoft setup candidate has no activation identity.");
        }

        var canonical = string.Join(
            '\n',
            state.ActivationId.Value.ToString("D"),
            state.Mode.ToString(),
            state.Certificate?.Thumbprint ?? string.Empty,
            state.Certificate?.StoreName ?? string.Empty,
            state.Certificate?.StoreLocation.ToString() ?? string.Empty,
            state.TenantId?.ToString("D") ?? string.Empty,
            state.ClientId?.ToString("D") ?? string.Empty,
            state.ServicePrincipalObjectId?.ToString("D") ?? string.Empty,
            state.SenderMailbox?.Trim().ToLowerInvariant() ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public sealed record EntraSetupResult(
    Guid TenantId,
    Guid ClientId,
    Guid ServicePrincipalObjectId,
    int ApiPermissionEntryCount);

public sealed record ExchangeSetupResult(
    bool ServicePrincipalConfigured,
    bool ScopeConfigured,
    string Role,
    bool SenderInScope);

public sealed record MicrosoftSetupOperationResult(
    bool Succeeded,
    string Message,
    MicrosoftSetupState State,
    string? TechnicalCode = null,
    string? CorrelationId = null);
