// SPDX-License-Identifier: MPL-2.0

using RelayBridge.Core.Microsoft;

namespace RelayBridge.Infrastructure.Microsoft;

public enum MicrosoftRuntimeReadiness
{
    NotConfigured,
    VerificationRequired,
    Ready,
    NeedsAttention,
}

public static class MicrosoftRuntimeReadinessPolicy
{
    public static MicrosoftRuntimeReadiness Evaluate(
        bool configured,
        string? currentConfigurationFingerprint,
        MicrosoftIdentityRuntimeState identity,
        ExchangeDeliveryRuntimeState exchange)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(exchange);
        return Evaluate(
            configured,
            currentConfigurationFingerprint,
            identity.GetCompletedSnapshot(currentConfigurationFingerprint),
            exchange.GetCompletedSnapshot(currentConfigurationFingerprint));
    }

    public static MicrosoftRuntimeReadiness Evaluate(
        bool configured,
        string? currentConfigurationFingerprint,
        MicrosoftIdentityHealthSnapshot identity,
        ExchangeDeliverySnapshot exchange)
    {
        if (!configured || string.IsNullOrWhiteSpace(currentConfigurationFingerprint))
        {
            return MicrosoftRuntimeReadiness.NotConfigured;
        }

        var evidence = new List<ReadinessEvidence>(2);
        if (string.Equals(
                identity.ConfigurationFingerprint,
                currentConfigurationFingerprint,
                StringComparison.Ordinal))
        {
            if (identity.Status == MicrosoftIdentityHealthStatus.Failed &&
                (identity.LastCompletedAt ?? identity.LastAttemptedAt) is { } identityFailureAt)
            {
                evidence.Add(new ReadinessEvidence(
                    identity.CompletionSequence,
                    identityFailureAt,
                    IsSuccess: false));
            }
            else if (identity.Status is MicrosoftIdentityHealthStatus.Healthy or MicrosoftIdentityHealthStatus.Attention &&
                     identity.LastSuccessfulAt is { } identitySuccessAt)
            {
                evidence.Add(new ReadinessEvidence(
                    identity.CompletionSequence,
                    identitySuccessAt,
                    IsSuccess: true));
            }
        }

        if (string.Equals(
                exchange.ConfigurationFingerprint,
                currentConfigurationFingerprint,
                StringComparison.Ordinal))
        {
            if (exchange.Status == ExchangeDeliveryStatus.Healthy && exchange.LastSuccessfulAt is { } exchangeSuccessAt)
            {
                evidence.Add(new ReadinessEvidence(
                    exchange.CompletionSequence,
                    exchangeSuccessAt,
                    IsSuccess: true));
            }
            else if (exchange.Status is ExchangeDeliveryStatus.Failed or ExchangeDeliveryStatus.Ambiguous &&
                     (exchange.LastCompletedAt ?? exchange.LastAttemptedAt) is { } exchangeFailureAt)
            {
                evidence.Add(new ReadinessEvidence(
                    exchange.CompletionSequence,
                    exchangeFailureAt,
                    IsSuccess: false));
            }
        }

        if (evidence.Count == 0)
        {
            return MicrosoftRuntimeReadiness.VerificationRequired;
        }

        var newest = evidence
            .OrderByDescending(item => item.CompletionSequence.HasValue)
            .ThenByDescending(item => item.CompletionSequence)
            .ThenByDescending(item => item.Timestamp)
            .ThenBy(item => item.IsSuccess)
            .First();
        return newest.IsSuccess
            ? MicrosoftRuntimeReadiness.Ready
            : MicrosoftRuntimeReadiness.NeedsAttention;
    }

    private readonly record struct ReadinessEvidence(
        long? CompletionSequence,
        DateTimeOffset Timestamp,
        bool IsSuccess);
}
