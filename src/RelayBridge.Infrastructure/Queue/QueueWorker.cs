// SPDX-License-Identifier: MPL-2.0

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RelayBridge.Core.Queue;
using RelayBridge.Infrastructure.Storage;

namespace RelayBridge.Infrastructure.Queue;

public sealed class QueueWorker
{
    private static readonly TimeSpan ForcedStopWait = TimeSpan.FromSeconds(5);
    private readonly RelayDatabase _database;
    private readonly ISpoolFileSystem _fileSystem;
    private readonly IMailDeliveryProvider _deliveryProvider;
    private readonly QueueOptions _options;
    private readonly QueueWorkSignal _workSignal;
    private readonly TimeProvider _timeProvider;
    private readonly QueueRetryPolicy _retryPolicy;
    private readonly ILogger<QueueWorker> _logger;
    private CancellationTokenSource? _forceStop;
    private Task[] _workerTasks = [];
    private volatile bool _stopRequested;

    public QueueWorker(
        RelayDatabase database,
        ISpoolFileSystem fileSystem,
        IMailDeliveryProvider deliveryProvider,
        QueueOptions options,
        QueueWorkSignal workSignal,
        TimeProvider timeProvider,
        ILogger<QueueWorker> logger)
    {
        _database = database;
        _fileSystem = fileSystem;
        _deliveryProvider = deliveryProvider;
        _options = options;
        _workSignal = workSignal;
        _timeProvider = timeProvider;
        _logger = logger;
        _retryPolicy = new QueueRetryPolicy(
            options.MaximumAttempts,
            options.MaximumMessageAge,
            options.InitialRetryDelay,
            options.MaximumRetryDelay,
            options.RetryJitterFactor);
    }

    public bool IsRunning { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("The queue worker is already running.");
        }

        _options.Validate();
        _database.Initialize(cancellationToken);
        if (!_options.Enabled)
        {
            return Task.CompletedTask;
        }

