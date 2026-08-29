// SPDX-License-Identifier: MPL-2.0

using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RelayBridge.Core.Queue;
using RelayBridge.Infrastructure.Queue;
using RelayBridge.Infrastructure.Storage;

namespace RelayBridge.IntegrationTests;

internal sealed class QueueTestContext : IAsyncDisposable
{
    private QueueTestContext(
        string dataDirectory,
        RelayDatabase database,
        ISpoolFileSystem fileSystem,
        QueueOptions options,
        QueueCapacityManager capacity,
        QueueWorkSignal workSignal,
        QueueDeliveryActivation deliveryActivation,
        DurableMessageStore messageStore,
        ManualTimeProvider timeProvider,
        Guid deviceId)
    {
        DataDirectory = dataDirectory;
        Database = database;
        FileSystem = fileSystem;
        Options = options;
        Capacity = capacity;
        WorkSignal = workSignal;
        DeliveryActivation = deliveryActivation;
        MessageStore = messageStore;
        TimeProvider = timeProvider;
        DeviceId = deviceId;
    }

    public string DataDirectory { get; }

    public RelayDatabase Database { get; }

    public ISpoolFileSystem FileSystem { get; }

    public QueueOptions Options { get; }

    public QueueCapacityManager Capacity { get; }

    public QueueWorkSignal WorkSignal { get; }

    public QueueDeliveryActivation DeliveryActivation { get; }

    public DurableMessageStore MessageStore { get; }

    public ManualTimeProvider TimeProvider { get; }

    public Guid DeviceId { get; }

