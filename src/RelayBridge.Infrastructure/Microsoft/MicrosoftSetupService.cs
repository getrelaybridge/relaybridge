// SPDX-License-Identifier: MPL-2.0

using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelayBridge.Core.Microsoft;
using RelayBridge.Core.Queue;
using RelayBridge.Infrastructure.Queue;
using RelayBridge.Infrastructure.Storage;

namespace RelayBridge.Infrastructure.Microsoft;

public static class MicrosoftSetupValidation
{
    public const int MaximumSetupResultBytes = 4096;

    public static string ValidateMailbox(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.Length > 254 || normalized.Any(character => character > 0x7f) ||
            normalized.IndexOfAny(['\r', '\n', '\0']) >= 0 ||
            !MailAddress.TryCreate(normalized, out var parsed) ||
            !string.Equals(parsed.Address, normalized, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Enter one ASCII mailbox address such as scanner@example.com.",
                nameof(value));
        }

        return parsed.Address.ToLowerInvariant();
    }
}

public sealed class MicrosoftSetupService
{
    private static readonly JsonSerializerOptions SetupJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    private readonly RelayDatabase _database;
    private readonly MicrosoftCertificateService _certificates;
    private readonly MicrosoftTokenProvider _tokens;
    private readonly MicrosoftAuthenticationTester _identityTester;
    private readonly ExchangeDeliveryTester _exchangeTester;
    private readonly QueueDeliveryActivation _queueDeliveryActivation;
    private readonly TimeProvider _timeProvider;
    private readonly MicrosoftSetupPersistenceHooks? _persistenceHooks;
    private readonly IReadOnlyList<TimeSpan> _smtpPropagationRetryDelays;

    public MicrosoftSetupService(
        RelayDatabase database,
        MicrosoftCertificateService certificates,
        MicrosoftTokenProvider tokens,
        MicrosoftAuthenticationTester identityTester,
        ExchangeDeliveryTester exchangeTester,
        QueueDeliveryActivation queueDeliveryActivation,
        TimeProvider timeProvider)
        : this(
            database,
            certificates,
            tokens,
            identityTester,
            exchangeTester,
            queueDeliveryActivation,
            timeProvider,
            persistenceHooks: null,
            smtpPropagationRetryDelays: null)
    {
    }

    internal MicrosoftSetupService(
        RelayDatabase database,
        MicrosoftCertificateService certificates,
        MicrosoftTokenProvider tokens,
        MicrosoftAuthenticationTester identityTester,
        ExchangeDeliveryTester exchangeTester,
        QueueDeliveryActivation queueDeliveryActivation,
        TimeProvider timeProvider,
        MicrosoftSetupPersistenceHooks? persistenceHooks,
        IReadOnlyList<TimeSpan>? smtpPropagationRetryDelays = null)
    {
        _database = database;
        _certificates = certificates;
        _tokens = tokens;
        _identityTester = identityTester;
        _exchangeTester = exchangeTester;
        _queueDeliveryActivation = queueDeliveryActivation;
        _timeProvider = timeProvider;
        _persistenceHooks = persistenceHooks;
        _smtpPropagationRetryDelays = smtpPropagationRetryDelays ??
            [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];
    }

    public MicrosoftSetupState GetState(CancellationToken cancellationToken = default)
    {
        return _database.GetMicrosoftSetupState(cancellationToken) ??
            MicrosoftSetupState.Fresh(_timeProvider.GetUtcNow());
    }

    public NativeMicrosoftCandidateIdentity CaptureNativeCandidate(
        CancellationToken cancellationToken = default)
    {
        var state = GetState(cancellationToken);
        if (state.Lifecycle != MicrosoftSetupCandidateLifecycle.Active ||
            state.Mode != MicrosoftSetupMode.NewApplication || state.Certificate is null ||
            string.IsNullOrWhiteSpace(state.SenderMailbox) || state.ActivationId is null ||
            state.ActivationId == Guid.Empty)
        {
            throw new InvalidOperationException("The native Microsoft setup candidate is incomplete.");
        }

        return CreateNativeCandidateIdentity(state);
    }

    internal static EntraSetupResult? GetReusableNativeEntraResult(MicrosoftSetupState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Lifecycle != MicrosoftSetupCandidateLifecycle.Active ||
            state.Mode != MicrosoftSetupMode.NewApplication ||
            !state.EntraResultValidated ||
            state.Step < MicrosoftSetupStep.ExchangePermission ||
            state.ActivationId is null || state.ActivationId == Guid.Empty ||
            state.Certificate is null ||
            state.TenantId is null || state.TenantId == Guid.Empty ||
            state.ClientId is null || state.ClientId == Guid.Empty ||
            state.ServicePrincipalObjectId is null || state.ServicePrincipalObjectId == Guid.Empty ||
            string.IsNullOrWhiteSpace(state.SenderMailbox))
        {
            return null;
        }

