// SPDX-License-Identifier: MPL-2.0

using System.Threading;
using RelayBridge.Core.Microsoft;

namespace RelayBridge.Infrastructure.Microsoft;

public sealed class MicrosoftRuntimeEvidenceSequence
{
    private long _value;

    internal long Next()
    {
        return Interlocked.Increment(ref _value);
    }
}

internal sealed record MicrosoftAttemptContext(
    Guid AttemptId,
    string? ConfigurationFingerprint,
    MicrosoftIdentityConfiguration? CapturedConfiguration,
    DateTimeOffset StartedAt,
    long StartSequence)
{
    internal static MicrosoftAttemptContext Create(
        MicrosoftRuntimeEvidenceSequence sequence,
        DateTimeOffset startedAt,
        MicrosoftIdentityConfiguration? capturedConfiguration,
        string? configurationFingerprint)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return new MicrosoftAttemptContext(
            Guid.NewGuid(),
            configurationFingerprint,
            capturedConfiguration,
            startedAt,
            sequence.Next());
    }
}
