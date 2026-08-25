// SPDX-License-Identifier: MPL-2.0

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using RelayBridge.Core.Microsoft;
using RelayBridge.Core.Queue;
using RelayBridge.Infrastructure.Microsoft;
using RelayBridge.Infrastructure.Storage;
using Xunit;
using Xunit.Abstractions;

namespace RelayBridge.IntegrationTests;

public sealed class ExchangeSmtpOAuthTests
{
    private const string FakeToken = "fake-token-value-never-use-in-production";
    private readonly ITestOutputHelper _output;

    public ExchangeSmtpOAuthTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Empty_envelope_sender_is_rejected_before_token_or_network_use()
    {
        await using var server = new FakeExchangeSmtpServer();
        var tokenProvider = new FakeTokenProvider();
        var message = CreateMessage(10) with { EnvelopeFrom = string.Empty };

        var result = await CreateProvider(server, tokenProvider).DeliverAsync(
            message,
            new MemoryStream(new byte[10]),
            CancellationToken.None);

        Assert.Equal(DeliveryOutcome.PermanentFailure, result.Outcome);
        Assert.Equal("Protocol", result.ErrorCategory);
        Assert.Equal(0, tokenProvider.Calls);
        Assert.Empty(server.Commands);
    }

    [Fact]
    public async Task Response_parser_reads_bounded_multiline_response_and_capabilities()
    {
        var bytes = Encoding.ASCII.GetBytes(
            "250-fake.exchange\r\n250-STARTTLS\r\n250-AUTH XOAUTH2\r\n250 SIZE 36700160\r\n");
        var response = await new SmtpResponseReader().ReadAsync(new MemoryStream(bytes), CancellationToken.None);
        var capabilities = SmtpCapabilities.Parse(response);

        Assert.Equal(250, response.Code);
        Assert.Equal(4, response.Lines.Count);
        Assert.True(capabilities.StartTls);
        Assert.True(capabilities.XOAuth2);
        Assert.True(capabilities.Size);
        Assert.Equal(36700160, capabilities.MaximumSize);
    }

    [Theory]
    [InlineData("250 incomplete\n")]
    [InlineData("25x malformed\r\n")]
    [InlineData("250-first\r\n550 changed\r\n")]
    public async Task Response_parser_rejects_malformed_responses(string response)
    {
        await Assert.ThrowsAsync<SmtpProtocolException>(() => new SmtpResponseReader().ReadAsync(
            new MemoryStream(Encoding.ASCII.GetBytes(response)),
            CancellationToken.None));
    }

    [Fact]
    public async Task Response_parser_bounds_line_length()
    {
        var response = $"250 {new string('x', SmtpResponseReader.MaximumLineLength)}\r\n";
        await Assert.ThrowsAsync<SmtpProtocolException>(() => new SmtpResponseReader().ReadAsync(
            new MemoryStream(Encoding.ASCII.GetBytes(response)),
            CancellationToken.None));
    }

    [Fact]
    public async Task Response_parser_bounds_line_count()
    {
        var response = string.Concat(
            Enumerable.Repeat("250-more\r\n", SmtpResponseReader.MaximumLines),
            "250 done\r\n");
        await Assert.ThrowsAsync<SmtpProtocolException>(() => new SmtpResponseReader().ReadAsync(
            new MemoryStream(Encoding.ASCII.GetBytes(response)),
            CancellationToken.None));
    }

    [Fact]
    public async Task Response_parser_bounds_total_response_size()
    {
        var response = string.Concat(
            Enumerable.Repeat($"250-{new string('x', 2000)}\r\n", 17),
            "250 done\r\n");
        await Assert.ThrowsAsync<SmtpProtocolException>(() => new SmtpResponseReader().ReadAsync(
            new MemoryStream(Encoding.ASCII.GetBytes(response)),
            CancellationToken.None));
    }

    [Theory]
    [InlineData("", ".\r\n")]
    [InlineData("message", "message\r\n.\r\n")]
    [InlineData("message\r\n", "message\r\n.\r\n")]
    [InlineData(".\r\n..text\r\n", "..\r\n...text\r\n.\r\n")]
    [InlineData("a\r\n.b\r\nc", "a\r\n..b\r\nc\r\n.\r\n")]
    public async Task Data_transform_applies_transparency_and_exact_terminator(string source, string expected)
    {
        await using var destination = new MemoryStream();
        await ExchangeSmtpOAuthProvider.StreamDataAsync(
            new MemoryStream(Encoding.ASCII.GetBytes(source)),
            destination,
            CancellationToken.None);

        Assert.Equal(expected, Encoding.ASCII.GetString(destination.ToArray()));
    }

