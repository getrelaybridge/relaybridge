// SPDX-License-Identifier: MPL-2.0

namespace RelayBridge.Infrastructure.Queue;

public sealed class QueueDeliveryActivation
{
    private readonly TaskCompletionSource _activated = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsActivated => _activated.Task.IsCompletedSuccessfully;

    public void Activate()
    {
        _activated.TrySetResult();
    }

    public Task WaitAsync(CancellationToken cancellationToken)
    {
        return _activated.Task.WaitAsync(cancellationToken);
    }
}
