// SPDX-License-Identifier: MPL-2.0

namespace RelayBridge.Infrastructure.Queue;

public sealed class QueueOptions
{
    public bool Enabled { get; set; }

    public int MaxConcurrency { get; set; } = 1;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int MaximumAttempts { get; set; } = 10;

    public TimeSpan MaximumMessageAge { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaximumRetryDelay { get; set; } = TimeSpan.FromHours(1);

    public double RetryJitterFactor { get; set; } = 0.2;

    public int MaximumQueuedMessages { get; set; } = 10_000;

    public long MaximumSpoolBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    public long MinimumFreeDiskBytes { get; set; } = 1024L * 1024 * 1024;

    public TimeSpan TemporaryFileMaxAge { get; set; } = TimeSpan.FromHours(1);

    public int MaximumReconciliationFiles { get; set; } = 100_000;

    public bool DeleteDeliveredPayload { get; set; } = true;

    public void Validate()
    {
        if (MaxConcurrency is < 1 or > 16)
        {
            throw new InvalidOperationException("Queue concurrency must be between 1 and 16.");
        }

        if (PollInterval < TimeSpan.FromMilliseconds(100) || PollInterval > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException("Queue poll interval must be between 100 ms and five minutes.");
        }

        if (MaximumAttempts < 1 || MaximumMessageAge <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Queue retry limits are invalid.");
        }

        if (InitialRetryDelay <= TimeSpan.Zero || MaximumRetryDelay < InitialRetryDelay)
        {
            throw new InvalidOperationException("Queue retry delays are invalid.");
        }

        if (RetryJitterFactor is < 0 or > 1)
        {
            throw new InvalidOperationException("Queue retry jitter must be between zero and one.");
        }

        if (MaximumQueuedMessages < 1 || MaximumSpoolBytes < 1024 || MinimumFreeDiskBytes < 0)
        {
            throw new InvalidOperationException("Queue capacity limits are invalid.");
        }

        if (TemporaryFileMaxAge < TimeSpan.FromMinutes(1) || MaximumReconciliationFiles < 1)
        {
            throw new InvalidOperationException("Queue reconciliation limits are invalid.");
        }
    }
}
