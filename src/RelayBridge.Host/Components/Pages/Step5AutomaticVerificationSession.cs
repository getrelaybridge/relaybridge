// SPDX-License-Identifier: MPL-2.0

using RelayBridge.Core.Microsoft;

namespace RelayBridge.Host.Components.Pages;

internal enum Step5AutomaticVerificationState
{
    Active,
    Stopped,
    Completed,
}

internal enum Step5CheckNowResult
{
    Scheduled,
    AlreadyRunning,
    Inactive,
}

internal sealed record Step5AutomaticVerificationOptions(
    TimeSpan Interval,
    int MaximumAttempts,
    TimeSpan MaximumDuration)
{
    internal static Step5AutomaticVerificationOptions Production { get; } = new(
        TimeSpan.FromMinutes(5),
        12,
        TimeSpan.FromMinutes(60));

    internal void Validate()
    {
        if (Interval <= TimeSpan.Zero || MaximumAttempts <= 0 || MaximumDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Interval));
        }
    }
}

internal sealed record Step5AutomaticVerificationSnapshot(
    Step5AutomaticVerificationState State,
    int AttemptCount,
    int MaximumAttempts,
    DateTimeOffset StartedUtc,
    DateTimeOffset? LastCheckUtc,
    DateTimeOffset? NextCheckUtc,
    TimeSpan Elapsed,
    string? LastResult,
    string? CorrelationId,
    bool AttemptRunning,
    string Message);

internal sealed class Step5AutomaticVerificationSession : IAsyncDisposable
{
    internal const string WaitingMessage =
        "Exchange Online has not accepted the application authentication yet. Recent authorization changes may still be propagating. RelayBridge will retry every five minutes for up to one hour.";

    internal const string LimitMessage =
        "Exchange Online has not accepted the application authentication within the automatic verification period. Review the Microsoft 365 configuration or try verification again later.";

    private readonly object _sync = new();
    private readonly NativeMicrosoftCandidateIdentity _candidate;
    private readonly Func<NativeMicrosoftCandidateIdentity, CancellationToken, bool> _isCandidateCurrent;
    private readonly Func<NativeMicrosoftCandidateIdentity, CancellationToken, Task<MicrosoftSetupOperationResult>> _verify;
    private readonly Func<Step5AutomaticVerificationSnapshot, Task> _updated;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _timeProvider;
    private readonly Step5AutomaticVerificationOptions _options;
    private readonly CancellationTokenSource _sessionCancellation = new();
    private readonly CancellationTokenSource _durationCancellation;
    private readonly CancellationTokenSource _lifetimeCancellation;
    private readonly SemaphoreSlim _checkNowSignal = new(0, 1);
    private Step5AutomaticVerificationSnapshot _snapshot;
    private Task? _runTask;
    private bool _started;
    private bool _waitingForNext;
    private bool _administratorStopped;
    private bool _disposed;

    internal Step5AutomaticVerificationSession(
        NativeMicrosoftCandidateIdentity candidate,
        MicrosoftSetupOperationResult initialResult,
        Func<NativeMicrosoftCandidateIdentity, CancellationToken, bool> isCandidateCurrent,
        Func<NativeMicrosoftCandidateIdentity, CancellationToken, Task<MicrosoftSetupOperationResult>> verify,
        TimeProvider timeProvider,
        Func<Step5AutomaticVerificationSnapshot, Task> updated,
        CancellationToken pageLifetime,
        CancellationToken hostStopping,
        Step5AutomaticVerificationOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(initialResult);
        ArgumentNullException.ThrowIfNull(isCandidateCurrent);
        ArgumentNullException.ThrowIfNull(verify);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(updated);

        _candidate = candidate;
        _isCandidateCurrent = isCandidateCurrent;
        _verify = verify;
        _timeProvider = timeProvider;
        _updated = updated;
        _options = options ?? Step5AutomaticVerificationOptions.Production;
        _options.Validate();
        if (initialResult.Succeeded || !initialResult.AutomaticVerificationEligible)
        {
            throw new InvalidOperationException(
                "Automatic verification requires the exact eligible Step 5 authentication failure.");
        }

        _delay = delay ?? ((duration, cancellationToken) =>
            Task.Delay(duration, _timeProvider, cancellationToken));
        _durationCancellation = new CancellationTokenSource(_options.MaximumDuration, _timeProvider);
        _lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _sessionCancellation.Token,
            _durationCancellation.Token,
            pageLifetime,
            hostStopping);

