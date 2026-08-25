// SPDX-License-Identifier: MPL-2.0

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using RelayBridge.Infrastructure.Storage;

namespace RelayBridge.Infrastructure.Smtp;

public sealed class SmtpListener : IAsyncDisposable
{
    private readonly SmtpListenerOptions _options;
    private readonly RelayDatabase _database;
    private readonly DeviceService _devices;
    private readonly DurableMessageStore _messageStore;
    private readonly ILogger<SmtpListener> _logger;
    private readonly ConcurrentDictionary<long, Task> _sessions = new();
    private readonly Dictionary<IPAddress, int> _sourceConnections = new();
    private readonly object _sourceConnectionsLock = new();
    private CancellationTokenSource? _acceptStopping;
    private CancellationTokenSource? _sessionStopping;
    private SemaphoreSlim? _connectionSlots;
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private long _nextSessionId;

    public SmtpListener(
        SmtpListenerOptions options,
        RelayDatabase database,
        DeviceService devices,
        DurableMessageStore messageStore,
        ILogger<SmtpListener> logger)
    {
        _options = options;
        _database = database;
        _devices = devices;
        _messageStore = messageStore;
        _logger = logger;
    }

    public IPEndPoint? BoundEndpoint { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("The SMTP listener is already running.");
        }

        _options.Validate();
        if (!_options.Enabled)
        {
            return Task.CompletedTask;
        }

        _database.Initialize(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _acceptStopping = new CancellationTokenSource();
        _sessionStopping = new CancellationTokenSource();
        _connectionSlots = new SemaphoreSlim(_options.MaxConnections, _options.MaxConnections);
        _listener = new TcpListener(_options.GetListenAddress(), _options.Port);
        _listener.Start(backlog: _options.MaxConnections);
        BoundEndpoint = (IPEndPoint)_listener.LocalEndpoint;
        _acceptLoop = AcceptLoopAsync(_acceptStopping.Token);
        _logger.LogInformation(
            "SmtpListenerStarted Address={Address} Port={Port} MaxConnections={MaxConnections}",
            BoundEndpoint.Address,
            BoundEndpoint.Port,
            _options.MaxConnections);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is null)
        {
            return;
        }

        _acceptStopping?.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_acceptStopping?.IsCancellationRequested == true)
            {
                _acceptLoop = null;
            }
            catch (SocketException) when (_acceptStopping?.IsCancellationRequested == true)
            {
                _acceptLoop = null;
            }
            catch (ObjectDisposedException) when (_acceptStopping?.IsCancellationRequested == true)
            {
                _acceptLoop = null;
            }
        }

        var activeSessions = _sessions.Values.ToArray();
        if (activeSessions.Length > 0)
        {
            try
            {
                await Task.WhenAll(activeSessions).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _sessionStopping?.Cancel();
                try
                {
                    await Task.WhenAll(activeSessions)
                        .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning(
                        "SmtpShutdownTimedOut ActiveSessionCount={ActiveSessionCount}",
                        activeSessions.Length);
                }
            }
        }

        _logger.LogInformation("SmtpListenerStopped");
        _listener = null;
        BoundEndpoint = null;
        _acceptLoop = null;
        _connectionSlots?.Dispose();
        _connectionSlots = null;
        _acceptStopping?.Dispose();
        _acceptStopping = null;
        _sessionStopping?.Dispose();
        _sessionStopping = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _connectionSlots!.WaitAsync(cancellationToken).ConfigureAwait(false);
            TcpClient? client = null;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                client.NoDelay = true;
                var sourceAddress = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                sourceAddress = sourceAddress.IsIPv4MappedToIPv6 ? sourceAddress.MapToIPv4() : sourceAddress;
                if (!TryEnterSource(sourceAddress))
                {
                    await RejectConnectionAsync(client, cancellationToken).ConfigureAwait(false);
                    client.Dispose();
                    _connectionSlots.Release();
                    continue;
                }

                var sessionId = Interlocked.Increment(ref _nextSessionId);
                var sessionTask = RunSessionAsync(client, sourceAddress, _sessionStopping!.Token);
                _sessions[sessionId] = sessionTask;
                _ = ObserveSessionAsync(sessionId, sessionTask);
                client = null;
            }
            catch
            {
                client?.Dispose();
                _connectionSlots.Release();
                throw;
            }
        }
    }

    private async Task RunSessionAsync(
        TcpClient client,
        IPAddress sourceAddress,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("SmtpConnectionAccepted RemoteAddress={RemoteAddress}", sourceAddress);
        try
        {
            var connection = new SmtpConnection(
                client,
                sourceAddress,
                _options,
                _devices,
                _messageStore,
                _logger);
            await connection.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            _logger.LogInformation(
                "SmtpConnectionEnded RemoteAddress={RemoteAddress} ErrorType={ErrorType}",
                sourceAddress,
                exception.GetType().Name);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "SmtpSessionFaulted RemoteAddress={RemoteAddress}", sourceAddress);
        }
        finally
        {
            client.Dispose();
            ExitSource(sourceAddress);
            _connectionSlots!.Release();
        }
    }

    private async Task ObserveSessionAsync(long sessionId, Task sessionTask)
    {
        try
        {
            await sessionTask.ConfigureAwait(false);
        }
        finally
        {
            _sessions.TryRemove(sessionId, out _);
        }
    }

    private bool TryEnterSource(IPAddress sourceAddress)
    {
        lock (_sourceConnectionsLock)
        {
            _sourceConnections.TryGetValue(sourceAddress, out var count);
            if (count >= _options.MaxConnectionsPerIp)
            {
                return false;
            }

            _sourceConnections[sourceAddress] = count + 1;
            return true;
        }
    }

    private void ExitSource(IPAddress sourceAddress)
    {
        lock (_sourceConnectionsLock)
        {
            var count = _sourceConnections[sourceAddress] - 1;
            if (count == 0)
            {
                _sourceConnections.Remove(sourceAddress);
            }
            else
            {
                _sourceConnections[sourceAddress] = count;
            }
        }
    }

    private static async Task RejectConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var response = Encoding.ASCII.GetBytes("421 4.7.0 Too many connections from this address\r\n");
        try
        {
            await client.GetStream().WriteAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            return;
        }
    }
}
