// SPDX-License-Identifier: MPL-2.0

using RelayBridge.Infrastructure.Storage;

namespace RelayBridge.Infrastructure.Queue;

public sealed class QueueCapacityManager
{
    private readonly RelayDatabase _database;
    private readonly ISpoolFileSystem _fileSystem;
    private readonly QueueOptions _options;
    private readonly object _lock = new();
    private int _reservedMessages;
    private long _reservedBytes;

    public QueueCapacityManager(
        RelayDatabase database,
        ISpoolFileSystem fileSystem,
        QueueOptions options)
    {
        _database = database;
        _fileSystem = fileSystem;
        _options = options;
    }

    public QueueCapacityReservation Reserve(long expectedBytes, CancellationToken cancellationToken = default)
    {
        if (expectedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedBytes));
        }

        lock (_lock)
        {
            EnsureCapacity(expectedBytes, includeMessage: true, cancellationToken);
            _reservedMessages++;
            _reservedBytes += expectedBytes;
            return new QueueCapacityReservation(this, expectedBytes);
        }
    }

    public QueueMetrics GetMetrics(CancellationToken cancellationToken = default)
    {
        var databaseMetrics = _database.GetQueueMetrics(cancellationToken);
        return databaseMetrics with
        {
            FreeDiskBytes = _fileSystem.GetAvailableFreeSpace(_database.SpoolDirectory),
        };
    }

    internal void Grow(QueueCapacityReservation reservation, long requiredBytes)
    {
        lock (_lock)
        {
            if (requiredBytes <= reservation.ReservedBytes)
            {
                return;
            }

            var additionalBytes = requiredBytes - reservation.ReservedBytes;
            EnsureCapacity(additionalBytes, includeMessage: false, CancellationToken.None);
            _reservedBytes = checked(_reservedBytes + additionalBytes);
            reservation.SetReservedBytes(requiredBytes);
        }
    }

    internal void Release(QueueCapacityReservation reservation)
    {
        lock (_lock)
        {
            if (!reservation.TryMarkReleased())
            {
                return;
            }

            _reservedMessages--;
            _reservedBytes -= reservation.ReservedBytes;
        }
    }

    private void EnsureCapacity(
        long additionalBytes,
        bool includeMessage,
        CancellationToken cancellationToken)
    {
        var usage = _database.GetQueueCapacityUsage(cancellationToken);
        if (includeMessage && usage.PayloadMessageCount + _reservedMessages >= _options.MaximumQueuedMessages)
        {
            throw new QueueCapacityExceededException(QueueCapacityLimit.MessageCount);
        }

        if (ExceedsLimit(
            usage.TotalSpoolBytes,
            _reservedBytes,
            additionalBytes,
            _options.MaximumSpoolBytes))
        {
            throw new QueueCapacityExceededException(QueueCapacityLimit.SpoolBytes);
        }

        var freeBytes = _fileSystem.GetAvailableFreeSpace(_database.SpoolDirectory);
        if (freeBytes < _options.MinimumFreeDiskBytes ||
            ExceedsLimit(
                0,
                _reservedBytes,
                additionalBytes,
                freeBytes - _options.MinimumFreeDiskBytes))
        {
            throw new QueueCapacityExceededException(QueueCapacityLimit.FreeDisk);
        }
    }

    private static bool ExceedsLimit(long existing, long reserved, long additional, long limit)
    {
        return existing < 0 || reserved < 0 || additional < 0 || limit < 0 ||
            existing > limit ||
            reserved > limit - existing ||
            additional > limit - existing - reserved;
    }
}

public sealed class QueueCapacityReservation : IDisposable
{
    private readonly QueueCapacityManager _manager;
    private int _released;

    internal QueueCapacityReservation(QueueCapacityManager manager, long reservedBytes)
    {
        _manager = manager;
        ReservedBytes = reservedBytes;
    }

    public long ReservedBytes { get; private set; }

    public void Ensure(long requiredBytes)
    {
        _manager.Grow(this, requiredBytes);
    }

    public void Dispose()
    {
        _manager.Release(this);
    }

    internal void SetReservedBytes(long value)
    {
        ReservedBytes = value;
    }

    internal bool TryMarkReleased()
    {
        return Interlocked.Exchange(ref _released, 1) == 0;
    }
}

public enum QueueCapacityLimit
{
    MessageCount,
    SpoolBytes,
    FreeDisk,
}

public sealed class QueueCapacityExceededException(QueueCapacityLimit limit)
    : IOException($"Queue capacity limit exceeded: {limit}.")
{
    public QueueCapacityLimit Limit { get; } = limit;
}

public sealed record QueueCapacityUsage(int PayloadMessageCount, long TotalSpoolBytes);

public sealed record QueueMetrics(
    int QueuedCount,
    int RetryScheduledCount,
    int DeliveringCount,
    int PermanentFailureCount,
    DateTimeOffset? OldestQueuedUtc,
    long TotalSpoolBytes,
    long FreeDiskBytes = 0);