        _ = MicrosoftSetupCandidateFingerprint.Create(state);
        _ = MicrosoftSetupValidation.ValidateMailbox(state.SenderMailbox);
        return new EntraSetupResult(
            state.TenantId.Value,
            state.ClientId.Value,
            state.ServicePrincipalObjectId.Value,
            ApiPermissionEntryCount: 0);
    }

    public NativeMicrosoftCandidateIdentity ApplyNativeEntraResult(
        NativeMicrosoftCandidateIdentity expected,
        EntraSetupResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(result);
        if (result.TenantId == Guid.Empty || result.ClientId == Guid.Empty ||
            result.ServicePrincipalObjectId == Guid.Empty || result.ApiPermissionEntryCount != 0)
        {
            throw new InvalidOperationException(
                "The Microsoft application result is invalid or contains unexpected API permissions.");
        }

        var replacement = expected.CapturedState with
        {
            TenantId = result.TenantId,
            ClientId = result.ClientId,
            ServicePrincipalObjectId = result.ServicePrincipalObjectId,
            EntraResultValidated = true,
            Step = MicrosoftSetupStep.ExchangePermission,
            ExchangeResultValidated = false,
            IdentityValidated = false,
            ExchangeValidated = false,
            TestMessageAccepted = false,
            UpdatedUtc = _timeProvider.GetUtcNow(),
        };
        _persistenceHooks?.BeforeEntraResultPersistence?.Invoke();
        var saved = _database.SaveMicrosoftSetupStateConditional(expected, replacement, cancellationToken);
        return AdvanceNativeCandidate(expected, saved);
    }

    public NativeMicrosoftCandidateIdentity ApplyNativeExchangeResult(
        NativeMicrosoftCandidateIdentity expected,
        ExchangeSetupResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(result);
        if (!result.ServicePrincipalConfigured || !result.ScopeConfigured || !result.SenderInScope ||
            !string.Equals(result.Role, "Application SMTP.SendAsApp", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Exchange did not confirm the exact scoped SMTP role for this candidate.");
        }

        var replacement = expected.CapturedState with
        {
            ExchangeResultValidated = true,
            Step = MicrosoftSetupStep.VerifyIdentity,
            IdentityValidated = false,
            ExchangeValidated = false,
            TestMessageAccepted = false,
            UpdatedUtc = _timeProvider.GetUtcNow(),
        };
        _persistenceHooks?.BeforeExchangeResultPersistence?.Invoke();
        var saved = _database.SaveMicrosoftSetupStateConditional(expected, replacement, cancellationToken);
        return AdvanceNativeCandidate(expected, saved);
    }

    public async Task<MicrosoftSetupOperationResult> VerifyAndActivateNativeAsync(
        NativeMicrosoftCandidateIdentity expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var state = expected.CapturedState;
        if (!state.EntraResultValidated || !state.ExchangeResultValidated)
        {
            return Failure(state, "Complete both Microsoft administrator stages before verification.");
        }

        MicrosoftIdentityConfiguration configuration;
        try
        {
            configuration = CreateCandidateConfiguration(state);
            var certificate = _certificates.Validate(configuration.Certificate, cancellationToken);
            if (!certificate.IsUsable)
            {
                return Failure(state, certificate.Message, certificate.Status.ToString());
            }

            await _tokens.GetExchangeTokenAsync(configuration, cancellationToken).ConfigureAwait(false);
        }
        catch (MicrosoftIdentityException exception)
        {
            return Failure(state, exception.Message, exception.TechnicalCode, exception.CorrelationId);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Failure(state, exception.Message);
        }

        var identityState = state with
        {
            IdentityValidated = true,
            Step = MicrosoftSetupStep.VerifyExchange,
            UpdatedUtc = _timeProvider.GetUtcNow(),
        };
        identityState = _database.SaveMicrosoftSetupStateConditional(expected, identityState, cancellationToken);
        expected = AdvanceNativeCandidate(expected, identityState);

        var finalFingerprint = MicrosoftConfigurationFingerprint.Create(configuration, expected.SenderMailbox);
        var runtimeEvidenceKey = MicrosoftConfigurationFingerprint.CreateRuntimeEvidenceKey(
            expected.ActivationId,
            finalFingerprint);
        var verification = await VerifySetupSmtpWithBoundedPropagationRetryAsync(
            token => _exchangeTester.TestAsync(
                expected.SenderMailbox,
                expected.SenderMailbox,
                new CandidateTokenProvider(_tokens, configuration),
                configuration,
                runtimeEvidenceKey,
                token),
            allowPropagationRetry: true,
            cancellationToken).ConfigureAwait(false);
        var result = verification.Result;
        if (result.Outcome != DeliveryOutcome.Success)
        {
            return Failure(
                identityState,
                PlainExchangeMessage(result.ErrorCategory, verification.AttemptCount > 1),
                ExchangeTechnicalCode(result),
                result.CorrelationId.ToString("D"));
        }

        cancellationToken.ThrowIfCancellationRequested();
        _persistenceHooks?.BeforeActivationPersistence?.Invoke();
        var evidence = new NativeMicrosoftActivationEvidence(
            expected.ActivationId,
            expected.ConfigurationFingerprint,
            finalFingerprint,
            expected.SenderMailbox,
            IdentityVerified: true,
            FinalSmtpAcceptanceReceived: true);
        var completed = _database.ActivateNativeMicrosoftConfiguration(
            configuration,
            expected,
            evidence,
            _timeProvider.GetUtcNow(),
            cancellationToken);
        _queueDeliveryActivation.Activate();
        _persistenceHooks?.AfterActivationCommit?.Invoke();
        return new MicrosoftSetupOperationResult(
            true,
            "Exchange accepted the verification message and the unchanged candidate is now active.",
            completed);
    }

    public MicrosoftSetupState Begin(MicrosoftSetupMode mode, CancellationToken cancellationToken = default)
    {
        var state = MicrosoftSetupState.Fresh(_timeProvider.GetUtcNow()) with
        {
            Step = MicrosoftSetupStep.Certificate,
            Mode = mode,
        };
        _database.SaveMicrosoftSetupState(state, cancellationToken);
        return state;
    }

    public MicrosoftSetupState BeginRepair(CancellationToken cancellationToken = default)
    {
        var active = _database.GetMicrosoftIdentityConfiguration(cancellationToken);
        if (active is null)
        {
            return Begin(MicrosoftSetupMode.NewApplication, cancellationToken);
        }

        var activeConfiguration = _database.GetActiveMicrosoftConfiguration(cancellationToken);
        var completedState = _database.GetMicrosoftSetupState(cancellationToken);
        var certificate = _certificates.Validate(active.Certificate, cancellationToken);
        var state = MicrosoftSetupState.Fresh(_timeProvider.GetUtcNow()) with
        {
            Step = certificate.IsUsable
                ? MicrosoftSetupStep.MicrosoftApplication
                : MicrosoftSetupStep.Certificate,
            Mode = MicrosoftSetupMode.ExistingApplication,
            Certificate = active.Certificate,
            TenantId = active.TenantId,
            ClientId = active.ClientId,
            ServicePrincipalObjectId = IsSameActiveConfiguration(completedState, activeConfiguration)
                ? completedState!.ServicePrincipalObjectId
                : null,
            SenderMailbox = _database.GetMicrosoftAuthorizedSender(cancellationToken),
            ActivationId = activeConfiguration?.ActivationId,
        };
        _database.SaveMicrosoftSetupState(state, cancellationToken);
        return state;
    }

    public async Task<MicrosoftSetupOperationResult> VerifyActiveConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        var state = GetState(cancellationToken);
        var active = _database.GetActiveMicrosoftConfiguration(cancellationToken);
        if (active?.AuthorizedSender is null)
        {
            return Failure(state, "Microsoft 365 has no active RelayBridge configuration to verify.");
        }

        var identity = await _identityTester.TestAsync(cancellationToken).ConfigureAwait(false);
        if (!identity.Succeeded)
        {
            return Failure(
                state,
                PlainIdentityVerificationMessage(identity.ErrorCategory),
                identity.TechnicalCode ?? identity.ErrorCategory?.ToString(),
                identity.CorrelationId);
        }

        var current = _database.GetActiveMicrosoftConfiguration(cancellationToken);
        if (current?.AuthorizedSender is null ||
            !string.Equals(current.Fingerprint, active.Fingerprint, StringComparison.Ordinal))
        {
            return Failure(
                state,
                "The active Microsoft configuration changed during verification. Review the current configuration and try again.");
        }

        ExchangeDeliveryDiagnosticResult exchange;
        try
        {
            exchange = await _exchangeTester.VerifyAuthenticationAsync(
                current.AuthorizedSender,
                current.Identity,
                current.Fingerprint,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(state, "Microsoft connection verification was cancelled.", "Cancelled");
        }

        if (exchange.Outcome != DeliveryOutcome.Success)
        {
            return Failure(
                state,
                PlainConnectionVerificationMessage(exchange.ErrorCategory),
                ExchangeTechnicalCode(exchange),
                exchange.CorrelationId.ToString("D"));
        }

        return new MicrosoftSetupOperationResult(
            true,
            "RelayBridge acquired an Exchange token and completed STARTTLS and XOAUTH2 authentication for the active sender. No email was sent.",
            state);
    }

    public MicrosoftSetupState SelectCertificate(
        MicrosoftCertificateReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var validation = _certificates.Validate(reference, cancellationToken);
        if (!validation.IsUsable)
        {
            throw new InvalidOperationException(validation.Message);
        }

        var current = GetState(cancellationToken);
        var state = current with
        {
            Certificate = reference,
            Step = MicrosoftSetupStep.MicrosoftApplication,
            EntraResultValidated = false,
            ExchangeResultValidated = false,
            IdentityValidated = false,
            ExchangeValidated = false,
            TestMessageAccepted = false,
            UpdatedUtc = _timeProvider.GetUtcNow(),
            Revision = checked(current.Revision + 1),
        };
        _database.SaveMicrosoftSetupState(state, cancellationToken);
        return state;
    }

    public MicrosoftSetupState GenerateCertificate(
        CertificateStoreTarget storeLocation,
        CancellationToken cancellationToken = default)
    {
        var certificate = _certificates.GenerateSelfSignedCertificate(storeLocation, cancellationToken);
        return SelectCertificate(
            MicrosoftCertificateReference.Create(certificate.Thumbprint, storeLocation),
            cancellationToken);
    }

    public MicrosoftSetupState SetExistingApplicationIdentifiers(
        string tenantId,
        string clientId,
        string servicePrincipalObjectId,
        CancellationToken cancellationToken = default)
    {
        var state = GetState(cancellationToken);
        if (state.Mode != MicrosoftSetupMode.ExistingApplication)
        {
            throw new InvalidOperationException("Manual identifiers are available only for the existing-application path.");
        }

        state = state with
        {
            TenantId = ParseRequiredGuid(tenantId, "Tenant ID"),
            ClientId = ParseRequiredGuid(clientId, "Application ID"),
            ServicePrincipalObjectId = ParseRequiredGuid(servicePrincipalObjectId, "Service principal object ID"),
            EntraResultValidated = false,
            ExchangeResultValidated = false,
            IdentityValidated = false,
            ExchangeValidated = false,
            TestMessageAccepted = false,
            UpdatedUtc = _timeProvider.GetUtcNow(),
            Revision = checked(state.Revision + 1),
        };
        _database.SaveMicrosoftSetupState(state, cancellationToken);
        return state;
    }

    public MicrosoftSetupState ApplyEntraResult(string json, CancellationToken cancellationToken = default)
    {
        var result = ParseResult<EntraSetupJson>(json);
        if (result.ApiPermissionEntryCount != 0)
        {
            throw new InvalidOperationException(
                "The Microsoft application has API permission entries. RelayBridge requires zero entries for scoped Exchange App RBAC.");
        }

        var state = GetState(cancellationToken);
        if (state.Certificate is null)
        {
            throw new InvalidOperationException("Choose the authentication certificate first.");
        }

        state = state with
        {
            TenantId = ParseRequiredGuid(result.TenantId, "Tenant ID"),
            ClientId = ParseRequiredGuid(result.ClientId, "Application ID"),
            ServicePrincipalObjectId = ParseRequiredGuid(result.ServicePrincipalObjectId, "Service principal object ID"),
            EntraResultValidated = true,
            Step = MicrosoftSetupStep.ExchangePermission,
            ExchangeResultValidated = false,
            IdentityValidated = false,
            ExchangeValidated = false,
            TestMessageAccepted = false,
            UpdatedUtc = _timeProvider.GetUtcNow(),
            Revision = checked(state.Revision + 1),
        };
        _database.SaveMicrosoftSetupState(state, cancellationToken);
        return state;
    }

    public MicrosoftSetupState SetSenderMailbox(string senderMailbox, CancellationToken cancellationToken = default)
    {
        var sender = MicrosoftSetupValidation.ValidateMailbox(senderMailbox);
        var current = GetState(cancellationToken);
        var state = current with
        {
            SenderMailbox = sender,
            ExchangeResultValidated = false,
            IdentityValidated = false,
            ExchangeValidated = false,
            TestMessageAccepted = false,
            UpdatedUtc = _timeProvider.GetUtcNow(),
            Revision = checked(current.Revision + 1),
        };
        _database.SaveMicrosoftSetupState(state, cancellationToken);
        return state;
    }

    public MicrosoftSetupState ApplyExchangeResult(string json, CancellationToken cancellationToken = default)
    {
        var result = ParseResult<ExchangeSetupJson>(json);
        if (!result.ServicePrincipalConfigured || !result.ScopeConfigured || !result.SenderInScope ||
            !string.Equals(result.Role, "Application SMTP.SendAsApp", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Exchange did not confirm the scoped Application SMTP.SendAsApp assignment for this sender.");
        }

        var state = GetState(cancellationToken);
        if (string.IsNullOrWhiteSpace(state.SenderMailbox))
        {
            throw new InvalidOperationException("Choose the authorized sender mailbox first.");
        }

        state = state with
        {
            ExchangeResultValidated = true,
            Step = MicrosoftSetupStep.VerifyIdentity,
            IdentityValidated = false,
            ExchangeValidated = false,
            TestMessageAccepted = false,
            UpdatedUtc = _timeProvider.GetUtcNow(),
            Revision = checked(state.Revision + 1),
        };
        _database.SaveMicrosoftSetupState(state, cancellationToken);
        return state;
    }

    public async Task<MicrosoftSetupOperationResult> VerifyIdentityAsync(
        CancellationToken cancellationToken = default)
    {
        var state = GetState(cancellationToken);
        MicrosoftIdentityConfiguration configuration;
        try
        {
            configuration = CreateCandidateConfiguration(state);
            var certificate = _certificates.Validate(configuration.Certificate, cancellationToken);
            if (!certificate.IsUsable)
            {
                return Failure(state, certificate.Message, certificate.Status.ToString());
            }

            await _tokens.GetExchangeTokenAsync(configuration, cancellationToken).ConfigureAwait(false);
        }
        catch (MicrosoftIdentityException exception)
        {
            return Failure(state, exception.Message, exception.TechnicalCode, exception.CorrelationId);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Failure(state, exception.Message);
        }

        state = state with
        {
            IdentityValidated = true,
            Step = MicrosoftSetupStep.VerifyExchange,
            ExchangeValidated = false,
            TestMessageAccepted = false,
            UpdatedUtc = _timeProvider.GetUtcNow(),
            Revision = checked(state.Revision + 1),
        };
        _database.SaveMicrosoftSetupState(state, cancellationToken);
        return new MicrosoftSetupOperationResult(
            true,
            "RelayBridge authenticated as the configured Microsoft application and acquired an Exchange token.",
            state);
    }

    public async Task<MicrosoftSetupOperationResult> VerifyExchangeAsync(
        CancellationToken cancellationToken = default)
    {
        var state = GetState(cancellationToken);
        if (!state.IdentityValidated)
        {
            return Failure(state, "Verify Microsoft identity before testing Exchange SMTP.");
        }

        NativeMicrosoftCandidateIdentity expected;
        try
        {
            expected = CreateStep5VerificationCandidate(state);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Failure(state, exception.Message);
        }

        return await VerifyExchangeAsync(
            expected,
            allowBriefPropagationRetry: state.ExchangeResultValidated,
            cancellationToken).ConfigureAwait(false);
    }

    public NativeMicrosoftCandidateIdentity CaptureStep5VerificationCandidate(
        CancellationToken cancellationToken = default)
    {
        return CreateStep5VerificationCandidate(GetState(cancellationToken));
    }

    public NativeMicrosoftCandidateIdentity CaptureStep5VerificationCandidate(
        MicrosoftSetupState verifiedState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verifiedState);
        var expected = CreateStep5VerificationCandidate(verifiedState);
        if (!IsStep5VerificationCandidateCurrent(expected, cancellationToken))
        {
            throw new MicrosoftSetupConcurrencyException();
        }

        return expected;
    }

    public bool IsStep5VerificationCandidateCurrent(
        NativeMicrosoftCandidateIdentity expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        try
        {
            var current = CreateStep5VerificationCandidate(GetState(cancellationToken));
            return current.ActivationId == expected.ActivationId &&
                current.Revision == expected.Revision &&
                current.Mode == expected.Mode &&
                string.Equals(
                    current.ConfigurationFingerprint,
                    expected.ConfigurationFingerprint,
                    StringComparison.Ordinal) &&
                string.Equals(current.SenderMailbox, expected.SenderMailbox, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    public Task<MicrosoftSetupOperationResult> VerifyExchangeAutomaticallyAsync(
        NativeMicrosoftCandidateIdentity expected,
        CancellationToken cancellationToken = default)
    {
        return VerifyExchangeAsync(expected, allowBriefPropagationRetry: false, cancellationToken);
    }

    private async Task<MicrosoftSetupOperationResult> VerifyExchangeAsync(
        NativeMicrosoftCandidateIdentity expected,
        bool allowBriefPropagationRetry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var state = GetState(cancellationToken);
        if (!IsStep5VerificationCandidateCurrent(expected, cancellationToken))
        {
            return Failure(
                state,
                "The Microsoft setup candidate changed. Review the current setup state before verifying again.");
        }

        MicrosoftIdentityConfiguration configuration;
        try
        {
            configuration = CreateCandidateConfiguration(state);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Failure(state, exception.Message);
        }

        var candidateTokenProvider = new CandidateTokenProvider(_tokens, configuration);
        var configurationFingerprint = MicrosoftConfigurationFingerprint.CreateRuntimeEvidenceKey(
            expected.ActivationId,
            MicrosoftConfigurationFingerprint.Create(
            configuration,
            state.SenderMailbox));
        var verification = await VerifySetupSmtpWithBoundedPropagationRetryAsync(
            token => _exchangeTester.TestAsync(
                state.SenderMailbox!,
                state.SenderMailbox!,
                candidateTokenProvider,
                configuration,
                configurationFingerprint,
                token),
            allowBriefPropagationRetry,
            cancellationToken).ConfigureAwait(false);
        var result = verification.Result;
        if (result.Outcome != DeliveryOutcome.Success)
        {
            return Failure(
                state,
                PlainExchangeMessage(result.ErrorCategory, verification.AttemptCount > 1),
                ExchangeTechnicalCode(result),
                result.CorrelationId.ToString("D"),
                IsPossibleExchangeAuthorizationPropagation(result));
        }

        state = state with
        {
            ExchangeValidated = true,
            Step = MicrosoftSetupStep.TestMessage,
            TestMessageAccepted = false,
            UpdatedUtc = _timeProvider.GetUtcNow(),
            Revision = checked(state.Revision + 1),
        };
        try
        {
            _persistenceHooks?.BeforeActivationPersistence?.Invoke();
            _database.ActivateMicrosoftConfigurationConditional(
                configuration,
                state.SenderMailbox!,
                state,
                expected,
                cancellationToken);
        }
        catch (MicrosoftSetupConcurrencyException exception)
        {
            return Failure(GetState(cancellationToken), exception.Message);
        }

        _queueDeliveryActivation.Activate();
        return new MicrosoftSetupOperationResult(
            true,
            verification.AttemptCount > 1
                ? "Exchange accepted a verification message after a brief bounded authorization retry. The candidate configuration is now active."
                : "Exchange accepted a verification message from the scoped sender. The candidate configuration is now active.",
            state);
    }

    public async Task<MicrosoftSetupOperationResult> SendTestMessageAsync(
        string recipient,
        CancellationToken cancellationToken = default)
    {
        var state = GetState(cancellationToken);
        if (!state.ExchangeValidated || string.IsNullOrWhiteSpace(state.SenderMailbox))
        {
            return Failure(state, "Verify Exchange SMTP and sender authorization first.");
        }

        string normalizedRecipient;
        try
        {
            normalizedRecipient = MicrosoftSetupValidation.ValidateMailbox(recipient);
        }
        catch (ArgumentException exception)
        {
            return Failure(state, exception.Message);
        }

        var result = await _exchangeTester.SendSetupTestAsync(
            state.SenderMailbox,
            normalizedRecipient,
            cancellationToken).ConfigureAwait(false);
        if (result.Outcome != DeliveryOutcome.Success)
        {
            return Failure(
                state,
                PlainExchangeMessage(result.ErrorCategory),
                ExchangeTechnicalCode(result),
                result.CorrelationId.ToString("D"));
        }

        state = state with
        {
            TestMessageAccepted = true,
            Step = MicrosoftSetupStep.Complete,
            UpdatedUtc = _timeProvider.GetUtcNow(),
            Revision = checked(state.Revision + 1),
        };
        _database.SaveMicrosoftSetupState(state, cancellationToken);
        return new MicrosoftSetupOperationResult(
            true,
            "Microsoft 365 accepted the RelayBridge test message. Check the recipient inbox.",
            state);
    }

    public MicrosoftSetupState CompleteNativeSetupAfterVerifiedExchange(
        CancellationToken cancellationToken = default)
    {
        var state = GetState(cancellationToken);
        var active = _database.GetActiveMicrosoftConfiguration(cancellationToken);
        if (!state.ExchangeValidated || state.Step != MicrosoftSetupStep.TestMessage ||
            state.ActivationId is null || active is null ||
            active.ActivationId != state.ActivationId.Value)
        {
            throw new InvalidOperationException(
                "Native Microsoft setup cannot complete until the verified candidate is active.");
        }

        state = state with
        {
            Step = MicrosoftSetupStep.Complete,
            TestMessageAccepted = true,
            UpdatedUtc = _timeProvider.GetUtcNow(),
            Revision = checked(state.Revision + 1),
        };
        _database.SaveMicrosoftSetupState(state, cancellationToken);
        return state;
    }

    public MicrosoftSetupState Back(CancellationToken cancellationToken = default)
    {
        var state = GetState(cancellationToken);
        var previous = state.Step switch
        {
            MicrosoftSetupStep.Certificate => MicrosoftSetupStep.Welcome,
            MicrosoftSetupStep.MicrosoftApplication => MicrosoftSetupStep.Certificate,
            MicrosoftSetupStep.ExchangePermission => MicrosoftSetupStep.MicrosoftApplication,
            MicrosoftSetupStep.VerifyIdentity => MicrosoftSetupStep.ExchangePermission,
            MicrosoftSetupStep.VerifyExchange => MicrosoftSetupStep.VerifyIdentity,
            MicrosoftSetupStep.TestMessage => MicrosoftSetupStep.VerifyExchange,
            MicrosoftSetupStep.Complete => MicrosoftSetupStep.TestMessage,
            _ => MicrosoftSetupStep.Welcome,
        };

        state = state with
        {
            Step = previous,
            IdentityValidated = previous > MicrosoftSetupStep.VerifyIdentity && state.IdentityValidated,
            ExchangeValidated = previous > MicrosoftSetupStep.VerifyExchange && state.ExchangeValidated,
            TestMessageAccepted = previous == MicrosoftSetupStep.Complete && state.TestMessageAccepted,
            UpdatedUtc = _timeProvider.GetUtcNow(),
            Revision = checked(state.Revision + 1),
        };
        _database.SaveMicrosoftSetupState(state, cancellationToken);
        return state;
    }

    public MicrosoftSetupState Cancel(CancellationToken cancellationToken = default)
    {
        var state = _database.GetMicrosoftSetupState(cancellationToken);
        if (state?.ActivationId is null || state.ActivationId == Guid.Empty)
        {
            return MicrosoftSetupState.Fresh(_timeProvider.GetUtcNow());
        }

        return CancelCandidate(state.ActivationId.Value, state.Revision, cancellationToken).State ?? state;
    }

    public MicrosoftSetupCancellationResult CancelNativeCandidate(
        NativeMicrosoftCandidateIdentity expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return CancelCandidate(expected.ActivationId, expected.Revision, cancellationToken);
    }

    private MicrosoftSetupCancellationResult CancelCandidate(
        Guid activationId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var result = _database.CancelMicrosoftSetupCandidate(
                activationId,
                expectedRevision,
                _timeProvider.GetUtcNow(),
                cancellationToken);
            if (result.Outcome != MicrosoftSetupCancellationOutcome.Changed || result.State is null)
            {
                return result;
            }

            expectedRevision = result.State.Revision;
        }

        throw new MicrosoftSetupConcurrencyException();
    }

    private static MicrosoftIdentityConfiguration CreateCandidateConfiguration(MicrosoftSetupState state)
    {
        if (state.Certificate is null || state.TenantId is null || state.ClientId is null ||
            string.IsNullOrWhiteSpace(state.SenderMailbox))
        {
            throw new InvalidOperationException("The candidate Microsoft configuration is incomplete.");
        }

        MicrosoftSetupValidation.ValidateMailbox(state.SenderMailbox);
        return MicrosoftIdentityConfiguration.Create(
            state.TenantId.Value,
            state.ClientId.Value,
            state.Certificate);
    }

    private static NativeMicrosoftCandidateIdentity CreateStep5VerificationCandidate(
        MicrosoftSetupState state)
    {
        if (state.Lifecycle != MicrosoftSetupCandidateLifecycle.Active ||
            state.Step != MicrosoftSetupStep.VerifyExchange ||
            !state.EntraResultValidated || !state.ExchangeResultValidated ||
            !state.IdentityValidated || state.ExchangeValidated ||
            state.Certificate is null || state.TenantId is null || state.ClientId is null ||
            state.ServicePrincipalObjectId is null || string.IsNullOrWhiteSpace(state.SenderMailbox) ||
            state.ActivationId is null || state.ActivationId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "The candidate Microsoft configuration is not ready for Exchange verification.");
        }

        _ = CreateCandidateConfiguration(state);
        return CreateNativeCandidateIdentity(state);
    }

    private static bool IsSameActiveConfiguration(
        MicrosoftSetupState? completedState,
        ActiveMicrosoftConfiguration? activeConfiguration)
    {
        return completedState is not null &&
            activeConfiguration?.AuthorizedSender is not null &&
            completedState.Lifecycle == MicrosoftSetupCandidateLifecycle.Activated &&
            completedState.ActivationId == activeConfiguration.ActivationId &&
            completedState.TenantId == activeConfiguration.Identity.TenantId &&
            completedState.ClientId == activeConfiguration.Identity.ClientId &&
            completedState.Certificate is not null &&
            string.Equals(
                completedState.Certificate.Thumbprint,
                activeConfiguration.Identity.Certificate.Thumbprint,
                StringComparison.Ordinal) &&
            completedState.Certificate.StoreLocation == activeConfiguration.Identity.Certificate.StoreLocation &&
            string.Equals(
                completedState.SenderMailbox,
                activeConfiguration.AuthorizedSender,
                StringComparison.OrdinalIgnoreCase);
    }

    private static NativeMicrosoftCandidateIdentity CreateNativeCandidateIdentity(MicrosoftSetupState state)
    {
        return new NativeMicrosoftCandidateIdentity(
            state.ActivationId!.Value,
            state.Revision,
            MicrosoftSetupCandidateFingerprint.Create(state),
            state.SenderMailbox!,
            state.Mode,
            state);
    }

    private static NativeMicrosoftCandidateIdentity AdvanceNativeCandidate(
        NativeMicrosoftCandidateIdentity expected,
        MicrosoftSetupState saved)
    {
        var fingerprint = MicrosoftSetupCandidateFingerprint.Create(saved);
        if (saved.Lifecycle != MicrosoftSetupCandidateLifecycle.Active ||
            saved.ActivationId != expected.ActivationId || saved.Mode != expected.Mode ||
            !string.Equals(saved.SenderMailbox, expected.SenderMailbox, StringComparison.OrdinalIgnoreCase))
        {
            throw new MicrosoftSetupConcurrencyException();
        }

        return expected with
        {
            Revision = saved.Revision,
            ConfigurationFingerprint = fingerprint,
            CapturedState = saved,
        };
    }

    private static T ParseResult<T>(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (Encoding.UTF8.GetByteCount(json) > MicrosoftSetupValidation.MaximumSetupResultBytes)
        {
            throw new ArgumentException("The pasted setup result is larger than RelayBridge accepts.", nameof(json));
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, SetupJsonOptions)
                ?? throw new ArgumentException("The pasted setup result is empty.", nameof(json));
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "Paste only the JSON result printed by the RelayBridge setup script.",
                nameof(json),
                exception);
        }
    }

    private static Guid ParseRequiredGuid(string value, string fieldName)
    {
        if (!Guid.TryParse(value, out var parsed) || parsed == Guid.Empty)
        {
            throw new ArgumentException($"{fieldName} must be a non-empty GUID.");
        }

        return parsed;
    }

    private static MicrosoftSetupOperationResult Failure(
        MicrosoftSetupState state,
        string message,
        string? technicalCode = null,
        string? correlationId = null,
        bool automaticVerificationEligible = false)
    {
        return new MicrosoftSetupOperationResult(
            false,
            message,
            state,
            technicalCode,
            correlationId,
            automaticVerificationEligible);
    }

    internal async Task<SetupSmtpVerificationResult> VerifySetupSmtpWithBoundedPropagationRetryAsync(
        Func<CancellationToken, Task<ExchangeDeliveryDiagnosticResult>> attempt,
        bool allowPropagationRetry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var result = await attempt(cancellationToken).ConfigureAwait(false);
        var attemptCount = 1;
        if (!allowPropagationRetry)
        {
            return new SetupSmtpVerificationResult(result, attemptCount);
        }

        foreach (var delay in _smtpPropagationRetryDelays)
        {
            if (!IsPossibleExchangeAuthorizationPropagation(result))
            {
                break;
            }

            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            result = await attempt(cancellationToken).ConfigureAwait(false);
            attemptCount++;
        }

        return new SetupSmtpVerificationResult(result, attemptCount);
    }

    internal static bool IsPossibleExchangeAuthorizationPropagation(ExchangeDeliveryDiagnosticResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return string.Equals(
                result.ErrorCategory,
                ExchangeSmtpErrorCategories.Authentication,
                StringComparison.Ordinal) &&
            result.Checkpoints.AuthenticationResponseCode == 535;
    }

    private static string PlainExchangeMessage(string? category, bool retriedBriefly = false)
    {
        return category switch
        {
            "Authentication" =>
                retriedBriefly
                    ? "Exchange Online has not accepted the application authentication yet. Recent permission changes can take time to become effective, but propagation is not certain. RelayBridge retried briefly. If the problem persists, check tenant and mailbox SMTP AUTH settings."
                    : "Microsoft authentication succeeded earlier, but Exchange SMTP authentication is unavailable. If Microsoft setup was just completed, Exchange authorization changes may still be propagating; wait briefly and retry. If the problem persists, check tenant and mailbox SMTP AUTH settings.",
            "Authorization" or "SenderRejected" =>
                "RelayBridge connected to Exchange, but the sender mailbox is not authorized. Check the Exchange permission step.",
            "Tls" =>
                "RelayBridge could not establish a trusted secure connection to Exchange Online.",
            "Network" or "DNS" or "Timeout" =>
                "RelayBridge could not reach Exchange Online. Check DNS, outbound TCP port 587, and network connectivity.",
            _ => "Exchange Online did not accept the RelayBridge verification message.",
        };
    }

    private static string PlainIdentityVerificationMessage(MicrosoftIdentityErrorCategory? category)
    {
        return category switch
        {
            MicrosoftIdentityErrorCategory.CertificateMissing =>
                "The configured Microsoft authentication certificate is not available on this computer.",
            MicrosoftIdentityErrorCategory.CertificateExpired =>
                "The configured Microsoft authentication certificate has expired.",
            MicrosoftIdentityErrorCategory.PrivateKeyUnavailable =>
                "RelayBridge cannot use the configured Microsoft authentication certificate private key.",
            MicrosoftIdentityErrorCategory.CertificateInvalid or MicrosoftIdentityErrorCategory.InvalidConfiguration =>
                "The saved Microsoft identity configuration or certificate is not currently usable.",
            MicrosoftIdentityErrorCategory.NetworkFailure =>
                "RelayBridge could not reach Microsoft identity services. Check DNS and Internet connectivity.",
            MicrosoftIdentityErrorCategory.Cancelled =>
                "Microsoft connection verification was cancelled.",
            _ =>
                "Microsoft rejected or could not verify the saved RelayBridge application identity.",
        };
    }

    private static string PlainConnectionVerificationMessage(string? category)
    {
        return category switch
        {
            "Authentication" or "Authorization" =>
                "RelayBridge reached Exchange Online, but XOAUTH2 authentication for the active sender was denied. Check SMTP AUTH and the existing scoped Exchange authorization.",
            "TLS" =>
                "RelayBridge could not establish a trusted STARTTLS connection to Exchange Online.",
            "Network" or "DNS" or "Timeout" =>
                "RelayBridge could not reach Exchange Online. Check DNS, outbound TCP port 587, and network connectivity.",
            "Protocol" =>
                "Exchange Online did not complete the expected secure SMTP authentication sequence.",
            _ =>
                "RelayBridge could not verify the active Exchange SMTP connection.",
        };
    }

    private static string ExchangeTechnicalCode(ExchangeDeliveryDiagnosticResult result)
    {
        var checkpoints = result.Checkpoints;
        int? recipientResponseCode = checkpoints.RecipientResponseCodes.Count == 0
            ? null
            : checkpoints.RecipientResponseCodes[^1];
        var responseCode = checkpoints.FinalResponseCode ??
            checkpoints.DataResponseCode ??
            recipientResponseCode ??
            checkpoints.MailFromResponseCode ??
            checkpoints.AuthenticationResponseCode ??
            checkpoints.StartTlsResponseCode ??
            checkpoints.GreetingResponseCode;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.ErrorCategory))
        {
            parts.Add(result.ErrorCategory);
        }
        if (responseCode is not null and not 0)
        {
            parts.Add($"SMTP {responseCode.Value}");
        }
        if (!string.IsNullOrWhiteSpace(checkpoints.FinalResponseEnhancedStatusCode))
        {
            parts.Add(checkpoints.FinalResponseEnhancedStatusCode);
        }

        return string.Join(" · ", parts);
    }

    private sealed class CandidateTokenProvider : IMicrosoftTokenProvider
    {
        private readonly MicrosoftTokenProvider _tokens;
        private readonly MicrosoftIdentityConfiguration _configuration;

        public CandidateTokenProvider(
            MicrosoftTokenProvider tokens,
            MicrosoftIdentityConfiguration configuration)
        {
            _tokens = tokens;
            _configuration = configuration;
        }

        public Task<MicrosoftAccessToken> GetExchangeTokenAsync(CancellationToken cancellationToken)
        {
            return _tokens.GetExchangeTokenAsync(_configuration, cancellationToken);
        }

        public Task<MicrosoftAccessToken> GetExchangeTokenAsync(
            MicrosoftIdentityConfiguration configuration,
            CancellationToken cancellationToken)
        {
            return _tokens.GetExchangeTokenAsync(configuration, cancellationToken);
        }
    }

    private sealed record EntraSetupJson(
        string TenantId,
        string ClientId,
        string ServicePrincipalObjectId,
        int ApiPermissionEntryCount);

    private sealed record ExchangeSetupJson(
        bool ServicePrincipalConfigured,
        bool ScopeConfigured,
        string Role,
        bool SenderInScope);
}

internal sealed record SetupSmtpVerificationResult(
    ExchangeDeliveryDiagnosticResult Result,
    int AttemptCount);

internal sealed class MicrosoftSetupPersistenceHooks
{
    internal Action? BeforeEntraResultPersistence { get; init; }

    internal Action? BeforeExchangeResultPersistence { get; init; }

    internal Action? BeforeActivationPersistence { get; init; }

    internal Action? AfterActivationCommit { get; init; }
}
