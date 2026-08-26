// SPDX-License-Identifier: MPL-2.0

using System.Buffers.Text;
using System.Net;
using System.Net.Mail;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using RelayBridge.Core.Microsoft;
using RelayBridge.Core.Queue;
using RelayBridge.Infrastructure.Storage;

namespace RelayBridge.Infrastructure.Microsoft;

internal enum SmtpDataProgressStage
{
    StreamingStarted,
    PayloadRead,
    SpoolEofReached,
    TerminatorWriteStarted,
    TerminatorFlushed,
}

internal readonly record struct SmtpDataProgress(
    SmtpDataProgressStage Stage,
    long PayloadBytesRead);

public sealed class ExchangeSmtpOAuthProvider : IMailDeliveryProvider
{
    private static readonly byte[] CrLf = "\r\n"u8.ToArray();
    private static readonly byte[] DataTerminator = ".\r\n"u8.ToArray();
    private static readonly byte[] LeadingCrLfDataTerminator = "\r\n.\r\n"u8.ToArray();
    private readonly IMicrosoftTokenProvider _tokenProvider;
    private readonly ExchangeSmtpOptions _options;
    private readonly ExchangeSmtpEndpoint _endpoint;
    private readonly ExchangeDeliveryRuntimeState _runtimeState;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ExchangeSmtpOAuthProvider> _logger;
    private readonly RelayDatabase? _database;

    public ExchangeSmtpOAuthProvider(
        IMicrosoftTokenProvider tokenProvider,
        ExchangeSmtpOptions options,
        ExchangeDeliveryRuntimeState runtimeState,
        TimeProvider timeProvider,
        ILogger<ExchangeSmtpOAuthProvider> logger,
        RelayDatabase database)
        : this(tokenProvider, options, ExchangeSmtpEndpoint.Production, runtimeState, timeProvider, logger, database)
    {
    }

    internal ExchangeSmtpOAuthProvider(
        IMicrosoftTokenProvider tokenProvider,
        ExchangeSmtpOptions options,
        ExchangeSmtpEndpoint endpoint,
        ExchangeDeliveryRuntimeState runtimeState,
        TimeProvider timeProvider,
        ILogger<ExchangeSmtpOAuthProvider> logger,
        RelayDatabase? database = null)
    {
        _tokenProvider = tokenProvider;
        _options = options;
        _endpoint = endpoint;
        _runtimeState = runtimeState;
        _timeProvider = timeProvider;
        _logger = logger;
        _database = database;
        _options.Validate();
        ValidateEndpoint(endpoint);
    }

    public Task<DeliveryResult> DeliverAsync(
        QueuedMessage message,
        Stream messageContent,
        CancellationToken cancellationToken)
    {
        var activeConfiguration = _database?.GetActiveMicrosoftConfiguration(cancellationToken);
        var configurationFingerprint = activeConfiguration?.AuthorizedSender is not null &&
            string.Equals(
                activeConfiguration.AuthorizedSender,
                message.EnvelopeFrom,
                StringComparison.OrdinalIgnoreCase)
                ? activeConfiguration.Fingerprint
                : null;
        return DeliverAsync(
            message,
            messageContent,
            _tokenProvider,
            cancellationToken,
            configurationFingerprint,
            activeConfiguration?.Identity);
    }

    internal Task<DeliveryResult> DeliverAsync(
        QueuedMessage message,
        Stream messageContent,
        IMicrosoftTokenProvider tokenProvider,
        CancellationToken cancellationToken,
        string? configurationFingerprint = null,
        MicrosoftIdentityConfiguration? capturedConfiguration = null)
    {
        return DeliverCoreAsync(
            message,
            messageContent,
            tokenProvider,
            cancellationToken,
            configurationFingerprint,
            capturedConfiguration,
            stopAfterAuthentication: false);
    }

    internal Task<DeliveryResult> VerifyAuthenticationAsync(
        Guid correlationId,
        string mailbox,
        MicrosoftIdentityConfiguration capturedConfiguration,
        string configurationFingerprint,
        CancellationToken cancellationToken)
    {
        var attemptedAt = _timeProvider.GetUtcNow();
        var operation = new QueuedMessage(
            correlationId,
            Guid.Empty,
            mailbox,
            [mailbox],
            attemptedAt,
            0,
            $"verification-{correlationId:N}.eml",
            QueueState.Delivering,
            AttemptCount: 1,
            LastAttemptUtc: attemptedAt,
            PayloadPresent: false);
        return DeliverCoreAsync(
            operation,
            Stream.Null,
            _tokenProvider,
            cancellationToken,
            configurationFingerprint,
            capturedConfiguration,
            stopAfterAuthentication: true);
    }

