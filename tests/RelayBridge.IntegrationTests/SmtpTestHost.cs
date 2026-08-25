// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RelayBridge.Infrastructure.Queue;
using RelayBridge.Infrastructure.Smtp;
using RelayBridge.Infrastructure.Storage;

namespace RelayBridge.IntegrationTests;

internal sealed class SmtpTestHost : IAsyncDisposable
{
    private readonly bool _deleteOnDispose;
    private SmtpListener _listener;

    private SmtpTestHost(
        string dataDirectory,
        bool deleteOnDispose,
        SmtpListenerOptions options,
        RelayDatabase database,
        DeviceService devices,
        ISpoolFileSystem fileSystem,
        QueueOptions queueOptions,
        QueueCapacityManager capacity,
        QueueWorkSignal workSignal,
        SmtpListener listener,
        CaptureLogger logger)
    {
        DataDirectory = dataDirectory;
        _deleteOnDispose = deleteOnDispose;
        Options = options;
        Database = database;
        Devices = devices;
        FileSystem = fileSystem;
        QueueOptions = queueOptions;
        Capacity = capacity;
        WorkSignal = workSignal;
        _listener = listener;
        Logger = logger;
    }

    public string DataDirectory { get; }

    public SmtpListenerOptions Options { get; }

    public RelayDatabase Database { get; private set; }

    public DeviceService Devices { get; private set; }

    public ISpoolFileSystem FileSystem { get; }

    public QueueOptions QueueOptions { get; }

    public QueueCapacityManager Capacity { get; private set; }

    public QueueWorkSignal WorkSignal { get; }

    public CaptureLogger Logger { get; }

    public LocalQueuePreview Preview => new(Database);

    public IPEndPoint Endpoint => _listener.BoundEndpoint
        ?? throw new InvalidOperationException("SMTP listener is not running.");

    public static async Task<SmtpTestHost> CreateAsync(
        Action<SmtpListenerOptions>? configure = null,
        string? dataDirectory = null,
        bool deleteOnDispose = true,
        Action<QueueOptions>? configureQueue = null,
        ISpoolFileSystem? fileSystem = null)
    {
        dataDirectory ??= Path.Combine(
            Path.GetTempPath(),
            "RelayBridge.Tests",
            Guid.NewGuid().ToString("N"));
        var options = new SmtpListenerOptions
        {
            ListenAddress = IPAddress.Loopback.ToString(),
            Port = 0,
            AllowEphemeralPortForTests = true,
            AllowCleartextAuthentication = true,
            AllowInsecureLoopbackAuthenticationForTests = true,
            MaxConnections = 8,
            MaxConnectionsPerIp = 8,
            IdleTimeout = TimeSpan.FromSeconds(10),
            MaxMessageBytes = 2 * 1024 * 1024,
        };
        configure?.Invoke(options);
        var queueOptions = new QueueOptions
        {
            MaximumQueuedMessages = 1000,
            MaximumSpoolBytes = 1024L * 1024 * 1024,
            MinimumFreeDiskBytes = 0,
            RetryJitterFactor = 0,
        };
        configureQueue?.Invoke(queueOptions);
        queueOptions.Validate();

        var database = CreateDatabase(dataDirectory);
        database.Initialize();
        fileSystem ??= new PhysicalSpoolFileSystem();
        var capacity = new QueueCapacityManager(database, fileSystem, queueOptions);
        var workSignal = new QueueWorkSignal();
        var devices = new DeviceService(database);
        var logger = new CaptureLogger();
        var listener = CreateListener(
            options,
            database,
            devices,
            new DurableMessageStore(database, fileSystem, capacity, workSignal),
            logger);
        await listener.StartAsync();
        return new SmtpTestHost(
            dataDirectory,
            deleteOnDispose,
            options,
            database,
            devices,
            fileSystem,
            queueOptions,
            capacity,
            workSignal,
            listener,
            logger);
    }

    public async Task<SmtpTestClient> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var client = new SmtpTestClient();
        await client.ConnectAsync(Endpoint, cancellationToken);
        return client;
    }

    public async Task RestartAsync()
    {
        await _listener.StopAsync();
        await _listener.DisposeAsync();
        Database = CreateDatabase(DataDirectory);
        Database.Initialize();
        Capacity = new QueueCapacityManager(Database, FileSystem, QueueOptions);
        Devices = new DeviceService(Database);
        _listener = CreateListener(
            Options,
            Database,
            Devices,
            new DurableMessageStore(Database, FileSystem, Capacity, WorkSignal),
            Logger);
        await _listener.StartAsync();
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return _listener.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _listener.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (_deleteOnDispose && Directory.Exists(DataDirectory))
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
    }

    private static RelayDatabase CreateDatabase(string dataDirectory)
    {
        return new RelayDatabase(
            new RelayStorageOptions { DataDirectory = dataDirectory },
            AppContext.BaseDirectory);
    }

    private static SmtpListener CreateListener(
        SmtpListenerOptions options,
        RelayDatabase database,
        DeviceService devices,
        DurableMessageStore messageStore,
        CaptureLogger logger)
    {
        return new SmtpListener(
            options,
            database,
            devices,
            messageStore,
            logger);
    }
}

internal sealed class CaptureLogger : ILogger<SmtpListener>
{
    private readonly List<string> _messages = [];
    private readonly object _lock = new();

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_lock)
            {
                return _messages.ToArray();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_lock)
        {
            _messages.Add(formatter(state, exception));
        }
    }
}

internal sealed class SmtpTestClient : IAsyncDisposable
{
    private readonly TcpClient _client = new();
    private StreamReader? _reader;

    public NetworkStream Stream => _client.GetStream();

    public async Task ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        await _client.ConnectAsync(endpoint.Address, endpoint.Port, cancellationToken);
        _reader = new StreamReader(
            Stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
    }

    public async Task<string> ReadResponseAsync(CancellationToken cancellationToken = default)
    {
        var first = await ReadLineAsync(cancellationToken);
        if (first.Length < 4 || first[3] != '-')
        {
            return first;
        }

        var code = first[..3];
        var lines = new List<string> { first };
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken);
            lines.Add(line);
            if (line.StartsWith($"{code} ", StringComparison.Ordinal))
            {
                return string.Join("\n", lines);
            }
        }
    }

    public async Task<string> CommandAsync(string command, CancellationToken cancellationToken = default)
    {
        await SendLineAsync(command, cancellationToken);
        return await ReadResponseAsync(cancellationToken);
    }

    public async Task SendLineAsync(string line, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.ASCII.GetBytes($"{line}\r\n");
        await Stream.WriteAsync(bytes, cancellationToken);
        await Stream.FlushAsync(cancellationToken);
    }

    public async Task SendBytesAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        await Stream.WriteAsync(bytes, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _reader?.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        if (_reader is null)
        {
            throw new InvalidOperationException("Client is not connected.");
        }

        return await _reader.ReadLineAsync(cancellationToken)
            ?? throw new IOException("SMTP server closed the connection.");
    }
}
