// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Text;
using RelayBridge.Core.Microsoft;
using RelayBridge.Core.Queue;

namespace RelayBridge.Infrastructure.Microsoft;

public sealed class ExchangeDeliveryTester
{
    private readonly ExchangeSmtpOAuthProvider _provider;
    private readonly ExchangeDeliveryRuntimeState _runtimeState;
    private readonly TimeProvider _timeProvider;

    public ExchangeDeliveryTester(
        ExchangeSmtpOAuthProvider provider,
        ExchangeDeliveryRuntimeState runtimeState,
        TimeProvider timeProvider)
    {
        _provider = provider;
        _runtimeState = runtimeState;
        _timeProvider = timeProvider;
    }

    public async Task<ExchangeDeliveryDiagnosticResult> TestAsync(
        string envelopeSender,
        string recipient,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync(
            envelopeSender,
            recipient,
            "RelayBridge Exchange delivery diagnostic",
            "RelayBridge Exchange SMTP OAuth delivery diagnostic.",
            tokenProvider: null,
            capturedConfiguration: null,
            configurationFingerprint: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExchangeDeliveryDiagnosticResult> SendSetupTestAsync(
        string envelopeSender,
        string recipient,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync(
            envelopeSender,
            recipient,
            "RelayBridge Microsoft 365 test",
            "RelayBridge successfully connected to Microsoft 365.",
            tokenProvider: null,
            capturedConfiguration: null,
            configurationFingerprint: null,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ExchangeDeliveryDiagnosticResult> TestAsync(
        string envelopeSender,
        string recipient,
        IMicrosoftTokenProvider? tokenProvider,
        MicrosoftIdentityConfiguration capturedConfiguration,
        string configurationFingerprint,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync(
            envelopeSender,
            recipient,
            "RelayBridge Exchange delivery diagnostic",
            "RelayBridge Exchange SMTP OAuth delivery diagnostic.",
            tokenProvider,
            capturedConfiguration,
            configurationFingerprint,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExchangeDeliveryDiagnosticResult> VerifyAuthenticationAsync(
        string mailbox,
        MicrosoftIdentityConfiguration capturedConfiguration,
        string configurationFingerprint,
        CancellationToken cancellationToken = default)
    {
        var correlationId = Guid.NewGuid();
        var result = await _provider.VerifyAuthenticationAsync(
            correlationId,
            mailbox,
            capturedConfiguration,
            configurationFingerprint,
            cancellationToken).ConfigureAwait(false);
        return new ExchangeDeliveryDiagnosticResult(
            result.Outcome,
            result.ErrorCategory,
            result.SafeMessage,
            _runtimeState.GetCompletedSnapshot(configurationFingerprint),
            correlationId);
    }

    private async Task<ExchangeDeliveryDiagnosticResult> SendAsync(
        string envelopeSender,
        string recipient,
        string subject,
        string body,
        IMicrosoftTokenProvider? tokenProvider,
        MicrosoftIdentityConfiguration? capturedConfiguration,
        string? configurationFingerprint,
        CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid();
        var now = _timeProvider.GetUtcNow();
        var content = Encoding.ASCII.GetBytes(
            $"From: <{envelopeSender}>\r\n" +
            $"To: <{recipient}>\r\n" +
            $"Date: {now.ToString("r", CultureInfo.InvariantCulture)}\r\n" +
            $"Message-ID: <{correlationId:N}@relaybridge.local>\r\n" +
            $"Subject: {subject}\r\n" +
            "Content-Type: text/plain; charset=us-ascii\r\n" +
            "\r\n" +
            $"{body}\r\n");
        var message = new QueuedMessage(
            correlationId,
            Guid.Empty,
            envelopeSender,
            [recipient],
            now,
            content.Length,
            $"diagnostic-{correlationId:N}.eml",
            QueueState.Delivering,
            AttemptCount: 1,
            LastAttemptUtc: now,
            PayloadPresent: false);
        using var contentStream = new MemoryStream(content, writable: false);
        var result = tokenProvider is null
            ? await _provider.DeliverAsync(message, contentStream, cancellationToken).ConfigureAwait(false)
            : await _provider.DeliverAsync(
                message,
                contentStream,
                tokenProvider,
                cancellationToken,
                configurationFingerprint,
                capturedConfiguration).ConfigureAwait(false);
        return new ExchangeDeliveryDiagnosticResult(
            result.Outcome,
            result.ErrorCategory,
            result.SafeMessage,
            _runtimeState.Snapshot,
            correlationId);
    }
}
