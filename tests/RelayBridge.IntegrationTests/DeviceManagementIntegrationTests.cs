// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RelayBridge.Core.Devices;
using RelayBridge.Core.Microsoft;
using RelayBridge.Core.Queue;
using RelayBridge.Infrastructure.Smtp;
using RelayBridge.Infrastructure.Storage;
using RelayBridge.Infrastructure.Microsoft;
using Xunit;

namespace RelayBridge.IntegrationTests;

public sealed class DeviceManagementIntegrationTests
{
    [Fact]
    public void Provisioning_generates_unique_credentials_and_persists_no_plaintext()
    {
        using var context = new DeviceDatabaseContext();

        var first = context.Devices.ProvisionAuthenticatedDevice(
            "Ricoh Reception",
            "Reception copier",
            ["192.168.10.31"],
            ["scanner@example.com"]);
        var second = context.Devices.ProvisionAuthenticatedDevice(
            "Ricoh Reception",
            null,
            ["192.168.10.32"],
            ["scanner@example.com"]);

        Assert.Equal("ricoh-reception", first.Device.SmtpUsername);
        Assert.Equal("ricoh-reception-2", second.Device.SmtpUsername);
        Assert.Equal("Reception copier", context.Database.GetDevice(first.Device.Id)!.Description);
        using var connection = context.Database.OpenConnectionForDiagnostics();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT PasswordVerifier FROM Devices WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", first.Device.Id.ToString("D"));
        var verifier = Assert.IsType<string>(command.ExecuteScalar());
        Assert.DoesNotContain(first.PlaintextPassword, verifier, StringComparison.Ordinal);
        Assert.True(DevicePassword.Verify(first.PlaintextPassword, verifier));
    }

    [Fact]
    public void Invalid_legacy_creation_fails_without_a_partial_device()
    {
        using var context = new DeviceDatabaseContext();

        Assert.Throws<ArgumentException>(() => context.Devices.AddLegacyDevice(
            "Unsafe device",
            "Must fail closed",
            ["0.0.0.0/0"],
            ["scanner@example.com"]));

        Assert.Empty(context.Database.GetDevices());
    }

    [Fact]
    public async Task Password_reset_invalidates_old_secret_and_disable_is_reversible()
    {
        using var context = new DeviceDatabaseContext();
        var original = context.Devices.ProvisionAuthenticatedDevice(
            "Canon Accounts",
            null,
            ["127.0.0.1"],
            ["scanner@example.com"]);

        var reset = context.Devices.ResetPassword(original.Device.Id, original.Device.Revision);

        Assert.Null(await context.Devices.AuthenticateAsync(
            original.Device.SmtpUsername!, original.PlaintextPassword, IPAddress.Loopback));
        Assert.NotNull(await context.Devices.AuthenticateAsync(
            original.Device.SmtpUsername!, reset.PlaintextPassword, IPAddress.Loopback));
        context.Devices.SetEnabled(original.Device.Id, enabled: false, reset.Device.Revision);
        Assert.Null(await context.Devices.AuthenticateAsync(
            original.Device.SmtpUsername!, reset.PlaintextPassword, IPAddress.Loopback));
        var disabled = context.Database.GetDevice(original.Device.Id)!;
        context.Devices.SetEnabled(original.Device.Id, enabled: true, disabled.Revision);
        Assert.NotNull(await context.Devices.AuthenticateAsync(
            original.Device.SmtpUsername!, reset.PlaintextPassword, IPAddress.Loopback));

        using var connection = context.Database.OpenConnectionForDiagnostics();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT PasswordVerifier FROM Devices WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", original.Device.Id.ToString("D"));
        var verifier = Assert.IsType<string>(command.ExecuteScalar());
        Assert.DoesNotContain(original.PlaintextPassword, verifier, StringComparison.Ordinal);
        Assert.DoesNotContain(reset.PlaintextPassword, verifier, StringComparison.Ordinal);
    }

