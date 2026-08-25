// SPDX-License-Identifier: MPL-2.0

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RelayBridge.Core.Queue;
using RelayBridge.Infrastructure.Storage;

namespace RelayBridge.Infrastructure.Queue;

public sealed class QueueReconciler
{
    private readonly RelayDatabase _database;
    private readonly ISpoolFileSystem _fileSystem;
    private readonly QueueOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<QueueReconciler> _logger;

    public QueueReconciler(
        RelayDatabase database,
        ISpoolFileSystem fileSystem,
        QueueOptions options,
        TimeProvider timeProvider,
        ILogger<QueueReconciler> logger)
    {
        _database = database;
        _fileSystem = fileSystem;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public QueueReconciliationResult Reconcile(CancellationToken cancellationToken = default)
    {
        try
        {
            return ReconcileCore(cancellationToken);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(
                exception,
                "QueueReconciliationFailed OrphanDeletionRequiresVerifiedOwnership={OrphanDeletionRequiresVerifiedOwnership}",
                true);
            throw;
        }
    }

    private QueueReconciliationResult ReconcileCore(CancellationToken cancellationToken)
    {
        _options.Validate();
        _database.Initialize(cancellationToken);
        var nowUtc = _timeProvider.GetUtcNow();
        var recovered = _database.RecoverAllDelivering(
            "RecoveredAfterRestart",
            "An interrupted delivery was returned to the queue during startup.",
            cancellationToken);
        if (recovered > 0)
        {
            _logger.LogWarning("QueueMessageRecovered Count={Count} Reason=StaleDelivering", recovered);
        }

        var messages = _database.GetQueuedMessages(cancellationToken);
        var knownFiles = messages
            .Select(message => message.SpoolFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = 0;
        var deliveredCleaned = 0;
        foreach (var message in messages.Where(message => message.PayloadPresent))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path;
            try
            {
                path = _database.GetPendingPath(message.SpoolFileName);
            }
            catch (InvalidOperationException)
            {
                if (_database.MarkMissingPayload(message.Id, nowUtc, cancellationToken))
                {
                    missing++;
                    _logger.LogError("MissingSpoolDetected MessageId={MessageId} Reason=InvalidFileName", message.Id);
                }

                continue;
            }

            if (!_fileSystem.Exists(path))
            {
                if (message.State == QueueState.Delivered)
                {
                    _database.MarkPayloadDeleted(message.Id, cancellationToken);
                    continue;
                }

                if (_database.MarkMissingPayload(message.Id, nowUtc, cancellationToken))
                {
                    missing++;
                    _logger.LogError("MissingSpoolDetected MessageId={MessageId}", message.Id);
                }

                continue;
            }

            if (message.State == QueueState.Delivered && _options.DeleteDeliveredPayload)
            {
                _fileSystem.Delete(path);
                _database.MarkPayloadDeleted(message.Id, cancellationToken);
                deliveredCleaned++;
            }
        }

        var orphaned = 0;
        foreach (var path in EnumerateBounded(_database.PendingDirectory, "*.eml", cancellationToken))
        {
            var fileName = Path.GetFileName(path);
            if (knownFiles.Contains(fileName))
            {
                continue;
            }

            if (_database.HasSpoolFile(fileName, cancellationToken))
            {
                continue;
            }

            _logger.LogWarning("OrphanSpoolDetected FileName={FileName} Action=Delete", fileName);
            _fileSystem.Delete(path);
            orphaned++;
        }

        var temporaryDeleted = 0;
        var temporaryCutoff = nowUtc - _options.TemporaryFileMaxAge;
        foreach (var path in EnumerateBounded(_database.IncomingDirectory, "*.tmp", cancellationToken))
        {
            if (_fileSystem.GetLastWriteTimeUtc(path) > temporaryCutoff)
            {
                continue;
            }

            _fileSystem.Delete(path);
            temporaryDeleted++;
        }

        return new QueueReconciliationResult(
            recovered,
            orphaned,
            missing,
            temporaryDeleted,
            deliveredCleaned);
    }

    private IEnumerable<string> EnumerateBounded(
        string directory,
        string pattern,
        CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var path in _fileSystem.EnumerateFiles(directory, pattern))
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
            if (count > _options.MaximumReconciliationFiles)
            {
                _logger.LogError(
                    "QueueReconciliationLimitExceeded Directory={Directory} Limit={Limit}",
                    directory,
                    _options.MaximumReconciliationFiles);
                yield break;
            }

            yield return path;
        }
    }
}

public sealed record QueueReconciliationResult(
    int RecoveredDelivering,
    int DeletedOrphans,
    int MissingPayloads,
    int DeletedTemporaryFiles,
    int DeletedDeliveredPayloads);
