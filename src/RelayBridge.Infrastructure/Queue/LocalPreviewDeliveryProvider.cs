// SPDX-License-Identifier: MPL-2.0

using RelayBridge.Core.Queue;

namespace RelayBridge.Infrastructure.Queue;

public sealed class LocalPreviewDeliveryProvider : IMailDeliveryProvider
{
    public Task<DeliveryResult> DeliverAsync(
        QueuedMessage message,
        Stream messageContent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DeliveryResult.TransientFailure(
            "DeliveryNotConfigured",
            "Outbound Microsoft 365 delivery is not configured."));
    }
}
