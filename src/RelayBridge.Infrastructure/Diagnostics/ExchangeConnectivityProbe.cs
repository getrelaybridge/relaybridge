// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RelayBridge.Core.Diagnostics;
using RelayBridge.Infrastructure.Microsoft;

namespace RelayBridge.Infrastructure.Diagnostics;

public interface IExchangeConnectivityProbe
{
    Task<ConnectivityDiagnosticSnapshot> RunAsync(CancellationToken cancellationToken = default);
}

internal interface IDiagnosticsAddressResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

internal sealed class SystemDiagnosticsAddressResolver : IDiagnosticsAddressResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);
}

internal sealed record DiagnosticsSmtpEndpoint(
    string Host,
    int Port,
    string TlsTargetHost,
    RemoteCertificateValidationCallback? TestCertificateValidation = null)
{
    internal static DiagnosticsSmtpEndpoint Production { get; } = new(
        ExchangeSmtpOptions.ProductionHost,
        ExchangeSmtpOptions.ProductionPort,
        ExchangeSmtpOptions.ProductionHost);
}

public sealed class ExchangeConnectivityProbe : IExchangeConnectivityProbe
{
    private readonly IDiagnosticsAddressResolver _resolver;
    private readonly DiagnosticsSmtpEndpoint _endpoint;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _totalTimeout;

    public ExchangeConnectivityProbe(TimeProvider timeProvider)
        : this(
            new SystemDiagnosticsAddressResolver(),
            DiagnosticsSmtpEndpoint.Production,
            timeProvider,
            TimeSpan.FromSeconds(15))
    {
    }

    internal ExchangeConnectivityProbe(
        IDiagnosticsAddressResolver resolver,
        DiagnosticsSmtpEndpoint endpoint,
        TimeProvider timeProvider,
        TimeSpan? totalTimeout = null)
    {
        _resolver = resolver;
        _endpoint = endpoint;
        _timeProvider = timeProvider;
        _totalTimeout = totalTimeout ?? TimeSpan.FromSeconds(15);
        if (_totalTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(totalTimeout));
        }
    }

    public async Task<ConnectivityDiagnosticSnapshot> RunAsync(
        CancellationToken cancellationToken = default)
    {
        var observedUtc = _timeProvider.GetUtcNow();
        var stopwatch = Stopwatch.StartNew();
        var stage = ConnectivityProbeStage.Dns;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_totalTimeout);

        try
        {
            var addresses = await _resolver.ResolveAsync(_endpoint.Host, timeout.Token)
                .ConfigureAwait(false);
            if (addresses.Length == 0)
            {
                return Failure(stage, "DNS returned no address for Exchange Online SMTP.", observedUtc, stopwatch.Elapsed);
            }

            stage = ConnectivityProbeStage.Tcp;
            using var client = new TcpClient();
            await client.ConnectAsync(addresses, _endpoint.Port, timeout.Token).ConfigureAwait(false);
            await using Stream stream = client.GetStream();

            stage = ConnectivityProbeStage.Greeting;
            var greeting = await new SmtpResponseReader().ReadAsync(stream, timeout.Token).ConfigureAwait(false);
            if (greeting.Code != 220)
            {
                return Failure(stage, "Exchange Online SMTP did not return a successful greeting.", observedUtc, stopwatch.Elapsed);
            }

            stage = ConnectivityProbeStage.Ehlo;
            await WriteLineAsync(stream, "EHLO relaybridge-diagnostics", timeout.Token).ConfigureAwait(false);
            var ehlo = await new SmtpResponseReader().ReadAsync(stream, timeout.Token).ConfigureAwait(false);
            if (ehlo.Code != 250)
            {
                return Failure(stage, "Exchange Online SMTP did not accept EHLO.", observedUtc, stopwatch.Elapsed);
            }

            stage = ConnectivityProbeStage.StartTls;
            if (!SmtpCapabilities.Parse(ehlo).StartTls)
            {
                return Failure(stage, "Exchange Online SMTP did not advertise STARTTLS.", observedUtc, stopwatch.Elapsed);
            }

            await WriteLineAsync(stream, "STARTTLS", timeout.Token).ConfigureAwait(false);
            var startTls = await new SmtpResponseReader().ReadAsync(stream, timeout.Token).ConfigureAwait(false);
            if (startTls.Code != 220)
            {
                return Failure(stage, "Exchange Online SMTP did not accept STARTTLS.", observedUtc, stopwatch.Elapsed);
            }

            stage = ConnectivityProbeStage.Tls;
            await using var tls = new SslStream(
                stream,
                leaveInnerStreamOpen: false,
                _endpoint.TestCertificateValidation);
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = _endpoint.TlsTargetHost,
                    EnabledSslProtocols = SslProtocols.None,
                    CertificateRevocationCheckMode = X509RevocationMode.Online,
                },
                timeout.Token).ConfigureAwait(false);

            stopwatch.Stop();
            return new ConnectivityDiagnosticSnapshot(
                new DiagnosticEvidence(
                    DiagnosticStatus.Healthy,
                    observedUtc,
                    DiagnosticEvidenceSource.ActiveProbe,
                    "DNS, TCP, SMTP greeting, EHLO, STARTTLS, and the trusted TLS handshake succeeded."),
                ConnectivityProbeStage.Complete,
                true,
                stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure(stage, "The connectivity check timed out.", observedUtc, stopwatch.Elapsed);
        }
        catch (Exception exception) when (exception is SocketException or IOException or
            AuthenticationException or SmtpProtocolException or ObjectDisposedException)
        {
            return Failure(stage, SafeFailure(stage), observedUtc, stopwatch.Elapsed);
        }
    }

    private static async Task WriteLineAsync(
        Stream stream,
        string command,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(command + "\r\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ConnectivityDiagnosticSnapshot Failure(
        ConnectivityProbeStage stage,
        string summary,
        DateTimeOffset observedUtc,
        TimeSpan elapsed) => new(
            new DiagnosticEvidence(
                DiagnosticStatus.Attention,
                observedUtc,
                DiagnosticEvidenceSource.ActiveProbe,
                summary),
            stage,
            false,
            elapsed);

    private static string SafeFailure(ConnectivityProbeStage stage) => stage switch
    {
        ConnectivityProbeStage.Dns => "DNS resolution for Exchange Online SMTP failed.",
        ConnectivityProbeStage.Tcp => "A TCP connection to Exchange Online SMTP port 587 could not be established.",
        ConnectivityProbeStage.Greeting => "The SMTP greeting could not be read safely.",
        ConnectivityProbeStage.Ehlo => "The SMTP EHLO response was unavailable or malformed.",
        ConnectivityProbeStage.StartTls => "The SMTP STARTTLS transition failed.",
        ConnectivityProbeStage.Tls => "The trusted TLS handshake failed.",
        _ => "The connectivity check did not complete.",
    };
}
