// SPDX-License-Identifier: MPL-2.0

using RelayBridge.Core.Queue;
using RelayBridge.Infrastructure.Queue;

namespace RelayBridge.Infrastructure.Storage;

public sealed class DurableMessageStore
{
    private readonly RelayDatabase _database;
    private readonly ISpoolFileSystem _fileSystem;
    private readonly QueueCapacityManager _capacity;
    private readonly QueueWorkSignal _workSignal;

    public DurableMessageStore(RelayDatabase database)
    {
        var fileSystem = new PhysicalSpoolFileSystem();
        _database = database;
        _fileSystem = fileSystem;
        _capacity = new QueueCapacityManager(database, fileSystem, new QueueOptions());
        _workSignal = new QueueWorkSignal();
    }

    public DurableMessageStore(
        RelayDatabase database,
        ISpoolFileSystem fileSystem,
        QueueCapacityManager capacity,
        QueueWorkSignal workSignal)
    {
        _database = database;
        _fileSystem = fileSystem;
        _capacity = capacity;
        _workSignal = workSignal;
    }

    public MessageReceiveTransaction BeginReceive(CancellationToken cancellationToken = default)
    {
        return BeginReceive(expectedBytes: 0, cancellationToken);
    }

    public MessageReceiveTransaction BeginReceive(
        long expectedBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _database.Initialize(cancellationToken);
        var reservation = _capacity.Reserve(expectedBytes, cancellationToken);
        try
        {
            var messageId = Guid.CreateVersion7();
            var temporaryPath = Path.Combine(_database.IncomingDirectory, $"{messageId:D}.tmp");
            var finalFileName = $"{messageId:D}.eml";
            var finalPath = Path.Combine(_database.PendingDirectory, finalFileName);
            var stream = _fileSystem.CreateReceiveStream(temporaryPath);
            return new MessageReceiveTransaction(
                _database,
                _fileSystem,
                _workSignal,
                reservation,
                messageId,
                temporaryPath,
                finalPath,
                finalFileName,
                stream);
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }
}

public sealed class MessageReceiveTransaction : IAsyncDisposable
{
    private readonly RelayDatabase _database;
    private readonly ISpoolFileSystem _fileSystem;
    private readonly QueueWorkSignal _workSignal;
    private readonly QueueCapacityReservation _reservation;
    private readonly string _temporaryPath;
    private readonly string _finalPath;
    private readonly string _finalFileName;
    private Stream? _stream;
    private bool _committed;

    internal MessageReceiveTransaction(
        RelayDatabase database,
        ISpoolFileSystem fileSystem,
        QueueWorkSignal workSignal,
        QueueCapacityReservation reservation,
        Guid messageId,
        string temporaryPath,
        string finalPath,
        string finalFileName,
        Stream stream)
    {
        _database = database;
        _fileSystem = fileSystem;
        _workSignal = workSignal;
        _reservation = reservation;
        MessageId = messageId;
        _temporaryPath = temporaryPath;
        _finalPath = finalPath;
        _finalFileName = finalFileName;
        _stream = stream;
    }

    public Guid MessageId { get; }

    public Stream Stream => _stream ?? throw new ObjectDisposedException(nameof(MessageReceiveTransaction));

    public void EnsureCapacity(long sizeBytes)
    {
        _reservation.Ensure(sizeBytes);
    }

    public async Task<QueuedMessage> CommitAsync(
        Guid deviceId,
        string envelopeFrom,
        IReadOnlyList<string> recipients,
        long sizeBytes,
        CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            throw new ObjectDisposedException(nameof(MessageReceiveTransaction));
        }

        await _fileSystem.FlushToDiskAsync(_stream, cancellationToken).ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        _stream = null;

        _fileSystem.Move(_temporaryPath, _finalPath);
        var message = new QueuedMessage(
            MessageId,
            deviceId,
            envelopeFrom,
            recipients.ToArray(),
            DateTimeOffset.UtcNow,
            sizeBytes,
            _finalFileName,
            QueueState.Queued);

        try
        {
            _database.InsertQueuedMessage(message, cancellationToken);
            _committed = true;
            _workSignal.Pulse();
            return message;
        }
        catch
        {
            TryDelete(_finalPath);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            try
            {
                if (_stream is not null)
                {
                    await _stream.DisposeAsync().ConfigureAwait(false);
                    _stream = null;
                }
            }
            finally
            {
                if (!_committed)
                {
                    TryDelete(_temporaryPath);
                }
            }
        }
        finally
        {
            _reservation.Dispose();
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            _fileSystem.Delete(path);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
    }
}