    [Fact]
    public void Editing_device_is_atomic_and_does_not_delete_accepted_queue_metadata()
    {
        using var context = new DeviceDatabaseContext();
        var device = context.Devices.AddLegacyDevice(
            "Warehouse scanner",
            null,
            ["192.168.20.15", "192.168.21.15"],
            ["scanner@example.com"]);
        var message = new QueuedMessage(
            Guid.CreateVersion7(),
            device.Id,
            "scanner@example.com",
            ["recipient@example.net"],
            DateTimeOffset.UtcNow,
            100,
            $"{Guid.NewGuid():N}.eml",
            QueueState.Queued);
        context.Database.InsertQueuedMessage(message);

        var updated = context.Devices.UpdateDevice(
            device.Id,
            "Warehouse MFP",
            "Loading bay",
            ["192.168.20.16", "192.168.21.15"],
            ["scanner@example.com"],
            device.Revision);

        Assert.Equal("Warehouse MFP", updated.Name);
        Assert.Equal("Loading bay", updated.Description);
        Assert.Equal(["192.168.20.16/32", "192.168.21.15/32"], updated.AllowedNetworks);
        Assert.Equal(message.Id, Assert.Single(context.Database.GetQueuedMessages()).Id);
    }

    [Fact]
    public void Password_reset_and_disable_apply_without_lost_updates()
    {
        using var context = new DeviceDatabaseContext();
        var original = context.Devices.ProvisionAuthenticatedDevice(
            "Concurrent device", null, ["192.168.1.20"], ["scanner@example.com"]);

        var reset = context.Devices.ResetPassword(original.Device.Id, original.Device.Revision);
        var disabled = context.Devices.SetEnabled(original.Device.Id, enabled: false, reset.Device.Revision);

        Assert.False(disabled.Enabled);
        Assert.True(DevicePassword.Verify(reset.PlaintextPassword, disabled.PasswordVerifier!));
        Assert.True(disabled.Revision >= 2);
    }