    private async Task<DeliveryResult> DeliverCoreAsync(
        QueuedMessage message,
        Stream messageContent,
        IMicrosoftTokenProvider tokenProvider,
        CancellationToken cancellationToken,
        string? configurationFingerprint,
        MicrosoftIdentityConfiguration? capturedConfiguration,
        bool stopAfterAuthentication)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(messageContent);

        var attemptedAt = _timeProvider.GetUtcNow();
        var attempt = _runtimeState.BeginAttempt(
            attemptedAt,
            capturedConfiguration,
            configurationFingerprint);
        try
        {
            ValidateEnvelope(message);
        }
        catch (ArgumentException exception)
        {
            return Record(attempt, message, DeliveryResult.PermanentFailure(
                ExchangeSmtpErrorCategories.Protocol,
                exception.Message));
        }

        _logger.LogInformation(
            "ExchangeConnectionStarted MessageId={MessageId} Host={Host} Port={Port}",
            message.Id,
            _endpoint.Host,
            _endpoint.Port);

        var stage = DeliveryStage.Disconnected;
        TcpClient? tcpClient = null;
        Stream? stream = null;
        try
        {
            tcpClient = new TcpClient();
            await WithTimeoutAsync(
                token => tcpClient.ConnectAsync(_endpoint.Host, _endpoint.Port, token).AsTask(),
                _options.ConnectTimeout,
                cancellationToken).ConfigureAwait(false);
            stream = tcpClient.GetStream();
            stage = DeliveryStage.Connected;
            _runtimeState.RecordStage(attempt, "TCP connection");
            _logger.LogInformation("ExchangeConnectionSucceeded MessageId={MessageId}", message.Id);

            var greeting = await ReadResponseAsync(stream, _options.CommandTimeout, cancellationToken)
                .ConfigureAwait(false);
            _runtimeState.RecordProtocolResponse(attempt, "Greeting", greeting);
            if (greeting.Code != 220)
            {
                return Record(attempt, message, ClassifyResponse(greeting, "Greeting", permanentCategory: ExchangeSmtpErrorCategories.Protocol));
            }

            stage = DeliveryStage.Greeted;
            _runtimeState.RecordStage(attempt, "Greeting");
            var preTlsEhlo = await EhloAsync(stream, cancellationToken).ConfigureAwait(false);
            if (preTlsEhlo.Code != 250)
            {
                return Record(attempt, message, ClassifyResponse(
                    preTlsEhlo,
                    "EHLO before STARTTLS",
                    ExchangeSmtpErrorCategories.Protocol));
            }

            stage = DeliveryStage.EhloComplete;
            var preTlsCapabilities = SmtpCapabilities.Parse(preTlsEhlo);
            if (!preTlsCapabilities.StartTls)
            {
                return Record(attempt, message, DeliveryResult.PermanentFailure(
                    ExchangeSmtpErrorCategories.Tls,
                    "The Exchange SMTP server did not advertise required STARTTLS."));
            }

            await WriteLineAsync(stream, "STARTTLS", _options.CommandTimeout, cancellationToken)
                .ConfigureAwait(false);
            var startTls = await ReadResponseAsync(stream, _options.CommandTimeout, cancellationToken)
                .ConfigureAwait(false);
            _runtimeState.RecordProtocolResponse(attempt, "STARTTLS", startTls);
            if (startTls.Code != 220)
            {
                return Record(attempt, message, ClassifyResponse(startTls, "STARTTLS", ExchangeSmtpErrorCategories.Tls));
            }

            var sslStream = new SslStream(
                stream,
                leaveInnerStreamOpen: false,
                _endpoint.TestCertificateValidation);
            var tlsOptions = new SslClientAuthenticationOptions
            {
                TargetHost = _endpoint.TlsTargetHost,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.None,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.Online,
            };
            await WithTimeoutAsync(
                token => sslStream.AuthenticateAsClientAsync(tlsOptions, token),
                _options.TlsTimeout,
                cancellationToken).ConfigureAwait(false);
            stream = sslStream;
            stage = DeliveryStage.TlsEstablished;
            _runtimeState.RecordStage(attempt, "STARTTLS");
            _logger.LogInformation("ExchangeTlsSucceeded MessageId={MessageId}", message.Id);

            var postTlsEhlo = await EhloAsync(stream, cancellationToken).ConfigureAwait(false);
            if (postTlsEhlo.Code != 250)
            {
                return Record(attempt, message, ClassifyResponse(
                    postTlsEhlo,
                    "EHLO after STARTTLS",
                    ExchangeSmtpErrorCategories.Protocol));
            }

            var capabilities = SmtpCapabilities.Parse(postTlsEhlo);
            if (!capabilities.XOAuth2)
            {
                return Record(attempt, message, DeliveryResult.TransientFailure(
                    ExchangeSmtpErrorCategories.Authentication,
                    "Exchange SMTP authentication is unavailable. The tenant or mailbox may have SMTP AUTH disabled.",
                    _options.ConfigurationFailureRetryAfter));
            }

            if (capabilities.MaximumSize is long maximumSize && message.SizeBytes > maximumSize)
            {
                return Record(attempt, message, DeliveryResult.PermanentFailure(
                    ExchangeSmtpErrorCategories.MessageTooLarge,
                    $"The queued message is {message.SizeBytes} bytes, larger than the server-advertised SIZE limit of {maximumSize} bytes."));
            }

            MicrosoftAccessToken token;
            try
            {
                token = attempt.CapturedConfiguration is null
                    ? await tokenProvider.GetExchangeTokenAsync(cancellationToken).ConfigureAwait(false)
                    : await tokenProvider.GetExchangeTokenAsync(
                        attempt.CapturedConfiguration,
                        cancellationToken).ConfigureAwait(false);
                _runtimeState.RecordStage(attempt, "Token acquisition");
            }
            catch (MicrosoftIdentityException exception)
            {
                _logger.LogWarning(
                    "ExchangeAuthenticationFailed MessageId={MessageId} Category={Category} TechnicalCode={TechnicalCode}",
                    message.Id,
                    exception.Category,
                    exception.TechnicalCode);
                return Record(attempt, message, DeliveryResult.TransientFailure(
                    ExchangeSmtpErrorCategories.Authentication,
                    $"Microsoft application authentication failed: {exception.Message}",
                    _options.ConfigurationFailureRetryAfter));
            }

            var authentication = await AuthenticateAsync(
                stream,
                message.EnvelopeFrom,
                token.Value,
                cancellationToken).ConfigureAwait(false);
            _runtimeState.RecordProtocolResponse(attempt, "AUTH", authentication);
            if (authentication.Code != 235)
            {
                _logger.LogWarning(
                    "ExchangeAuthenticationFailed MessageId={MessageId} SmtpCode={SmtpCode} EnhancedCode={EnhancedCode}",
                    message.Id,
                    authentication.Code,
                    authentication.EnhancedStatusCode);
                return Record(attempt, message, DeliveryResult.TransientFailure(
                    authentication.Code == 535
                        ? ExchangeSmtpErrorCategories.Authentication
                        : ExchangeSmtpErrorCategories.Authorization,
                    SafeFailure("Exchange SMTP authentication is unavailable or the mailbox is not authorized.", authentication),
                    _options.ConfigurationFailureRetryAfter));
            }

            stage = DeliveryStage.Authenticated;
            _runtimeState.RecordStage(attempt, "XOAUTH2");
            _logger.LogInformation("ExchangeAuthenticationSucceeded MessageId={MessageId}", message.Id);
            if (stopAfterAuthentication)
            {
                await TryQuitAsync(stream).ConfigureAwait(false);
                return Record(attempt, message, DeliveryResult.Succeeded(), authenticationVerification: true);
            }

            var mailCommand = capabilities.Size
                ? $"MAIL FROM:<{message.EnvelopeFrom}> SIZE={message.SizeBytes}"
                : $"MAIL FROM:<{message.EnvelopeFrom}>";
            await WriteLineAsync(stream, mailCommand, _options.CommandTimeout, cancellationToken).ConfigureAwait(false);
            var mailResponse = await ReadResponseAsync(stream, _options.CommandTimeout, cancellationToken)
                .ConfigureAwait(false);
            _runtimeState.RecordProtocolResponse(attempt, "MAIL FROM", mailResponse);
            if (!mailResponse.IsPositiveCompletion)
            {
                _logger.LogWarning(
                    "ExchangeSenderRejected MessageId={MessageId} SmtpCode={SmtpCode} EnhancedCode={EnhancedCode}",
                    message.Id,
                    mailResponse.Code,
                    mailResponse.EnhancedStatusCode);
                return Record(attempt, message, ClassifyResponse(
                    mailResponse,
                    "MAIL FROM",
                    IsMessageTooLarge(mailResponse)
                        ? ExchangeSmtpErrorCategories.MessageTooLarge
                        : ExchangeSmtpErrorCategories.SenderRejected));
            }

            stage = DeliveryStage.EnvelopeStarted;
            _runtimeState.RecordStage(attempt, "Sender authorization");
            SmtpResponse? permanentRecipientFailure = null;
            SmtpResponse? transientRecipientFailure = null;
            foreach (var recipient in message.Recipients)
            {
                await WriteLineAsync(stream, $"RCPT TO:<{recipient}>", _options.CommandTimeout, cancellationToken)
                    .ConfigureAwait(false);
                var recipientResponse = await ReadResponseAsync(stream, _options.CommandTimeout, cancellationToken)
                    .ConfigureAwait(false);
                _runtimeState.RecordProtocolResponse(attempt, "RCPT TO", recipientResponse);
                if (recipientResponse.IsPositiveCompletion)
                {
                    continue;
                }

                if (recipientResponse.IsPermanentFailure)
                {
                    permanentRecipientFailure ??= recipientResponse;
                }
                else if (recipientResponse.IsTransientFailure)
                {
                    transientRecipientFailure ??= recipientResponse;
                }
                else
                {
                    await TryRsetAsync(stream).ConfigureAwait(false);
                    return Record(attempt, message, DeliveryResult.PermanentFailure(
                        ExchangeSmtpErrorCategories.Protocol,
                        SafeFailure("Exchange SMTP returned an unexpected RCPT TO response; DATA was not sent.", recipientResponse)));
                }
            }

            if (permanentRecipientFailure is not null || transientRecipientFailure is not null)
            {
                await TryRsetAsync(stream).ConfigureAwait(false);
                var failure = permanentRecipientFailure ?? transientRecipientFailure!;
                _logger.LogWarning(
                    "ExchangeRecipientRejected MessageId={MessageId} SmtpCode={SmtpCode} EnhancedCode={EnhancedCode}",
                    message.Id,
                    failure.Code,
                    failure.EnhancedStatusCode);
                return Record(attempt, message, permanentRecipientFailure is not null
                    ? DeliveryResult.PermanentFailure(
                        ExchangeSmtpErrorCategories.RecipientRejected,
                        SafeFailure("Exchange permanently rejected at least one recipient; DATA was not sent.", failure))
                    : DeliveryResult.TransientFailure(
                        ExchangeSmtpErrorCategories.RecipientRejected,
                        SafeFailure("Exchange temporarily rejected at least one recipient; DATA was not sent.", failure)));
            }

            stage = DeliveryStage.RecipientsAccepted;
            _runtimeState.RecordStage(attempt, "Recipients");
            await WriteLineAsync(stream, "DATA", _options.CommandTimeout, cancellationToken).ConfigureAwait(false);
            var dataResponse = await ReadResponseAsync(stream, _options.CommandTimeout, cancellationToken)
                .ConfigureAwait(false);
            _runtimeState.RecordProtocolResponse(attempt, "DATA", dataResponse);
            if (dataResponse.Code != 354)
            {
                return Record(attempt, message, ClassifyResponse(
                    dataResponse,
                    "DATA",
                    IsMessageTooLarge(dataResponse)
                        ? ExchangeSmtpErrorCategories.MessageTooLarge
                        : ExchangeSmtpErrorCategories.PermanentServerFailure));
            }

            stage = DeliveryStage.DataStarted;
            await WithTimeoutAsync(
                tokenValue => StreamDataAsync(
                    messageContent,
                    stream,
                    tokenValue,
                    progress =>
                    {
                        if (progress.Stage == SmtpDataProgressStage.TerminatorWriteStarted)
                        {
                            stage = DeliveryStage.DataTerminatorWriteStarted;
                        }
                        else if (progress.Stage == SmtpDataProgressStage.TerminatorFlushed)
                        {
                            stage = DeliveryStage.DataTerminatorFlushed;
                        }

                        RecordDataProgress(attempt, message, progress);
                    }),
                _options.GetDataTimeout(message.SizeBytes),
                cancellationToken).ConfigureAwait(false);
            stage = DeliveryStage.AwaitingFinalAcceptance;
            var finalWaitStartedAt = _timeProvider.GetUtcNow();
            _runtimeState.RecordFinalResponseWait(attempt, finalWaitStartedAt);
            _logger.LogInformation(
                "ExchangeFinalResponseWaitStarted MessageId={MessageId} Timeout={Timeout}",
                message.Id,
                _options.DataTerminationTimeout);
            var finalResponse = await ReadResponseAsync(stream, _options.DataTerminationTimeout, cancellationToken)
                .ConfigureAwait(false);
            var finalResponseReceivedAt = _timeProvider.GetUtcNow();
            _runtimeState.RecordFinalResponse(attempt, finalResponse, finalResponseReceivedAt);
            _logger.LogInformation(
                "ExchangeFinalResponseReceived MessageId={MessageId} SmtpCode={SmtpCode} EnhancedCode={EnhancedCode}",
                message.Id,
                finalResponse.Code,
                finalResponse.EnhancedStatusCode);
            if (finalResponse.Code != 250)
            {
                return Record(attempt, message, ClassifyResponse(
                    finalResponse,
                    "final DATA acceptance",
                    IsMessageTooLarge(finalResponse)
                        ? ExchangeSmtpErrorCategories.MessageTooLarge
                        : ExchangeSmtpErrorCategories.PermanentServerFailure));
            }

            stage = DeliveryStage.Accepted;
            _runtimeState.RecordStage(attempt, "Accepted");
            _logger.LogInformation(
                "ExchangeMessageAccepted MessageId={MessageId} SmtpCode={SmtpCode} EnhancedCode={EnhancedCode}",
                message.Id,
                finalResponse.Code,
                finalResponse.EnhancedStatusCode);
            await TryQuitAsync(stream).ConfigureAwait(false);
            return Record(attempt, message, DeliveryResult.Succeeded());
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            _runtimeState.RecordException(attempt, exception);
            if (IsAmbiguousStage(stage))
            {
                return Record(attempt, message, Ambiguous("Delivery was cancelled after the SMTP DATA terminator may have reached the server; remote acceptance is unknown."));
            }

            if (stage >= DeliveryStage.DataStarted)
            {
                return Record(attempt, message, DeliveryResult.TransientFailure(
                    ExchangeSmtpErrorCategories.Cancelled,
                    "Delivery was cancelled before the SMTP DATA terminator was sent."));
            }

            throw;
        }
        catch (TimeoutException exception)
        {
            _runtimeState.RecordException(attempt, exception);
            return Record(attempt, message, IsAmbiguousStage(stage)
                ? Ambiguous("The SMTP operation timed out after the DATA terminator may have reached the server; remote acceptance is unknown.")
                : DeliveryResult.TransientFailure(ExchangeSmtpErrorCategories.Timeout, exception.Message));
        }
        catch (AuthenticationException exception)
        {
            _runtimeState.RecordException(attempt, exception);
            return Record(attempt, message, DeliveryResult.TransientFailure(
                ExchangeSmtpErrorCategories.Tls,
                "TLS negotiation or certificate validation with Exchange SMTP failed."));
        }
        catch (IOException exception) when (exception.InnerException is AuthenticationException)
        {
            _runtimeState.RecordException(attempt, exception);
            return Record(attempt, message, DeliveryResult.TransientFailure(
                ExchangeSmtpErrorCategories.Tls,
                "TLS negotiation or certificate validation with Exchange SMTP failed."));
        }
        catch (SocketException exception)
        {
            _runtimeState.RecordException(attempt, exception);
            if (IsAmbiguousStage(stage))
            {
                return Record(attempt, message, Ambiguous("The network connection failed after the DATA terminator may have reached the server; remote acceptance is unknown."));
            }

            var category = exception.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData
                ? ExchangeSmtpErrorCategories.Dns
                : ExchangeSmtpErrorCategories.Network;
            return Record(attempt, message, DeliveryResult.TransientFailure(category, "The Exchange SMTP network connection failed."));
        }
        catch (IOException exception)
        {
            _runtimeState.RecordException(attempt, exception);
            return Record(attempt, message, IsAmbiguousStage(stage)
                ? Ambiguous("The SMTP connection closed after the DATA terminator may have reached the server; remote acceptance is unknown.")
                : DeliveryResult.TransientFailure(
                    ExchangeSmtpErrorCategories.Network,
                    "The Exchange SMTP connection closed unexpectedly."));
        }
        catch (SmtpProtocolException exception)
        {
            _runtimeState.RecordException(attempt, exception);
            return Record(attempt, message, IsAmbiguousStage(stage)
                ? Ambiguous("Exchange returned an invalid response after the DATA terminator may have reached the server; remote acceptance is unknown.")
                : DeliveryResult.PermanentFailure(
                    ExchangeSmtpErrorCategories.Protocol,
                    exception.Message));
        }
        finally
        {
            _runtimeState.Abandon(attempt);
            if (stream is not null)
            {
                try
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is IOException or SocketException or AuthenticationException or ObjectDisposedException)
                {
                    _logger.LogDebug(
                        "ExchangeConnectionCloseFailed MessageId={MessageId} ErrorType={ErrorType}",
                        message.Id,
                        exception.GetType().Name);
                }
            }