    public static QueueTestContext Create(
        Action<QueueOptions>? configure = null,
        ISpoolFileSystem? fileSystem = null,
        string? dataDirectory = null)
    {
        dataDirectory ??= Path.Combine(Path.GetTempPath(), "RelayBridge.Tests", Guid.NewGuid().ToString("N"));
        var database = new RelayDatabase(
            new RelayStorageOptions { DataDirectory = dataDirectory },
            AppContext.BaseDirectory);
        database.Initialize();
        var device = new DeviceService(database).AddLegacyDevice(
            "Queue test device",
            ["127.0.0.1"],
            ["scanner@example.com"]);
        var options = new QueueOptions
        {
            Enabled = true,
            PollInterval = TimeSpan.FromMilliseconds(100),
            InitialRetryDelay = TimeSpan.FromSeconds(10),
            MaximumRetryDelay = TimeSpan.FromMinutes(10),
            MaximumMessageAge = TimeSpan.FromHours(24),
            MaximumAttempts = 5,
            MaximumQueuedMessages = 1000,
            MaximumSpoolBytes = 1024L * 1024 * 1024,
            MinimumFreeDiskBytes = 0,
            RetryJitterFactor = 0,
        };
        configure?.Invoke(options);
        options.Validate();
        fileSystem ??= new PhysicalSpoolFileSystem();
        var capacity = new QueueCapacityManager(database, fileSystem, options);
        var signal = new QueueWorkSignal();
        var deliveryActivation = new QueueDeliveryActivation();
        deliveryActivation.Activate();
        var store = new DurableMessageStore(database, fileSystem, capacity, signal);
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero));
        return new QueueTestContext(
            dataDirectory,
            database,
            fileSystem,
            options,
            capacity,
            signal,
            deliveryActivation,
            store,
            timeProvider,
            device.Id);
    }

    public QueueWorker CreateWorker(IMailDeliveryProvider provider)
    {
        return new QueueWorker(
            Database,
            FileSystem,
            provider,
            Options,
            WorkSignal,
            DeliveryActivation,
            TimeProvider,
            NullLogger<QueueWorker>.Instance);
    }

    public QueueReconciler CreateReconciler()
    {
        return new QueueReconciler(
            Database,
            FileSystem,
            Options,
            TimeProvider,
            NullLogger<QueueReconciler>.Instance);
    }

    public async Task<QueuedMessage> EnqueueAsync(int sizeBytes = 64)
    {
        await using var receive = MessageStore.BeginReceive(sizeBytes);
        var buffer = new byte[Math.Min(sizeBytes, 64 * 1024)];
        Array.Fill(buffer, (byte)'x');
        var remaining = sizeBytes;
        while (remaining > 0)
        {
            var count = Math.Min(remaining, buffer.Length);
            receive.EnsureCapacity(sizeBytes - remaining + count);
            await receive.Stream.WriteAsync(buffer.AsMemory(0, count));
            remaining -= count;
        }

        return await receive.CommitAsync(
            DeviceId,
            "scanner@example.com",
            ["recipient@example.net"],
            sizeBytes,
            CancellationToken.None);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(DataDirectory))
        {
            Directory.Delete(DataDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public ManualTimeProvider(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    public override DateTimeOffset GetUtcNow()
    {
        return _utcNow;
    }

    public void Advance(TimeSpan duration)
    {
        _utcNow += duration;
    }
}

internal sealed class ScriptedDeliveryProvider : IMailDeliveryProvider
{
    private readonly Func<QueuedMessage, Stream, CancellationToken, Task<DeliveryResult>> _handler;
    private readonly ConcurrentQueue<Guid> _messageIds = new();

    public ScriptedDeliveryProvider(DeliveryResult result)
        : this((_, _, _) => Task.FromResult(result))
    {
    }

    public ScriptedDeliveryProvider(
        Func<QueuedMessage, Stream, CancellationToken, Task<DeliveryResult>> handler)
    {
        _handler = handler;
    }

    public IReadOnlyList<Guid> MessageIds => _messageIds.ToArray();

    public Task<DeliveryResult> DeliverAsync(
        QueuedMessage message,
        Stream messageContent,
        CancellationToken cancellationToken)
    {
        _messageIds.Enqueue(message.Id);
        return _handler(message, messageContent, cancellationToken);
    }
}

internal sealed class FaultInjectingSpoolFileSystem : ISpoolFileSystem
{
    private readonly PhysicalSpoolFileSystem _inner = new();

    public bool FailCreate { get; set; }

    public bool FailWrite { get; set; }

    public bool FailFlush { get; set; }

    public bool FailMove { get; set; }

    public bool FailDelete { get; set; }

    public bool FailDispose { get; set; }

    public long? AvailableFreeSpace { get; set; }

    public Stream CreateReceiveStream(string path)
    {
        if (FailCreate)
        {
            throw new IOException("Injected spool creation failure.");
        }

        var stream = _inner.CreateReceiveStream(path);
        return FailWrite || FailDispose
            ? new FaultingWriteStream(stream, FailWrite, FailDispose)
            : stream;
    }

    public Task FlushToDiskAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (FailFlush)
        {
            throw new IOException("Injected durable flush failure.");
        }

        return stream is FaultingWriteStream faulting
            ? _inner.FlushToDiskAsync(faulting.Inner, cancellationToken)
            : _inner.FlushToDiskAsync(stream, cancellationToken);
    }

    public Stream OpenRead(string path) => _inner.OpenRead(path);

    public void Move(string sourcePath, string destinationPath)
    {
        if (FailMove)
        {
            throw new IOException("Injected spool promotion failure.");
        }

        _inner.Move(sourcePath, destinationPath);
    }

    public void Delete(string path)
    {
        if (FailDelete)
        {
            throw new IOException("Injected spool deletion failure.");
        }

        _inner.Delete(path);
    }

    public bool Exists(string path) => _inner.Exists(path);

    public IEnumerable<string> EnumerateFiles(string directory, string pattern) =>
        _inner.EnumerateFiles(directory, pattern);

    public DateTimeOffset GetLastWriteTimeUtc(string path) => _inner.GetLastWriteTimeUtc(path);

    public long GetAvailableFreeSpace(string path) => AvailableFreeSpace ?? _inner.GetAvailableFreeSpace(path);

    public bool CanWrite(string directory) => _inner.CanWrite(directory);

    private sealed class FaultingWriteStream : Stream
    {
        private readonly bool _failWrite;
        private readonly bool _failDispose;

        public FaultingWriteStream(Stream inner, bool failWrite, bool failDispose)
        {
            Inner = inner;
            _failWrite = failWrite;
            _failDispose = failDispose;
        }

        public Stream Inner { get; }

        public override bool CanRead => Inner.CanRead;

        public override bool CanSeek => Inner.CanSeek;

        public override bool CanWrite => Inner.CanWrite;

        public override long Length => Inner.Length;

        public override long Position
        {
            get => Inner.Position;
            set => Inner.Position = value;
        }

        public override void Flush() => Inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => Inner.Seek(offset, origin);

        public override void SetLength(long value) => Inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_failWrite)
            {
                throw new IOException("Injected spool write failure.");
            }

            Inner.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _failWrite
                ? ValueTask.FromException(new IOException("Injected spool write failure."))
                : Inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await Inner.DisposeAsync();
            if (_failDispose)
            {
                throw new IOException("Injected spool disposal failure.");
            }

            GC.SuppressFinalize(this);
        }
    }
}

internal sealed class TestLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyList<string> Messages => _messages.ToArray();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _messages.Enqueue(formatter(state, exception));
    }
}
