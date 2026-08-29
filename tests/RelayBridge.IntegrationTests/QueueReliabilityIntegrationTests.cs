// SPDX-License-Identifier: MPL-2.0

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RelayBridge.Core.Queue;
using RelayBridge.Infrastructure.Queue;
using RelayBridge.Infrastructure.Storage;
using Xunit;
using Xunit.Abstractions;

namespace RelayBridge.IntegrationTests;

public sealed class QueueReliabilityIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public QueueReliabilityIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Claim_is_atomic_and_does_not_hold_database_lock_during_delivery()
    {
        await using var context = QueueTestContext.Create();
        var queued = await context.EnqueueAsync();

        var claims = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => context.Database.ClaimNextEligible(context.TimeProvider.GetUtcNow()))));
        var claim = Assert.Single(claims, candidate => candidate is not null);
        Assert.Equal(queued.Id, claim!.Id);
        Assert.Equal(QueueState.Delivering, claim.State);
        Assert.Equal(1, claim.AttemptCount);

        Assert.True(context.Database.RecoverInterruptedClaim(queued.Id, "Test", "Recovered for test."));
        var deliveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedDeliveryProvider(async (_, _, cancellationToken) =>
        {
            deliveryStarted.SetResult();
            await releaseDelivery.Task.WaitAsync(cancellationToken);
            return DeliveryResult.Succeeded();
        });
        var worker = context.CreateWorker(provider);
        var processing = worker.ProcessOneAsync();
        await deliveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await context.EnqueueAsync();
        Assert.Contains(context.Database.GetQueuedMessages(), message => message.Id == second.Id);

        releaseDelivery.SetResult();
        Assert.True(await processing);
    }

    [Fact]
    public async Task Success_marks_metadata_delivered_and_removes_payload()
    {
        await using var context = QueueTestContext.Create();
        var queued = await context.EnqueueAsync();
        var worker = context.CreateWorker(new ScriptedDeliveryProvider(DeliveryResult.Succeeded()));

        Assert.True(await worker.ProcessOneAsync());

        var completed = Assert.Single(context.Database.GetQueuedMessages());
        Assert.Equal(QueueState.Delivered, completed.State);
        Assert.False(completed.PayloadPresent);
        Assert.NotNull(completed.CompletedUtc);
        Assert.False(File.Exists(context.Database.GetPendingPath(queued.SpoolFileName)));
    }

    [Fact]
    public async Task Transient_retry_is_persisted_and_due_time_is_honored_after_restart()
    {
        await using var context = QueueTestContext.Create();
        var queued = await context.EnqueueAsync();
        var worker = context.CreateWorker(new ScriptedDeliveryProvider(
            DeliveryResult.TransientFailure("Temporary", "Try later.", TimeSpan.FromMinutes(2))));

        Assert.True(await worker.ProcessOneAsync());
        var scheduled = Assert.Single(context.Database.GetQueuedMessages());
        Assert.Equal(QueueState.RetryScheduled, scheduled.State);
        Assert.Equal(context.TimeProvider.GetUtcNow() + TimeSpan.FromMinutes(2), scheduled.NextAttemptUtc);
        Assert.Null(context.Database.ClaimNextEligible(context.TimeProvider.GetUtcNow()));

        var restarted = new RelayBridge.Infrastructure.Storage.RelayDatabase(
            new RelayBridge.Infrastructure.Storage.RelayStorageOptions { DataDirectory = context.DataDirectory },
            AppContext.BaseDirectory);
        restarted.Initialize();
        Assert.Null(restarted.ClaimNextEligible(context.TimeProvider.GetUtcNow()));
        context.TimeProvider.Advance(TimeSpan.FromMinutes(2));
        var retry = restarted.ClaimNextEligible(context.TimeProvider.GetUtcNow());
        Assert.NotNull(retry);
        Assert.Equal(queued.Id, retry.Id);
        Assert.Equal(2, retry.AttemptCount);
    }

    [Fact]
    public async Task Queued_message_survives_logical_service_restart_and_then_processes()
    {
        await using var original = QueueTestContext.Create();
        var queued = await original.EnqueueAsync();
        var restartedDatabase = new RelayDatabase(
            new RelayStorageOptions { DataDirectory = original.DataDirectory },
            AppContext.BaseDirectory);
        restartedDatabase.Initialize();
        var restartedOptions = original.Options;
        var restartedSignal = new QueueWorkSignal();
        var restartedDeliveryActivation = new QueueDeliveryActivation();
        restartedDeliveryActivation.Activate();
        var reconciler = new QueueReconciler(
            restartedDatabase,
            original.FileSystem,
            restartedOptions,
            original.TimeProvider,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<QueueReconciler>.Instance);
        _ = reconciler.Reconcile();
        var provider = new ScriptedDeliveryProvider(DeliveryResult.Succeeded());
        var worker = new QueueWorker(
            restartedDatabase,
            original.FileSystem,
            provider,
            restartedOptions,
            restartedSignal,
            restartedDeliveryActivation,
            original.TimeProvider,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<QueueWorker>.Instance);

        Assert.True(await worker.ProcessOneAsync());

        var delivered = Assert.Single(restartedDatabase.GetQueuedMessages());
        Assert.Equal(queued.Id, delivered.Id);
        Assert.Equal(QueueState.Delivered, delivered.State);
    }

    [Fact]
    public async Task Permanent_failure_is_terminal_and_payload_is_retained()
    {
        await using var context = QueueTestContext.Create();
        var queued = await context.EnqueueAsync();
        var worker = context.CreateWorker(new ScriptedDeliveryProvider(
            DeliveryResult.PermanentFailure("Rejected", "Recipient rejected.")));

        Assert.True(await worker.ProcessOneAsync());

        var failed = Assert.Single(context.Database.GetQueuedMessages());
        Assert.Equal(QueueState.PermanentFailure, failed.State);
        Assert.Equal("Rejected", failed.LastErrorCategory);
        Assert.True(failed.PayloadPresent);
        Assert.True(File.Exists(context.Database.GetPendingPath(queued.SpoolFileName)));
        Assert.Null(context.Database.ClaimNextEligible(context.TimeProvider.GetUtcNow()));
    }

    [Fact]
    public async Task Retry_limits_convert_transient_failure_to_permanent_failure()
    {
        await using var context = QueueTestContext.Create(options => options.MaximumAttempts = 1);
        await context.EnqueueAsync();
        var worker = context.CreateWorker(new ScriptedDeliveryProvider(
            DeliveryResult.TransientFailure("Temporary", "Still unavailable.")));

        Assert.True(await worker.ProcessOneAsync());

        var failed = Assert.Single(context.Database.GetQueuedMessages());
        Assert.Equal(QueueState.PermanentFailure, failed.State);
        Assert.Equal("RetryLimitExceeded", failed.LastErrorCategory);
    }

    [Fact]
    public async Task Startup_reconciliation_is_repeatable_and_repairs_stale_missing_orphan_and_temp_files()
    {
        await using var context = QueueTestContext.Create(options =>
            options.TemporaryFileMaxAge = TimeSpan.FromMinutes(10));
        var stale = await context.EnqueueAsync();
        var missing = await context.EnqueueAsync();
        _ = context.Database.ClaimNextEligible(context.TimeProvider.GetUtcNow());
        File.Delete(context.Database.GetPendingPath(missing.SpoolFileName));
        var orphanPath = Path.Combine(context.Database.PendingDirectory, $"{Guid.NewGuid():N}.eml");
        await File.WriteAllTextAsync(orphanPath, "orphan");
        var oldTemp = Path.Combine(context.Database.IncomingDirectory, $"{Guid.NewGuid():N}.tmp");
        var recentTemp = Path.Combine(context.Database.IncomingDirectory, $"{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(oldTemp, "old");
        await File.WriteAllTextAsync(recentTemp, "recent");
        File.SetLastWriteTimeUtc(oldTemp, context.TimeProvider.GetUtcNow().UtcDateTime - TimeSpan.FromHours(1));

        var first = context.CreateReconciler().Reconcile();
        var second = context.CreateReconciler().Reconcile();

        Assert.Equal(1, first.RecoveredDelivering);
        Assert.Equal(1, first.MissingPayloads);
        Assert.Equal(1, first.DeletedOrphans);
        Assert.Equal(1, first.DeletedTemporaryFiles);
        Assert.Equal(new QueueReconciliationResult(0, 0, 0, 0, 0), second);
        Assert.False(File.Exists(orphanPath));
        Assert.False(File.Exists(oldTemp));
        Assert.True(File.Exists(recentTemp));
        var messages = context.Database.GetQueuedMessages();
        Assert.Equal(QueueState.Queued, messages.Single(message => message.Id == stale.Id).State);
        var missingRow = messages.Single(message => message.Id == missing.Id);
        Assert.Equal(QueueState.PermanentFailure, missingRow.State);
        Assert.False(missingRow.PayloadPresent);
    }

    [Fact]
    public async Task Reconciliation_database_query_failure_logs_and_preserves_all_spool_files()
    {
        await using var context = QueueTestContext.Create();
        var accepted = await context.EnqueueAsync();
        var acceptedPath = context.Database.GetPendingPath(accepted.SpoolFileName);
        var orphanPath = Path.Combine(context.Database.PendingDirectory, $"{Guid.NewGuid():N}.eml");
        await File.WriteAllTextAsync(orphanPath, "unverified orphan");
        using (var connection = context.Database.OpenConnectionForDiagnostics())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE QueueRecipients;";
            command.ExecuteNonQuery();
        }

        var logger = new TestLogger<QueueReconciler>();
        var reconciler = new QueueReconciler(
            context.Database,
            context.FileSystem,
            context.Options,
            context.TimeProvider,
            logger);

        Assert.Throws<SqliteException>(() => reconciler.Reconcile());
        Assert.True(File.Exists(acceptedPath));
        Assert.True(File.Exists(orphanPath));
        Assert.Contains(logger.Messages, message => message.Contains("QueueReconciliationFailed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancelled_reconciliation_preserves_unverified_orphan()
    {
        await using var context = QueueTestContext.Create();
        var orphanPath = Path.Combine(context.Database.PendingDirectory, $"{Guid.NewGuid():N}.eml");
        await File.WriteAllTextAsync(orphanPath, "unverified orphan");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => context.CreateReconciler().Reconcile(cancelled.Token));
        Assert.True(File.Exists(orphanPath));
    }

    [Fact]
    public async Task Active_receive_temp_is_preserved_and_old_temp_cleanup_remains_idempotent()
    {
        await using var context = QueueTestContext.Create(options =>
            options.TemporaryFileMaxAge = TimeSpan.FromMinutes(10));
        await using var activeReceive = context.MessageStore.BeginReceive(expectedBytes: 16);
        await activeReceive.Stream.WriteAsync("active"u8.ToArray());
        var activePath = Assert.Single(Directory.EnumerateFiles(context.Database.IncomingDirectory, "*.tmp"));
        var oldPath = Path.Combine(context.Database.IncomingDirectory, $"{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(oldPath, "old");
        File.SetLastWriteTimeUtc(oldPath, context.TimeProvider.GetUtcNow().UtcDateTime - TimeSpan.FromHours(1));

        var first = context.CreateReconciler().Reconcile();
        var second = context.CreateReconciler().Reconcile();

        Assert.Equal(1, first.DeletedTemporaryFiles);
        Assert.Equal(0, second.DeletedTemporaryFiles);
        Assert.True(File.Exists(activePath));
        Assert.False(File.Exists(oldPath));
    }

    [Fact]
    public async Task Payload_count_and_bytes_follow_persisted_payload_presence_across_states_and_restart()
    {
        await using var context = QueueTestContext.Create();
        var delivered = await context.EnqueueAsync(10);
        var permanent = await context.EnqueueAsync(20);
        var retry = await context.EnqueueAsync(30);
        var outcomes = new Dictionary<Guid, DeliveryResult>
        {
            [delivered.Id] = DeliveryResult.Succeeded(),
            [permanent.Id] = DeliveryResult.PermanentFailure("Rejected", "Permanent."),
            [retry.Id] = DeliveryResult.TransientFailure("Temporary", "Retry."),
        };
        var worker = context.CreateWorker(new ScriptedDeliveryProvider(
            (message, _, _) => Task.FromResult(outcomes[message.Id])));
        while (await worker.ProcessOneAsync())
        {
        }

        Assert.Equal(new QueueCapacityUsage(2, 50), context.Database.GetQueueCapacityUsage());
        File.Delete(context.Database.GetPendingPath(retry.SpoolFileName));
        var orphanPath = Path.Combine(context.Database.PendingDirectory, $"{Guid.NewGuid():N}.eml");
        await File.WriteAllTextAsync(orphanPath, "not counted");

        _ = context.CreateReconciler().Reconcile();
        _ = context.CreateReconciler().Reconcile();

        Assert.Equal(new QueueCapacityUsage(1, 20), context.Database.GetQueueCapacityUsage());
        Assert.False(File.Exists(orphanPath));
        var restarted = new RelayDatabase(
            new RelayStorageOptions { DataDirectory = context.DataDirectory },
            AppContext.BaseDirectory);
        restarted.Initialize();
        Assert.Equal(new QueueCapacityUsage(1, 20), restarted.GetQueueCapacityUsage());

        await using (var abandoned = context.MessageStore.BeginReceive(expectedBytes: 12))
        {
            await abandoned.Stream.WriteAsync("not accepted"u8.ToArray());
        }

        Assert.Equal(new QueueCapacityUsage(1, 20), restarted.GetQueueCapacityUsage());
    }

    [Fact]
    public async Task Failed_delivered_payload_cleanup_remains_conservatively_counted_until_reconciliation()
    {
        var fileSystem = new FaultInjectingSpoolFileSystem();
        await using var context = QueueTestContext.Create(fileSystem: fileSystem);
        var queued = await context.EnqueueAsync(64);
        fileSystem.FailDelete = true;

        Assert.True(await context.CreateWorker(
            new ScriptedDeliveryProvider(DeliveryResult.Succeeded())).ProcessOneAsync());

        var delivered = Assert.Single(context.Database.GetQueuedMessages());
        Assert.Equal(QueueState.Delivered, delivered.State);
        Assert.True(delivered.PayloadPresent);
        Assert.Equal(new QueueCapacityUsage(1, 64), context.Database.GetQueueCapacityUsage());
        Assert.True(File.Exists(context.Database.GetPendingPath(queued.SpoolFileName)));

        fileSystem.FailDelete = false;
        var reconciliation = context.CreateReconciler().Reconcile();
        Assert.Equal(1, reconciliation.DeletedDeliveredPayloads);
        Assert.Equal(new QueueCapacityUsage(0, 0), context.Database.GetQueueCapacityUsage());
    }

    [Fact]
    public async Task Result_persistence_failure_recovers_claim_for_retry()
    {
        await using var context = QueueTestContext.Create();
        var queued = await context.EnqueueAsync();
        using (var connection = context.Database.OpenConnectionForDiagnostics())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TRIGGER FailDeliveredUpdate
                BEFORE UPDATE OF State ON QueueMessages
                WHEN NEW.State = 'Delivered'
                BEGIN
                    SELECT RAISE(FAIL, 'injected result persistence failure');
                END;
                """;
            command.ExecuteNonQuery();
        }

        var provider = new ScriptedDeliveryProvider(DeliveryResult.Succeeded());
        await Assert.ThrowsAsync<SqliteException>(() => context.CreateWorker(provider).ProcessOneAsync());

        var recovered = Assert.Single(context.Database.GetQueuedMessages());
        Assert.Equal(queued.Id, recovered.Id);
        Assert.Equal(QueueState.Queued, recovered.State);
        Assert.True(recovered.PayloadPresent);
        using (var connection = context.Database.OpenConnectionForDiagnostics())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER FailDeliveredUpdate;";
            command.ExecuteNonQuery();
        }

        Assert.True(await context.CreateWorker(provider).ProcessOneAsync());
        Assert.Equal(QueueState.Delivered, Assert.Single(context.Database.GetQueuedMessages()).State);
        Assert.Equal(2, provider.MessageIds.Count);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("write")]
    [InlineData("flush")]
    [InlineData("move")]
    [InlineData("dispose")]
    public async Task Spool_fault_releases_capacity_and_next_message_can_be_accepted(string fault)
    {
        var fileSystem = new FaultInjectingSpoolFileSystem
        {
            FailCreate = fault == "create",
            FailWrite = fault == "write",
            FailFlush = fault == "flush",
            FailMove = fault == "move",
            FailDispose = fault == "dispose",
        };
        await using var host = await SmtpTestHost.CreateAsync(
            configureQueue: options => options.MaximumQueuedMessages = 1,
            fileSystem: fileSystem);
        _ = host.Devices.AddLegacyDevice(
            "Scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);

        var response = await SendMessageAsync(host, declaredSize: 32, body: "Subject: test\r\n\r\nhello");

        Assert.False(response.StartsWith("250", StringComparison.Ordinal));
        Assert.Empty(host.Database.GetQueuedMessages());
        await WaitUntilAsync(
            () => !Directory.EnumerateFiles(host.Database.IncomingDirectory).Any(),
            TimeSpan.FromSeconds(2));
        var incomingFiles = Directory.EnumerateFiles(host.Database.IncomingDirectory).ToArray();
        Assert.True(incomingFiles.Length == 0, $"Unexpected incoming files: {string.Join(", ", incomingFiles)}");
        Assert.Empty(Directory.EnumerateFiles(host.Database.PendingDirectory));

        fileSystem.FailCreate = false;
        fileSystem.FailWrite = false;
        fileSystem.FailFlush = false;
        fileSystem.FailMove = false;
        fileSystem.FailDispose = false;
        Assert.StartsWith(
            "250",
            await SendMessageAsync(host, 32, "Subject: recovered\r\n\r\naccepted"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SQLite_commit_failure_releases_reservation_for_next_acceptance()
    {
        await using var host = await SmtpTestHost.CreateAsync(
            configureQueue: options => options.MaximumQueuedMessages = 1);
        _ = host.Devices.AddLegacyDevice(
            "Scanner",
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

        Assert.StartsWith(
            "451",
            await SendMessageAsync(host, 32, "Subject: failed\r\n\r\nfailed"),
            StringComparison.Ordinal);
        using (var connection = host.Database.OpenConnectionForDiagnostics())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER FailQueueInsert;";
            command.ExecuteNonQuery();
        }

        Assert.StartsWith(
            "250",
            await SendMessageAsync(host, 32, "Subject: next\r\n\r\naccepted"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_disconnect_releases_reservation_for_next_acceptance()
    {
        await using var host = await SmtpTestHost.CreateAsync(
            configureQueue: options => options.MaximumQueuedMessages = 1);
        _ = host.Devices.AddLegacyDevice(
            "Scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);
        await using (var client = await host.ConnectAsync())
        {
            Assert.StartsWith("220", await client.ReadResponseAsync(), StringComparison.Ordinal);
            Assert.StartsWith("250", await client.CommandAsync("EHLO test"), StringComparison.Ordinal);
            Assert.StartsWith("250", await client.CommandAsync("MAIL FROM:<scanner@example.com> SIZE=32"), StringComparison.Ordinal);
            Assert.StartsWith("250", await client.CommandAsync("RCPT TO:<recipient@example.net>"), StringComparison.Ordinal);
            Assert.StartsWith("354", await client.CommandAsync("DATA"), StringComparison.Ordinal);
            await client.SendBytesAsync("partial"u8.ToArray());
        }

        await WaitUntilAsync(
            () => !Directory.EnumerateFiles(host.Database.IncomingDirectory).Any(),
            TimeSpan.FromSeconds(2));
        Assert.StartsWith(
            "250",
            await SendMessageAsync(host, 32, "Subject: next\r\n\r\naccepted"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capacity_refusal_is_temporary_and_preserves_previously_accepted_mail()
    {
        await using var host = await SmtpTestHost.CreateAsync(
            configureQueue: options => options.MaximumQueuedMessages = 1);
        _ = host.Devices.AddLegacyDevice(
            "Scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);

        Assert.StartsWith("250", await SendMessageAsync(host, 32, "Subject: one\r\n\r\none"), StringComparison.Ordinal);
        var accepted = Assert.Single(host.Database.GetQueuedMessages());
        var acceptedBytes = await File.ReadAllBytesAsync(host.Database.GetPendingPath(accepted.SpoolFileName));

        var refused = await SendMessageAsync(host, 32, "Subject: two\r\n\r\ntwo");

        Assert.StartsWith("452", refused, StringComparison.Ordinal);
        Assert.Single(host.Database.GetQueuedMessages());
        Assert.Equal(acceptedBytes, await File.ReadAllBytesAsync(host.Database.GetPendingPath(accepted.SpoolFileName)));
    }

    [Fact]
    public async Task Spool_byte_limit_is_enforced_while_DATA_is_streaming()
    {
        await using var host = await SmtpTestHost.CreateAsync(
            configureQueue: options => options.MaximumSpoolBytes = 1024);
        _ = host.Devices.AddLegacyDevice(
            "Scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);

        var response = await SendMessageAsync(host, 1, new string('x', 1500));

        Assert.StartsWith("452", response, StringComparison.Ordinal);
        Assert.Empty(host.Database.GetQueuedMessages());
        Assert.StartsWith(
            "250",
            await SendMessageAsync(host, 32, "Subject: next\r\n\r\naccepted"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Minimum_free_space_limit_rejects_reservation()
    {
        var fileSystem = new FaultInjectingSpoolFileSystem { AvailableFreeSpace = 1024 };
        await using var context = QueueTestContext.Create(
            options => options.MinimumFreeDiskBytes = 1024,
            fileSystem);

        var exception = Assert.Throws<QueueCapacityExceededException>(() =>
            context.MessageStore.BeginReceive(expectedBytes: 1));

        Assert.Equal(QueueCapacityLimit.FreeDisk, exception.Limit);

        fileSystem.AvailableFreeSpace = 1025;
        await using var reservation = context.MessageStore.BeginReceive(expectedBytes: 1);
    }

    [Fact]
    public async Task Oversized_reservation_and_cancelled_receive_do_not_leak_capacity()
    {
        await using var context = QueueTestContext.Create(options =>
        {
            options.MaximumQueuedMessages = 1;
            options.MaximumSpoolBytes = 1024;
        });

        Assert.Throws<QueueCapacityExceededException>(() =>
            context.MessageStore.BeginReceive(expectedBytes: long.MaxValue));
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await using var receive = context.MessageStore.BeginReceive(expectedBytes: 16);
                await receive.Stream.WriteAsync(new byte[16], cancelled.Token);
            });
        }

        await using var next = context.MessageStore.BeginReceive(expectedBytes: 1024);
    }

    [Fact]
    public async Task Forced_worker_shutdown_cancels_delivery_and_recovers_claim()
    {
        await using var context = QueueTestContext.Create();
        var queued = await context.EnqueueAsync();
        var waiting = await context.EnqueueAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedDeliveryProvider(async (_, _, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return DeliveryResult.Succeeded();
        });
        var worker = context.CreateWorker(provider);
        await worker.StartAsync();
        context.WorkSignal.Pulse();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var stopDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await worker.StopAsync(stopDeadline.Token);

        var recovered = context.Database.GetQueuedMessages();
        Assert.Equal(2, recovered.Count);
        Assert.Equal(QueueState.Queued, recovered.Single(message => message.Id == queued.Id).State);
        Assert.Equal(QueueState.Queued, recovered.Single(message => message.Id == waiting.Id).State);
        Assert.False(worker.IsRunning);
        Assert.False(await worker.ProcessOneAsync());
        Assert.Single(provider.MessageIds);
    }

    [Fact]
    public async Task Active_SMTP_DATA_can_finish_during_graceful_listener_shutdown()
    {
        await using var host = await SmtpTestHost.CreateAsync();
        _ = host.Devices.AddLegacyDevice(
            "Scanner",
            ["127.0.0.1"],
            ["scanner@example.com"]);
        await using var client = await host.ConnectAsync();
        Assert.StartsWith("220", await client.ReadResponseAsync(), StringComparison.Ordinal);
        Assert.StartsWith("250", await client.CommandAsync("EHLO test"), StringComparison.Ordinal);
        Assert.StartsWith("250", await client.CommandAsync("MAIL FROM:<scanner@example.com> SIZE=32"), StringComparison.Ordinal);
        Assert.StartsWith("250", await client.CommandAsync("RCPT TO:<recipient@example.net>"), StringComparison.Ordinal);
        Assert.StartsWith("354", await client.CommandAsync("DATA"), StringComparison.Ordinal);
        await client.SendBytesAsync(System.Text.Encoding.ASCII.GetBytes("Subject: stopping\r\n\r\n"));

        using var shutdownDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stopping = host.StopAsync(shutdownDeadline.Token);
        await client.SendBytesAsync(System.Text.Encoding.ASCII.GetBytes("complete\r\n.\r\n"));

        Assert.StartsWith("250", await client.ReadResponseAsync(), StringComparison.Ordinal);
        client.Stream.Close();
        await stopping;
        Assert.Single(host.Database.GetQueuedMessages());
    }

    [Fact]
    public async Task Multiple_workers_process_each_message_once()
    {
        await using var context = QueueTestContext.Create(options => options.MaxConcurrency = 3);
        const int messageCount = 100;
        for (var index = 0; index < messageCount; index++)
        {
            await context.EnqueueAsync(256);
        }

        var provider = new ScriptedDeliveryProvider(DeliveryResult.Succeeded());
        var workers = Enumerable.Range(0, 3).Select(_ => context.CreateWorker(provider)).ToArray();
        while (await Task.WhenAll(workers.Select(worker => worker.ProcessOneAsync())) is { } results &&
               results.Any(processed => processed))
        {
        }

        Assert.Equal(messageCount, provider.MessageIds.Count);
        Assert.Equal(messageCount, provider.MessageIds.Distinct().Count());
        Assert.All(context.Database.GetQueuedMessages(), message => Assert.Equal(QueueState.Delivered, message.State));
    }

    [Fact]
    public async Task Eight_claimers_process_200_mixed_outcomes_without_duplicates_or_locks()
    {
        await using var context = QueueTestContext.Create(options => options.MaxConcurrency = 8);
        const int messageCount = 200;
        var outcomes = new Dictionary<Guid, DeliveryResult>();
        for (var index = 0; index < messageCount; index++)
        {
            var message = await context.EnqueueAsync(256);
            outcomes[message.Id] = (index % 4) switch
            {
                0 or 1 => DeliveryResult.Succeeded(),
                2 => DeliveryResult.TransientFailure("Temporary", "Retry later."),
                _ => DeliveryResult.PermanentFailure("Rejected", "Permanent rejection."),
            };
        }

        var provider = new ScriptedDeliveryProvider(
            (message, _, _) => Task.FromResult(outcomes[message.Id]));
        var workers = Enumerable.Range(0, 8)
            .Select(_ => context.CreateWorker(provider))
            .ToArray();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await Task.WhenAll(workers.Select(worker => Task.Run(() => DrainAsync(worker))));
        stopwatch.Stop();

        var processed = provider.MessageIds;
        Assert.Equal(messageCount, processed.Count);
        Assert.Equal(messageCount, processed.Distinct().Count());
        var messages = context.Database.GetQueuedMessages();
        Assert.Equal(messageCount, messages.Count);
        Assert.Equal(100, messages.Count(message => message.State == QueueState.Delivered));
        Assert.Equal(50, messages.Count(message => message.State == QueueState.RetryScheduled));
        Assert.Equal(50, messages.Count(message => message.State == QueueState.PermanentFailure));
        Assert.Null(context.Database.ClaimNextEligible(context.TimeProvider.GetUtcNow()));
        _output.WriteLine(
            "200-message/8-claimer mixed-outcome stress completed in {0:N2} ms.",
            stopwatch.Elapsed.TotalMilliseconds);
    }

    [Fact]
    public async Task Eight_claimers_claim_each_due_retry_only_once()
    {
        await using var context = QueueTestContext.Create(options => options.MaxConcurrency = 8);
        const int messageCount = 40;
        for (var index = 0; index < messageCount; index++)
        {
            await context.EnqueueAsync(128);
        }

        var dueUtc = context.TimeProvider.GetUtcNow() + TimeSpan.FromMinutes(1);
        QueuedMessage? claimed;
        while ((claimed = context.Database.ClaimNextEligible(context.TimeProvider.GetUtcNow())) is not null)
        {
            Assert.True(context.Database.ScheduleRetry(claimed.Id, dueUtc, "Temporary", "Retry later."));
        }

        context.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        var provider = new ScriptedDeliveryProvider(DeliveryResult.Succeeded());
        var workers = Enumerable.Range(0, 8)
            .Select(_ => context.CreateWorker(provider))
            .ToArray();

        await Task.WhenAll(workers.Select(worker => Task.Run(() => DrainAsync(worker))));

        Assert.Equal(messageCount, provider.MessageIds.Count);
        Assert.Equal(messageCount, provider.MessageIds.Distinct().Count());
        Assert.All(context.Database.GetQueuedMessages(), message =>
        {
            Assert.Equal(QueueState.Delivered, message.State);
            Assert.Equal(2, message.AttemptCount);
        });
    }

    [Fact]
    public async Task Multiple_ten_mebibyte_messages_are_streamed_by_bounded_workers()
    {
        await using var context = QueueTestContext.Create(options =>
        {
            options.MaxConcurrency = 3;
            options.MaximumSpoolBytes = 64L * 1024 * 1024;
        });
        const int size = 10 * 1024 * 1024;
        for (var index = 0; index < 3; index++)
        {
            await context.EnqueueAsync(size);
        }

        long bytesRead = 0;
        var provider = new ScriptedDeliveryProvider(async (_, stream, cancellationToken) =>
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                Interlocked.Add(ref bytesRead, read);
            }

            return DeliveryResult.Succeeded();
        });
        var workers = Enumerable.Range(0, 3).Select(_ => context.CreateWorker(provider)).ToArray();

        var results = await Task.WhenAll(workers.Select(worker => worker.ProcessOneAsync()));

        Assert.All(results, Assert.True);
        Assert.Equal(3L * size, bytesRead);
        Assert.All(context.Database.GetQueuedMessages(), message => Assert.Equal(QueueState.Delivered, message.State));
    }

    [Fact]
    public async Task Milestone_one_queue_schema_is_migrated_without_losing_message_or_recipient()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "RelayBridge.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        var databasePath = Path.Combine(dataDirectory, "relaybridge.db");
        var deviceId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var receivedUtc = new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE Devices (
                        Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Enabled INTEGER NOT NULL,
                        AuthenticationMode TEXT NOT NULL, SmtpUsername TEXT, PasswordVerifier TEXT,
                        CreatedUtc TEXT NOT NULL);
                    CREATE TABLE DeviceAllowedNetworks (DeviceId TEXT NOT NULL, Network TEXT NOT NULL,
                        PRIMARY KEY (DeviceId, Network));
                    CREATE TABLE DeviceAllowedSenders (DeviceId TEXT NOT NULL, Sender TEXT NOT NULL,
                        PRIMARY KEY (DeviceId, Sender));
                    CREATE TABLE QueueMessages (
                        Id TEXT PRIMARY KEY, DeviceId TEXT NOT NULL REFERENCES Devices(Id),
                        EnvelopeFrom TEXT NOT NULL, ReceivedUtc TEXT NOT NULL, SizeBytes INTEGER NOT NULL,
                        SpoolFileName TEXT NOT NULL UNIQUE, State TEXT NOT NULL CHECK (State = 'Queued'),
                        RecipientCount INTEGER NOT NULL);
                    CREATE TABLE QueueRecipients (
                        MessageId TEXT NOT NULL REFERENCES QueueMessages(Id) ON DELETE CASCADE,
                        Ordinal INTEGER NOT NULL, Recipient TEXT NOT NULL,
                        PRIMARY KEY (MessageId, Ordinal));
                    CREATE INDEX IX_QueueMessages_State_ReceivedUtc ON QueueMessages(State, ReceivedUtc);
                    """;
                await command.ExecuteNonQueryAsync();

                command.CommandText =
                    """
                    INSERT INTO Devices (Id, Name, Enabled, AuthenticationMode, CreatedUtc)
                    VALUES ($deviceId, 'Legacy scanner', 1, 'Legacy', $receivedUtc);
                    INSERT INTO QueueMessages
                        (Id, DeviceId, EnvelopeFrom, ReceivedUtc, SizeBytes, SpoolFileName, State, RecipientCount)
                    VALUES ($messageId, $deviceId, 'scanner@example.com', $receivedUtc, 12, $spool, 'Queued', 1);
                    INSERT INTO QueueRecipients (MessageId, Ordinal, Recipient)
                    VALUES ($messageId, 0, 'recipient@example.net');
                    """;
                command.Parameters.AddWithValue("$deviceId", deviceId.ToString("D"));
                command.Parameters.AddWithValue("$messageId", messageId.ToString("D"));
                command.Parameters.AddWithValue("$receivedUtc", receivedUtc.ToString("O"));
                command.Parameters.AddWithValue("$spool", $"{messageId:D}.eml");
                await command.ExecuteNonQueryAsync();
            }

            var database = new RelayDatabase(
                new RelayStorageOptions { DataDirectory = dataDirectory },
                AppContext.BaseDirectory);
            database.Initialize();

            var migrated = Assert.Single(database.GetQueuedMessages());
            Assert.Equal(messageId, migrated.Id);
            Assert.Equal(QueueState.Queued, migrated.State);
            Assert.Equal(0, migrated.AttemptCount);
            Assert.True(migrated.PayloadPresent);
            Assert.Equal(["recipient@example.net"], migrated.Recipients);
            Assert.Equal(9, ReadSchemaVersion(database));

            var restarted = new RelayDatabase(
                new RelayStorageOptions { DataDirectory = dataDirectory },
                AppContext.BaseDirectory);
            restarted.Initialize();
            var afterRestart = Assert.Single(restarted.GetQueuedMessages());
            Assert.Equal(migrated.Id, afterRestart.Id);
            Assert.Equal(migrated.State, afterRestart.State);
            Assert.Equal(migrated.Recipients, afterRestart.Recipients);
            Assert.Equal(9, ReadSchemaVersion(restarted));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Migration_failure_rolls_back_partial_renames_and_preserves_original_schema()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "RelayBridge.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        var databasePath = Path.Combine(dataDirectory, "relaybridge.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE Devices (
                        Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Enabled INTEGER NOT NULL,
                        AuthenticationMode TEXT NOT NULL, SmtpUsername TEXT, PasswordVerifier TEXT,
                        CreatedUtc TEXT NOT NULL);
                    CREATE TABLE DeviceAllowedNetworks (DeviceId TEXT NOT NULL, Network TEXT NOT NULL,
                        PRIMARY KEY (DeviceId, Network));
                    CREATE TABLE DeviceAllowedSenders (DeviceId TEXT NOT NULL, Sender TEXT NOT NULL,
                        PRIMARY KEY (DeviceId, Sender));
                    CREATE TABLE QueueMessages (
                        Id TEXT PRIMARY KEY, DeviceId TEXT NOT NULL REFERENCES Devices(Id),
                        EnvelopeFrom TEXT NOT NULL, ReceivedUtc TEXT NOT NULL, SizeBytes INTEGER NOT NULL,
                        SpoolFileName TEXT NOT NULL UNIQUE, State TEXT NOT NULL CHECK (State = 'Queued'),
                        RecipientCount INTEGER NOT NULL);
                    CREATE TABLE QueueRecipients (
                        MessageId TEXT NOT NULL REFERENCES QueueMessages(Id) ON DELETE CASCADE,
                        Ordinal INTEGER NOT NULL, Recipient TEXT NOT NULL,
                        PRIMARY KEY (MessageId, Ordinal));
                    CREATE INDEX IX_QueueMessages_State_ReceivedUtc ON QueueMessages(State, ReceivedUtc);
                    CREATE TABLE QueueMessages_Milestone1 (Sentinel INTEGER);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var database = new RelayDatabase(
                new RelayStorageOptions { DataDirectory = dataDirectory },
                AppContext.BaseDirectory);
            Assert.Throws<SqliteException>(() => database.Initialize());

            await using var verification = new SqliteConnection($"Data Source={databasePath}");
            await verification.OpenAsync();
            Assert.True(TableExists(verification, "QueueMessages"));
            Assert.True(TableExists(verification, "QueueRecipients"));
            Assert.True(TableExists(verification, "QueueMessages_Milestone1"));
            Assert.False(TableExists(verification, "QueueRecipients_Milestone1"));
            Assert.False(ColumnExists(verification, "QueueMessages", "AttemptCount"));
            await using var versionCommand = verification.CreateCommand();
            versionCommand.CommandText = "PRAGMA user_version;";
            Assert.Equal(0L, (long)(await versionCommand.ExecuteScalarAsync())!);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void Corrupt_database_fails_initialization_without_replacing_the_file()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "RelayBridge.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        var databasePath = Path.Combine(dataDirectory, "relaybridge.db");
        var corruptBytes = "not-a-sqlite-database"u8.ToArray();
        File.WriteAllBytes(databasePath, corruptBytes);
        try
        {
            var database = new RelayDatabase(
                new RelayStorageOptions { DataDirectory = dataDirectory },
                AppContext.BaseDirectory);

            Assert.Throws<SqliteException>(() => database.Initialize());
            SqliteConnection.ClearAllPools();
            Assert.Equal(corruptBytes, File.ReadAllBytes(databasePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public void Newer_database_schema_fails_closed_without_downgrading()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "RelayBridge.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        var databasePath = Path.Combine(dataDirectory, "relaybridge.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version = 10;";
                command.ExecuteNonQuery();
            }

            var database = new RelayDatabase(
                new RelayStorageOptions { DataDirectory = dataDirectory },
                AppContext.BaseDirectory);
            var exception = Assert.Throws<InvalidOperationException>(() => database.Initialize());

            Assert.Contains("newer than supported", exception.Message, StringComparison.Ordinal);
            using var verification = new SqliteConnection($"Data Source={databasePath}");
            verification.Open();
            using var version = verification.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            Assert.Equal(10L, (long)version.ExecuteScalar()!);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private static async Task<string> SendMessageAsync(
        SmtpTestHost host,
        int declaredSize,
        string body)
    {
        await using var client = await host.ConnectAsync();
        Assert.StartsWith("220", await client.ReadResponseAsync(), StringComparison.Ordinal);
        Assert.StartsWith("250", await client.CommandAsync("EHLO test"), StringComparison.Ordinal);
        Assert.StartsWith(
            "250",
            await client.CommandAsync($"MAIL FROM:<scanner@example.com> SIZE={declaredSize}"),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "250",
            await client.CommandAsync("RCPT TO:<recipient@example.net>"),
            StringComparison.Ordinal);
        var dataResponse = await client.CommandAsync("DATA");
        if (!dataResponse.StartsWith("354", StringComparison.Ordinal))
        {
            return dataResponse;
        }

        await client.SendBytesAsync(System.Text.Encoding.ASCII.GetBytes($"{body}\r\n.\r\n"));
        return await client.ReadResponseAsync();
    }

    private static async Task DrainAsync(QueueWorker worker)
    {
        while (await worker.ProcessOneAsync())
        {
        }
    }

    private static int ReadSchemaVersion(RelayDatabase database)
    {
        using var connection = database.OpenConnectionForDiagnostics();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return command.ExecuteScalar() is not null;
    }

    private static bool ColumnExists(SqliteConnection connection, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }
}
