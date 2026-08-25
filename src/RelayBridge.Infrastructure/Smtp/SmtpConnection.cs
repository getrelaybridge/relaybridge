// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RelayBridge.Core.Devices;
using RelayBridge.Infrastructure.Queue;
using RelayBridge.Infrastructure.Storage;

namespace RelayBridge.Infrastructure.Smtp;

internal sealed class SmtpConnection
{
    private static readonly byte[] CrLf = "\r\n"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly TcpClient _client;
    private readonly IPAddress _sourceAddress;
    private readonly SmtpListenerOptions _options;
    private readonly DeviceService _devices;
    private readonly DurableMessageStore _messageStore;
    private readonly ILogger _logger;
    private readonly List<string> _recipients = [];
    private DeviceDefinition? _device;
    private string? _mailFrom;
    private long? _declaredSizeBytes;
    private bool _greeted;
    private bool _extendedGreeting;
    private int _authenticationFailures;

    public SmtpConnection(
        TcpClient client,
        IPAddress sourceAddress,
        SmtpListenerOptions options,
        DeviceService devices,
        DurableMessageStore messageStore,
        ILogger logger)
    {
        _client = client;
        _sourceAddress = sourceAddress;
        _options = options;
        _devices = devices;
        _messageStore = messageStore;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var stream = _client.GetStream();
        using var reader = new BufferedSmtpReader(stream, _options.IdleTimeout);
        await WriteReplyAsync(stream, $"220 {_options.ServerName} ESMTP RelayBridge", cancellationToken)
            .ConfigureAwait(false);

        for (var commandCount = 0; commandCount < _options.MaxCommandsPerSession; commandCount++)
        {
            ReadOnlyMemory<byte>? line;
            try
            {
                line = await reader.ReadLineAsync(_options.MaxCommandLength, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await TryWriteReplyAsync(stream, "421 4.4.2 Idle timeout", cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (SmtpLineTooLongException)
            {
                await TryWriteReplyAsync(stream, "500 5.5.2 Command line too long", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (SmtpProtocolException)
            {
                await TryWriteReplyAsync(stream, "500 5.5.2 Invalid SMTP line ending", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (line is null)
            {
                return;
            }

            if (!TryDecodeCommand(line.Value.Span, out var command))
            {
                await WriteReplyAsync(stream, "500 5.5.2 Commands must be ASCII", cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var verbEnd = command.IndexOf(' ');
            var verb = (verbEnd < 0 ? command : command[..verbEnd]).ToUpperInvariant();
            switch (verb)
            {
                case "EHLO":
                    if (!HasSingleArgument(command))
                    {
                        await WriteReplyAsync(stream, "501 5.5.4 EHLO requires a domain", cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }

                    _greeted = true;
                    _extendedGreeting = true;
                    ResetEnvelope();
                    var capabilities = _options.AllowCleartextAuthentication
                        ? $"250-{_options.ServerName}\r\n250-SIZE {_options.MaxMessageBytes.ToString(CultureInfo.InvariantCulture)}\r\n250 AUTH PLAIN LOGIN"
                        : $"250-{_options.ServerName}\r\n250 SIZE {_options.MaxMessageBytes.ToString(CultureInfo.InvariantCulture)}";
                    await WriteReplyAsync(stream, capabilities, cancellationToken).ConfigureAwait(false);
                    break;

                case "HELO":
                    if (!HasSingleArgument(command))
                    {
                        await WriteReplyAsync(stream, "501 5.5.4 HELO requires a domain", cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }

                    _greeted = true;
                    _extendedGreeting = false;
                    ResetEnvelope();
                    await WriteReplyAsync(stream, $"250 {_options.ServerName}", cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case "AUTH":
                    if (!await HandleAuthAsync(command, stream, reader, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    break;

                case "MAIL":
                    await HandleMailAsync(command, stream, cancellationToken).ConfigureAwait(false);
                    break;

                case "RCPT":
                    await HandleRecipientAsync(command, stream, cancellationToken).ConfigureAwait(false);
                    break;

                case "DATA":
                    if (!await HandleDataAsync(command, stream, reader, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    break;

                case "RSET":
                    ResetEnvelope();
                    await WriteReplyAsync(stream, "250 2.0.0 Reset", cancellationToken).ConfigureAwait(false);
                    break;

                case "NOOP":
                    await WriteReplyAsync(stream, "250 2.0.0 OK", cancellationToken).ConfigureAwait(false);
                    break;

                case "QUIT":
                    await WriteReplyAsync(stream, "221 2.0.0 Bye", cancellationToken).ConfigureAwait(false);
                    return;

                case "STARTTLS":
                    await WriteReplyAsync(stream, "502 5.5.1 STARTTLS is not available", cancellationToken)
                        .ConfigureAwait(false);
                    break;

                default:
                    await WriteReplyAsync(stream, "500 5.5.1 Command unrecognized", cancellationToken)
                        .ConfigureAwait(false);
                    break;
            }
        }

        await TryWriteReplyAsync(stream, "421 4.7.0 Too many commands", cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> HandleAuthAsync(
        string command,
        Stream stream,
        BufferedSmtpReader reader,
        CancellationToken cancellationToken)
    {
        if (!_greeted)
        {
            await WriteReplyAsync(stream, "503 5.5.1 Send HELO or EHLO first", cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        if (!_extendedGreeting)
        {
            await WriteReplyAsync(stream, "503 5.5.1 Send EHLO before AUTH", cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        if (_mailFrom is not null || _device is not null)
        {
            await WriteReplyAsync(stream, "503 5.5.1 AUTH not permitted now", cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        if (!_options.AllowCleartextAuthentication)
        {
            await WriteReplyAsync(
                stream,
                "538 5.7.11 Encryption required for requested authentication mechanism",
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        var parts = command.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await WriteReplyAsync(stream, "501 5.5.4 AUTH mechanism required", cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        if (parts[1].ToUpperInvariant() is not ("PLAIN" or "LOGIN"))
        {
            await WriteReplyAsync(stream, "504 5.5.4 Authentication mechanism unsupported", cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        AuthenticationReadResult authentication;
        try
        {
            authentication = parts[1].ToUpperInvariant() switch
            {
                "PLAIN" => await ReadPlainCredentialsAsync(
                    parts.Length == 3 ? parts[2] : null,
                    stream,
                    reader,
                    cancellationToken).ConfigureAwait(false),
                "LOGIN" => await ReadLoginCredentialsAsync(
                    parts.Length == 3 ? parts[2] : null,
                    stream,
                    reader,
                    cancellationToken).ConfigureAwait(false),
                _ => throw new UnreachableException(),
            };
        }
        catch (SmtpLineTooLongException)
        {
            await TryWriteReplyAsync(stream, "500 5.5.6 Authentication exchange line too long", cancellationToken)
                .ConfigureAwait(false);
            return false;
        }
        catch (SmtpProtocolException)
        {
            await TryWriteReplyAsync(stream, "500 5.5.2 Invalid SMTP line ending", cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        if (authentication.FailureReply is not null)
        {
            await WriteReplyAsync(stream, authentication.FailureReply, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (authentication.Credentials is null)
        {
            return await AuthenticationFailedAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        _device = await _devices.AuthenticateAsync(
            authentication.Credentials.Username,
            authentication.Credentials.Password,
            _sourceAddress,
            cancellationToken).ConfigureAwait(false);
        if (_device is null)
        {
            return await AuthenticationFailedAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "DeviceAuthenticated DeviceId={DeviceId} RemoteAddress={RemoteAddress}",
            _device.Id,
            _sourceAddress);
        await WriteReplyAsync(stream, "235 2.7.0 Authentication successful", cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private async Task<bool> AuthenticationFailedAsync(Stream stream, CancellationToken cancellationToken)
    {
        _authenticationFailures++;
        _logger.LogWarning(
            "DeviceAuthenticationFailed RemoteAddress={RemoteAddress} Attempt={Attempt}",
            _sourceAddress,
            _authenticationFailures);
        await WriteReplyAsync(stream, "535 5.7.8 Authentication credentials invalid", cancellationToken)
            .ConfigureAwait(false);
        return _authenticationFailures < _options.MaxAuthenticationAttempts;
    }

    private async Task HandleMailAsync(string command, Stream stream, CancellationToken cancellationToken)
    {
        if (!_greeted)
        {
            await WriteReplyAsync(stream, "503 5.5.1 Send HELO or EHLO first", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (_mailFrom is not null)
        {
            await WriteReplyAsync(stream, "503 5.5.1 Nested MAIL command", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var parseResult = TryParsePathCommand(command, "MAIL FROM:", allowSize: true, out var sender, out var size);
        if (parseResult != PathParseResult.Success)
        {
            await WriteReplyAsync(
                stream,
                parseResult == PathParseResult.UnsupportedParameter
                    ? "555 5.5.4 Unsupported MAIL parameter"
                    : "501 5.5.4 Invalid MAIL FROM syntax",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var device = _device;
        if (device is null && !_devices.TryResolveLegacyDevice(
                _sourceAddress,
                sender!,
                out device,
                out var legacySourceMatched,
                cancellationToken))
        {
            await WriteReplyAsync(
                stream,
                legacySourceMatched
                    ? "550 5.7.1 Sender not authorized or device mapping is ambiguous"
                    : "530 5.7.0 Authentication required",
                cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!device!.IsSenderAllowed(sender!))
        {
            await WriteReplyAsync(stream, "550 5.7.1 Sender not authorized", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (size is not null && size > _options.MaxMessageBytes)
        {
            await WriteReplyAsync(stream, "552 5.3.4 Message exceeds fixed maximum size", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        _device = device;
        _mailFrom = sender;
        _declaredSizeBytes = size is null ? null : (long)size.Value;
        await WriteReplyAsync(stream, "250 2.1.0 Sender accepted", cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleRecipientAsync(string command, Stream stream, CancellationToken cancellationToken)
    {
        if (_mailFrom is null)
        {
            await WriteReplyAsync(stream, "503 5.5.1 MAIL command required", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (_recipients.Count >= _options.MaxRecipients)
        {
            await WriteReplyAsync(stream, "452 4.5.3 Too many recipients", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var parseResult = TryParsePathCommand(command, "RCPT TO:", allowSize: false, out var recipient, out _);
        if (parseResult != PathParseResult.Success)
        {
            await WriteReplyAsync(
                stream,
                parseResult == PathParseResult.UnsupportedParameter
                    ? "555 5.5.4 Unsupported RCPT parameter"
                    : "501 5.5.4 Invalid RCPT TO syntax",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        _recipients.Add(recipient!);
        await WriteReplyAsync(stream, "250 2.1.5 Recipient accepted", cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> HandleDataAsync(
        string command,
        Stream stream,
        BufferedSmtpReader reader,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(command, "DATA", StringComparison.OrdinalIgnoreCase))
        {
            await WriteReplyAsync(stream, "501 5.5.4 DATA takes no arguments", cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        if (_mailFrom is null || _device is null || _recipients.Count == 0)
        {
            await WriteReplyAsync(stream, "503 5.5.1 MAIL and RCPT commands required", cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        MessageReceiveTransaction receive;
        try
        {
            receive = _messageStore.BeginReceive(
                _declaredSizeBytes ?? _options.MaxMessageBytes,
                cancellationToken);
        }
        catch (QueueCapacityExceededException exception)
        {
            _logger.LogWarning(
                "QueueCapacityExceeded RemoteAddress={RemoteAddress} Limit={Limit}",
                _sourceAddress,
                exception.Limit);
            await WriteReplyAsync(stream, "452 4.3.1 Insufficient system storage", cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "MessageSpoolCreateFailed RemoteAddress={RemoteAddress}", _sourceAddress);
            await WriteReplyAsync(stream, "451 4.3.0 Local storage unavailable", cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        await using (receive.ConfigureAwait(false))
        {
            await WriteReplyAsync(stream, "354 End data with <CRLF>.<CRLF>", cancellationToken)
                .ConfigureAwait(false);
            long sizeBytes = 0;
            try
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync(_options.MaxDataLineLength, cancellationToken)
                        .ConfigureAwait(false);
                    if (line is null)
                    {
                        return false;
                    }

                    var content = line.Value;
                    if (content.Length == 1 && content.Span[0] == '.')
                    {
                        break;
                    }

                    if (!content.IsEmpty && content.Span[0] == '.')
                    {
                        content = content[1..];
                    }

                    var nextSize = checked(sizeBytes + content.Length + CrLf.Length);
                    if (nextSize > _options.MaxMessageBytes)
                    {
                        await WriteReplyAsync(stream, "552 5.3.4 Message exceeds fixed maximum size", cancellationToken)
                            .ConfigureAwait(false);
                        return false;
                    }

                    receive.EnsureCapacity(nextSize);
                    await receive.Stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                    await receive.Stream.WriteAsync(CrLf, cancellationToken).ConfigureAwait(false);
                    sizeBytes = nextSize;
                }

                var message = await receive.CommitAsync(
                    _device.Id,
                    _mailFrom,
                    _recipients,
                    sizeBytes,
                    cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "MessageQueued MessageId={MessageId} DeviceId={DeviceId} SizeBytes={SizeBytes} RecipientCount={RecipientCount}",
                    message.Id,
                    message.DeviceId,
                    message.SizeBytes,
                    message.Recipients.Count);
                ResetEnvelope();
                await WriteReplyAsync(stream, $"250 2.0.0 Queued as {message.Id:D}", cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch (SmtpLineTooLongException)
            {
                await TryWriteReplyAsync(stream, "552 5.3.4 DATA line too long", cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }
            catch (SmtpProtocolException)
            {
                await TryWriteReplyAsync(stream, "554 5.5.2 Invalid DATA line ending", cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }
            catch (OverflowException)
            {
                await TryWriteReplyAsync(stream, "552 5.3.4 Message exceeds fixed maximum size", cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }
            catch (QueueCapacityExceededException exception)
            {
                _logger.LogWarning(
                    "QueueCapacityExceeded MessageId={MessageId} Limit={Limit}",
                    receive.MessageId,
                    exception.Limit);
                await TryWriteReplyAsync(stream, "452 4.3.1 Insufficient system storage", cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or global::Microsoft.Data.Sqlite.SqliteException)
            {
                _logger.LogError(exception, "MessagePersistenceFailed MessageId={MessageId}", receive.MessageId);
                ResetEnvelope();
                await TryWriteReplyAsync(stream, "451 4.3.0 Message could not be persisted", cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
        }
    }

    private async Task<AuthenticationReadResult> ReadPlainCredentialsAsync(
        string? initialResponse,
        Stream stream,
        BufferedSmtpReader reader,
        CancellationToken cancellationToken)
    {
        var response = initialResponse;
        if (response is null)
        {
            await WriteReplyAsync(stream, "334 ", cancellationToken).ConfigureAwait(false);
            response = await ReadAuthenticationResponseAsync(reader, cancellationToken).ConfigureAwait(false);
        }

        if (response is null)
        {
            return AuthenticationReadResult.InvalidCredentials;
        }

        if (response == "*")
        {
            return AuthenticationReadResult.Cancelled;
        }

        var decoded = DecodeBase64(response, 767);
        if (decoded is null)
        {
            return AuthenticationReadResult.InvalidResponse;
        }

        try
        {
            var firstNull = Array.IndexOf(decoded, (byte)0);
            var secondNull = firstNull < 0 ? -1 : Array.IndexOf(decoded, (byte)0, firstNull + 1);
            if (firstNull < 0 || secondNull < 0 || Array.IndexOf(decoded, (byte)0, secondNull + 1) >= 0)
            {
                return AuthenticationReadResult.InvalidCredentials;
            }

            var authzid = decoded.AsSpan(0, firstNull);
            var usernameBytes = decoded.AsSpan(firstNull + 1, secondNull - firstNull - 1);
            var passwordBytes = decoded.AsSpan(secondNull + 1);
            if (authzid.Length > 255 ||
                usernameBytes.IsEmpty ||
                usernameBytes.Length > 255 ||
                passwordBytes.IsEmpty ||
                passwordBytes.Length > 255)
            {
                return AuthenticationReadResult.ResponseTooLong;
            }

            var username = StrictUtf8.GetString(usernameBytes);
            if (!authzid.IsEmpty && !string.Equals(StrictUtf8.GetString(authzid), username, StringComparison.Ordinal))
            {
                return AuthenticationReadResult.InvalidCredentials;
            }

            return AuthenticationReadResult.Success(
                new AuthenticationCredentials(username, StrictUtf8.GetString(passwordBytes)));
        }
        catch (DecoderFallbackException)
        {
            return AuthenticationReadResult.InvalidCredentials;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    private async Task<AuthenticationReadResult> ReadLoginCredentialsAsync(
        string? initialResponse,
        Stream stream,
        BufferedSmtpReader reader,
        CancellationToken cancellationToken)
    {
        var usernameResponse = initialResponse;
        if (usernameResponse is null)
        {
            await WriteReplyAsync(stream, "334 VXNlcm5hbWU6", cancellationToken).ConfigureAwait(false);
            usernameResponse = await ReadAuthenticationResponseAsync(reader, cancellationToken).ConfigureAwait(false);
        }

        if (usernameResponse is null)
        {
            return AuthenticationReadResult.InvalidCredentials;
        }

        if (usernameResponse == "*")
        {
            return AuthenticationReadResult.Cancelled;
        }

        var usernameBytes = DecodeBase64(usernameResponse, 255);
        if (usernameBytes is null)
        {
            return AuthenticationReadResult.InvalidResponse;
        }

        if (usernameBytes.Length == 0)
        {
            return AuthenticationReadResult.InvalidCredentials;
        }

        try
        {
            await WriteReplyAsync(stream, "334 UGFzc3dvcmQ6", cancellationToken).ConfigureAwait(false);
            var passwordResponse = await ReadAuthenticationResponseAsync(reader, cancellationToken).ConfigureAwait(false);
            if (passwordResponse is null)
            {
                return AuthenticationReadResult.InvalidCredentials;
            }

            if (passwordResponse == "*")
            {
                return AuthenticationReadResult.Cancelled;
            }

            var passwordBytes = DecodeBase64(passwordResponse, 255);
            if (passwordBytes is null)
            {
                return AuthenticationReadResult.InvalidResponse;
            }

            if (passwordBytes.Length == 0)
            {
                return AuthenticationReadResult.InvalidCredentials;
            }

            try
            {
                return AuthenticationReadResult.Success(
                    new AuthenticationCredentials(
                        StrictUtf8.GetString(usernameBytes),
                        StrictUtf8.GetString(passwordBytes)));
            }
            catch (DecoderFallbackException)
            {
                return AuthenticationReadResult.InvalidCredentials;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(usernameBytes);
        }
    }

    private async Task<string?> ReadAuthenticationResponseAsync(
        BufferedSmtpReader reader,
        CancellationToken cancellationToken)
    {
        var response = await reader.ReadLineAsync(_options.MaxCommandLength, cancellationToken).ConfigureAwait(false);
        return response is not null && TryDecodeCommand(response.Value.Span, out var value) ? value : null;
    }

    private static byte[]? DecodeBase64(string value, int maximumDecodedLength)
    {
        if (value == "=")
        {
            return [];
        }

        var maximumEncodedLength = ((maximumDecodedLength + 2) / 3) * 4;
        if (value.Length == 0 ||
            value.Length > maximumEncodedLength ||
            value.Any(character =>
                !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '=')))
        {
            return null;
        }

        var firstPadding = value.IndexOf('=');
        if (firstPadding >= 0 && value[firstPadding..].Any(character => character != '='))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length > maximumDecodedLength)
            {
                CryptographicOperations.ZeroMemory(bytes);
                return null;
            }

            return bytes;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static PathParseResult TryParsePathCommand(
        string command,
        string prefix,
        bool allowSize,
        out string? address,
        out decimal? declaredSize)
    {
        address = null;
        declaredSize = null;
        if (!command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return PathParseResult.Invalid;
        }

        var remainder = command[prefix.Length..].TrimStart();
        if (!remainder.StartsWith('<'))
        {
            return PathParseResult.Invalid;
        }

        var closingBracket = remainder.IndexOf('>');
        if (closingBracket <= 1)
        {
            return PathParseResult.Invalid;
        }

        var candidate = remainder[1..closingBracket];
        if (!MailAddress.TryCreate(candidate, out var parsed) ||
            !string.Equals(parsed.Address, candidate, StringComparison.OrdinalIgnoreCase))
        {
            return PathParseResult.Invalid;
        }

        address = parsed.Address.ToLowerInvariant();
        var parameters = remainder[(closingBracket + 1)..].Trim();
        if (parameters.Length == 0)
        {
            return PathParseResult.Success;
        }

        if (!allowSize || !parameters.StartsWith("SIZE=", StringComparison.OrdinalIgnoreCase) || parameters.Contains(' '))
        {
            return PathParseResult.UnsupportedParameter;
        }

        var sizeValue = parameters[5..];
        return sizeValue.Length is >= 1 and <= 20 &&
            sizeValue.All(character => character is >= '0' and <= '9') &&
            decimal.TryParse(sizeValue, NumberStyles.None, CultureInfo.InvariantCulture, out var size)
            ? SetSize(size, out declaredSize)
            : PathParseResult.Invalid;
    }

    private static PathParseResult SetSize(decimal size, out decimal? declaredSize)
    {
        declaredSize = size;
        return PathParseResult.Success;
    }

    private static bool HasSingleArgument(string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2;
    }

    private static bool TryDecodeCommand(ReadOnlySpan<byte> bytes, out string command)
    {
        command = string.Empty;
        if (bytes.IsEmpty || bytes.IndexOfAnyInRange((byte)0x80, byte.MaxValue) >= 0 || bytes.Contains((byte)0))
        {
            return false;
        }

        command = Encoding.ASCII.GetString(bytes);
        return true;
    }

    private static async Task WriteReplyAsync(Stream stream, string reply, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes($"{reply}\r\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task TryWriteReplyAsync(Stream stream, string reply, CancellationToken cancellationToken)
    {
        try
        {
            await WriteReplyAsync(stream, reply, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            return;
        }
    }

    private void ResetEnvelope()
    {
        _mailFrom = null;
        _declaredSizeBytes = null;
        _recipients.Clear();
        if (_device?.AuthenticationMode == DeviceAuthenticationMode.Legacy)
        {
            _device = null;
        }
    }

    private sealed record AuthenticationReadResult(
        AuthenticationCredentials? Credentials,
        string? FailureReply)
    {
        public static AuthenticationReadResult InvalidCredentials { get; } = new(null, null);

        public static AuthenticationReadResult InvalidResponse { get; } =
            new(null, "501 5.5.2 Invalid authentication response");

        public static AuthenticationReadResult Cancelled { get; } =
            new(null, "501 5.7.0 Authentication cancelled");

        public static AuthenticationReadResult ResponseTooLong { get; } =
            new(null, "501 5.5.6 Authentication response too long");

        public static AuthenticationReadResult Success(AuthenticationCredentials credentials)
        {
            return new AuthenticationReadResult(credentials, null);
        }
    }

    private sealed class AuthenticationCredentials(string username, string password)
    {
        public string Username { get; } = username;

        [JsonIgnore]
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public string Password { get; } = password;

        public override string ToString() => "AuthenticationCredentials { Username = [REDACTED], Password = [REDACTED] }";
    }

    private enum PathParseResult
    {
        Success,
        Invalid,
        UnsupportedParameter,
    }
}