    [Fact]
    public void Password_reset_makes_stale_configuration_edit_fail_closed()
    {
        using var context = new DeviceDatabaseContext();
        var original = context.Devices.ProvisionAuthenticatedDevice(
            "Concurrent device", null, ["192.168.1.20"], ["scanner@example.com"]);

        var reset = context.Devices.ResetPassword(original.Device.Id, original.Device.Revision);
        var exception = Assert.Throws<DeviceConcurrencyException>(() => context.Devices.UpdateDevice(
            original.Device.Id,
            "Stale name",
            null,
            ["192.168.1.99"],
            original.Device.AllowedSenders,
            original.Device.Revision));

        var current = context.Database.GetDevice(original.Device.Id)!;
        Assert.Contains("changed while you were editing", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Concurrent device", current.Name);
        Assert.Equal(["192.168.1.20/32"], current.AllowedNetworks);
        Assert.True(DevicePassword.Verify(reset.PlaintextPassword, current.PasswordVerifier!));
    }

    [Fact]
    public void Two_stale_edits_cannot_overwrite_each_other()
    {
        using var context = new DeviceDatabaseContext();
        var original = context.Devices.AddLegacyDevice(
            "Original", null, ["192.168.1.20"], ["scanner@example.com"]);

        var first = context.Devices.UpdateDevice(
            original.Id, "First", null, ["192.168.1.21"], original.AllowedSenders, original.Revision);
        Assert.Throws<DeviceConcurrencyException>(() => context.Devices.UpdateDevice(
            original.Id, "Second", null, ["192.168.1.22"], original.AllowedSenders, original.Revision));

        var current = context.Database.GetDevice(original.Id)!;
        Assert.Equal(first.Name, current.Name);
        Assert.Equal(first.AllowedNetworks, current.AllowedNetworks);
    }

    [Fact]
    public void Disable_is_not_reverted_by_a_stale_edit()
    {
        using var context = new DeviceDatabaseContext();
        var original = context.Devices.AddLegacyDevice(
            "Original", null, ["192.168.1.20"], ["scanner@example.com"]);

        context.Devices.SetEnabled(original.Id, enabled: false, original.Revision);
        Assert.Throws<DeviceConcurrencyException>(() => context.Devices.UpdateDevice(
            original.Id, "Stale", null, ["192.168.1.22"], original.AllowedSenders, original.Revision));

        var current = context.Database.GetDevice(original.Id)!;
        Assert.False(current.Enabled);
        Assert.Equal("Original", current.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Duplicate_enabled_state_change_with_same_revision_causes_one_transition(bool enabled)
    {
        using var context = new DeviceDatabaseContext();
        var original = context.Devices.AddLegacyDevice(
            "Duplicate enabled change",
            null,
            ["192.168.1.20"],
            ["scanner@example.com"],
            enabled: !enabled);

        var changed = context.Devices.SetEnabled(original.Id, enabled, original.Revision);
        Assert.Throws<DeviceConcurrencyException>(() =>
            context.Devices.SetEnabled(original.Id, enabled, original.Revision));

        var current = context.Database.GetDevice(original.Id)!;
        Assert.Equal(enabled, current.Enabled);
        Assert.Equal(original.Revision + 1, current.Revision);
        Assert.Equal(changed.Revision, current.Revision);
    }

    [Fact]
    public void Secret_bearing_results_redact_string_representations()
    {
        using var context = new DeviceDatabaseContext();
        var provisioned = context.Devices.ProvisionAuthenticatedDevice(
            "Secret device", null, ["192.168.1.20"], ["scanner@example.com"]);
        var generated = DevicePassword.Generate();

        Assert.DoesNotContain(provisioned.PlaintextPassword, provisioned.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(generated.Plaintext, generated.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(generated.Verifier, generated.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(provisioned.Device.PasswordVerifier!, provisioned.Device.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", provisioned.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Secret_bearing_results_exclude_plaintext_and_verifiers_from_default_json()
    {
        using var context = new DeviceDatabaseContext();
        var provisioned = context.Devices.ProvisionAuthenticatedDevice(
            "Secret device", null, ["192.168.1.20"], ["scanner@example.com"]);
        var generated = DevicePassword.Generate();
        var nested = new SecretContainer(generated, provisioned);

        var generatedJson = JsonSerializer.Serialize(generated);
        var provisionedJson = JsonSerializer.Serialize(provisioned);
        var nestedJson = JsonSerializer.Serialize(nested);

        foreach (var json in new[] { generatedJson, provisionedJson, nestedJson })
        {
            Assert.DoesNotContain(generated.Plaintext, json, StringComparison.Ordinal);
            Assert.DoesNotContain(generated.Verifier, json, StringComparison.Ordinal);
            Assert.DoesNotContain(provisioned.PlaintextPassword, json, StringComparison.Ordinal);
            Assert.DoesNotContain(provisioned.Device.PasswordVerifier!, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Concurrent_password_resets_with_same_revision_have_exactly_one_winner()
    {
        using var context = new DeviceDatabaseContext();
        var original = context.Devices.ProvisionAuthenticatedDevice(
            "Concurrent reset", null, ["192.168.1.20"], ["scanner@example.com"]);
        const string firstPassword = "first-concurrent-password";
        const string secondPassword = "second-concurrent-password";
        using var barrier = new Barrier(2);
        var firstService = CreateResetService(context.Database, firstPassword, barrier);
        var secondService = CreateResetService(context.Database, secondPassword, barrier);

        var attempts = await Task.WhenAll(
            Task.Run(() => AttemptReset(firstService, original.Device.Id, original.Device.Revision, firstPassword)),
            Task.Run(() => AttemptReset(secondService, original.Device.Id, original.Device.Revision, secondPassword)));

        var winner = Assert.Single(attempts, attempt => attempt.Result is not null);
        var loser = Assert.Single(attempts, attempt => attempt.Exception is DeviceConcurrencyException);
        var current = context.Database.GetDevice(original.Device.Id)!;
        Assert.Equal(original.Device.Revision + 1, current.Revision);
        Assert.True(DevicePassword.Verify(winner.Password, current.PasswordVerifier!));
        Assert.False(DevicePassword.Verify(loser.Password, current.PasswordVerifier!));
        Assert.Equal(original.Device.Enabled, current.Enabled);
        Assert.Equal(original.Device.AllowedNetworks, current.AllowedNetworks);
        Assert.Equal(original.Device.AllowedSenders, current.AllowedSenders);
    }

    [Fact]
    public void Stale_reset_cannot_overwrite_a_concurrent_disable()
    {
        using var context = new DeviceDatabaseContext();
        var original = context.Devices.ProvisionAuthenticatedDevice(
            "Reset versus disable", null, ["192.168.1.20"], ["scanner@example.com"]);
        var disabled = context.Devices.SetEnabled(
            original.Device.Id,
            enabled: false,
            original.Device.Revision);

        Assert.Throws<DeviceConcurrencyException>(() => context.Devices.ResetPassword(
            original.Device.Id,
            original.Device.Revision));
        var current = context.Database.GetDevice(original.Device.Id)!;
        Assert.False(current.Enabled);
        Assert.Equal(disabled.Revision, current.Revision);
        Assert.True(DevicePassword.Verify(original.PlaintextPassword, current.PasswordVerifier!));
    }

    [Fact]
    public async Task Synchronized_reset_and_disable_preserve_one_complete_winner()
    {
        using var context = new DeviceDatabaseContext();
        var original = context.Devices.ProvisionAuthenticatedDevice(
            "Concurrent reset disable", null, ["192.168.1.20"], ["scanner@example.com"]);
        using var barrier = new Barrier(2);
        var resetTask = Task.Run(() => AttemptMutation("reset", barrier, () =>
            context.Devices.ResetPassword(original.Device.Id, original.Device.Revision)));
        var disableTask = Task.Run(() => AttemptMutation("disable", barrier, () =>
            context.Devices.SetEnabled(original.Device.Id, false, original.Device.Revision)));

        var attempts = await Task.WhenAll(resetTask, disableTask);

        var winner = Assert.Single(attempts, attempt => attempt.Result is not null);
        Assert.Single(attempts, attempt => attempt.Exception is DeviceConcurrencyException);
        var current = context.Database.GetDevice(original.Device.Id)!;
        Assert.Equal(original.Device.Revision + 1, current.Revision);
        if (winner.Name == "reset")
        {
            var reset = Assert.IsType<ProvisionedDevice>(winner.Result);
            Assert.True(current.Enabled);
            Assert.True(DevicePassword.Verify(reset.PlaintextPassword, current.PasswordVerifier!));
        }
        else
        {
            Assert.False(current.Enabled);
            Assert.True(DevicePassword.Verify(original.PlaintextPassword, current.PasswordVerifier!));
        }
    }

    [Fact]
    public async Task Synchronized_reset_and_edit_preserve_one_complete_winner()
    {
        using var context = new DeviceDatabaseContext();
        var original = context.Devices.ProvisionAuthenticatedDevice(
            "Concurrent reset edit", null, ["192.168.1.20"], ["scanner@example.com"]);
        using var barrier = new Barrier(2);
        var resetTask = Task.Run(() => AttemptMutation("reset", barrier, () =>
            context.Devices.ResetPassword(original.Device.Id, original.Device.Revision)));
        var editTask = Task.Run(() => AttemptMutation("edit", barrier, () =>
            context.Devices.UpdateDevice(
                original.Device.Id,
                "Edited winner",
                null,
                ["192.168.1.21"],
                original.Device.AllowedSenders,
                original.Device.Revision)));

        var attempts = await Task.WhenAll(resetTask, editTask);

        var winner = Assert.Single(attempts, attempt => attempt.Result is not null);
        Assert.Single(attempts, attempt => attempt.Exception is DeviceConcurrencyException);
        var current = context.Database.GetDevice(original.Device.Id)!;
        Assert.Equal(original.Device.Revision + 1, current.Revision);
        if (winner.Name == "reset")
        {
            var reset = Assert.IsType<ProvisionedDevice>(winner.Result);
            Assert.Equal("Concurrent reset edit", current.Name);
            Assert.Equal(["192.168.1.20/32"], current.AllowedNetworks);
            Assert.True(DevicePassword.Verify(reset.PlaintextPassword, current.PasswordVerifier!));
        }
        else
        {
            Assert.Equal("Edited winner", current.Name);
            Assert.Equal(["192.168.1.21/32"], current.AllowedNetworks);
            Assert.True(DevicePassword.Verify(original.PlaintextPassword, current.PasswordVerifier!));
        }
    }

    [Fact]
    public void Device_creation_fails_when_active_microsoft_configuration_changed_before_transaction()
    {
        using var context = new DeviceDatabaseContext();
        var activeA = ActivateMicrosoft(context.Database, "20000000-0000-0000-0000-000000000001");
        _ = ActivateMicrosoft(context.Database, "20000000-0000-0000-0000-000000000002");

        Assert.Throws<MicrosoftConfigurationConcurrencyException>(() =>
            context.Devices.AddLegacyDeviceForActiveMicrosoftConfiguration(
                Guid.CreateVersion7(),
                "Stale sender",
                null,
                ["192.168.1.20"],
                [activeA.AuthorizedSender!],
                activeA.Fingerprint,
                activeA.AuthorizedSender!));
        Assert.Empty(context.Database.GetDevices());
    }

    [Fact]
    public void Activating_replacement_configuration_invalidates_old_runtime_success()
    {
        using var context = new DeviceDatabaseContext();
        var activeA = ActivateMicrosoft(context.Database, "20000000-0000-0000-0000-000000000001");
        var succeededAt = new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);
        var exchangeA = new ExchangeDeliverySnapshot(
            ExchangeDeliveryStatus.Healthy,
            succeededAt,
            succeededAt,
            "Accepted",
            null,
            true,
            true,
            true,
            true,
            true,
            true,
            true)
        {
            ConfigurationFingerprint = activeA.Fingerprint,
            LastCompletedAt = succeededAt,
        };
        var identity = new MicrosoftIdentityHealthSnapshot(
            MicrosoftIdentityHealthStatus.Attention,
            null,
            null,
            null,
            null);
        Assert.Equal(
            MicrosoftRuntimeReadiness.Ready,
            MicrosoftRuntimeReadinessPolicy.Evaluate(true, activeA.Fingerprint, identity, exchangeA));

        var activeB = ActivateMicrosoft(context.Database, "20000000-0000-0000-0000-000000000002");
        Assert.NotEqual(activeA.Fingerprint, activeB.Fingerprint);
        Assert.Equal(
            MicrosoftRuntimeReadiness.VerificationRequired,
            MicrosoftRuntimeReadinessPolicy.Evaluate(true, activeB.Fingerprint, identity, exchangeA));
        var exchangeB = exchangeA with
        {
            ConfigurationFingerprint = activeB.Fingerprint,
            LastAttemptedAt = succeededAt.AddMinutes(1),
            LastSuccessfulAt = succeededAt.AddMinutes(1),
            LastCompletedAt = succeededAt.AddMinutes(1),
        };
        Assert.Equal(
            MicrosoftRuntimeReadiness.Ready,
            MicrosoftRuntimeReadinessPolicy.Evaluate(true, activeB.Fingerprint, identity, exchangeB));
    }

    [Fact]
    public async Task Device_creation_rechecks_configuration_after_waiting_for_concurrent_writer()
    {
        using var context = new DeviceDatabaseContext();
        var activeA = ActivateMicrosoft(context.Database, "20000000-0000-0000-0000-000000000001");
        using var connection = context.Database.OpenConnectionForDiagnostics();
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE MicrosoftIdentityConfiguration SET ClientId = $clientId WHERE Id = 1;";
            update.Parameters.AddWithValue("$clientId", "20000000-0000-0000-0000-000000000002");
            Assert.Equal(1, update.ExecuteNonQuery());
        }

        using var started = new ManualResetEventSlim();
        var creation = Task.Run(() =>
        {
            started.Set();
            return Record.Exception(() => context.Devices.AddLegacyDeviceForActiveMicrosoftConfiguration(
                Guid.CreateVersion7(),
                "Concurrent sender",
                null,
                ["192.168.1.20"],
                [activeA.AuthorizedSender!],
                activeA.Fingerprint,
                activeA.AuthorizedSender!));
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        await Task.Delay(100);
        transaction.Commit();

        Assert.IsType<MicrosoftConfigurationConcurrencyException>(await creation);
        Assert.Empty(context.Database.GetDevices());
    }

    [Fact]
    public void Device_creation_with_unchanged_active_configuration_succeeds_once()
    {
        using var context = new DeviceDatabaseContext();
        var active = ActivateMicrosoft(context.Database, "20000000-0000-0000-0000-000000000001");
        var deviceId = Guid.CreateVersion7();

        var device = context.Devices.AddLegacyDeviceForActiveMicrosoftConfiguration(
            deviceId,
            "Atomic device",
            null,
            ["192.168.1.20"],
            [active.AuthorizedSender!],
            active.Fingerprint,
            active.AuthorizedSender!);
        Assert.Throws<SqliteException>(() => context.Devices.AddLegacyDeviceForActiveMicrosoftConfiguration(
            deviceId,
            "Atomic device",
            null,
            ["192.168.1.20"],
            [active.AuthorizedSender!],
            active.Fingerprint,
            active.AuthorizedSender!));

        Assert.Equal(deviceId, device.Id);
        Assert.Single(context.Database.GetDevices());
    }

    [Fact]
    public void Delivering_message_is_counted_as_active_queue_work()
    {
        using var context = new DeviceDatabaseContext();
        var device = context.Devices.AddLegacyDevice(
            "Queue device", ["192.168.1.20"], ["scanner@example.com"]);
        context.Database.InsertQueuedMessage(new QueuedMessage(
            Guid.CreateVersion7(), device.Id, "scanner@example.com", ["recipient@example.net"],
            DateTimeOffset.UtcNow, 12, $"{Guid.NewGuid():N}.eml", QueueState.Delivering));

        var metrics = context.Database.GetQueueMetrics();

        Assert.Equal(1, metrics.DeliveringCount);
        Assert.NotNull(metrics.OldestQueuedUtc);
    }

    [Fact]
    public void Dashboard_activity_uses_bounded_metadata_queries_and_no_message_content()
    {
        using var context = new DeviceDatabaseContext();
        var device = context.Devices.AddLegacyDevice(
            "Status device",
            ["127.0.0.1"],
            ["scanner@example.com"]);
        var now = DateTimeOffset.UtcNow;
        context.Database.InsertQueuedMessage(new QueuedMessage(
            Guid.CreateVersion7(), device.Id, "scanner@example.com", ["recipient@example.net"],
            now, 12, $"{Guid.NewGuid():N}.eml", QueueState.PermanentFailure,
            CompletedUtc: now, LastErrorCategory: "RecipientRejected", PayloadPresent: false));

        var snapshot = new DeviceOverviewService(
            context.Database,
            TimeProvider.System,
            new SmtpListenerOptions { ListenAddress = "0.0.0.0", AllowCleartextAuthentication = true },
            new MicrosoftIdentityRuntimeState(context.Database),
            new ExchangeDeliveryRuntimeState())
            .GetSnapshot();

        var item = Assert.Single(snapshot.Devices);
        Assert.Equal(DeviceUiStatus.NeedsAttention, item.Status);
        Assert.Equal(1, item.Activity.MessagesSince);
        Assert.Equal(1, snapshot.Today.PermanentFailures);
        Assert.Equal(QueueState.PermanentFailure, item.Activity.LatestMessageState);
    }

    [Fact]
    public void Loopback_listener_never_generates_a_false_LAN_address()
    {
        var advice = new DeviceEndpointAdvisor(new SmtpListenerOptions
        {
            ListenAddress = IPAddress.Loopback.ToString(),
            Port = 2525,
        }).GetAdvice();

        Assert.False(advice.IsLanReachable);
        Assert.Empty(advice.Candidates);
        Assert.Contains("local-only", advice.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.False(advice.IsAuthenticatedSmtpAvailable);
    }

    [Fact]
    public void Disabled_listener_is_not_reported_as_reachable()
    {
        var advice = new DeviceEndpointAdvisor(new SmtpListenerOptions
        {
            Enabled = false,
            ListenAddress = "0.0.0.0",
            AllowCleartextAuthentication = true,
        }).GetAdvice();

        Assert.False(advice.IsLanReachable);
        Assert.False(advice.IsAuthenticatedSmtpAvailable);
        Assert.Contains("disabled", advice.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Public_or_unavailable_listener_address_is_never_suggested_to_a_device()
    {
        var advice = new DeviceEndpointAdvisor(new SmtpListenerOptions
        {
            ListenAddress = "203.0.113.25",
            Port = 2525,
        }).GetAdvice();

        Assert.False(advice.IsLanReachable);
        Assert.Empty(advice.Candidates);
        Assert.Contains("private", advice.Warning, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class DeviceDatabaseContext : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "RelayBridge.Tests", Guid.NewGuid().ToString("N"));

        public DeviceDatabaseContext()
        {
            Database = new RelayDatabase(new RelayStorageOptions { DataDirectory = _directory }, AppContext.BaseDirectory);
            Database.Initialize();
            Devices = new DeviceService(Database);
        }

        public RelayDatabase Database { get; }
        public DeviceService Devices { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }

    private static DeviceService CreateResetService(
        RelayDatabase database,
        string password,
        Barrier barrier)
    {
        return new DeviceService(database, () =>
        {
            var generated = new GeneratedDevicePassword(password, DevicePassword.CreateVerifier(password));
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
            return generated;
        });
    }

    private static ResetAttempt AttemptReset(
        DeviceService service,
        Guid deviceId,
        long expectedRevision,
        string password)
    {
        try
        {
            return new ResetAttempt(password, service.ResetPassword(deviceId, expectedRevision), null);
        }
        catch (Exception exception)
        {
            return new ResetAttempt(password, null, exception);
        }
    }

    private static MutationAttempt AttemptMutation<T>(
        string name,
        Barrier barrier,
        Func<T> mutation)
    {
        Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
        try
        {
            return new MutationAttempt(name, mutation(), null);
        }
        catch (Exception exception)
        {
            return new MutationAttempt(name, null, exception);
        }
    }

    private static ActiveMicrosoftConfiguration ActivateMicrosoft(RelayDatabase database, string clientId)
    {
        var certificate = MicrosoftCertificateReference.Create(
            "0123456789ABCDEF0123456789ABCDEF01234567",
            CertificateStoreTarget.CurrentUser);
        var configuration = MicrosoftIdentityConfiguration.Create(
            "10000000-0000-0000-0000-000000000001",
            clientId,
            certificate);
        var state = MicrosoftSetupState.Fresh(DateTimeOffset.Parse("2026-08-22T10:00:00Z"));
        database.ActivateMicrosoftConfiguration(configuration, "scanner@example.com", state);
        return database.GetActiveMicrosoftConfiguration()!;
    }

    private sealed record SecretContainer(
        GeneratedDevicePassword Generated,
        ProvisionedDevice Provisioned);

    private sealed record ResetAttempt(
        string Password,
        ProvisionedDevice? Result,
        Exception? Exception);

    private sealed record MutationAttempt(
        string Name,
        object? Result,
        Exception? Exception);
}