            tcpClient?.Dispose();
        }
    }

    internal static byte[] CreateXOAuth2Response(string mailbox, string accessToken)
    {
        var prefix = "user="u8;
        var middle = "\u0001auth=Bearer "u8;
        var suffix = "\u0001\u0001"u8;
        var payload = new byte[
            prefix.Length +
            Encoding.UTF8.GetByteCount(mailbox) +
            middle.Length +
            Encoding.UTF8.GetByteCount(accessToken) +
            suffix.Length];
        var encoded = new byte[Base64.GetMaxEncodedToUtf8Length(payload.Length)];
        try
        {
            var offset = 0;
            prefix.CopyTo(payload);
            offset += prefix.Length;
            offset += Encoding.UTF8.GetBytes(mailbox, payload.AsSpan(offset));
            middle.CopyTo(payload.AsSpan(offset));
            offset += middle.Length;
            offset += Encoding.UTF8.GetBytes(accessToken, payload.AsSpan(offset));
            suffix.CopyTo(payload.AsSpan(offset));

            var status = Base64.EncodeToUtf8(payload, encoded, out var consumed, out var written);
            if (status != System.Buffers.OperationStatus.Done ||
                consumed != payload.Length ||
                written != encoded.Length)
            {
                CryptographicOperations.ZeroMemory(encoded);
                throw new InvalidOperationException("Could not encode XOAUTH2 credentials.");
            }

            return encoded;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    internal static async Task StreamDataAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken,
        Action<SmtpDataProgress>? reportProgress = null)
    {
        var input = new byte[64 * 1024];
        var output = new byte[128 * 1024];
        var atLineStart = true;
        var previousWasCr = false;
        long totalRead = 0;
        reportProgress?.Invoke(new SmtpDataProgress(SmtpDataProgressStage.StreamingStarted, 0));

        while (true)
        {
            var read = await source.ReadAsync(input, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                reportProgress?.Invoke(new SmtpDataProgress(
                    SmtpDataProgressStage.SpoolEofReached,
                    totalRead));
                break;
            }

            totalRead += read;
            reportProgress?.Invoke(new SmtpDataProgress(
                SmtpDataProgressStage.PayloadRead,
                totalRead));
            var outputCount = 0;
            for (var index = 0; index < read; index++)
            {
                var value = input[index];
                if (atLineStart && value == (byte)'.')
                {
                    output[outputCount++] = (byte)'.';
                }

                output[outputCount++] = value;
                atLineStart = previousWasCr && value == (byte)'\n';
                previousWasCr = value == (byte)'\r';
            }

            await destination.WriteAsync(output.AsMemory(0, outputCount), cancellationToken)
                .ConfigureAwait(false);
        }

        reportProgress?.Invoke(new SmtpDataProgress(
            SmtpDataProgressStage.TerminatorWriteStarted,
            totalRead));
        if (totalRead == 0 || atLineStart)
        {
            await destination.WriteAsync(DataTerminator, cancellationToken).ConfigureAwait(false);
        }
        else if (previousWasCr)
        {
            await destination.WriteAsync("\n.\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await destination.WriteAsync(LeadingCrLfDataTerminator, cancellationToken).ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        reportProgress?.Invoke(new SmtpDataProgress(
            SmtpDataProgressStage.TerminatorFlushed,
            totalRead));
    }

    private void RecordDataProgress(
        MicrosoftAttemptContext attempt,
        QueuedMessage message,
        SmtpDataProgress progress)
    {
        _runtimeState.RecordDataProgress(attempt, progress, _timeProvider.GetUtcNow());
        switch (progress.Stage)
        {
            case SmtpDataProgressStage.StreamingStarted:
                _logger.LogInformation(
                    "ExchangeDataStarted MessageId={MessageId} SizeBytes={SizeBytes}",
                    message.Id,
                    message.SizeBytes);
                break;
            case SmtpDataProgressStage.SpoolEofReached:
                _logger.LogInformation(
                    "ExchangeDataSpoolEofReached MessageId={MessageId} PayloadBytesRead={PayloadBytesRead}",
                    message.Id,
                    progress.PayloadBytesRead);
                break;
            case SmtpDataProgressStage.TerminatorWriteStarted:
                _logger.LogInformation(
                    "ExchangeDataTerminatorWriteStarted MessageId={MessageId}",
                    message.Id);
                break;
            case SmtpDataProgressStage.TerminatorFlushed:
                _logger.LogInformation(
                    "ExchangeDataTerminatorFlushed MessageId={MessageId}",
                    message.Id);
                break;
        }
    }

    private async Task<SmtpResponse> EhloAsync(Stream stream, CancellationToken cancellationToken)
    {
        await WriteLineAsync(stream, "EHLO relaybridge.local", _options.CommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        var response = await ReadResponseAsync(stream, _options.CommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        return response;
    }

    private async Task<SmtpResponse> AuthenticateAsync(
        Stream stream,
        string mailbox,
        string accessToken,
        CancellationToken cancellationToken)
    {
        await WriteLineAsync(stream, "AUTH XOAUTH2", _options.CommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        var response = await ReadResponseAsync(stream, _options.CommandTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (response.Code != 334)
        {
            return response;
        }

        var encoded = CreateXOAuth2Response(mailbox, accessToken);
        var command = new byte[encoded.Length + CrLf.Length];
        try
        {
            encoded.CopyTo(command, 0);
            CrLf.CopyTo(command, encoded.Length);
            await WithTimeoutAsync(
                token => stream.WriteAsync(command, token).AsTask(),
                _options.CommandTimeout,
                cancellationToken).ConfigureAwait(false);
            await WithTimeoutAsync(
                token => stream.FlushAsync(token),
                _options.CommandTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
            CryptographicOperations.ZeroMemory(command);
        }

        return await ReadResponseAsync(stream, _options.CommandTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task TryRsetAsync(Stream stream)
    {
        try
        {
            await WriteLineAsync(stream, "RSET", _options.CommandTimeout, CancellationToken.None)
                .ConfigureAwait(false);
            _ = await ReadResponseAsync(stream, _options.CommandTimeout, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or SocketException or TimeoutException or SmtpProtocolException or AuthenticationException or ObjectDisposedException)
        {
            _logger.LogDebug("Exchange RSET failed after recipient rejection: {ErrorType}", exception.GetType().Name);
        }
    }

    private async Task TryQuitAsync(Stream stream)
    {
        try
        {
            await WriteLineAsync(stream, "QUIT", _options.CommandTimeout, CancellationToken.None)
                .ConfigureAwait(false);
            _ = await ReadResponseAsync(stream, _options.CommandTimeout, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or SocketException or TimeoutException or SmtpProtocolException or AuthenticationException or ObjectDisposedException)
        {
            _logger.LogDebug("Exchange QUIT failed after the SMTP operation: {ErrorType}", exception.GetType().Name);
        }
    }

    private async Task<SmtpResponse> ReadResponseAsync(
        Stream stream,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var reader = new SmtpResponseReader();
        return await WithTimeoutAsync(
            token => reader.ReadAsync(stream, token),
            timeout,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteLineAsync(
        Stream stream,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes($"{command}\r\n");
        await WithTimeoutAsync(
            token => stream.WriteAsync(bytes, token).AsTask(),
            timeout,
            cancellationToken).ConfigureAwait(false);
        await WithTimeoutAsync(
            token => stream.FlushAsync(token),
            timeout,
            cancellationToken).ConfigureAwait(false);
    }

    private DeliveryResult Record(
        MicrosoftAttemptContext attempt,
        QueuedMessage message,
        DeliveryResult result,
        bool authenticationVerification = false)
    {
        if (authenticationVerification && result.Outcome == DeliveryOutcome.Success)
        {
            _runtimeState.RecordAuthenticationVerificationSuccess(attempt, _timeProvider.GetUtcNow());
        }
        else
        {
            _runtimeState.RecordResult(attempt, _timeProvider.GetUtcNow(), result);
        }
        if (result.Outcome == DeliveryOutcome.TransientFailure)
        {
            _logger.LogWarning(
                "ExchangeDeliveryTransientFailure MessageId={MessageId} Category={Category}",
                message.Id,
                result.ErrorCategory);
        }
        else if (result.Outcome == DeliveryOutcome.PermanentFailure)
        {
            _logger.LogError(
                "ExchangeDeliveryPermanentFailure MessageId={MessageId} Category={Category}",
                message.Id,
                result.ErrorCategory);
        }

        if (string.Equals(result.ErrorCategory, ExchangeSmtpErrorCategories.AmbiguousAcceptance, StringComparison.Ordinal))
        {
            _logger.LogWarning("ExchangeDeliveryAmbiguous MessageId={MessageId}", message.Id);
        }

        return result;
    }

    private static DeliveryResult Ambiguous(string safeMessage)
    {
        return DeliveryResult.TransientFailure(ExchangeSmtpErrorCategories.AmbiguousAcceptance, safeMessage);
    }

    private static bool IsAmbiguousStage(DeliveryStage stage)
    {
        return stage is >= DeliveryStage.DataTerminatorWriteStarted and < DeliveryStage.Accepted;
    }

    private static bool IsMessageTooLarge(SmtpResponse response)
    {
        return response.Code == 552 ||
            string.Equals(response.EnhancedStatusCode, "5.2.270", StringComparison.Ordinal);
    }

    private static DeliveryResult ClassifyResponse(
        SmtpResponse response,
        string stage,
        string permanentCategory)
    {
        var message = SafeFailure($"Exchange SMTP rejected {stage}.", response);
        if (response.IsTransientFailure)
        {
            return DeliveryResult.TransientFailure(ExchangeSmtpErrorCategories.TemporaryServerFailure, message);
        }

        if (response.IsPermanentFailure)
        {
            return DeliveryResult.PermanentFailure(permanentCategory, message);
        }

        return DeliveryResult.PermanentFailure(
            ExchangeSmtpErrorCategories.Protocol,
            SafeFailure($"Exchange SMTP returned an unexpected status during {stage}.", response));
    }

    private static string SafeFailure(string prefix, SmtpResponse response)
    {
        return $"{prefix} SMTP {response.SafeSummary}";
    }

    private static void ValidateEnvelope(QueuedMessage message)
    {
        ValidateMailbox(message.EnvelopeFrom, "envelope sender");
        if (message.Recipients.Count == 0)
        {
            throw new ArgumentException("At least one envelope recipient is required.", nameof(message));
        }

        foreach (var recipient in message.Recipients)
        {
            ValidateMailbox(recipient, "envelope recipient");
        }

        if (message.SizeBytes < 0)
        {
            throw new ArgumentException("Queued message size cannot be negative.", nameof(message));
        }
    }

    private static void ValidateMailbox(string value, string description)
    {
        if (value.Length is 0 or > 254 ||
            value.Any(character => character > 0x7f || character is '\r' or '\n' or '<' or '>') ||
            !MailAddress.TryCreate(value, out var parsed) ||
            !string.Equals(parsed.Address, value, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The {description} is not a supported ASCII mailbox address.");
        }
    }

    private static void ValidateEndpoint(ExchangeSmtpEndpoint endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Host) ||
            string.IsNullOrWhiteSpace(endpoint.TlsTargetHost) ||
            endpoint.Port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new ArgumentException("The SMTP endpoint is invalid.", nameof(endpoint));
        }
    }

    private static async Task WithTimeoutAsync(
        Func<CancellationToken, Task> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await operation(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The SMTP operation exceeded its {timeout} timeout.");
        }
    }

    private static async Task<T> WithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await operation(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The SMTP operation exceeded its {timeout} timeout.");
        }
    }

    private enum DeliveryStage
    {
        Disconnected,
        Connected,
        Greeted,
        EhloComplete,
        TlsEstablished,
        Authenticated,
        EnvelopeStarted,
        RecipientsAccepted,
        DataStarted,
        DataTerminatorWriteStarted,
        DataTerminatorFlushed,
        AwaitingFinalAcceptance,
        Accepted,
    }
}
