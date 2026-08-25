// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RelayBridge.Core.Diagnostics;
using RelayBridge.Infrastructure.Diagnostics;
using Xunit;

namespace RelayBridge.IntegrationTests;

public sealed class DiagnosticsConnectivityTests
{
    [Fact]
    public async Task Probe_completes_dns_ehlo_starttls_and_tls_without_authentication()
    {
        await using var server = new FakeExchangeSmtpServer();
        server.Start();
        var deliveryEndpoint = server.CreateEndpoint();
        var probe = new ExchangeConnectivityProbe(
            new FixedResolver(),
            new DiagnosticsSmtpEndpoint(
                deliveryEndpoint.Host,
                deliveryEndpoint.Port,
                deliveryEndpoint.TlsTargetHost,
                deliveryEndpoint.TestCertificateValidation),
            TimeProvider.System,
            TimeSpan.FromSeconds(3));

        var result = await probe.RunAsync();

        Assert.True(result.Succeeded, $"{result.Stage}: {result.Evidence.Summary}; server={server.Fault}");
        Assert.Equal(DiagnosticStatus.Healthy, result.Evidence.Status);
        Assert.Equal(DiagnosticEvidenceSource.ActiveProbe, result.Evidence.Source);
        Assert.Equal(ConnectivityProbeStage.Complete, result.Stage);
        Assert.True(result.Succeeded);
        Assert.Equal(["EHLO relaybridge-diagnostics", "STARTTLS"], server.Commands);
    }

    [Fact]
    public async Task Probe_reports_absent_starttls_at_the_exact_stage()
    {
        await using var server = await DiagnosticSmtpServer.StartAsync(DiagnosticServerBehavior.NoStartTls);
        var result = await CreateProbe(server.Port).RunAsync();

        Assert.Equal(DiagnosticStatus.Attention, result.Evidence.Status);
        Assert.Equal(ConnectivityProbeStage.StartTls, result.Stage);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Probe_rejects_an_untrusted_tls_certificate()
    {
        await using var server = new FakeExchangeSmtpServer();
        server.Start();
        var deliveryEndpoint = server.CreateEndpoint(trustTestCertificate: false);
        var result = await new ExchangeConnectivityProbe(
            new FixedResolver(),
            new DiagnosticsSmtpEndpoint(
                deliveryEndpoint.Host,
                deliveryEndpoint.Port,
                deliveryEndpoint.TlsTargetHost),
            TimeProvider.System,
            TimeSpan.FromSeconds(3)).RunAsync();

        Assert.Equal(DiagnosticStatus.Attention, result.Evidence.Status);
        Assert.Equal(ConnectivityProbeStage.Tls, result.Stage);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Probe_reports_dns_and_tcp_failures_separately()
    {
        var dns = new ExchangeConnectivityProbe(
            new ThrowingResolver(),
            new DiagnosticsSmtpEndpoint("test.invalid", 587, "test.invalid"),
            TimeProvider.System,
            TimeSpan.FromSeconds(1));
        var dnsResult = await dns.RunAsync();

        var unusedPort = GetUnusedPort();
        var tcpResult = await CreateProbe(unusedPort).RunAsync();

        Assert.Equal(ConnectivityProbeStage.Dns, dnsResult.Stage);
        Assert.Equal(ConnectivityProbeStage.Tcp, tcpResult.Stage);
    }

    [Fact]
    public async Task Probe_bounds_timeout_and_honors_caller_cancellation()
    {
        await using var timeoutServer = await DiagnosticSmtpServer.StartAsync(DiagnosticServerBehavior.StallGreeting);
        var timeoutProbe = CreateProbe(timeoutServer.Port, totalTimeout: TimeSpan.FromMilliseconds(150));
        var timedOut = await timeoutProbe.RunAsync();

        Assert.Equal(ConnectivityProbeStage.Greeting, timedOut.Stage);
        Assert.Contains("timed out", timedOut.Evidence.Summary, StringComparison.OrdinalIgnoreCase);

        await using var cancellationServer = await DiagnosticSmtpServer.StartAsync(DiagnosticServerBehavior.StallGreeting);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateProbe(cancellationServer.Port).RunAsync(cancellation.Token));
    }

    [Fact]
    public async Task Probe_rejects_oversized_or_malformed_smtp_replies()
    {
        await using var server = await DiagnosticSmtpServer.StartAsync(DiagnosticServerBehavior.OversizedGreeting);
        var result = await CreateProbe(server.Port).RunAsync();

        Assert.Equal(ConnectivityProbeStage.Greeting, result.Stage);
        Assert.Equal(DiagnosticStatus.Attention, result.Evidence.Status);
    }

    private static ExchangeConnectivityProbe CreateProbe(
        int port,
        RemoteCertificateValidationCallback? certificateValidation = null,
        TimeSpan? totalTimeout = null) => new(
            new FixedResolver(),
            new DiagnosticsSmtpEndpoint("localhost", port, "localhost", certificateValidation),
            TimeProvider.System,
            totalTimeout ?? TimeSpan.FromSeconds(3));

    private static int GetUnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class FixedResolver : IDiagnosticsAddressResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(new[] { IPAddress.Loopback });
    }