        var startedUtc = _timeProvider.GetUtcNow();
        _snapshot = new Step5AutomaticVerificationSnapshot(
            Step5AutomaticVerificationState.Active,
            0,
            _options.MaximumAttempts,
            startedUtc,
            LastCheckUtc: startedUtc,
            NextCheckUtc: startedUtc + _options.Interval,
            Elapsed: TimeSpan.Zero,
            LastResult: initialResult.TechnicalCode,
            CorrelationId: initialResult.CorrelationId,
            AttemptRunning: false,
            WaitingMessage);
    }

    internal Step5AutomaticVerificationSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return _snapshot;
            }
        }
    }

    internal void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("Automatic verification is already running.");
            }

            _started = true;
            _runTask = RunAsync(_lifetimeCancellation.Token);
        }
    }

    internal Step5CheckNowResult CheckNow()
    {
        lock (_sync)
        {
            if (_disposed || _snapshot.State != Step5AutomaticVerificationState.Active)
            {
                return Step5CheckNowResult.Inactive;
            }

            if (_snapshot.AttemptRunning || !_waitingForNext)
            {
                return Step5CheckNowResult.AlreadyRunning;
            }

            return _checkNowSignal.CurrentCount == 0 && _checkNowSignal.Release() == 0
                ? Step5CheckNowResult.Scheduled
                : Step5CheckNowResult.AlreadyRunning;
        }
    }

    internal async Task StopAsync()
    {
        Task? runTask;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _administratorStopped = true;
            _sessionCancellation.Cancel();
            runTask = _runTask;
        }

        if (runTask is not null)
        {
            await runTask.ConfigureAwait(false);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            await PublishAsync().ConfigureAwait(false);
            while (true)
            {
                var now = _timeProvider.GetUtcNow();
                if (ReachedLimit(now))
                {
                    await StopWithMessageAsync(LimitMessage).ConfigureAwait(false);
                    return;
                }

                var nextCheckUtc = now + _options.Interval;
                SetSnapshot(current => current with
                {
                    NextCheckUtc = nextCheckUtc,
                    Elapsed = Elapsed(now),
                    AttemptRunning = false,
                });
                lock (_sync)
                {
                    _waitingForNext = true;
                }

                await PublishAsync().ConfigureAwait(false);
                await WaitForNextCheckAsync(nextCheckUtc, cancellationToken).ConfigureAwait(false);

                lock (_sync)
                {
                    _waitingForNext = false;
                    while (_checkNowSignal.Wait(0))
                    {
                    }
                }

                now = _timeProvider.GetUtcNow();
                if (ReachedLimit(now))
                {
                    await StopWithMessageAsync(LimitMessage).ConfigureAwait(false);
                    return;
                }

                if (!_isCandidateCurrent(_candidate, cancellationToken))
                {
                    await StopWithMessageAsync(
                        "The Microsoft setup candidate changed. Automatic verification stopped without activating another candidate.")
                        .ConfigureAwait(false);
                    return;
                }

                SetSnapshot(current => current with
                {
                    AttemptCount = checked(current.AttemptCount + 1),
                    NextCheckUtc = null,
                    Elapsed = Elapsed(now),
                    AttemptRunning = true,
                });
                await PublishAsync().ConfigureAwait(false);

                var result = await _verify(_candidate, cancellationToken).ConfigureAwait(false);
                now = _timeProvider.GetUtcNow();
                SetSnapshot(current => current with
                {
                    LastCheckUtc = now,
                    NextCheckUtc = null,
                    Elapsed = Elapsed(now),
                    LastResult = result.TechnicalCode ?? (result.Succeeded ? "Accepted" : "Unavailable"),
                    CorrelationId = result.CorrelationId,
                    AttemptRunning = false,
                    Message = result.Message,
                });

                if (result.Succeeded)
                {
                    SetSnapshot(current => current with
                    {
                        State = Step5AutomaticVerificationState.Completed,
                        Message = result.Message,
                    });
                    await PublishAsync().ConfigureAwait(false);
                    return;
                }

                if (!result.AutomaticVerificationEligible)
                {
                    SetSnapshot(current => current with
                    {
                        State = Step5AutomaticVerificationState.Stopped,
                        Message = result.Message,
                    });
                    await PublishAsync().ConfigureAwait(false);
                    return;
                }

                if (ReachedLimit(now))
                {
                    await StopWithMessageAsync(LimitMessage).ConfigureAwait(false);
                    return;
                }

                SetSnapshot(current => current with { Message = WaitingMessage });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_durationCancellation.IsCancellationRequested)
            {
                await StopWithMessageAsync(LimitMessage).ConfigureAwait(false);
            }
            else if (!_disposed)
            {
                await StopWithMessageAsync(
                    _administratorStopped
                        ? "Automatic verification stopped. The Microsoft setup candidate was not cancelled."
                        : "Automatic verification stopped because the page or RelayBridge service is closing.")
                    .ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            await StopWithMessageAsync(
                "Automatic verification stopped because RelayBridge could not complete the local verification session.",
                "Internal")
                .ConfigureAwait(false);
        }
    }

    private async Task WaitForNextCheckAsync(
        DateTimeOffset nextCheckUtc,
        CancellationToken cancellationToken)
    {
        var delay = nextCheckUtc - _timeProvider.GetUtcNow();
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = _delay(delay, waitCancellation.Token);
        var signalTask = _checkNowSignal.WaitAsync(waitCancellation.Token);
        var completed = await Task.WhenAny(delayTask, signalTask).ConfigureAwait(false);
        await completed.ConfigureAwait(false);
        waitCancellation.Cancel();
        await ObserveCancellationAsync(delayTask).ConfigureAwait(false);
        await ObserveCancellationAsync(signalTask).ConfigureAwait(false);
    }

    private async Task StopWithMessageAsync(string message, string? lastResult = null)
    {
        var now = _timeProvider.GetUtcNow();
        SetSnapshot(current => current with
        {
            State = Step5AutomaticVerificationState.Stopped,
            NextCheckUtc = null,
            Elapsed = Elapsed(now),
            LastResult = lastResult ?? current.LastResult,
            AttemptRunning = false,
            Message = message,
        });
        await PublishAsync().ConfigureAwait(false);
    }

    private bool ReachedLimit(DateTimeOffset now)
    {
        var current = Snapshot;
        return current.AttemptCount >= _options.MaximumAttempts ||
            now - current.StartedUtc >= _options.MaximumDuration;
    }

    private TimeSpan Elapsed(DateTimeOffset now)
    {
        var elapsed = now - Snapshot.StartedUtc;
        if (elapsed < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return elapsed > _options.MaximumDuration ? _options.MaximumDuration : elapsed;
    }

    private void SetSnapshot(
        Func<Step5AutomaticVerificationSnapshot, Step5AutomaticVerificationSnapshot> update)
    {
        lock (_sync)
        {
            _snapshot = update(_snapshot);
        }
    }

    private async Task PublishAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _updated(Snapshot).ConfigureAwait(false);
    }

    private static async Task ObserveCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? runTask;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _sessionCancellation.Cancel();
            runTask = _runTask;
        }

        if (runTask is not null)
        {
            await runTask.ConfigureAwait(false);
        }

        _lifetimeCancellation.Dispose();
        _durationCancellation.Dispose();
        _sessionCancellation.Dispose();
        _checkNowSignal.Dispose();
    }
}
