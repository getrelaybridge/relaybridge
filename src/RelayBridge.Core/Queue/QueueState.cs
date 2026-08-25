// SPDX-License-Identifier: MPL-2.0

namespace RelayBridge.Core.Queue;

public enum QueueState
{
    Queued,
    Delivering,
    RetryScheduled,
    Delivered,
    PermanentFailure,
}
