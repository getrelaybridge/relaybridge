// SPDX-License-Identifier: MPL-2.0

using RelayBridge.Core.Queue;

namespace RelayBridge.Host.Services;

public sealed record DeviceTestPresentation(
    bool LocalAccepted,
    bool MicrosoftAccepted,
    string Message,
    bool IsFailure = false)
{
    public static DeviceTestPresentation From(bool localAccepted, QueueState? state)
    {
        if (!localAccepted)
        {
            return new DeviceTestPresentation(
                false,
                false,
                "RelayBridge has not received a new message from this device yet.");
        }

        return state switch
        {
            QueueState.Delivered => new DeviceTestPresentation(
                true,
                true,
                "Microsoft 365 accepted the message."),
            QueueState.RetryScheduled => new DeviceTestPresentation(
                true,
                false,
                "RelayBridge safely accepted the message. Microsoft 365 delivery will retry automatically."),
            QueueState.PermanentFailure => new DeviceTestPresentation(
                true,
                false,
                "RelayBridge accepted the message locally, but Microsoft 365 permanently rejected delivery.",
                true),
            QueueState.Queued or QueueState.Delivering => new DeviceTestPresentation(
                true,
                false,
                "RelayBridge safely accepted the message. It is waiting for Microsoft 365 delivery."),
            _ => new DeviceTestPresentation(
                true,
                false,
                "RelayBridge safely accepted the message. It is waiting for Microsoft 365 delivery."),
        };
    }
}
