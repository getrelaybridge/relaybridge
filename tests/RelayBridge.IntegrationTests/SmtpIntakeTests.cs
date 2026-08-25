// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RelayBridge.Infrastructure.Storage;
using Xunit;
using Xunit.Abstractions;

namespace RelayBridge.IntegrationTests;

public sealed class SmtpIntakeTests
{
    private readonly ITestOutputHelper _output;

    public SmtpIntakeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Owned_loopback_test_seam_starts_real_listener_and_accepts_authenticated_intake()
    {
        await using var host = await SmtpTestHost.CreateAsync();
        var provisioned = host.Devices.AddAuthenticatedDevice(
            "Owned listener",
            "owned-listener",
            ["127.0.0.1"],
            ["scanner@example.com"]);
        Assert.Equal(IPAddress.Loopback, host.Endpoint.Address);
        Assert.True(host.Options.AllowCleartextAuthentication);
        Assert.True(host.Options.AllowInsecureLoopbackAuthenticationForTests);
        await using var client = await host.ConnectAsync();
        Assert.StartsWith("220", await client.ReadResponseAsync(), StringComparison.Ordinal);
        Assert.Contains("AUTH PLAIN LOGIN", await client.CommandAsync("EHLO test.local"), StringComparison.Ordinal);
        var payload = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"\0owned-listener\0{provisioned.PlaintextPassword}"));

        Assert.StartsWith("235", await client.CommandAsync($"AUTH PLAIN {payload}"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anonymous_client_cannot_use_an_authenticated_device()
    {
        await using var host = await SmtpTestHost.CreateAsync();
        _ = host.Devices.AddAuthenticatedDevice(
            "Scanner",
            "scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);
        await using var client = await ConnectAndGreetAsync(host);

        var response = await client.CommandAsync("MAIL FROM:<scanner@example.com>");

        Assert.StartsWith("530", response, StringComparison.Ordinal);
        Assert.Empty(host.Preview.GetMessages());
    }

    [Fact]
    public async Task Null_reverse_path_is_rejected_before_queueing()
    {
        await using var host = await SmtpTestHost.CreateAsync();
        _ = host.Devices.AddLegacyDevice(
            "Legacy scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);
        await using var client = await host.ConnectAsync();
        Assert.StartsWith("220", await client.ReadResponseAsync(), StringComparison.Ordinal);
        Assert.StartsWith("250", await client.CommandAsync("EHLO test.local"), StringComparison.Ordinal);

        Assert.StartsWith("501", await client.CommandAsync("MAIL FROM:<>"), StringComparison.Ordinal);
        Assert.StartsWith("503", await client.CommandAsync("RCPT TO:<recipient@example.net>"), StringComparison.Ordinal);
        Assert.Empty(host.Preview.GetMessages());
    }

    [Fact]
    public async Task Cleartext_authentication_is_not_advertised_or_accepted_by_default()
    {
        await using var host = await SmtpTestHost.CreateAsync(options =>
            options.AllowCleartextAuthentication = false);
        await using var client = await host.ConnectAsync();
        Assert.StartsWith("220", await client.ReadResponseAsync(), StringComparison.Ordinal);

        var ehlo = await client.CommandAsync("EHLO test.local");

        Assert.DoesNotContain("AUTH", ehlo, StringComparison.Ordinal);
        Assert.StartsWith("538", await client.CommandAsync("AUTH PLAIN invalid"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Size_is_advertised_parsed_and_independently_enforced_during_DATA()
    {
        await using var host = await SmtpTestHost.CreateAsync(options => options.MaxMessageBytes = 1024);
        _ = host.Devices.AddLegacyDevice(
            "Legacy scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);
        await using var client = await host.ConnectAsync();
        Assert.StartsWith("220", await client.ReadResponseAsync(), StringComparison.Ordinal);

        var ehlo = await client.CommandAsync("EHLO test.local");

        Assert.Contains("SIZE 1024", ehlo, StringComparison.Ordinal);
        Assert.StartsWith(
            "552",
            await client.CommandAsync("MAIL FROM:<scanner@example.com> SIZE=1025"),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "552",
            await client.CommandAsync("MAIL FROM:<scanner@example.com> SIZE=99999999999999999999"),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "501",
            await client.CommandAsync("MAIL FROM:<scanner@example.com> SIZE=100000000000000000000"),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "250",
            await client.CommandAsync("MAIL FROM:<scanner@example.com> SIZE=10"),
            StringComparison.Ordinal);
        Assert.StartsWith("250", await client.CommandAsync("RCPT TO:<recipient@example.net>"), StringComparison.Ordinal);
        Assert.StartsWith("354", await client.CommandAsync("DATA"), StringComparison.Ordinal);
        await client.SendLineAsync(new string('a', 1023));

        Assert.StartsWith("552", await client.ReadResponseAsync(), StringComparison.Ordinal);
        Assert.Empty(host.Preview.GetMessages());
    }

    [Fact]
    public async Task Auth_exchange_rejects_invalid_sequence_cancel_and_oversized_credentials()
    {
        await using var host = await SmtpTestHost.CreateAsync();
        var provisioned = host.Devices.AddAuthenticatedDevice(
            "Scanner",
            "scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);
        await using var client = await host.ConnectAsync();
        Assert.StartsWith("220", await client.ReadResponseAsync(), StringComparison.Ordinal);

        Assert.StartsWith("503", await client.CommandAsync("AUTH LOGIN"), StringComparison.Ordinal);
        Assert.StartsWith("250", await client.CommandAsync("HELO test.local"), StringComparison.Ordinal);
        Assert.StartsWith("503", await client.CommandAsync("AUTH LOGIN"), StringComparison.Ordinal);
        Assert.Contains("AUTH PLAIN LOGIN", await client.CommandAsync("EHLO test.local"), StringComparison.Ordinal);
        Assert.StartsWith("334", await client.CommandAsync("AUTH LOGIN"), StringComparison.Ordinal);
        Assert.StartsWith("501", await client.CommandAsync("*"), StringComparison.Ordinal);
        Assert.StartsWith("501", await client.CommandAsync("AUTH PLAIN not!base64"), StringComparison.Ordinal);

        var oversizedPassword = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0scanner\0{new string('x', 256)}"));
        Assert.StartsWith("501", await client.CommandAsync($"AUTH PLAIN {oversizedPassword}"), StringComparison.Ordinal);

        var payload = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"\0scanner\0{provisioned.PlaintextPassword}"));
        Assert.StartsWith("235", await client.CommandAsync($"AUTH PLAIN {payload}"), StringComparison.Ordinal);
        Assert.StartsWith("503", await client.CommandAsync($"AUTH PLAIN {payload}"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Auth_login_accepts_raw_message_only_after_it_is_queued()
    {
        await using var host = await SmtpTestHost.CreateAsync();
        var provisioned = host.Devices.AddAuthenticatedDevice(
            "Scanner",
            "scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);
        await using var client = await ConnectAndGreetAsync(host);

        Assert.StartsWith("334", await client.CommandAsync("AUTH LOGIN"), StringComparison.Ordinal);
        Assert.StartsWith(
            "334",
            await client.CommandAsync(Convert.ToBase64String("scanner"u8)),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "235",
            await client.CommandAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(provisioned.PlaintextPassword))),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            host.Logger.Messages,
            message => message.Contains(provisioned.PlaintextPassword, StringComparison.Ordinal));

        var response = await SendSmallMessageAsync(client);

        Assert.StartsWith("250", response, StringComparison.Ordinal);
        var queued = Assert.Single(host.Preview.GetMessages());
        Assert.Equal("scanner@example.com", queued.EnvelopeFrom);
        Assert.Equal(["recipient@example.net"], queued.Recipients);
        var expected = "From: scanner@example.com\r\nX-Test: dots\r\n\r\nfirst\r\n.\r\n..text\r\n";
        Assert.Equal(expected, await File.ReadAllTextAsync(host.Preview.GetSpoolPath(queued), Encoding.ASCII));
        Assert.Equal(Encoding.ASCII.GetByteCount(expected), queued.SizeBytes);
    }

    [Fact]
    public async Task Auth_plain_accepts_valid_credentials()
    {
        await using var host = await SmtpTestHost.CreateAsync();
        var provisioned = host.Devices.AddAuthenticatedDevice(
            "Scanner",
            "scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);
        await using var client = await ConnectAndGreetAsync(host);
        var payload = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"\0scanner\0{provisioned.PlaintextPassword}"));

        var response = await client.CommandAsync($"AUTH PLAIN {payload}");

        Assert.StartsWith("235", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_password_malformed_auth_disabled_device_and_wrong_ip_are_rejected()
    {
        await AssertAuthenticationRejectedAsync(enabled: true, ["127.0.0.1"], "wrong-password");
        await AssertAuthenticationRejectedAsync(enabled: false, ["127.0.0.1"], useRealPassword: true);
        await AssertAuthenticationRejectedAsync(enabled: true, ["192.0.2.0/24"], useRealPassword: true);

        await using var host = await SmtpTestHost.CreateAsync();
        await using var client = await ConnectAndGreetAsync(host);
        Assert.StartsWith("501", await client.CommandAsync("AUTH PLAIN not!base64"), StringComparison.Ordinal);
        Assert.StartsWith("250", await client.CommandAsync("NOOP"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Device_password_plaintext_is_not_stored_in_SQLite()
    {
        await using var host = await SmtpTestHost.CreateAsync();
        var provisioned = host.Devices.AddAuthenticatedDevice(
            "Scanner",
            "scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);
        using var connection = host.Database.OpenConnectionForDiagnostics();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT PasswordVerifier FROM Devices WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", provisioned.Device.Id.ToString("D"));

        var verifier = Assert.IsType<string>(command.ExecuteScalar());

        Assert.DoesNotContain(provisioned.PlaintextPassword, verifier, StringComparison.Ordinal);
        Assert.StartsWith("v1$pbkdf2-sha256$600000$", verifier, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legacy_mode_requires_matching_source_and_sender()
    {
        await using var host = await SmtpTestHost.CreateAsync();
        _ = host.Devices.AddLegacyDevice(
            "Legacy scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);
        await using var client = await ConnectAndGreetAsync(host);

        Assert.StartsWith(
            "550",
            await client.CommandAsync("MAIL FROM:<unauthorized@example.com>"),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "250",
            await client.CommandAsync("MAIL FROM:<scanner@example.com>"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legacy_mode_uses_source_and_sender_together_without_ambiguous_device_selection()
    {
        await using var host = await SmtpTestHost.CreateAsync();
        var first = host.Devices.AddLegacyDevice(
            "First scanner",
            ["127.0.0.0/8"],
            ["first@example.com"]);
        var second = host.Devices.AddLegacyDevice(
            "Second scanner",
            ["127.0.0.1"],
            ["second@example.com"]);
        await using (var client = await ConnectAndGreetAsync(host))
        {
            Assert.StartsWith(
                "250",
                await client.CommandAsync("MAIL FROM:<second@example.com>"),
                StringComparison.Ordinal);
            Assert.StartsWith("250", await client.CommandAsync("RCPT TO:<recipient@example.net>"), StringComparison.Ordinal);
            Assert.StartsWith("354", await client.CommandAsync("DATA"), StringComparison.Ordinal);
            await client.SendLineAsync("body");
            await client.SendLineAsync(".");
            Assert.StartsWith("250", await client.ReadResponseAsync(), StringComparison.Ordinal);
        }

        Assert.Equal(second.Id, Assert.Single(host.Preview.GetMessages()).DeviceId);
        Assert.NotEqual(first.Id, second.Id);

        _ = host.Devices.AddLegacyDevice(
            "Ambiguous scanner",
            ["127.0.0.1"],
            ["first@example.com"]);
        await using var ambiguousClient = await ConnectAndGreetAsync(host);

        Assert.StartsWith(
            "550",
            await ambiguousClient.CommandAsync("MAIL FROM:<first@example.com>"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_command_order_is_rejected_and_RSET_preserves_authentication()
    {
        await using var host = await SmtpTestHost.CreateAsync();
        var provisioned = host.Devices.AddAuthenticatedDevice(
            "Scanner",
            "scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);
        await using var client = await host.ConnectAsync();
        Assert.StartsWith("220", await client.ReadResponseAsync(), StringComparison.Ordinal);

        Assert.StartsWith("503", await client.CommandAsync("MAIL FROM:<scanner@example.com>"), StringComparison.Ordinal);
        Assert.Contains("AUTH PLAIN LOGIN", await client.CommandAsync("EHLO test.local"), StringComparison.Ordinal);
        Assert.StartsWith("503", await client.CommandAsync("RCPT TO:<recipient@example.net>"), StringComparison.Ordinal);
        Assert.StartsWith("503", await client.CommandAsync("DATA"), StringComparison.Ordinal);
        Assert.StartsWith("250", await client.CommandAsync("RSET"), StringComparison.Ordinal);

        var payload = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"\0scanner\0{provisioned.PlaintextPassword}"));
        Assert.StartsWith("235", await client.CommandAsync($"AUTH PLAIN {payload}"), StringComparison.Ordinal);
        Assert.StartsWith("250", await client.CommandAsync("MAIL FROM:<scanner@example.com>"), StringComparison.Ordinal);
        Assert.StartsWith("503", await client.CommandAsync($"AUTH PLAIN {payload}"), StringComparison.Ordinal);
        Assert.StartsWith("503", await client.CommandAsync("MAIL FROM:<scanner@example.com>"), StringComparison.Ordinal);
        Assert.StartsWith("503", await client.CommandAsync("DATA"), StringComparison.Ordinal);
        Assert.StartsWith("250", await client.CommandAsync("RCPT TO:<recipient@example.net>"), StringComparison.Ordinal);
        Assert.StartsWith("250", await client.CommandAsync("RSET"), StringComparison.Ordinal);
        Assert.StartsWith("503", await client.CommandAsync("RCPT TO:<recipient@example.net>"), StringComparison.Ordinal);
        Assert.StartsWith("503", await client.CommandAsync("DATA"), StringComparison.Ordinal);
        Assert.StartsWith("250", await client.CommandAsync("MAIL FROM:<scanner@example.com>"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rset_clears_envelope_and_recipient_count_is_bounded()
    {
        await using var host = await SmtpTestHost.CreateAsync(options => options.MaxRecipients = 2);
        _ = host.Devices.AddLegacyDevice(
            "Legacy scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);
        await using var client = await ConnectAndGreetAsync(host);
        Assert.StartsWith("250", await client.CommandAsync("MAIL FROM:<scanner@example.com>"), StringComparison.Ordinal);
        Assert.StartsWith("250", await client.CommandAsync("RCPT TO:<one@example.net>"), StringComparison.Ordinal);
        Assert.StartsWith("250", await client.CommandAsync("RCPT TO:<two@example.net>"), StringComparison.Ordinal);
        Assert.StartsWith("452", await client.CommandAsync("RCPT TO:<three@example.net>"), StringComparison.Ordinal);

        Assert.StartsWith("250", await client.CommandAsync("RSET"), StringComparison.Ordinal);
        Assert.StartsWith("503", await client.CommandAsync("RCPT TO:<one@example.net>"), StringComparison.Ordinal);
        Assert.StartsWith("503", await client.CommandAsync("DATA"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Multiple_recipients_are_persisted_in_envelope_order()
    {
        await using var host = await SmtpTestHost.CreateAsync();
        _ = host.Devices.AddLegacyDevice(
            "Legacy scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);
        await using var client = await ConnectAndGreetAsync(host);
        Assert.StartsWith("250", await client.CommandAsync("MAIL FROM:<scanner@example.com>"), StringComparison.Ordinal);
        Assert.StartsWith("250", await client.CommandAsync("RCPT TO:<one@example.net>"), StringComparison.Ordinal);
        Assert.StartsWith("250", await client.CommandAsync("RCPT TO:<two@example.net>"), StringComparison.Ordinal);
        Assert.StartsWith("354", await client.CommandAsync("DATA"), StringComparison.Ordinal);
        await client.SendLineAsync("From: scanner@example.com");
        await client.SendLineAsync(string.Empty);
        await client.SendLineAsync("body");
        await client.SendLineAsync(".");

        Assert.StartsWith("250", await client.ReadResponseAsync(), StringComparison.Ordinal);
        Assert.Equal(
            ["one@example.net", "two@example.net"],
            Assert.Single(host.Preview.GetMessages()).Recipients);
    }

    [Fact]
    public async Task Partial_DATA_disconnect_removes_temporary_spool()
    {
        await using var host = await SmtpTestHost.CreateAsync();
        _ = host.Devices.AddLegacyDevice(
            "Legacy scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);
        await using (var client = await ConnectAndGreetAsync(host))
        {
            await StartDataAsync(client);
            await client.SendLineAsync("From: scanner@example.com");
            await client.SendLineAsync(string.Empty);
            await client.SendLineAsync("incomplete body");
        }

        await EventuallyAsync(() => !Directory.EnumerateFiles(host.Database.IncomingDirectory).Any());
        Assert.Empty(host.Preview.GetMessages());
        Assert.Empty(Directory.EnumerateFiles(host.Database.PendingDirectory));
    }

    [Fact]
    public async Task Oversized_DATA_is_rejected_without_queue_artifacts()
    {
        await using var host = await SmtpTestHost.CreateAsync(options => options.MaxMessageBytes = 1024);
        _ = host.Devices.AddLegacyDevice(
            "Legacy scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);
        await using var client = await ConnectAndGreetAsync(host);
        await StartDataAsync(client);

        await client.SendLineAsync(new string('a', 1500));
        var response = await client.ReadResponseAsync();

        Assert.StartsWith("552", response, StringComparison.Ordinal);
        await EventuallyAsync(() => !Directory.EnumerateFiles(host.Database.IncomingDirectory).Any());
        Assert.Empty(host.Preview.GetMessages());
        Assert.Empty(Directory.EnumerateFiles(host.Database.PendingDirectory));
    }

    [Fact]
    public async Task SQLite_commit_failure_returns_451_and_does_not_leave_accepted_message()
    {
        await using var host = await SmtpTestHost.CreateAsync();
        _ = host.Devices.AddLegacyDevice(
            "Legacy scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);
        using (var connection = host.Database.OpenConnectionForDiagnostics())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TRIGGER FailQueueInsert
                BEFORE INSERT ON QueueMessages
                BEGIN
                    SELECT RAISE(FAIL, 'injected queue failure');
                END;
                """;
            command.ExecuteNonQuery();
        }

        await using var client = await ConnectAndGreetAsync(host);
        var response = await SendSmallMessageAsync(client);

        Assert.StartsWith("451", response, StringComparison.Ordinal);
        Assert.Empty(host.Preview.GetMessages());
        Assert.Empty(Directory.EnumerateFiles(host.Database.IncomingDirectory));
        Assert.Empty(Directory.EnumerateFiles(host.Database.PendingDirectory));
    }

    [Fact]
    public async Task Accepted_message_survives_listener_restart()
    {
        await using var host = await SmtpTestHost.CreateAsync();
        _ = host.Devices.AddLegacyDevice(
            "Legacy scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);
        await using (var client = await ConnectAndGreetAsync(host))
        {
            Assert.StartsWith("250", await SendSmallMessageAsync(client), StringComparison.Ordinal);
        }

        var before = Assert.Single(host.Preview.GetMessages());
        await host.RestartAsync();
        var after = Assert.Single(host.Preview.GetMessages());

        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.DeviceId, after.DeviceId);
        Assert.Equal(before.EnvelopeFrom, after.EnvelopeFrom);
        Assert.Equal(before.Recipients, after.Recipients);
        Assert.Equal(before.ReceivedUtc, after.ReceivedUtc);
        Assert.Equal(before.SizeBytes, after.SizeBytes);
        Assert.Equal(before.SpoolFileName, after.SpoolFileName);
        Assert.Equal(before.State, after.State);
        Assert.True(File.Exists(host.Preview.GetSpoolPath(after)));
    }

    [Fact]
    public async Task Source_connection_limit_rejects_excess_session()
    {
        await using var host = await SmtpTestHost.CreateAsync(options =>
        {
            options.MaxConnections = 2;
            options.MaxConnectionsPerIp = 1;
        });
        await using var first = await host.ConnectAsync();
        Assert.StartsWith("220", await first.ReadResponseAsync(), StringComparison.Ordinal);
        await using var second = await host.ConnectAsync();

        Assert.StartsWith("421", await second.ReadResponseAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Multiple_concurrent_clients_queue_independent_messages()
    {
        await using var host = await SmtpTestHost.CreateAsync(options =>
        {
            options.MaxConnections = 4;
            options.MaxConnectionsPerIp = 4;
        });
        _ = host.Devices.AddLegacyDevice(
            "Concurrent scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);

        var submissions = Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var client = await ConnectAndGreetAsync(host);
            return await SendSmallMessageAsync(client);
        });
        var responses = await Task.WhenAll(submissions);

        Assert.All(responses, response => Assert.StartsWith("250", response, StringComparison.Ordinal));
        Assert.Equal(4, host.Preview.GetMessages().Count);
        Assert.Equal(4, Directory.EnumerateFiles(host.Database.PendingDirectory).Count());
    }

    [Fact]
    public async Task Command_length_is_bounded_and_listener_remains_available()
    {
        await using var host = await SmtpTestHost.CreateAsync(options => options.MaxCommandLength = 512);
        await using (var client = await host.ConnectAsync())
        {
            Assert.StartsWith("220", await client.ReadResponseAsync(), StringComparison.Ordinal);
            await client.SendLineAsync($"NOOP {new string('x', 600)}");
            Assert.StartsWith("500", await client.ReadResponseAsync(), StringComparison.Ordinal);
        }

        await using var nextClient = await host.ConnectAsync();
        Assert.StartsWith("220", await nextClient.ReadResponseAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_flood_is_bounded_and_listener_remains_available()
    {
        await using var host = await SmtpTestHost.CreateAsync(options => options.MaxCommandsPerSession = 10);
        await using (var client = await host.ConnectAsync())
        {
            Assert.StartsWith("220", await client.ReadResponseAsync(), StringComparison.Ordinal);
            for (var index = 0; index < 10; index++)
            {
                Assert.StartsWith("250", await client.CommandAsync("NOOP"), StringComparison.Ordinal);
            }

            using var rejectionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.StartsWith(
                "421 4.7.0 Too many commands",
                await client.ReadResponseAsync(rejectionDeadline.Token),
                StringComparison.Ordinal);
            await AssertConnectionTerminatesAsync(client, TimeSpan.FromSeconds(5));
        }

        Assert.Empty(host.Preview.GetMessages());
        Assert.Empty(host.Database.GetQueuedMessages());
        Assert.Empty(Directory.EnumerateFiles(host.Database.IncomingDirectory));
        Assert.Empty(Directory.EnumerateFiles(host.Database.PendingDirectory));

        await using var nextClient = await host.ConnectAsync();
        Assert.StartsWith("220", await nextClient.ReadResponseAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Partial_command_line_times_out_and_releases_connection()
    {
        await using var host = await SmtpTestHost.CreateAsync(options => options.IdleTimeout = TimeSpan.FromSeconds(1));
        await using (var client = await host.ConnectAsync())
        {
            Assert.StartsWith("220", await client.ReadResponseAsync(), StringComparison.Ordinal);
            await client.SendBytesAsync("EHLO incomplete"u8.ToArray());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.StartsWith("421", await client.ReadResponseAsync(timeout.Token), StringComparison.Ordinal);
        }

        await using var nextClient = await host.ConnectAsync();
        Assert.StartsWith("220", await nextClient.ReadResponseAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_mime_many_headers_and_binary_like_body_are_preserved_as_received()
    {
        await using var host = await SmtpTestHost.CreateAsync();
        _ = host.Devices.AddLegacyDevice(
            "Legacy scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);
        await using var client = await ConnectAndGreetAsync(host);
        await StartDataAsync(client);

        var prefix = Encoding.ASCII.GetBytes(string.Join(
            "\r\n",
            Enumerable.Range(0, 100).Select(index => $"X-RelayBridge-{index}: value")) +
            "\r\nContent-Type: multipart/mixed; boundary=missing\r\n\r\n");
        var binaryLine = new byte[] { 0x00, 0x01, 0x7f, 0x80, 0xff, (byte)'x', 0x0d, 0x0a };
        var expected = prefix.Concat(binaryLine).ToArray();
        await client.SendBytesAsync(expected);
        await client.SendBytesAsync(".\r\n"u8.ToArray());

        Assert.StartsWith("250", await client.ReadResponseAsync(), StringComparison.Ordinal);
        var queued = Assert.Single(host.Preview.GetMessages());
        Assert.Equal(expected, await File.ReadAllBytesAsync(host.Preview.GetSpoolPath(queued)));
    }

    [Fact]
    public async Task Helo_noop_and_quit_follow_expected_session_semantics()
    {
        await using var host = await SmtpTestHost.CreateAsync();
        await using var client = await host.ConnectAsync();
        Assert.StartsWith("220", await client.ReadResponseAsync(), StringComparison.Ordinal);

        Assert.StartsWith("250", await client.CommandAsync("HELO printer.local"), StringComparison.Ordinal);
        Assert.StartsWith("250", await client.CommandAsync("NOOP"), StringComparison.Ordinal);
        Assert.StartsWith("221", await client.CommandAsync("QUIT"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bare_line_feed_is_rejected_and_listener_keeps_running()
    {
        await using var host = await SmtpTestHost.CreateAsync();
        await using (var client = await host.ConnectAsync())
        {
            Assert.StartsWith("220", await client.ReadResponseAsync(), StringComparison.Ordinal);
            await client.SendBytesAsync("EHLO invalid\n"u8.ToArray());
            Assert.StartsWith("500", await client.ReadResponseAsync(), StringComparison.Ordinal);
        }

        await using var nextClient = await host.ConnectAsync();
        Assert.StartsWith("220", await nextClient.ReadResponseAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authentication_attempts_are_bounded_per_session()
    {
        await using var host = await SmtpTestHost.CreateAsync(options => options.MaxAuthenticationAttempts = 3);
        _ = host.Devices.AddAuthenticatedDevice(
            "Scanner",
            "scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);
        await using var client = await ConnectAndGreetAsync(host);
        var invalidPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0scanner\0wrong-password"));

        Assert.StartsWith("535", await client.CommandAsync($"AUTH PLAIN {invalidPayload}"), StringComparison.Ordinal);
        Assert.StartsWith("535", await client.CommandAsync($"AUTH PLAIN {invalidPayload}"), StringComparison.Ordinal);
        Assert.StartsWith("535", await client.CommandAsync($"AUTH PLAIN {invalidPayload}"), StringComparison.Ordinal);
        await client.SendLineAsync("NOOP");
        await Assert.ThrowsAsync<IOException>(() => client.ReadResponseAsync());
    }

    [Fact]
    public async Task Concurrent_authentication_is_serialized_without_rejecting_valid_devices()
    {
        await using var host = await SmtpTestHost.CreateAsync(options =>
        {
            options.MaxConnections = 5;
            options.MaxConnectionsPerIp = 5;
        });
        var provisioned = host.Devices.AddAuthenticatedDevice(
            "Scanner",
            "scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);
        var payload = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"\0scanner\0{provisioned.PlaintextPassword}"));
        var stopwatch = Stopwatch.StartNew();

        var authentications = Enumerable.Range(0, 5).Select(async _ =>
        {
            await using var client = await ConnectAndGreetAsync(host);
            return await client.CommandAsync($"AUTH PLAIN {payload}");
        });
        var responses = await Task.WhenAll(authentications);
        stopwatch.Stop();

        Assert.All(responses, response => Assert.StartsWith("235", response, StringComparison.Ordinal));
        _output.WriteLine(
            "Five concurrent SMTP authentications completed through the serialized PBKDF2 gate in {0:N2} ms.",
            stopwatch.Elapsed.TotalMilliseconds);
    }

    [Fact]
    public async Task Ten_megabyte_message_streams_to_spool_with_bounded_memory()
    {
        const int lineCount = 10_500;
        await using var host = await SmtpTestHost.CreateAsync(options =>
        {
            options.MaxMessageBytes = 12L * 1024 * 1024;
            options.MaxDataLineLength = 2048;
            options.IdleTimeout = TimeSpan.FromSeconds(30);
        });
        _ = host.Devices.AddLegacyDevice(
            "Performance scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);
        await using var client = await ConnectAndGreetAsync(host);
        await StartDataAsync(client);
        var line = Encoding.ASCII.GetBytes($"{new string('a', 998)}\r\n");
        var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
        var stopwatch = Stopwatch.StartNew();

        for (var index = 0; index < lineCount; index++)
        {
            await client.SendBytesAsync(line);
        }

        await client.SendLineAsync(".");
        var response = await client.ReadResponseAsync();
        stopwatch.Stop();
        var memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
        var memoryGrowth = memoryAfter - memoryBefore;

        Assert.StartsWith("250", response, StringComparison.Ordinal);
        var queued = Assert.Single(host.Preview.GetMessages());
        Assert.Equal(lineCount * 1000L, queued.SizeBytes);
        Assert.Equal(queued.SizeBytes, new FileInfo(host.Preview.GetSpoolPath(queued)).Length);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60));
        Assert.True(memoryGrowth < 32L * 1024 * 1024, $"Managed memory grew by {memoryGrowth} bytes.");
        _output.WriteLine(
            "10 MB intake: {0:N2} MiB in {1:N2}s ({2:N2} MiB/s), managed-memory delta {3:N2} MiB",
            queued.SizeBytes / 1024d / 1024d,
            stopwatch.Elapsed.TotalSeconds,
            queued.SizeBytes / 1024d / 1024d / stopwatch.Elapsed.TotalSeconds,
            memoryGrowth / 1024d / 1024d);
    }

    [Fact]
    public async Task Small_message_acceptance_latency_sanity()
    {
        await using var host = await SmtpTestHost.CreateAsync();
        _ = host.Devices.AddLegacyDevice(
            "Latency scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);
        await using var client = await ConnectAndGreetAsync(host);
        var stopwatch = Stopwatch.StartNew();

        var response = await SendSmallMessageAsync(client);

        stopwatch.Stop();
        Assert.StartsWith("250", response, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
        _output.WriteLine("Small-message SMTP transaction and durable acceptance: {0:N2} ms", stopwatch.Elapsed.TotalMilliseconds);
    }

    private static async Task<SmtpTestClient> ConnectAndGreetAsync(SmtpTestHost host)
    {
        var client = await host.ConnectAsync();
        Assert.StartsWith("220", await client.ReadResponseAsync(), StringComparison.Ordinal);
        var response = await client.CommandAsync("EHLO test.local");
        Assert.Contains("SIZE", response, StringComparison.Ordinal);
        Assert.Contains("AUTH PLAIN LOGIN", response, StringComparison.Ordinal);
        Assert.DoesNotContain("STARTTLS", response, StringComparison.Ordinal);
        return client;
    }

    private static async Task AssertConnectionTerminatesAsync(SmtpTestClient client, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            var response = await client.ReadResponseAsync(deadline.Token);
            Assert.Fail($"SMTP session remained usable after the command limit and returned: {response}");
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private static async Task StartDataAsync(SmtpTestClient client)
    {
        Assert.StartsWith("250", await client.CommandAsync("MAIL FROM:<scanner@example.com>"), StringComparison.Ordinal);
        Assert.StartsWith("250", await client.CommandAsync("RCPT TO:<recipient@example.net>"), StringComparison.Ordinal);
        Assert.StartsWith("354", await client.CommandAsync("DATA"), StringComparison.Ordinal);
    }

    private static async Task<string> SendSmallMessageAsync(SmtpTestClient client)
    {
        await StartDataAsync(client);
        await client.SendLineAsync("From: scanner@example.com");
        await client.SendLineAsync("X-Test: dots");
        await client.SendLineAsync(string.Empty);
        await client.SendLineAsync("first");
        await client.SendLineAsync("..");
        await client.SendLineAsync("...text");
        await client.SendLineAsync(".");
        return await client.ReadResponseAsync();
    }

    private static async Task AssertAuthenticationRejectedAsync(
        bool enabled,
        string[] allowedNetworks,
        string? password = null,
        bool useRealPassword = false)
    {
        await using var host = await SmtpTestHost.CreateAsync();
        var provisioned = host.Devices.AddAuthenticatedDevice(
            "Scanner",
            "scanner",
            allowedNetworks,
            ["scanner@example.com"],
            enabled);
        await using var client = await ConnectAndGreetAsync(host);
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"\0scanner\0{(useRealPassword ? provisioned.PlaintextPassword : password)}"));

        Assert.StartsWith("535", await client.CommandAsync($"AUTH PLAIN {payload}"), StringComparison.Ordinal);
        Assert.StartsWith("530", await client.CommandAsync("MAIL FROM:<scanner@example.com>"), StringComparison.Ordinal);
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}
