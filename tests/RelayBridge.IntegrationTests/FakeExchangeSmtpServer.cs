// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RelayBridge.Infrastructure.Microsoft;

namespace RelayBridge.IntegrationTests;

public enum FakeExchangeDisconnect
{
    None,
    BeforeGreeting,
    AfterEhlo,
    DuringTls,
    AfterAuth,
    AfterMail,
    DuringRecipient,
    DuringData,
    AfterDataTerminator,
    AfterFinalResponse,
}

internal sealed class FakeExchangeScenario
{
    public string Greeting { get; set; } = "220 fake.exchange ESMTP";

    public IReadOnlyList<string> PreTlsEhlo { get; set; } =
        ["250-fake.exchange", "250-STARTTLS", "250 SIZE 36700160"];

    public string StartTls { get; set; } = "220 2.0.0 Ready to start TLS";

    public IReadOnlyList<string> PostTlsEhlo { get; set; } =
        ["250-fake.exchange", "250-AUTH XOAUTH2", "250 SIZE 36700160"];

    public string AuthChallenge { get; set; } = "334 ";

    public string AuthResult { get; set; } = "235 2.7.0 Authentication successful";

    public string MailResult { get; set; } = "250 2.1.0 Sender accepted";

    public IReadOnlyList<string> RecipientResults { get; set; } = ["250 2.1.5 Recipient accepted"];

    public string DataResult { get; set; } = "354 Start mail input";

    public string FinalResult { get; set; } = "250 2.0.0 Message accepted";

    public FakeExchangeDisconnect Disconnect { get; set; }

    public TimeSpan FinalResponseDelay { get; set; }

    public Task? FinalResponseRelease { get; set; }
}

internal sealed class FakeExchangeSmtpServer : IAsyncDisposable
{
    private readonly FakeExchangeScenario _scenario;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly X509Certificate2 _certificate;
    private readonly List<string> _commands = [];
    private Task? _runTask;

    public FakeExchangeSmtpServer(FakeExchangeScenario? scenario = null)
    {
        _scenario = scenario ?? new FakeExchangeScenario();
        _certificate = CreateCertificate();
    }

    public IReadOnlyList<string> Commands
    {
        get
        {
            lock (_commands)
            {
                return _commands.ToArray();
            }
        }
    }

    public string? AuthenticationPayload { get; private set; }

    public string? MailCommand { get; private set; }

    public bool DataCommandReceived { get; private set; }

    public bool RsetReceived { get; private set; }

    public bool QuitReceived { get; private set; }

    public long ReceivedDataLength { get; private set; }

    public byte[]? ReceivedData { get; private set; }

    public byte[]? ReceivedDataHash { get; private set; }

    public Exception? Fault { get; private set; }

