// SPDX-License-Identifier: MPL-2.0

namespace RelayBridge.Core.Queue;

public sealed record QueuedMessage(
    Guid Id,
    Guid DeviceId,
    string EnvelopeFrom,
    IReadOnlyList<string> Recipients,
    DateTimeOffset ReceivedUtc,
    long SizeBytes,
    string SpoolFileName,
    QueueState State,
    int AttemptCount = 0,
    DateTimeOffset? NextAttemptUtc = null,
    DateTimeOffset? LastAttemptUtc = null,
    DateTimeOffset? CompletedUtc = null,
    string? LastErrorCategory = null,
    string? LastErrorMessage = null,
    bool PayloadPresent = true);