    [Fact]
    public async Task Complete_exchange_uses_tls_xoauth2_envelope_size_and_preserves_raw_mime()
    {
        await using var server = new FakeExchangeSmtpServer();
        server.Start();
        var logger = new TestLogger<ExchangeSmtpOAuthProvider>();
        var tokenProvider = new FakeTokenProvider();
        var provider = CreateProvider(server, tokenProvider, logger);
        var content = Encoding.ASCII.GetBytes(
            "From: visible@example.org\r\nContent-Type: multipart/mixed; boundary=x\r\n\r\n--x\r\n" +
            "Content-Transfer-Encoding: quoted-printable\r\n\r\n.dot=3Dvalue\r\n..two\r\n--x\r\n" +
            "Content-Transfer-Encoding: base64\r\n\r\nJVBERi0xLjQKZmFrZS1wZGY=\r\n--x--\r\n");
        var message = CreateMessage(content.Length, ["one@example.net"]);

        var result = await provider.DeliverAsync(message, new MemoryStream(content), CancellationToken.None);

        Assert.True(
            result.Outcome == DeliveryOutcome.Success,
            $"Outcome={result.Outcome}; Category={result.ErrorCategory}; Message={result.SafeMessage}; ServerFault={server.Fault}; Commands={string.Join(",", server.Commands)}");
        Assert.Equal(1, tokenProvider.Calls);
        Assert.Equal(
            $"user=scanner@example.com\u0001auth=Bearer {FakeToken}\u0001\u0001",
            server.AuthenticationPayload);
        Assert.Equal($"MAIL FROM:<scanner@example.com> SIZE={content.Length}", server.MailCommand);
        Assert.Equal(content, server.ReceivedData);
        Assert.True(server.QuitReceived);
        Assert.Equal(2, server.Commands.Count(command => command == "EHLO relaybridge.local"));
        Assert.Contains("AUTH XOAUTH2 [REDACTED]", server.Commands);
        Assert.DoesNotContain(logger.Messages, entry => entry.Contains(FakeToken, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, entry => entry.Contains(
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"user=scanner@example.com\u0001auth=Bearer {FakeToken}\u0001\u0001")),
            StringComparison.Ordinal));
    }

    [Fact]
    public void Data_termination_timeout_defaults_to_rfc_recommended_ten_minutes()
    {
        var options = new ExchangeSmtpOptions();

        Assert.Equal(TimeSpan.FromMinutes(10), options.DataTerminationTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.CommandTimeout);
        options.Validate();
    }

    [Fact]
    public async Task Delivery_telemetry_records_sanitized_data_acceptance_boundaries()
    {
        await using var server = new FakeExchangeSmtpServer();
        server.Start();
        var runtimeState = new ExchangeDeliveryRuntimeState();
        var content = "Subject: telemetry\r\n\r\nbody\r\n"u8.ToArray();

        var result = await CreateProvider(server, runtimeState: runtimeState).DeliverAsync(
            CreateMessage(content.Length),
            new MemoryStream(content),
            CancellationToken.None);

        var snapshot = runtimeState.Snapshot;
        Assert.Equal(DeliveryOutcome.Success, result.Outcome);
        Assert.Equal(220, snapshot.GreetingResponseCode);
        Assert.Equal(220, snapshot.StartTlsResponseCode);
        Assert.Equal(235, snapshot.AuthenticationResponseCode);
        Assert.Equal(250, snapshot.MailFromResponseCode);
        Assert.Equal([250], snapshot.RecipientResponseCodes);
        Assert.Equal(354, snapshot.DataResponseCode);
        Assert.True(snapshot.DataStreamingStarted);
        Assert.Equal(content.Length, snapshot.PayloadBytesRead);
        Assert.True(snapshot.SpoolEofReached);
        Assert.True(snapshot.DataTerminatorWriteStarted);
        Assert.True(snapshot.DataTerminatorFlushed);
        Assert.True(snapshot.FinalResponseWaitStarted);
        Assert.True(snapshot.FinalResponseReceived);
        Assert.Equal(250, snapshot.FinalResponseCode);
        Assert.Equal("2.0.0", snapshot.FinalResponseEnhancedStatusCode);
        Assert.Equal("250 2.0.0 Message accepted", snapshot.FinalResponseSafeSummary);
        Assert.NotNull(snapshot.DataStreamingStartedAt);
        Assert.NotNull(snapshot.SpoolEofReachedAt);
        Assert.NotNull(snapshot.DataTerminatorWriteStartedAt);
        Assert.NotNull(snapshot.DataTerminatorFlushedAt);
        Assert.NotNull(snapshot.FinalResponseWaitStartedAt);
        Assert.NotNull(snapshot.FinalResponseReceivedAt);
        Assert.NotNull(snapshot.LastCompletedAt);
    }

    [Fact]
    public async Task StartTls_is_required_before_token_or_authentication()
    {
        var scenario = new FakeExchangeScenario
        {
            PreTlsEhlo = ["250-fake.exchange", "250 SIZE 1000000"],
        };
        await using var server = new FakeExchangeSmtpServer(scenario);
        server.Start();
        var tokenProvider = new FakeTokenProvider();

        var result = await CreateProvider(server, tokenProvider).DeliverAsync(
            CreateMessage(10),
            new MemoryStream(new byte[10]),
            CancellationToken.None);

        Assert.True(
            result.Outcome == DeliveryOutcome.PermanentFailure,
            $"Outcome={result.Outcome}; Category={result.ErrorCategory}; Message={result.SafeMessage}; ServerFault={server.Fault}; Commands={string.Join(",", server.Commands)}");
        Assert.Equal("TLS", result.ErrorCategory);
        Assert.Equal(0, tokenProvider.Calls);
        Assert.DoesNotContain(server.Commands, command => command.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Production_tls_validation_rejects_untrusted_test_certificate()
    {
        await using var server = new FakeExchangeSmtpServer();
        server.Start();
        var tokenProvider = new FakeTokenProvider();

        var result = await CreateProvider(server, tokenProvider, trustTestCertificate: false).DeliverAsync(
            CreateMessage(10),
            new MemoryStream(new byte[10]),
            CancellationToken.None);

        Assert.Equal(DeliveryOutcome.TransientFailure, result.Outcome);
        Assert.Equal("TLS", result.ErrorCategory);
        Assert.Equal(0, tokenProvider.Calls);
    }

    [Fact]
    public async Task Xoauth2_must_be_advertised_after_tls_and_token_is_not_requested_early()
    {
        var scenario = new FakeExchangeScenario
        {
            PreTlsEhlo = ["250-fake.exchange", "250-STARTTLS", "250 AUTH XOAUTH2"],
            PostTlsEhlo = ["250-fake.exchange", "250 SIZE 1000000"],
        };
        await using var server = new FakeExchangeSmtpServer(scenario);
        server.Start();
        var tokenProvider = new FakeTokenProvider();

        var result = await CreateProvider(server, tokenProvider).DeliverAsync(
            CreateMessage(10),
            new MemoryStream(new byte[10]),
            CancellationToken.None);

        Assert.Equal(DeliveryOutcome.TransientFailure, result.Outcome);
        Assert.Equal("Authentication", result.ErrorCategory);
        Assert.NotNull(result.RetryAfter);
        Assert.Equal(0, tokenProvider.Calls);
        Assert.Equal(2, server.Commands.Count(command => command == "EHLO relaybridge.local"));
    }

    [Fact]
    public async Task Authentication_rejection_is_retryable_and_secret_safe()
    {
        var scenario = new FakeExchangeScenario { AuthResult = "535 5.7.3 Authentication unsuccessful" };
        await using var server = new FakeExchangeSmtpServer(scenario);
        server.Start();
        var logger = new TestLogger<ExchangeSmtpOAuthProvider>();

        var result = await CreateProvider(server, new FakeTokenProvider(), logger).DeliverAsync(
            CreateMessage(10),
            new MemoryStream(new byte[10]),
            CancellationToken.None);

        Assert.Equal(DeliveryOutcome.TransientFailure, result.Outcome);
        Assert.Equal("Authentication", result.ErrorCategory);
        Assert.NotNull(result.RetryAfter);
        Assert.DoesNotContain(logger.Messages, entry => entry.Contains(FakeToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Token_provider_failure_is_classified_before_auth_command()
    {
        await using var server = new FakeExchangeSmtpServer();
        server.Start();
        var tokens = new FakeTokenProvider(_ => throw new MicrosoftIdentityException(
            MicrosoftIdentityErrorCategory.CredentialRejected,
            "The certificate credential was rejected.",
            "AADSTS700027"));

        var result = await CreateProvider(server, tokens).DeliverAsync(
            CreateMessage(10),
            new MemoryStream(new byte[10]),
            CancellationToken.None);

        Assert.Equal(DeliveryOutcome.TransientFailure, result.Outcome);
        Assert.Equal("Authentication", result.ErrorCategory);
        Assert.DoesNotContain(server.Commands, command => command.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Cancellation_during_token_acquisition_propagates_before_data()
    {
        await using var server = new FakeExchangeSmtpServer();
        server.Start();
        var tokens = new FakeTokenProvider(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateProvider(server, tokens).DeliverAsync(
            CreateMessage(10),
            new MemoryStream(new byte[10]),
            cancellation.Token));
        Assert.False(server.DataCommandReceived);
    }

    [Theory]
    [InlineData("550 5.7.1 Sender not authorized", "SenderRejected")]
    [InlineData("552 5.3.4 Message too large", "MessageTooLarge")]
    public async Task Mail_from_permanent_rejections_are_classified_without_data(string response, string category)
    {
        await using var server = new FakeExchangeSmtpServer(new FakeExchangeScenario { MailResult = response });
        server.Start();

        var result = await CreateProvider(server).DeliverAsync(
            CreateMessage(10),
            new MemoryStream(new byte[10]),
            CancellationToken.None);

        Assert.Equal(DeliveryOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(category, result.ErrorCategory);
        Assert.False(server.DataCommandReceived);
    }

    [Fact]
    public async Task All_multiple_recipients_are_accepted_before_data()
    {
        var scenario = new FakeExchangeScenario
        {
            RecipientResults = ["250 accepted", "251 will forward", "250 accepted"],
        };
        await using var server = new FakeExchangeSmtpServer(scenario);
        server.Start();
        var content = "Subject: multi\r\n\r\nbody\r\n"u8.ToArray();

        var result = await CreateProvider(server).DeliverAsync(
            CreateMessage(content.Length, ["one@example.net", "two@example.net", "three@example.net"]),
            new MemoryStream(content),
            CancellationToken.None);

        Assert.Equal(DeliveryOutcome.Success, result.Outcome);
        Assert.True(server.DataCommandReceived);
    }

    [Theory]
    [InlineData("450 4.2.0 Try later", "TransientFailure")]
    [InlineData("550 5.1.1 No such recipient", "PermanentFailure")]
    public async Task Any_recipient_rejection_prevents_data_and_resets_transaction(
        string response,
        string expectedOutcome)
    {
        var scenario = new FakeExchangeScenario
        {
            RecipientResults = ["250 accepted", response, "250 accepted"],
        };
        await using var server = new FakeExchangeSmtpServer(scenario);
        server.Start();

        var result = await CreateProvider(server).DeliverAsync(
            CreateMessage(10, ["one@example.net", "two@example.net", "three@example.net"]),
            new MemoryStream(new byte[10]),
            CancellationToken.None);

        Assert.Equal(Enum.Parse<DeliveryOutcome>(expectedOutcome), result.Outcome);
        Assert.Equal("RecipientRejected", result.ErrorCategory);
        Assert.True(server.RsetReceived);
        Assert.False(server.DataCommandReceived);
    }

    [Theory]
    [InlineData("451 4.3.0 Try later", "TransientFailure", "TemporaryServerFailure")]
    [InlineData("554 5.6.0 Rejected", "PermanentFailure", "PermanentServerFailure")]
    public async Task Data_command_rejection_is_classified_without_streaming(
        string response,
        string expectedOutcome,
        string category)
    {
        await using var server = new FakeExchangeSmtpServer(new FakeExchangeScenario { DataResult = response });
        server.Start();

        var result = await CreateProvider(server).DeliverAsync(
            CreateMessage(10),
            new ThrowOnReadStream(),
            CancellationToken.None);

        Assert.Equal(Enum.Parse<DeliveryOutcome>(expectedOutcome), result.Outcome);
        Assert.Equal(category, result.ErrorCategory);
    }

    [Theory]
    [InlineData("451 4.3.0 Try later", "TransientFailure")]
    [InlineData("554 5.6.0 Message rejected", "PermanentFailure")]
    public async Task Final_data_rejection_is_not_success(string response, string expectedOutcome)
    {
        await using var server = new FakeExchangeSmtpServer(new FakeExchangeScenario { FinalResult = response });
        server.Start();
        var content = "Subject: final\r\n\r\nbody\r\n"u8.ToArray();

        var result = await CreateProvider(server).DeliverAsync(
            CreateMessage(content.Length),
            new MemoryStream(content),
            CancellationToken.None);

        Assert.Equal(Enum.Parse<DeliveryOutcome>(expectedOutcome), result.Outcome);
        Assert.NotEqual(DeliveryOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task Exchange_final_submission_size_rejection_is_message_too_large()
    {
        await using var server = new FakeExchangeSmtpServer(new FakeExchangeScenario
        {
            FinalResult = "554 5.2.270 Message size exceeds maximum size limit",
        });
        server.Start();
        var content = "Subject: too large\r\n\r\nbody\r\n"u8.ToArray();

        var result = await CreateProvider(server).DeliverAsync(
            CreateMessage(content.Length),
            new MemoryStream(content),
            CancellationToken.None);

        Assert.Equal(DeliveryOutcome.PermanentFailure, result.Outcome);
        Assert.Equal("MessageTooLarge", result.ErrorCategory);
    }

    [Fact]
    public async Task Disconnect_after_terminator_before_final_response_is_ambiguous()
    {
        await using var server = new FakeExchangeSmtpServer(new FakeExchangeScenario
        {
            Disconnect = FakeExchangeDisconnect.AfterDataTerminator,
        });
        server.Start();
        var content = "Subject: ambiguous\r\n\r\nbody\r\n"u8.ToArray();

        var result = await CreateProvider(server).DeliverAsync(
            CreateMessage(content.Length),
            new MemoryStream(content),
            CancellationToken.None);

        Assert.Equal(DeliveryOutcome.TransientFailure, result.Outcome);
        Assert.Equal("AmbiguousAcceptance", result.ErrorCategory);
    }

    [Fact]
    public async Task Final_response_delayed_beyond_generic_thirty_second_timeout_can_succeed()
    {
        await using var server = new FakeExchangeSmtpServer(new FakeExchangeScenario
        {
            FinalResponseDelay = TimeSpan.FromSeconds(31),
        });
        server.Start();
        var runtimeState = new ExchangeDeliveryRuntimeState();
        var content = "Subject: delayed\r\n\r\nbody\r\n"u8.ToArray();

        var result = await CreateProvider(
            server,
            runtimeState: runtimeState,
            configureOptions: options =>
            {
                options.CommandTimeout = TimeSpan.FromSeconds(30);
                options.DataTerminationTimeout = TimeSpan.FromSeconds(45);
            }).DeliverAsync(
                CreateMessage(content.Length),
                new MemoryStream(content),
                CancellationToken.None);

        Assert.Equal(DeliveryOutcome.Success, result.Outcome);
        Assert.True(runtimeState.Snapshot.FinalResponseReceived);
        Assert.Equal(250, runtimeState.Snapshot.FinalResponseCode);
    }

    [Fact]
    public async Task Final_response_inside_configured_data_termination_timeout_succeeds()
    {
        await using var server = new FakeExchangeSmtpServer(new FakeExchangeScenario
        {
            FinalResponseDelay = TimeSpan.FromMilliseconds(250),
        });
        server.Start();
        var content = "Subject: bounded\r\n\r\nbody\r\n"u8.ToArray();

        var result = await CreateProvider(
            server,
            configureOptions: options =>
            {
                options.CommandTimeout = TimeSpan.FromMilliseconds(100);
                options.DataTerminationTimeout = TimeSpan.FromSeconds(2);
            }).DeliverAsync(
                CreateMessage(content.Length),
                new MemoryStream(content),
                CancellationToken.None);

        Assert.Equal(DeliveryOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task Final_response_exceeding_data_termination_timeout_is_ambiguous_without_immediate_resend()
    {
        await using var server = new FakeExchangeSmtpServer(new FakeExchangeScenario
        {
            FinalResponseDelay = TimeSpan.FromSeconds(2),
        });
        server.Start();
        await using var context = QueueTestContext.Create();
        var runtimeState = new ExchangeDeliveryRuntimeState();
        var provider = CreateProvider(
            server,
            runtimeState: runtimeState,
            configureOptions: options => options.DataTerminationTimeout = TimeSpan.FromMilliseconds(200));
        var queued = await context.EnqueueAsync(128);
        var worker = context.CreateWorker(provider);

        Assert.True(await worker.ProcessOneAsync());

        var persisted = Assert.Single(context.Database.GetQueuedMessages());
        var snapshot = runtimeState.Snapshot;
        Assert.Equal(queued.Id, persisted.Id);
        Assert.Equal(QueueState.RetryScheduled, persisted.State);
        Assert.True(persisted.PayloadPresent);
        Assert.True(snapshot.SpoolEofReached);
        Assert.True(snapshot.DataTerminatorFlushed);
        Assert.True(snapshot.FinalResponseWaitStarted);
        Assert.False(snapshot.FinalResponseReceived);
        Assert.Equal("TimeoutException", snapshot.LastExceptionType);
        Assert.Equal(ExchangeDeliveryStatus.Ambiguous, snapshot.Status);
        Assert.False(await worker.ProcessOneAsync());
        Assert.Single(server.Commands, command => command == "DATA");
    }

    [Fact]
    public async Task Final_250_is_success_even_when_server_closes_before_quit()
    {
        await using var server = new FakeExchangeSmtpServer(new FakeExchangeScenario
        {
            Disconnect = FakeExchangeDisconnect.AfterFinalResponse,
        });
        server.Start();
        var content = "Subject: accepted\r\n\r\nbody\r\n"u8.ToArray();

        var result = await CreateProvider(server).DeliverAsync(
            CreateMessage(content.Length),
            new MemoryStream(content),
            CancellationToken.None);

        Assert.Equal(DeliveryOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_final_acceptance_is_ambiguous()
    {
        await using var server = new FakeExchangeSmtpServer(new FakeExchangeScenario
        {
            FinalResponseDelay = TimeSpan.FromSeconds(30),
        });
        server.Start();
        var content = "Subject: cancel\r\n\r\nbody\r\n"u8.ToArray();
        using var cancellation = new CancellationTokenSource();
        var delivery = CreateProvider(server).DeliverAsync(
            CreateMessage(content.Length),
            new MemoryStream(content),
            cancellation.Token);
        await server.DataReceivedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        var result = await delivery;

        Assert.Equal(DeliveryOutcome.TransientFailure, result.Outcome);
        Assert.Equal("AmbiguousAcceptance", result.ErrorCategory);
    }

    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(100, 101, true)]
    [InlineData(100, 99, false)]
    public async Task Advertised_size_is_preflighted_and_equal_size_is_allowed(
        long messageSize,
        long advertisedSize,
        bool shouldSend)
    {
        var scenario = new FakeExchangeScenario
        {
            PostTlsEhlo = ["250-fake.exchange", "250-AUTH XOAUTH2", $"250 SIZE {advertisedSize}"],
        };
        await using var server = new FakeExchangeSmtpServer(scenario);
        server.Start();
        var tokens = new FakeTokenProvider();

        var result = await CreateProvider(server, tokens).DeliverAsync(
            CreateMessage(messageSize),
            new MemoryStream(new byte[messageSize]),
            CancellationToken.None);

        Assert.Equal(shouldSend ? DeliveryOutcome.Success : DeliveryOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(shouldSend ? 1 : 0, tokens.Calls);
        Assert.Equal(shouldSend, server.DataCommandReceived);
        if (!shouldSend)
        {
            Assert.Equal("MessageTooLarge", result.ErrorCategory);
        }
    }

    [Fact]
    public async Task Size_omission_does_not_add_mail_parameter()
    {
        var scenario = new FakeExchangeScenario
        {
            PostTlsEhlo = ["250-fake.exchange", "250 AUTH XOAUTH2"],
        };
        await using var server = new FakeExchangeSmtpServer(scenario);
        server.Start();

        var result = await CreateProvider(server).DeliverAsync(
            CreateMessage(100),
            new MemoryStream(new byte[100]),
            CancellationToken.None);

        Assert.Equal(DeliveryOutcome.Success, result.Outcome);
        Assert.Equal("MAIL FROM:<scanner@example.com>", server.MailCommand);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(25)]
    public async Task Large_messages_stream_with_bounded_buffers_and_integrity(int mebibytes)
    {
        var size = mebibytes * 1024L * 1024;
        await using var server = new FakeExchangeSmtpServer(new FakeExchangeScenario
        {
            PostTlsEhlo = ["250-fake.exchange", "250-AUTH XOAUTH2", "250 SIZE 104857600"],
        });
        server.Start();
        await using var source = new PatternMessageStream(size);
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var stopwatch = Stopwatch.StartNew();

        var result = await CreateProvider(server).DeliverAsync(
            CreateMessage(size),
            source,
            CancellationToken.None);
        stopwatch.Stop();
        var managedAfter = GC.GetTotalMemory(forceFullCollection: true);

        Assert.Equal(DeliveryOutcome.Success, result.Outcome);
        Assert.Equal(size, server.ReceivedDataLength);
        Assert.Equal(PatternMessageStream.ComputeHash(size), server.ReceivedDataHash);
        _output.WriteLine(
            "{0} MiB: {1:N2} ms, {2:N2} MiB/s, managed heap delta {3:N0} bytes.",
            mebibytes,
            stopwatch.Elapsed.TotalMilliseconds,
            mebibytes / stopwatch.Elapsed.TotalSeconds,
            managedAfter - managedBefore);
    }

    [Theory]
    [InlineData(FakeExchangeDisconnect.BeforeGreeting)]
    [InlineData(FakeExchangeDisconnect.AfterEhlo)]
    [InlineData(FakeExchangeDisconnect.DuringTls)]
    [InlineData(FakeExchangeDisconnect.AfterAuth)]
    [InlineData(FakeExchangeDisconnect.AfterMail)]
    [InlineData(FakeExchangeDisconnect.DuringRecipient)]
    public async Task Disconnect_before_data_is_retryable_not_ambiguous(FakeExchangeDisconnect disconnect)
    {
        await using var server = new FakeExchangeSmtpServer(new FakeExchangeScenario { Disconnect = disconnect });
        server.Start();

        var result = await CreateProvider(server).DeliverAsync(
            CreateMessage(10),
            new MemoryStream(new byte[10]),
            CancellationToken.None);

        Assert.Equal(DeliveryOutcome.TransientFailure, result.Outcome);
        Assert.NotEqual("AmbiguousAcceptance", result.ErrorCategory);
    }

    [Fact]
    public async Task Disconnect_during_data_before_terminator_is_retryable_not_ambiguous()
    {
        await using var server = new FakeExchangeSmtpServer(new FakeExchangeScenario
        {
            Disconnect = FakeExchangeDisconnect.DuringData,
        });
        server.Start();
        var runtimeState = new ExchangeDeliveryRuntimeState();

        var result = await CreateProvider(server, runtimeState: runtimeState).DeliverAsync(
            CreateMessage(1024 * 1024),
            new PatternMessageStream(1024 * 1024),
            CancellationToken.None);

        Assert.Equal(DeliveryOutcome.TransientFailure, result.Outcome);
        Assert.Equal("Network", result.ErrorCategory);
        Assert.False(runtimeState.Snapshot.SpoolEofReached);
        Assert.False(runtimeState.Snapshot.DataTerminatorWriteStarted);
        Assert.False(runtimeState.Snapshot.FinalResponseWaitStarted);
    }

    [Theory]
    [InlineData("250 2.0.0 Accepted", false, QueueState.Delivered)]
    [InlineData("451 4.3.0 Try later", false, QueueState.RetryScheduled)]
    [InlineData("554 5.6.0 Rejected", false, QueueState.PermanentFailure)]
    [InlineData("250 2.0.0 Accepted", true, QueueState.RetryScheduled)]
    public async Task Queue_worker_persists_exchange_result_without_holding_delivery_transaction(
        string finalResponse,
        bool disconnectBeforeFinalResponse,
        QueueState expectedState)
    {
        var scenario = new FakeExchangeScenario
        {
            FinalResult = finalResponse,
            Disconnect = disconnectBeforeFinalResponse
                ? FakeExchangeDisconnect.AfterDataTerminator
                : FakeExchangeDisconnect.None,
        };
        await using var server = new FakeExchangeSmtpServer(scenario);
        server.Start();
        await using var context = QueueTestContext.Create();
        var queued = await context.EnqueueAsync(128);
        var worker = context.CreateWorker(CreateProvider(server));

        Assert.True(await worker.ProcessOneAsync());

        var persisted = Assert.Single(context.Database.GetQueuedMessages());
        Assert.Equal(queued.Id, persisted.Id);
        Assert.Equal(expectedState, persisted.State);
        Assert.Equal(expectedState != QueueState.Delivered, persisted.PayloadPresent);
    }

    [Fact]
    public async Task Explicit_delivery_diagnostic_returns_safe_structured_checkpoints()
    {
        await using var server = new FakeExchangeSmtpServer();
        server.Start();
        var runtimeState = new ExchangeDeliveryRuntimeState();
        var provider = CreateProvider(server, runtimeState: runtimeState);
        var tester = new ExchangeDeliveryTester(provider, runtimeState, TimeProvider.System);

        var result = await tester.TestAsync(
            "scanner@example.com",
            "recipient@example.net",
            CancellationToken.None);

        Assert.Equal(DeliveryOutcome.Success, result.Outcome);
        Assert.True(result.Checkpoints.DnsResolved);
        Assert.True(result.Checkpoints.TcpConnected);
        Assert.True(result.Checkpoints.TlsEstablished);
        Assert.True(result.Checkpoints.TokenAcquired);
        Assert.True(result.Checkpoints.XOAuth2Authenticated);
        Assert.True(result.Checkpoints.SenderAuthorized);
        Assert.True(result.Checkpoints.MessageAccepted);
        Assert.DoesNotContain(FakeToken, result.SafeMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Completed_delivery_evidence_remains_tied_to_its_captured_configuration()
    {
        await using var context = QueueTestContext.Create();
        var certificate = MicrosoftCertificateReference.Create(
            "0123456789ABCDEF0123456789ABCDEF01234567",
            CertificateStoreTarget.CurrentUser);
        var configurationA = MicrosoftIdentityConfiguration.Create(Guid.NewGuid(), Guid.NewGuid(), certificate);
        var configurationB = MicrosoftIdentityConfiguration.Create(Guid.NewGuid(), Guid.NewGuid(), certificate);
        SetActiveMicrosoftConfiguration(context.Database, configurationA, "scanner@example.com");
        var activeA = context.Database.GetActiveMicrosoftConfiguration()!;
        var sequence = new MicrosoftRuntimeEvidenceSequence();
        var runtimeState = new ExchangeDeliveryRuntimeState(sequence);
        var identityState = new MicrosoftIdentityRuntimeState(context.Database, sequence);
        var tokenProvider = new FakeTokenProvider();
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var serverA = new FakeExchangeSmtpServer(new FakeExchangeScenario
        {
            FinalResponseRelease = releaseA.Task,
        });
        serverA.Start();
        var providerA = CreateProvider(
            serverA,
            tokenProvider,
            runtimeState: runtimeState,
            database: context.Database);

        var deliveryA = providerA.DeliverAsync(
            CreateMessage("Subject: A\r\n\r\nbody\r\n"u8.Length),
            new MemoryStream("Subject: A\r\n\r\nbody\r\n"u8.ToArray()),
            CancellationToken.None);
        var reachedDataOrCompleted = await Task.WhenAny(
            serverA.DataReceivedSignal.Task,
            deliveryA).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(
            ReferenceEquals(reachedDataOrCompleted, serverA.DataReceivedSignal.Task),
            deliveryA.IsCompleted
                ? $"Delivery completed before DATA: {(await deliveryA).ErrorCategory}; commands: {string.Join(", ", serverA.Commands)}"
                : "Delivery did not reach DATA.");
        SetActiveMicrosoftConfiguration(context.Database, configurationB, "scanner@example.com");
        var activeB = context.Database.GetActiveMicrosoftConfiguration()!;
        releaseA.SetResult();

        Assert.Equal(DeliveryOutcome.Success, (await deliveryA).Outcome);
        Assert.Equal(
            MicrosoftRuntimeReadiness.VerificationRequired,
            MicrosoftRuntimeReadinessPolicy.Evaluate(true, activeB.Fingerprint, identityState, runtimeState));
        Assert.Equal(activeA.Fingerprint, runtimeState.GetCompletedSnapshot(activeA.Fingerprint).ConfigurationFingerprint);
        Assert.Equal(ExchangeDeliveryStatus.NotTested, runtimeState.GetCompletedSnapshot(activeB.Fingerprint).Status);

        await using var serverB = new FakeExchangeSmtpServer();
        serverB.Start();
        var providerB = CreateProvider(
            serverB,
            tokenProvider,
            runtimeState: runtimeState,
            database: context.Database);
        var messageB = "Subject: B\r\n\r\nbody\r\n"u8.ToArray();
        Assert.Equal(
            DeliveryOutcome.Success,
            (await providerB.DeliverAsync(
                CreateMessage(messageB.Length),
                new MemoryStream(messageB),
                CancellationToken.None)).Outcome);
        Assert.Equal(
            MicrosoftRuntimeReadiness.Ready,
            MicrosoftRuntimeReadinessPolicy.Evaluate(true, activeB.Fingerprint, identityState, runtimeState));
        Assert.Equal([configurationA.ClientId, configurationB.ClientId], tokenProvider.ExplicitConfigurations.Select(item => item.ClientId));
    }

    [Fact]
    public void Same_configuration_uses_success_that_completes_after_an_overlapping_failure()
    {
        var sequence = new MicrosoftRuntimeEvidenceSequence();
        var state = new ExchangeDeliveryRuntimeState(sequence);
        var now = new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero);
        var olderStart = state.BeginAttempt(now, capturedConfiguration: null, "same");
        var newerStart = state.BeginAttempt(now, capturedConfiguration: null, "same");

        state.RecordResult(
            newerStart,
            now,
            DeliveryResult.TransientFailure("Network", "Failed."));
        state.RecordResult(olderStart, now, DeliveryResult.Succeeded());

        var completed = state.GetCompletedSnapshot("same");
        Assert.Equal(ExchangeDeliveryStatus.Healthy, completed.Status);
        Assert.Equal(olderStart.AttemptId, completed.AttemptId);
    }

    [Fact]
    public void Same_configuration_uses_failure_that_completes_after_an_overlapping_success()
    {
        var sequence = new MicrosoftRuntimeEvidenceSequence();
        var state = new ExchangeDeliveryRuntimeState(sequence);
        var now = new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero);
        var olderStart = state.BeginAttempt(now, capturedConfiguration: null, "same");
        var newerStart = state.BeginAttempt(now, capturedConfiguration: null, "same");

        state.RecordResult(olderStart, now, DeliveryResult.Succeeded());
        state.RecordResult(
            newerStart,
            now,
            DeliveryResult.TransientFailure("Network", "Failed."));

        var completed = state.GetCompletedSnapshot("same");
        Assert.Equal(ExchangeDeliveryStatus.Failed, completed.Status);
        Assert.Equal(newerStart.AttemptId, completed.AttemptId);
    }

    [Fact]
    public void Abandoned_exchange_attempt_does_not_replace_completed_evidence()
    {
        var sequence = new MicrosoftRuntimeEvidenceSequence();
        var state = new ExchangeDeliveryRuntimeState(sequence);
        var now = new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero);
        var completedAttempt = state.BeginAttempt(now, capturedConfiguration: null, "same");
        state.RecordResult(completedAttempt, now, DeliveryResult.Succeeded());
        var abandoned = state.BeginAttempt(now, capturedConfiguration: null, "same");

        state.Abandon(abandoned);

        Assert.Equal(ExchangeDeliveryStatus.Healthy, state.GetCompletedSnapshot("same").Status);
        Assert.Equal(completedAttempt.AttemptId, state.GetCompletedSnapshot("same").AttemptId);
    }

    private static ExchangeSmtpOAuthProvider CreateProvider(
        FakeExchangeSmtpServer server,
        FakeTokenProvider? tokenProvider = null,
        ILogger<ExchangeSmtpOAuthProvider>? logger = null,
        bool trustTestCertificate = true,
        ExchangeDeliveryRuntimeState? runtimeState = null,
        Action<ExchangeSmtpOptions>? configureOptions = null,
        RelayDatabase? database = null)
    {
        var options = new ExchangeSmtpOptions
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            TlsTimeout = TimeSpan.FromSeconds(5),
            CommandTimeout = TimeSpan.FromSeconds(5),
            DataTerminationTimeout = TimeSpan.FromSeconds(5),
            MinimumDataTimeout = TimeSpan.FromSeconds(10),
            DataTimeoutPerMiB = TimeSpan.FromSeconds(2),
            MaximumDataTimeout = TimeSpan.FromMinutes(2),
            ConfigurationFailureRetryAfter = TimeSpan.FromMinutes(5),
        };
        configureOptions?.Invoke(options);
        return new ExchangeSmtpOAuthProvider(
            tokenProvider ?? new FakeTokenProvider(),
            options,
            server.CreateEndpoint(trustTestCertificate),
            runtimeState ?? new ExchangeDeliveryRuntimeState(),
            TimeProvider.System,
            logger ?? new TestLogger<ExchangeSmtpOAuthProvider>(),
            database);
    }

    private static void SetActiveMicrosoftConfiguration(
        RelayDatabase database,
        MicrosoftIdentityConfiguration configuration,
        string sender)
    {
        database.SaveMicrosoftIdentityConfiguration(configuration);
        using var connection = database.OpenConnectionForDiagnostics();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE MicrosoftIdentityConfiguration SET AuthorizedSender = $sender WHERE Id = 1;";
        command.Parameters.AddWithValue("$sender", sender);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static QueuedMessage CreateMessage(long sizeBytes, IReadOnlyList<string>? recipients = null)
    {
        return new QueuedMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "scanner@example.com",
            recipients ?? ["recipient@example.net"],
            DateTimeOffset.UtcNow,
            sizeBytes,
            $"{Guid.NewGuid():N}.eml",
            QueueState.Delivering,
            AttemptCount: 1);
    }

    private sealed class FakeTokenProvider : IMicrosoftTokenProvider
    {
        private readonly Func<CancellationToken, Task<MicrosoftAccessToken>> _handler;
        private int _calls;

        public FakeTokenProvider(Func<CancellationToken, Task<MicrosoftAccessToken>>? handler = null)
        {
            _handler = handler ?? (_ => Task.FromResult(new MicrosoftAccessToken(
                FakeToken,
                DateTimeOffset.UtcNow.AddMinutes(30),
                Guid.NewGuid())));
        }

        public int Calls => Volatile.Read(ref _calls);

        public ConcurrentQueue<MicrosoftIdentityConfiguration> ExplicitConfigurations { get; } = new();

        public Task<MicrosoftAccessToken> GetExchangeTokenAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return _handler(cancellationToken);
        }

        public Task<MicrosoftAccessToken> GetExchangeTokenAsync(
            MicrosoftIdentityConfiguration configuration,
            CancellationToken cancellationToken)
        {
            ExplicitConfigurations.Enqueue(configuration);
            return GetExchangeTokenAsync(cancellationToken);
        }
    }

    private sealed class ThrowOnReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("DATA was read.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class PatternMessageStream : Stream
    {
        private static readonly byte[] Pattern = Encoding.ASCII.GetBytes($"{new string('x', 62)}\r\n");
        private readonly long _length;
        private long _position;

        public PatternMessageStream(long length)
        {
            if (length % Pattern.Length != 0)
            {
                throw new ArgumentException("Pattern test length must be a multiple of 64.", nameof(length));
            }

            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public static byte[] ComputeHash(long length)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (long position = 0; position < length; position += Pattern.Length)
            {
                hash.AppendData(Pattern);
            }

            return hash.GetHashAndReset();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _length)
            {
                return 0;
            }

            var remaining = (int)Math.Min(count, _length - _position);
            for (var index = 0; index < remaining; index++)
            {
                buffer[offset + index] = Pattern[(_position + index) % Pattern.Length];
            }

            _position += remaining;
            return remaining;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position >= _length)
            {
                return ValueTask.FromResult(0);
            }

            var read = (int)Math.Min(buffer.Length, _length - _position);
            for (var index = 0; index < read; index++)
            {
                buffer.Span[index] = Pattern[(_position + index) % Pattern.Length];
            }

            _position += read;
            return ValueTask.FromResult(read);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