    public TaskCompletionSource DataReceivedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _runTask = RunAsync(_stop.Token);
    }

    public ExchangeSmtpEndpoint CreateEndpoint(bool trustTestCertificate = true)
    {
        RemoteCertificateValidationCallback? callback = trustTestCertificate
            ? (_, certificate, _, _) => certificate is not null &&
                string.Equals(certificate.GetCertHashString(), _certificate.Thumbprint, StringComparison.OrdinalIgnoreCase)
            : null;
        return new ExchangeSmtpEndpoint("127.0.0.1", Port, "localhost", callback);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        if (_runTask is not null)
        {
            try
            {
                await _runTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or TimeoutException or IOException or SocketException or AuthenticationException)
            {
            }
        }

        _stop.Dispose();
        _certificate.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
            Stream stream = client.GetStream();
            if (_scenario.Disconnect == FakeExchangeDisconnect.BeforeGreeting)
            {
                return;
            }

            await WriteLineAsync(stream, _scenario.Greeting, cancellationToken);
            AddCommand(await ReadLineAsync(stream, cancellationToken));
            await WriteLinesAsync(stream, _scenario.PreTlsEhlo, cancellationToken);
            if (_scenario.Disconnect == FakeExchangeDisconnect.AfterEhlo)
            {
                return;
            }

            var startTls = await ReadLineAsync(stream, cancellationToken);
            AddCommand(startTls);
            if (!string.Equals(startTls, "STARTTLS", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await WriteLineAsync(stream, _scenario.StartTls, cancellationToken);
            if (!StartsWithCode(_scenario.StartTls, 220) || _scenario.Disconnect == FakeExchangeDisconnect.DuringTls)
            {
                return;
            }

            var sslStream = new SslStream(stream, leaveInnerStreamOpen: false);
            await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.None,
            }, cancellationToken);
            stream = sslStream;

            AddCommand(await ReadLineAsync(stream, cancellationToken));
            await WriteLinesAsync(stream, _scenario.PostTlsEhlo, cancellationToken);
            var auth = await ReadLineAsync(stream, cancellationToken);
            AddCommand(auth.StartsWith("AUTH XOAUTH2", StringComparison.OrdinalIgnoreCase)
                ? "AUTH XOAUTH2 [REDACTED]"
                : auth);
            if (!auth.StartsWith("AUTH XOAUTH2", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await WriteLineAsync(stream, _scenario.AuthChallenge, cancellationToken);
            if (StartsWithCode(_scenario.AuthChallenge, 334))
            {
                var encoded = await ReadLineAsync(stream, cancellationToken);
                AuthenticationPayload = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                await WriteLineAsync(stream, _scenario.AuthResult, cancellationToken);
            }

            if (!StartsWithCode(_scenario.AuthResult, 235) || _scenario.Disconnect == FakeExchangeDisconnect.AfterAuth)
            {
                return;
            }

            MailCommand = await ReadLineAsync(stream, cancellationToken);
            AddCommand(MailCommand);
            if (string.Equals(MailCommand, "QUIT", StringComparison.OrdinalIgnoreCase))
            {
                QuitReceived = true;
                MailCommand = null;
                await WriteLineAsync(stream, "221 2.0.0 Bye", cancellationToken);
                return;
            }

            await WriteLineAsync(stream, _scenario.MailResult, cancellationToken);
            if (!IsPositiveCompletion(_scenario.MailResult) || _scenario.Disconnect == FakeExchangeDisconnect.AfterMail)
            {
                return;
            }

            var recipientRejected = false;
            for (var index = 0; index < _scenario.RecipientResults.Count; index++)
            {
                var recipient = await ReadLineAsync(stream, cancellationToken);
                AddCommand(recipient);
                await WriteLineAsync(stream, _scenario.RecipientResults[index], cancellationToken);
                recipientRejected |= !IsPositiveCompletion(_scenario.RecipientResults[index]);
                if (_scenario.Disconnect == FakeExchangeDisconnect.DuringRecipient)
                {
                    return;
                }
            }

            var next = await ReadLineAsync(stream, cancellationToken);
            AddCommand(next);
            if (recipientRejected)
            {
                RsetReceived = string.Equals(next, "RSET", StringComparison.OrdinalIgnoreCase);
                if (RsetReceived)
                {
                    await WriteLineAsync(stream, "250 2.0.0 Reset", cancellationToken);
                }

                return;
            }

            DataCommandReceived = string.Equals(next, "DATA", StringComparison.OrdinalIgnoreCase);
            await WriteLineAsync(stream, _scenario.DataResult, cancellationToken);
            if (!StartsWithCode(_scenario.DataResult, 354))
            {
                return;
            }

            if (_scenario.Disconnect == FakeExchangeDisconnect.DuringData)
            {
                var buffer = new byte[1024];
                _ = await stream.ReadAsync(buffer, cancellationToken);
                return;
            }

            await ReadDataAsync(stream, cancellationToken);
            DataReceivedSignal.TrySetResult();
            if (_scenario.Disconnect == FakeExchangeDisconnect.AfterDataTerminator)
            {
                return;
            }

            if (_scenario.FinalResponseDelay > TimeSpan.Zero)
            {
                await Task.Delay(_scenario.FinalResponseDelay, cancellationToken);
            }

            if (_scenario.FinalResponseRelease is not null)
            {
                await _scenario.FinalResponseRelease.WaitAsync(cancellationToken);
            }

            await WriteLineAsync(stream, _scenario.FinalResult, cancellationToken);
            if (_scenario.Disconnect == FakeExchangeDisconnect.AfterFinalResponse ||
                !StartsWithCode(_scenario.FinalResult, 250))
            {
                return;
            }

            var quit = await ReadLineAsync(stream, cancellationToken);
            AddCommand(quit);
            QuitReceived = string.Equals(quit, "QUIT", StringComparison.OrdinalIgnoreCase);
            if (QuitReceived)
            {
                await WriteLineAsync(stream, "221 2.0.0 Bye", cancellationToken);
            }
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or IOException or SocketException or AuthenticationException or FormatException)
        {
            Fault = exception;
            DataReceivedSignal.TrySetCanceled(cancellationToken);
        }
    }

    private async Task ReadDataAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        MemoryStream? capture = new();
        long length = 0;
        while (true)
        {
            var line = await ReadLineBytesAsync(stream, 1024 * 1024, cancellationToken);
            if (line.AsSpan().SequenceEqual("."u8))
            {
                break;
            }

            var content = line.Length >= 2 && line[0] == (byte)'.' && line[1] == (byte)'.'
                ? line.AsSpan(1)
                : line.AsSpan();
            hash.AppendData(content);
            hash.AppendData("\r\n"u8);
            length += content.Length + 2;
            if (capture is not null)
            {
                if (length <= 1024 * 1024)
                {
                    capture.Write(content);
                    capture.Write("\r\n"u8);
                }
                else
                {
                    capture.Dispose();
                    capture = null;
                }
            }
        }

        ReceivedDataLength = length;
        ReceivedDataHash = hash.GetHashAndReset();
        ReceivedData = capture?.ToArray();
        capture?.Dispose();
    }

    private void AddCommand(string command)
    {
        lock (_commands)
        {
            _commands.Add(command);
        }
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        return Encoding.ASCII.GetString(await ReadLineBytesAsync(stream, 64 * 1024, cancellationToken));
    }

    private static async Task<byte[]> ReadLineBytesAsync(
        Stream stream,
        int maximumLength,
        CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        var one = new byte[1];
        var previousCr = false;
        while (true)
        {
            var read = await stream.ReadAsync(one, cancellationToken);
            if (read == 0)
            {
                throw new IOException("Test SMTP client disconnected.");
            }

            if (previousCr)
            {
                if (one[0] != (byte)'\n')
                {
                    throw new IOException("Test SMTP command did not use CRLF.");
                }

                return line.ToArray();
            }

            if (one[0] == (byte)'\r')
            {
                previousCr = true;
                continue;
            }

            if (line.Length >= maximumLength)
            {
                throw new IOException("Test SMTP line exceeded its limit.");
            }

            line.WriteByte(one[0]);
        }
    }

    private static Task WriteLineAsync(Stream stream, string value, CancellationToken cancellationToken)
    {
        return stream.WriteAsync(Encoding.ASCII.GetBytes($"{value}\r\n"), cancellationToken).AsTask();
    }

    private static async Task WriteLinesAsync(
        Stream stream,
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken)
    {
        foreach (var line in lines)
        {
            await WriteLineAsync(stream, line, cancellationToken);
        }
    }

    private static bool StartsWithCode(string response, int code)
    {
        return response.StartsWith(code.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static bool IsPositiveCompletion(string response)
    {
        return response.Length >= 1 && response[0] == '2';
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(7));
        var pfx = generated.Export(X509ContentType.Pfx);
        try
        {
            return X509CertificateLoader.LoadPkcs12(
                pfx,
                password: null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }
}