        _stopRequested = false;
        _forceStop = new CancellationTokenSource();
        _workerTasks = Enumerable.Range(0, _options.MaxConcurrency)
            .Select(workerId => Task.Run(() => WorkerLoopAsync(workerId, _forceStop.Token), CancellationToken.None))
            .ToArray();
        IsRunning = true;
        _logger.LogInformation("QueueWorkerStarted Concurrency={Concurrency}", _options.MaxConcurrency);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            return;
        }

        _stopRequested = true;
        _workSignal.Pulse();
        try
        {
            await Task.WhenAll(_workerTasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _forceStop!.Cancel();
            _workSignal.Pulse();
            try
            {
                await Task.WhenAll(_workerTasks)
                    .WaitAsync(ForcedStopWait, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Interrupted claims recover themselves before their worker exits.
            }
            catch (TimeoutException)
            {
                _logger.LogError(
                    "QueueWorkerShutdownTimedOut ActiveWorkerCount={ActiveWorkerCount}",
                    _workerTasks.Count(task => !task.IsCompleted));
            }
        }
        finally
        {
            _forceStop?.Dispose();
            _forceStop = null;
            _workerTasks = [];
            IsRunning = false;
            _logger.LogInformation("QueueWorkerStopped");
        }
    }

    public async Task<bool> ProcessOneAsync(CancellationToken cancellationToken = default)
    {
        if (_stopRequested)
        {
            return false;
        }

        var nowUtc = _timeProvider.GetUtcNow();
        var message = _database.ClaimNextEligible(nowUtc, cancellationToken);
        if (message is null)
        {
            return false;
        }

        _logger.LogInformation(
            "QueueMessageClaimed MessageId={MessageId} Attempt={Attempt}",
            message.Id,
            message.AttemptCount);

        try
        {
            var path = _database.GetPendingPath(message.SpoolFileName);
            if (!_fileSystem.Exists(path))
            {
                _database.MarkMissingPayload(message.Id, nowUtc, cancellationToken);
                _logger.LogError("MissingSpoolDetected MessageId={MessageId}", message.Id);
                return true;
            }

            DeliveryResult result;
            try
            {
                await using var content = _fileSystem.OpenRead(path);
                _logger.LogInformation("DeliveryStarted MessageId={MessageId}", message.Id);
                result = await _deliveryProvider
                    .DeliverAsync(message, content, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "DeliveryProviderFailed MessageId={MessageId} ErrorType={ErrorType}",
                    message.Id,
                    exception.GetType().Name);
                result = DeliveryResult.TransientFailure(
                    "DeliveryProviderException",
                    "The delivery provider failed unexpectedly.");
            }

            ApplyResult(message, path, result, _timeProvider.GetUtcNow(), cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RecoverInterrupted(message.Id);
            throw;
        }
        catch
        {
            RecoverInterrupted(message.Id);
            throw;
        }
    }

    private async Task WorkerLoopAsync(int workerId, CancellationToken cancellationToken)
    {
        while (!_stopRequested && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (await ProcessOneAsync(cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                if (!_stopRequested)
                {
                    await _workSignal.WaitAsync(_options.PollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "QueueWorkerFaulted WorkerId={WorkerId}", workerId);
                await _workSignal.WaitAsync(_options.PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void ApplyResult(
        QueuedMessage message,
        string path,
        DeliveryResult result,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        switch (result.Outcome)
        {
            case DeliveryOutcome.Success:
                if (_database.MarkDelivered(message.Id, nowUtc, cancellationToken))
                {
                    _logger.LogInformation("DeliverySucceeded MessageId={MessageId}", message.Id);
                    DeleteDeliveredPayload(message.Id, path, cancellationToken);
                }

                break;

            case DeliveryOutcome.TransientFailure:
                _logger.LogWarning(
                    "DeliveryTransientFailure MessageId={MessageId} Attempt={Attempt} Category={Category}",
                    message.Id,
                    message.AttemptCount,
                    result.ErrorCategory);
                var decision = _retryPolicy.GetDecision(
                    message.ReceivedUtc,
                    message.AttemptCount,
                    nowUtc,
                    result.RetryAfter,
                    Random.Shared.NextDouble());
                if (decision.ShouldRetry)
                {
                    _ = _database.ScheduleRetry(
                        message.Id,
                        decision.NextAttemptUtc!.Value,
                        result.ErrorCategory!,
                        result.SafeMessage!,
                        cancellationToken);
                    _logger.LogWarning(
                        "RetryScheduled MessageId={MessageId} Attempt={Attempt} NextAttemptUtc={NextAttemptUtc} Category={Category}",
                        message.Id,
                        message.AttemptCount,
                        decision.NextAttemptUtc,
                        result.ErrorCategory);
                }
                else
                {
                    _ = _database.MarkPermanentFailure(
                        message.Id,
                        nowUtc,
                        "RetryLimitExceeded",
                        result.SafeMessage!,
                        cancellationToken);
                    _logger.LogError(
                        "DeliveryPermanentFailure MessageId={MessageId} Category=RetryLimitExceeded",
                        message.Id);
                }

                break;

            case DeliveryOutcome.PermanentFailure:
                _ = _database.MarkPermanentFailure(
                    message.Id,
                    nowUtc,
                    result.ErrorCategory!,
                    result.SafeMessage!,
                    cancellationToken);
                _logger.LogError(
                    "DeliveryPermanentFailure MessageId={MessageId} Category={Category}",
                    message.Id,
                    result.ErrorCategory);
                break;

            default:
                throw new InvalidOperationException($"Unsupported delivery outcome {result.Outcome}.");
        }
    }

    private void DeleteDeliveredPayload(Guid messageId, string path, CancellationToken cancellationToken)
    {
        if (!_options.DeleteDeliveredPayload)
        {
            return;
        }

        try
        {
            _fileSystem.Delete(path);
            _database.MarkPayloadDeleted(messageId, cancellationToken);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception,
                "DeliveredPayloadCleanupFailed MessageId={MessageId}",
                messageId);
        }
    }

    private void RecoverInterrupted(Guid messageId)
    {
        try
        {
            _database.RecoverInterruptedClaim(
                messageId,
                "DeliveryInterrupted",
                "Delivery was interrupted during service shutdown.",
                CancellationToken.None);
            _logger.LogWarning("QueueMessageRecovered MessageId={MessageId} Reason=DeliveryInterrupted", messageId);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "QueueMessageRecoveryFailed MessageId={MessageId}", messageId);
        }
    }
}