    private sealed class ThrowingResolver : IDiagnosticsAddressResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            throw new SocketException((int)SocketError.HostNotFound);
    }

    private enum DiagnosticServerBehavior
    {
        Success,
        NoStartTls,
        StallGreeting,
        OversizedGreeting,
    }

    private sealed class DiagnosticSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _serverTask;
        private readonly X509Certificate2 _certificate;
        private readonly List<string> _commands = [];

        private DiagnosticSmtpServer(TcpListener listener, DiagnosticServerBehavior behavior)
        {
            _listener = listener;
            _certificate = CreateCertificate();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _serverTask = RunAsync(behavior, _stopping.Token);
        }

        internal int Port { get; }

        internal Exception? Fault { get; private set; }

        internal IReadOnlyList<string> Commands
        {
            get
            {
                lock (_commands)
                {
                    return _commands.ToArray();
                }
            }
        }

        internal static Task<DiagnosticSmtpServer> StartAsync(DiagnosticServerBehavior behavior)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new DiagnosticSmtpServer(listener, behavior));
        }

        public async ValueTask DisposeAsync()
        {
            _stopping.Cancel();
            _listener.Stop();
            try
            {
                await _serverTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (exception is OperationCanceledException or
                ObjectDisposedException or SocketException or TimeoutException or IOException)
            {
            }
            _certificate.Dispose();
            _stopping.Dispose();
        }

        private async Task RunAsync(DiagnosticServerBehavior behavior, CancellationToken cancellationToken)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                await using var stream = client.GetStream();
                if (behavior == DiagnosticServerBehavior.StallGreeting)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return;
                }

                if (behavior == DiagnosticServerBehavior.OversizedGreeting)
                {
                    await WriteAsync(stream, "220 " + new string('A', 3000) + "\r\n", cancellationToken);
                    return;
                }

                await WriteAsync(stream, "220 diagnostic.example ESMTP\r\n", cancellationToken);
                Record(await ReadLineAsync(stream, cancellationToken));
                if (behavior == DiagnosticServerBehavior.NoStartTls)
                {
                    await WriteAsync(stream, "250-diagnostic.example\r\n250 SIZE 1000\r\n", cancellationToken);
                    return;
                }

                await WriteAsync(
                    stream,
                    "250-diagnostic.example\r\n250-PIPELINING\r\n250 STARTTLS\r\n",
                    cancellationToken);
                Record(await ReadLineAsync(stream, cancellationToken));
                await WriteAsync(stream, "220 2.0.0 Ready to start TLS\r\n", cancellationToken);
                await using var tls = new SslStream(stream, leaveInnerStreamOpen: false);
                await tls.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate,
                        EnabledSslProtocols = System.Security.Authentication.SslProtocols.None,
                    },
                    cancellationToken);
            }
            catch (Exception exception)
            {
                Fault = exception;
                throw;
            }
        }

        private void Record(string command)
        {
            lock (_commands)
            {
                _commands.Add(command);
            }
        }

        private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
        {
            var bytes = new List<byte>();
            while (bytes.Count < 4096)
            {
                var buffer = new byte[1];
                if (await stream.ReadAsync(buffer, cancellationToken) == 0)
                {
                    throw new IOException("Client disconnected.");
                }
                bytes.Add(buffer[0]);
                if (bytes.Count >= 2 && bytes[^2] == '\r' && bytes[^1] == '\n')
                {
                    return Encoding.ASCII.GetString([.. bytes.Take(bytes.Count - 2)]);
                }
            }
            throw new IOException("Command exceeded test limit.");
        }

        private static Task WriteAsync(Stream stream, string value, CancellationToken cancellationToken) =>
            stream.WriteAsync(Encoding.ASCII.GetBytes(value), cancellationToken).AsTask();

        private static X509Certificate2 CreateCertificate()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=localhost",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                true));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
            using var generated = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddDays(1));
            var pfx = generated.Export(X509ContentType.Pfx);
            try
            {
                return X509CertificateLoader.LoadPkcs12(
                    pfx,
                    password: null,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }
        }
    }
}
