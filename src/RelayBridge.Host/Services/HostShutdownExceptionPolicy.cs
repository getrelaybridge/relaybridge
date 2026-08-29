// SPDX-License-Identifier: MPL-2.0

namespace RelayBridge.Host.Services;

internal static class HostShutdownExceptionPolicy
{
    internal static bool IsExpected(Exception exception, CancellationToken applicationStopping)
    {
        return exception is OperationCanceledException && applicationStopping.IsCancellationRequested;
    }
}
