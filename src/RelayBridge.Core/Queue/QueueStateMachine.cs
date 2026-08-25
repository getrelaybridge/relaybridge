// SPDX-License-Identifier: MPL-2.0

namespace RelayBridge.Core.Queue;

public static class QueueStateMachine
{
    public static bool CanTransition(QueueState current, QueueState next)
    {
        return (current, next) switch
        {
            (QueueState.Queued, QueueState.Delivering) => true,
            (QueueState.RetryScheduled, QueueState.Delivering) => true,
            (QueueState.Delivering, QueueState.Queued) => true,
            (QueueState.Delivering, QueueState.Delivered) => true,
            (QueueState.Delivering, QueueState.RetryScheduled) => true,
            (QueueState.Delivering, QueueState.PermanentFailure) => true,
            _ => false,
        };
    }

    public static void RequireTransition(QueueState current, QueueState next)
    {
        if (!CanTransition(current, next))
        {
            throw new InvalidOperationException($"Queue transition {current} -> {next} is not allowed.");
        }
    }
}
