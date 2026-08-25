// SPDX-License-Identifier: MPL-2.0

namespace RelayBridge.Infrastructure.Queue;

public sealed class QueueWorkSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Pulse()
    {
        if (_signal.CurrentCount == 0)
        {
            _signal.Release();
        }
    }

    public async Task WaitAsync(TimeSpan safetyPollInterval, CancellationToken cancellationToken)
    {
        _ = await _signal.WaitAsync(safetyPollInterval, cancellationToken).ConfigureAwait(false);
    }
}
