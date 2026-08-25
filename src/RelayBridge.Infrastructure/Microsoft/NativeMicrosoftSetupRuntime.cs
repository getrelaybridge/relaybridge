// SPDX-License-Identifier: MPL-2.0

using RelayBridge.Core.Microsoft;

namespace RelayBridge.Infrastructure.Microsoft;

public sealed class NativeMicrosoftSetupRuntime
{
    private readonly object _lock = new();
    private readonly TimeProvider _timeProvider;
    private NativeSetupRuntimeSnapshot _snapshot;
    private Func<CancellationToken, Task>? _cancel;

    public NativeMicrosoftSetupRuntime(NativeMicrosoftSetupOptions options, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _snapshot = new NativeSetupRuntimeSnapshot(
            options.Enabled,
            false,
            NativeSetupStage.WaitingForHelper,
            options.Enabled
                ? "Ready to launch the local Microsoft setup helper."
                : "Native Microsoft setup tools are not installed. Use Advanced manual setup or repair the RelayBridge installation.",
            NativeSetupFailureCategory.None,
            null,
            null,
            timeProvider.GetUtcNow());
    }

    public NativeSetupRuntimeSnapshot Snapshot
    {
        get
        {
            lock (_lock)
            {
                return _snapshot;
            }
        }
    }

    public Task CancelAsync(CancellationToken cancellationToken = default)
    {
        Func<CancellationToken, Task>? cancel;
        lock (_lock)
        {
            cancel = _cancel;
        }

        return cancel is null ? Task.CompletedTask : cancel(cancellationToken);
    }

    public void PrepareForLaunch()
    {
        lock (_lock)
        {
            if (_snapshot.Running)
            {
                throw new InvalidOperationException("Microsoft setup is already running.");
            }

            _snapshot = _snapshot with
            {
                Stage = NativeSetupStage.WaitingForHelper,
                Message = "Waiting for the local RelayBridge Setup confirmation.",
                FailureCategory = NativeSetupFailureCategory.None,
                SafeCode = null,
                SafeCorrelationId = null,
                SafeFailureDetails = null,
                UpdatedUtc = _timeProvider.GetUtcNow(),
            };
        }
    }

    internal void Start(Func<CancellationToken, Task> cancel)
    {
        lock (_lock)
        {
            if (_snapshot.Running)
            {
                throw new InvalidOperationException("Microsoft setup is already running.");
            }

            _cancel = cancel;
            _snapshot = _snapshot with
            {
                Running = true,
                Stage = NativeSetupStage.Confirming,
                Message = "Waiting for confirmation in RelayBridge Setup.",
                FailureCategory = NativeSetupFailureCategory.None,
                SafeCode = null,
                SafeCorrelationId = null,
                SafeFailureDetails = null,
                UpdatedUtc = _timeProvider.GetUtcNow(),
            };
        }
    }

    internal void Update(NativeSetupStage stage, string message)
    {
        lock (_lock)
        {
            _snapshot = _snapshot with
            {
                Running = true,
                Stage = stage,
                Message = message,
                UpdatedUtc = _timeProvider.GetUtcNow(),
            };
        }
    }

    internal void Complete(string message)
    {
        lock (_lock)
        {
            _cancel = null;
            _snapshot = _snapshot with
            {
                Running = false,
                Stage = NativeSetupStage.Complete,
                Message = message,
                FailureCategory = NativeSetupFailureCategory.None,
                SafeCode = null,
                SafeCorrelationId = null,
                SafeFailureDetails = null,
                UpdatedUtc = _timeProvider.GetUtcNow(),
            };
        }
    }

    internal void Fail(
        NativeSetupFailureCategory category,
        string message,
        string? safeCode,
        string? correlationId,
        NativeSetupSafeFailureDetails? safeFailureDetails = null)
    {
        lock (_lock)
        {
            _cancel = null;
            _snapshot = _snapshot with
            {
                Running = false,
                Message = message,
                FailureCategory = category,
                SafeCode = safeCode,
                SafeCorrelationId = correlationId,
                SafeFailureDetails = safeFailureDetails,
                UpdatedUtc = _timeProvider.GetUtcNow(),
            };
        }
    }

    internal void Unavailable(string message, string safeCode)
    {
        lock (_lock)
        {
            _cancel = null;
            _snapshot = _snapshot with
            {
                Available = false,
                Running = false,
                Stage = NativeSetupStage.WaitingForHelper,
                Message = message,
                FailureCategory = NativeSetupFailureCategory.HelperFailed,
                SafeCode = safeCode,
                SafeCorrelationId = null,
                SafeFailureDetails = null,
                UpdatedUtc = _timeProvider.GetUtcNow(),
            };
        }
    }

    internal void ListenerReady()
    {
        lock (_lock)
        {
            if (_snapshot.Running)
            {
                return;
            }

            if (_snapshot.FailureCategory == NativeSetupFailureCategory.None)
            {
                _snapshot = _snapshot with
                {
                    Available = true,
                    Message = "Ready to launch the local Microsoft setup helper.",
                    UpdatedUtc = _timeProvider.GetUtcNow(),
                };
                return;
            }

            // Listener availability is independent of the last completed attempt.
            // Preserve its sanitized stage/category/code until PrepareForLaunch starts
            // a deliberate new attempt, otherwise the UI loses the useful failure.
            _snapshot = _snapshot with { Available = true };
        }
    }
}
