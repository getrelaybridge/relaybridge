// SPDX-License-Identifier: MPL-2.0

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RelayBridge.Infrastructure.Queue;
using RelayBridge.Infrastructure.Storage;

namespace RelayBridge.Host.Services;

public sealed class QueueHealthCheck : IHealthCheck
{
    private readonly RelayDatabase _database;
    private readonly ISpoolFileSystem _fileSystem;
    private readonly QueueCapacityManager _capacity;
    private readonly QueueWorker _worker;
    private readonly QueueOptions _options;

    public QueueHealthCheck(
        RelayDatabase database,
        ISpoolFileSystem fileSystem,
        QueueCapacityManager capacity,
        QueueWorker worker,
        QueueOptions options)
    {
        _database = database;
        _fileSystem = fileSystem;
        _capacity = capacity;
        _worker = worker;
        _options = options;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var databaseUsable = _database.IsUsable(cancellationToken);
        var spoolWritable = _fileSystem.CanWrite(_database.IncomingDirectory);
        QueueMetrics? metrics = null;
        QueueCapacityUsage? usage = null;
        try
        {
            metrics = _capacity.GetMetrics(cancellationToken);
            usage = _database.GetQueueCapacityUsage(cancellationToken);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Queue storage is unavailable.", exception));
        }

        var capacityAvailable = usage.PayloadMessageCount < _options.MaximumQueuedMessages &&
            usage.TotalSpoolBytes < _options.MaximumSpoolBytes &&
            metrics.FreeDiskBytes >= _options.MinimumFreeDiskBytes;
        var workerRunning = !_options.Enabled || _worker.IsRunning;
        var data = new Dictionary<string, object>
        {
            ["databaseUsable"] = databaseUsable,
            ["spoolWritable"] = spoolWritable,
            ["freeDiskBytes"] = metrics.FreeDiskBytes,
            ["totalSpoolBytes"] = metrics.TotalSpoolBytes,
            ["queuedCount"] = metrics.QueuedCount,
            ["retryCount"] = metrics.RetryScheduledCount,
            ["deliveringCount"] = metrics.DeliveringCount,
            ["permanentFailureCount"] = metrics.PermanentFailureCount,
            ["workerRunning"] = workerRunning,
        };

        return Task.FromResult(databaseUsable && spoolWritable && capacityAvailable && workerRunning
            ? HealthCheckResult.Healthy("Local queue is healthy.", data)
            : HealthCheckResult.Unhealthy("Local queue requires attention.", data: data));
    }
}
