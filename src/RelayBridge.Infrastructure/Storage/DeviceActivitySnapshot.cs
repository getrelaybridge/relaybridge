// SPDX-License-Identifier: MPL-2.0

using RelayBridge.Core.Queue;

namespace RelayBridge.Infrastructure.Storage;

public sealed record DeviceActivitySnapshot(
    Guid DeviceId,
    DateTimeOffset? LastAcceptedUtc,
    DateTimeOffset? LastDeliveredUtc,
    int MessagesSince,
    QueueState? LatestMessageState,
    string? LatestErrorCategory);

public sealed record MessageOutcomeCounts(int Delivered, int PermanentFailures);
