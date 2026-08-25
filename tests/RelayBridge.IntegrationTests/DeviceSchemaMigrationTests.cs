// SPDX-License-Identifier: MPL-2.0

using Microsoft.Data.Sqlite;
using RelayBridge.Core.Devices;
using RelayBridge.Core.Queue;
using RelayBridge.Core.Microsoft;
using RelayBridge.Infrastructure.Storage;
using Xunit;

namespace RelayBridge.IntegrationTests;

public sealed class DeviceSchemaMigrationTests
{
    [Fact]
    public void Genuine_schema_v4_upgrades_through_normal_initialization_to_v9_without_data_loss()
    {
        var directory = CreateSchemaV4Database(out var deviceId, out var messageId, out var verifier);
        try
        {
            var database = Open(directory);
            database.Initialize();

            var device = Assert.Single(database.GetDevices());
            Assert.Equal(deviceId, device.Id);
            Assert.Equal("Historical authenticated device", device.Name);
            Assert.Null(device.Description);
            Assert.False(device.Enabled);
            Assert.Equal(DeviceAuthenticationMode.Authenticated, device.AuthenticationMode);
            Assert.Equal("historical-device", device.SmtpUsername);
            Assert.Equal(verifier, device.PasswordVerifier);
            Assert.Equal(["192.168.40.20/32", "192.168.41.0/24"], device.AllowedNetworks);
            Assert.Equal(["scanner@example.com"], device.AllowedSenders);
            Assert.Equal(0, device.Revision);
            var message = Assert.Single(database.GetQueuedMessages());
            Assert.Equal(messageId, message.Id);
            Assert.Equal(deviceId, message.DeviceId);
            Assert.Equal(["recipient@example.net"], message.Recipients);
            Assert.Equal(9, GetVersion(database));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    private static string CreateSchemaV4Database(
        out Guid deviceId,
        out Guid messageId,
        out string verifier)
    {
        var directory = Path.Combine(Path.GetTempPath(), "RelayBridge.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        deviceId = Guid.CreateVersion7();
        messageId = Guid.CreateVersion7();
        verifier = DevicePassword.CreateVerifier("historical-password");
        using var connection = OpenRaw(directory);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA foreign_keys = ON;
            CREATE TABLE Devices (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Enabled INTEGER NOT NULL CHECK (Enabled IN (0, 1)),
                AuthenticationMode TEXT NOT NULL CHECK (AuthenticationMode IN ('Authenticated', 'Legacy')),
                SmtpUsername TEXT COLLATE NOCASE UNIQUE,
                PasswordVerifier TEXT,
                CreatedUtc TEXT NOT NULL,
                CHECK (
                    (AuthenticationMode = 'Authenticated' AND SmtpUsername IS NOT NULL AND PasswordVerifier IS NOT NULL)
                    OR
                    (AuthenticationMode = 'Legacy' AND SmtpUsername IS NULL AND PasswordVerifier IS NULL)
                )
            );
            CREATE TABLE DeviceAllowedNetworks (
                DeviceId TEXT NOT NULL REFERENCES Devices(Id) ON DELETE CASCADE,
                Network TEXT NOT NULL,
                PRIMARY KEY (DeviceId, Network)
            );
            CREATE TABLE DeviceAllowedSenders (
                DeviceId TEXT NOT NULL REFERENCES Devices(Id) ON DELETE CASCADE,
                Sender TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY (DeviceId, Sender)
            );
            CREATE TABLE QueueMessages (
                Id TEXT PRIMARY KEY,
                DeviceId TEXT NOT NULL REFERENCES Devices(Id),
                EnvelopeFrom TEXT NOT NULL,
                ReceivedUtc TEXT NOT NULL,
                SizeBytes INTEGER NOT NULL CHECK (SizeBytes >= 0),
                SpoolFileName TEXT NOT NULL UNIQUE,
                State TEXT NOT NULL CHECK (State IN ('Queued', 'Delivering', 'RetryScheduled', 'Delivered', 'PermanentFailure')),
                RecipientCount INTEGER NOT NULL CHECK (RecipientCount > 0),
                AttemptCount INTEGER NOT NULL DEFAULT 0 CHECK (AttemptCount >= 0),
                NextAttemptUtc TEXT,
                LastAttemptUtc TEXT,
                CompletedUtc TEXT,
                LastErrorCategory TEXT,
                LastErrorMessage TEXT,
                PayloadPresent INTEGER NOT NULL DEFAULT 1 CHECK (PayloadPresent IN (0, 1))
            );
            CREATE TABLE QueueRecipients (
                MessageId TEXT NOT NULL REFERENCES QueueMessages(Id) ON DELETE CASCADE,
                Ordinal INTEGER NOT NULL CHECK (Ordinal >= 0),
                Recipient TEXT NOT NULL,
                PRIMARY KEY (MessageId, Ordinal)
            );
            INSERT INTO Devices
                (Id, Name, Enabled, AuthenticationMode, SmtpUsername, PasswordVerifier, CreatedUtc)
            VALUES
                ($deviceId, 'Historical authenticated device', 0, 'Authenticated', 'historical-device', $verifier, '2026-08-20T10:00:00.0000000+00:00');
            INSERT INTO DeviceAllowedNetworks (DeviceId, Network) VALUES ($deviceId, '192.168.40.20/32');
            INSERT INTO DeviceAllowedNetworks (DeviceId, Network) VALUES ($deviceId, '192.168.41.0/24');
            INSERT INTO DeviceAllowedSenders (DeviceId, Sender) VALUES ($deviceId, 'scanner@example.com');
            INSERT INTO QueueMessages
                (Id, DeviceId, EnvelopeFrom, ReceivedUtc, SizeBytes, SpoolFileName, State,
                 RecipientCount, AttemptCount, PayloadPresent)
            VALUES
                ($messageId, $deviceId, 'scanner@example.com', '2026-08-20T10:01:00.0000000+00:00',
                 128, 'historical.eml', 'Queued', 1, 0, 1);
            INSERT INTO QueueRecipients (MessageId, Ordinal, Recipient)
            VALUES ($messageId, 0, 'recipient@example.net');
            PRAGMA user_version = 4;
            """;
        command.Parameters.AddWithValue("$deviceId", deviceId.ToString("D"));
        command.Parameters.AddWithValue("$messageId", messageId.ToString("D"));
        command.Parameters.AddWithValue("$verifier", verifier);
        command.ExecuteNonQuery();
        return directory;
    }

    [Fact]
    public void Schema_v5_upgrades_to_current_schema_without_data_loss()
    {
        var directory = CreateCurrentDatabase("Preserved description", out var deviceId, out var messageId);
        try
        {
            DowngradeSchema(directory, version: 5, dropDescription: false);

            var database = Open(directory);
            database.Initialize();

            var device = Assert.Single(database.GetDevices());
            Assert.Equal(deviceId, device.Id);
            Assert.Equal("Preserved description", device.Description);
            Assert.Equal(["192.168.40.20/32", "192.168.41.0/24"], device.AllowedNetworks);
            Assert.Equal(messageId, Assert.Single(database.GetQueuedMessages()).Id);
            Assert.Equal(0, device.Revision);
            Assert.Equal(9, GetVersion(database));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void Genuine_schema_v6_upgrades_to_v9_with_fresh_matching_activation_identity()
    {
        var directory = CreateCurrentDatabase("Preserved at v6", out var deviceId, out var messageId);
        try
        {
            var database = Open(directory);
            var identity = MicrosoftIdentityConfiguration.Create(
                Guid.NewGuid(),
                Guid.NewGuid(),
                MicrosoftCertificateReference.Create(
                    new string('A', 40),
                    CertificateStoreTarget.CurrentUser));
            var legacySetup = new MicrosoftSetupState(
                MicrosoftSetupStep.Complete,
                MicrosoftSetupMode.ExistingApplication,
                identity.Certificate,
                identity.TenantId,
                identity.ClientId,
                Guid.NewGuid(),
                "scanner@example.com",
                true,
                true,
                true,
                true,
                true,
                DateTimeOffset.Parse("2026-08-22T10:00:00Z"),
                Guid.NewGuid());
            database.ActivateMicrosoftConfiguration(identity, "scanner@example.com", legacySetup);
            SqliteConnection.ClearAllPools();

            using (var connection = OpenRaw(directory))
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    ALTER TABLE MicrosoftIdentityConfiguration DROP COLUMN ActivationId;
                    ALTER TABLE MicrosoftSetupState DROP COLUMN ActivationId;
                    PRAGMA user_version = 6;
                    """;
                command.ExecuteNonQuery();
            }

            var upgraded = Open(directory);
            upgraded.Initialize();
            var active = upgraded.GetActiveMicrosoftConfiguration();
            var setup = upgraded.GetMicrosoftSetupState();

            Assert.NotNull(active);
            Assert.NotNull(setup);
            Assert.NotEqual(Guid.Empty, active.ActivationId);
            Assert.Equal(active.ActivationId, setup.ActivationId);
            Assert.Equal(identity.ClientId, active.Identity.ClientId);
            Assert.Equal("scanner@example.com", active.AuthorizedSender);
            Assert.Equal(deviceId, Assert.Single(upgraded.GetDevices()).Id);
            Assert.Equal(messageId, Assert.Single(upgraded.GetQueuedMessages()).Id);
            Assert.Equal(9, GetVersion(upgraded));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void Schema_v7_candidate_upgrades_to_v9_with_deterministic_revision_and_lifecycle()
    {
        var directory = CreateCurrentDatabase("Preserved at v7", out var deviceId, out var messageId);
        try
        {
            var database = Open(directory);
            var candidate = MicrosoftSetupState.Fresh(DateTimeOffset.Parse("2026-08-23T10:00:00Z")) with
            {
                Step = MicrosoftSetupStep.ExchangePermission,
                SenderMailbox = "scanner@example.com",
            };
            database.SaveMicrosoftSetupState(candidate);
            SqliteConnection.ClearAllPools();
            using (var connection = OpenRaw(directory))
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    ALTER TABLE MicrosoftSetupState DROP COLUMN Revision;
                    ALTER TABLE MicrosoftSetupState DROP COLUMN Lifecycle;
                    PRAGMA user_version = 7;
                    """;
                command.ExecuteNonQuery();
            }

            var upgraded = Open(directory);
            upgraded.Initialize();
            var state = Assert.IsType<MicrosoftSetupState>(upgraded.GetMicrosoftSetupState());

            Assert.Equal(candidate.ActivationId, state.ActivationId);
            Assert.Equal("scanner@example.com", state.SenderMailbox);
            Assert.Equal(0, state.Revision);
            Assert.Equal(deviceId, Assert.Single(upgraded.GetDevices()).Id);
            Assert.Equal(messageId, Assert.Single(upgraded.GetQueuedMessages()).Id);
            Assert.Equal(9, GetVersion(upgraded));
            Assert.Equal(MicrosoftSetupCandidateLifecycle.Active, state.Lifecycle);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void Schema_v8_upgrades_to_v9_and_marks_matching_completed_candidate_activated()
    {
        var directory = CreateCurrentDatabase("Preserved at v8", out var deviceId, out var messageId);
        try
        {
            var database = Open(directory);
            var identity = MicrosoftIdentityConfiguration.Create(
                Guid.NewGuid(),
                Guid.NewGuid(),
                MicrosoftCertificateReference.Create(new string('B', 40), CertificateStoreTarget.CurrentUser));
            var completed = MicrosoftSetupState.Fresh(DateTimeOffset.Parse("2026-08-23T12:00:00Z")) with
            {
                Step = MicrosoftSetupStep.Complete,
                Certificate = identity.Certificate,
                TenantId = identity.TenantId,
                ClientId = identity.ClientId,
                ServicePrincipalObjectId = Guid.NewGuid(),
                SenderMailbox = "scanner@example.com",
                EntraResultValidated = true,
                ExchangeResultValidated = true,
                IdentityValidated = true,
                ExchangeValidated = true,
                TestMessageAccepted = true,
            };
            database.ActivateMicrosoftConfiguration(identity, "scanner@example.com", completed);
            SqliteConnection.ClearAllPools();
            using (var connection = OpenRaw(directory))
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    ALTER TABLE MicrosoftSetupState DROP COLUMN Lifecycle;
                    PRAGMA user_version = 8;
                    """;
                command.ExecuteNonQuery();
            }

            var upgraded = Open(directory);
            upgraded.Initialize();
            var state = Assert.IsType<MicrosoftSetupState>(upgraded.GetMicrosoftSetupState());

            Assert.Equal(MicrosoftSetupCandidateLifecycle.Activated, state.Lifecycle);
            Assert.Equal(completed.ActivationId, state.ActivationId);
            Assert.Equal(deviceId, Assert.Single(upgraded.GetDevices()).Id);
            Assert.Equal(messageId, Assert.Single(upgraded.GetQueuedMessages()).Id);
            Assert.Equal(9, GetVersion(upgraded));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    private static string CreateCurrentDatabase(string description, out Guid deviceId, out Guid messageId)
    {
        var directory = Path.Combine(Path.GetTempPath(), "RelayBridge.Tests", Guid.NewGuid().ToString("N"));
        var database = Open(directory);
        database.Initialize();
        var devices = new DeviceService(database);
        var device = devices.AddLegacyDevice(
            "Migration device",
            description,
            ["192.168.40.20", "192.168.41.0/24"],
            ["scanner@example.com"]);
        var message = new QueuedMessage(
            Guid.CreateVersion7(),
            device.Id,
            "scanner@example.com",
            ["recipient@example.net"],
            DateTimeOffset.UtcNow,
            128,
            $"{Guid.NewGuid():N}.eml",
            QueueState.Queued);
        database.InsertQueuedMessage(message);
        deviceId = device.Id;
        messageId = message.Id;
        SqliteConnection.ClearAllPools();
        return directory;
    }

    private static void DowngradeSchema(string directory, int version, bool dropDescription)
    {
        var path = Path.Combine(directory, "relaybridge.db");
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = dropDescription
            ? $"ALTER TABLE Devices DROP COLUMN Revision; ALTER TABLE Devices DROP COLUMN Description; PRAGMA user_version = {version};"
            : $"ALTER TABLE Devices DROP COLUMN Revision; PRAGMA user_version = {version};";
        command.ExecuteNonQuery();
    }

    private static SqliteConnection OpenRaw(string directory)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "relaybridge.db")};Pooling=False");
        connection.Open();
        return connection;
    }

    private static RelayDatabase Open(string directory) => new(
        new RelayStorageOptions { DataDirectory = directory },
        AppContext.BaseDirectory);

    private static int GetVersion(RelayDatabase database)
    {
        using var connection = database.OpenConnectionForDiagnostics();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int GetVersion(SqliteConnection connection) =>
        Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"), System.Globalization.CultureInfo.InvariantCulture);

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ScalarText(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private static void Cleanup(string directory)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
