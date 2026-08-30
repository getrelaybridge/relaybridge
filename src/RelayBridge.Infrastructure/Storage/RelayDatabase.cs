// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using Microsoft.Data.Sqlite;
using RelayBridge.Core.Devices;
using RelayBridge.Core.Microsoft;
using RelayBridge.Core.Queue;
using RelayBridge.Infrastructure.Queue;

namespace RelayBridge.Infrastructure.Storage;

public sealed class RelayDatabase
{
    private const int CurrentSchemaVersion = 9;
    private readonly object _initializationLock = new();
    private readonly string _connectionString;
    private bool _initialized;

    public RelayDatabase(RelayStorageOptions options, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            throw new ArgumentException("A data directory is required.", nameof(options));
        }

        DataDirectory = Path.GetFullPath(options.DataDirectory, baseDirectory);
        DatabasePath = Path.Combine(DataDirectory, "relaybridge.db");
        SpoolDirectory = Path.Combine(DataDirectory, "spool");
        IncomingDirectory = Path.Combine(SpoolDirectory, "incoming");
        PendingDirectory = Path.Combine(SpoolDirectory, "pending");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    public string DataDirectory { get; }

    public string DatabasePath { get; }

    public string SpoolDirectory { get; }

    public string IncomingDirectory { get; }

    public string PendingDirectory { get; }

    public string GetPendingPath(string spoolFileName)
    {
        if (string.IsNullOrWhiteSpace(spoolFileName) ||
            !string.Equals(Path.GetFileName(spoolFileName), spoolFileName, StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(spoolFileName), ".eml", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Queue metadata contains an invalid spool file name.");
        }

        return Path.Combine(PendingDirectory, spoolFileName);
    }

    public void Initialize(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_initialized)
        {
            return;
        }

        lock (_initializationLock)
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(IncomingDirectory);
            Directory.CreateDirectory(PendingDirectory);

            using var connection = OpenConnectionCore();
            using (var journalCommand = connection.CreateCommand())
            {
                journalCommand.CommandText = "PRAGMA journal_mode = WAL;";
                journalCommand.ExecuteNonQuery();
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = BaseSchema;
                command.ExecuteNonQuery();
            }

            EnsureQueueSchema(connection);
            _initialized = true;
        }
    }

    public void AddDevice(DeviceDefinition device, CancellationToken cancellationToken = default)
    {
        AddDeviceCore(device, null, null, requireActiveMicrosoftConfiguration: false, cancellationToken);
    }

    public void AddDeviceForActiveMicrosoftConfiguration(
        DeviceDefinition device,
        string expectedConfigurationFingerprint,
        string expectedSender,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedConfigurationFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSender);
        AddDeviceCore(
            device,
            expectedConfigurationFingerprint,
            expectedSender,
            requireActiveMicrosoftConfiguration: true,
            cancellationToken);
    }

    private void AddDeviceCore(
        DeviceDefinition device,
        string? expectedConfigurationFingerprint,
        string? expectedSender,
        bool requireActiveMicrosoftConfiguration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);

        using var connection = OpenConnectionCore();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (requireActiveMicrosoftConfiguration)
        {
            var active = ReadActiveMicrosoftConfiguration(connection, transaction);
            if (active is null ||
                !string.Equals(active.Fingerprint, expectedConfigurationFingerprint, StringComparison.Ordinal) ||
                !string.Equals(active.AuthorizedSender, expectedSender, StringComparison.OrdinalIgnoreCase))
            {
                throw new MicrosoftConfigurationConcurrencyException();
            }
        }

        using (var deviceCommand = connection.CreateCommand())
        {
            deviceCommand.Transaction = transaction;
            deviceCommand.CommandText =
                """
                INSERT INTO Devices
                    (Id, Name, Description, Enabled, AuthenticationMode, SmtpUsername, PasswordVerifier, CreatedUtc, Revision)
                VALUES
                    ($id, $name, $description, $enabled, $mode, $username, $verifier, $createdUtc, $revision);
                """;
            deviceCommand.Parameters.AddWithValue("$id", device.Id.ToString("D"));
            deviceCommand.Parameters.AddWithValue("$name", device.Name);
            deviceCommand.Parameters.AddWithValue("$description", (object?)device.Description ?? DBNull.Value);
            deviceCommand.Parameters.AddWithValue("$enabled", device.Enabled ? 1 : 0);
            deviceCommand.Parameters.AddWithValue("$mode", device.AuthenticationMode.ToString());
            deviceCommand.Parameters.AddWithValue("$username", (object?)device.SmtpUsername ?? DBNull.Value);
            deviceCommand.Parameters.AddWithValue("$verifier", (object?)device.PasswordVerifier ?? DBNull.Value);
            deviceCommand.Parameters.AddWithValue("$createdUtc", FormatDate(device.CreatedUtc));
            deviceCommand.Parameters.AddWithValue("$revision", device.Revision);
            deviceCommand.ExecuteNonQuery();
        }

        foreach (var network in device.AllowedNetworks)
        {
            using var networkCommand = connection.CreateCommand();
            networkCommand.Transaction = transaction;
            networkCommand.CommandText =
                "INSERT INTO DeviceAllowedNetworks (DeviceId, Network) VALUES ($deviceId, $network);";
            networkCommand.Parameters.AddWithValue("$deviceId", device.Id.ToString("D"));
            networkCommand.Parameters.AddWithValue("$network", network);
            networkCommand.ExecuteNonQuery();
        }

        foreach (var sender in device.AllowedSenders)
        {
            using var senderCommand = connection.CreateCommand();
            senderCommand.Transaction = transaction;
            senderCommand.CommandText =
                "INSERT INTO DeviceAllowedSenders (DeviceId, Sender) VALUES ($deviceId, $sender);";
            senderCommand.Parameters.AddWithValue("$deviceId", device.Id.ToString("D"));
            senderCommand.Parameters.AddWithValue("$sender", sender);
            senderCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public MicrosoftIdentityConfiguration? GetMicrosoftIdentityConfiguration(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);
        using var connection = OpenConnectionCore();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT TenantId, ClientId, CertificateThumbprint, CertificateStoreName, CertificateStoreLocation
            FROM MicrosoftIdentityConfiguration
            WHERE Id = 1;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var storeName = reader.GetString(3);
        if (!string.Equals(storeName, "My", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Microsoft identity configuration contains an unsupported certificate store.");
        }

        var storeLocation = Enum.Parse<CertificateStoreTarget>(reader.GetString(4), ignoreCase: false);
        return MicrosoftIdentityConfiguration.Create(
            reader.GetString(0),
            reader.GetString(1),
            MicrosoftCertificateReference.Create(reader.GetString(2), storeLocation));
    }

    public ActiveMicrosoftConfiguration? GetActiveMicrosoftConfiguration(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);
        using var connection = OpenConnectionCore();
        return ReadActiveMicrosoftConfiguration(connection, transaction: null);
    }

    private static ActiveMicrosoftConfiguration? ReadActiveMicrosoftConfiguration(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT TenantId, ClientId, CertificateThumbprint, CertificateStoreName,
                   CertificateStoreLocation, AuthorizedSender, ActivationId
            FROM MicrosoftIdentityConfiguration
            WHERE Id = 1;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var storeName = reader.GetString(3);
        if (!string.Equals(storeName, "My", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Microsoft identity configuration contains an unsupported certificate store.");
        }

        var configuration = MicrosoftIdentityConfiguration.Create(
            reader.GetString(0),
            reader.GetString(1),
            MicrosoftCertificateReference.Create(
                reader.GetString(2),
                Enum.Parse<CertificateStoreTarget>(reader.GetString(4), ignoreCase: false)));
        var sender = reader.IsDBNull(5) ? null : reader.GetString(5);
        if (reader.IsDBNull(6) || !Guid.TryParse(reader.GetString(6), out var activationId) || activationId == Guid.Empty)
        {
            throw new InvalidOperationException("Microsoft identity configuration contains an invalid activation ID.");
        }

        return new ActiveMicrosoftConfiguration(
            configuration,
            sender,
            MicrosoftConfigurationFingerprint.Create(configuration, sender),
            activationId);
    }

    public void SaveMicrosoftIdentityConfiguration(
        MicrosoftIdentityConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);
        using var connection = OpenConnectionCore();
        var activationId = Guid.NewGuid();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO MicrosoftIdentityConfiguration
                (Id, TenantId, ClientId, CertificateThumbprint, CertificateStoreName, CertificateStoreLocation, ActivationId)
            VALUES
                (1, $tenantId, $clientId, $thumbprint, $storeName, $storeLocation, $activationId)
            ON CONFLICT(Id) DO UPDATE SET
                TenantId = excluded.TenantId,
                ClientId = excluded.ClientId,
                CertificateThumbprint = excluded.CertificateThumbprint,
                CertificateStoreName = excluded.CertificateStoreName,
                CertificateStoreLocation = excluded.CertificateStoreLocation,
                ActivationId = excluded.ActivationId;
            """;
        command.Parameters.AddWithValue("$tenantId", configuration.TenantId.ToString("D"));
        command.Parameters.AddWithValue("$clientId", configuration.ClientId.ToString("D"));
        command.Parameters.AddWithValue("$thumbprint", configuration.Certificate.Thumbprint);
        command.Parameters.AddWithValue("$storeName", configuration.Certificate.StoreName);
        command.Parameters.AddWithValue("$storeLocation", configuration.Certificate.StoreLocation.ToString());
        command.Parameters.AddWithValue("$activationId", activationId.ToString("D"));
        command.ExecuteNonQuery();
    }

    public string? GetMicrosoftAuthorizedSender(CancellationToken cancellationToken = default)
    {
        return GetActiveMicrosoftConfiguration(cancellationToken)?.AuthorizedSender;
    }

    public void ActivateMicrosoftConfiguration(
        MicrosoftIdentityConfiguration configuration,
        string authorizedSender,
        MicrosoftSetupState setupState,
        CancellationToken cancellationToken = default)
    {
        ActivateMicrosoftConfigurationCore(
            configuration,
            authorizedSender,
            setupState,
            expected: null,
            cancellationToken);
    }

    public void ActivateMicrosoftConfigurationConditional(
        MicrosoftIdentityConfiguration configuration,
        string authorizedSender,
        MicrosoftSetupState setupState,
        NativeMicrosoftCandidateIdentity expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ActivateMicrosoftConfigurationCore(
            configuration,
            authorizedSender,
            setupState,
            expected,
            cancellationToken);
    }

    private void ActivateMicrosoftConfigurationCore(
        MicrosoftIdentityConfiguration configuration,
        string authorizedSender,
        MicrosoftSetupState setupState,
        NativeMicrosoftCandidateIdentity? expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizedSender);
        ArgumentNullException.ThrowIfNull(setupState);
        if (setupState.ActivationId is null || setupState.ActivationId == Guid.Empty)
        {
            throw new InvalidOperationException("The candidate Microsoft configuration has no activation ID.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);

        using var connection = OpenConnectionCore();
        using var transaction = connection.BeginTransaction(deferred: expected is null);
        if (expected is not null)
        {
            var current = ReadMicrosoftSetupState(connection, transaction);
            EnsureCandidateMatches(current, expected);
            if (setupState.ActivationId != expected.ActivationId ||
                setupState.Revision != checked(expected.Revision + 1) ||
                setupState.Step != MicrosoftSetupStep.TestMessage ||
                !setupState.ExchangeValidated ||
                !string.Equals(setupState.SenderMailbox, expected.SenderMailbox, StringComparison.OrdinalIgnoreCase))
            {
                throw new MicrosoftSetupConcurrencyException();
            }
        }

        using (var identityCommand = connection.CreateCommand())
        {
            identityCommand.Transaction = transaction;
            identityCommand.CommandText =
                """
                INSERT INTO MicrosoftIdentityConfiguration
                    (Id, TenantId, ClientId, CertificateThumbprint, CertificateStoreName,
                     CertificateStoreLocation, AuthorizedSender, ActivationId)
                VALUES
                    (1, $tenantId, $clientId, $thumbprint, $storeName, $storeLocation, $authorizedSender, $activationId)
                ON CONFLICT(Id) DO UPDATE SET
                    TenantId = excluded.TenantId,
                    ClientId = excluded.ClientId,
                    CertificateThumbprint = excluded.CertificateThumbprint,
                    CertificateStoreName = excluded.CertificateStoreName,
                    CertificateStoreLocation = excluded.CertificateStoreLocation,
                    AuthorizedSender = excluded.AuthorizedSender,
                    ActivationId = excluded.ActivationId;
                """;
            identityCommand.Parameters.AddWithValue("$tenantId", configuration.TenantId.ToString("D"));
            identityCommand.Parameters.AddWithValue("$clientId", configuration.ClientId.ToString("D"));
            identityCommand.Parameters.AddWithValue("$thumbprint", configuration.Certificate.Thumbprint);
            identityCommand.Parameters.AddWithValue("$storeName", configuration.Certificate.StoreName);
            identityCommand.Parameters.AddWithValue("$storeLocation", configuration.Certificate.StoreLocation.ToString());
            identityCommand.Parameters.AddWithValue("$authorizedSender", authorizedSender);
            identityCommand.Parameters.AddWithValue("$activationId", setupState.ActivationId.Value.ToString("D"));
            identityCommand.ExecuteNonQuery();
        }

        SaveMicrosoftSetupState(
            connection,
            transaction,
            setupState with { Lifecycle = MicrosoftSetupCandidateLifecycle.Activated });
        transaction.Commit();
    }

    public MicrosoftSetupState? GetMicrosoftSetupState(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);
        using var connection = OpenConnectionCore();
        return ReadMicrosoftSetupState(connection, transaction: null);
    }

    private static MicrosoftSetupState? ReadMicrosoftSetupState(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT Step, Mode, CertificateThumbprint, CertificateStoreLocation,
                   TenantId, ClientId, ServicePrincipalObjectId, SenderMailbox,
                   EntraResultValidated, ExchangeResultValidated, IdentityValidated,
                   ExchangeValidated, TestMessageAccepted, UpdatedUtc, ActivationId, Revision, Lifecycle
            FROM MicrosoftSetupState
            WHERE Id = 1;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        MicrosoftCertificateReference? certificate = null;
        if (!reader.IsDBNull(2))
        {
            certificate = MicrosoftCertificateReference.Create(
                reader.GetString(2),
                Enum.Parse<CertificateStoreTarget>(reader.GetString(3), ignoreCase: false));
        }

        return new MicrosoftSetupState(
            Enum.Parse<MicrosoftSetupStep>(reader.GetString(0), ignoreCase: false),
            Enum.Parse<MicrosoftSetupMode>(reader.GetString(1), ignoreCase: false),
            certificate,
            reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
            reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetBoolean(8),
            reader.GetBoolean(9),
            reader.GetBoolean(10),
            reader.GetBoolean(11),
            reader.GetBoolean(12),
            ParseDate(reader.GetString(13)),
            reader.IsDBNull(14) ? null : Guid.Parse(reader.GetString(14)),
            reader.GetInt64(15),
            Enum.Parse<MicrosoftSetupCandidateLifecycle>(reader.GetString(16), ignoreCase: false));
    }

    public MicrosoftSetupState SaveMicrosoftSetupStateConditional(
        NativeMicrosoftCandidateIdentity expected,
        MicrosoftSetupState replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(replacement);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);
        using var connection = OpenConnectionCore();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = ReadMicrosoftSetupState(connection, transaction);
        EnsureCandidateMatches(current, expected);
        var saved = replacement with { Revision = checked(expected.Revision + 1) };
        SaveMicrosoftSetupState(connection, transaction, saved);
        transaction.Commit();
        return saved;
    }

    public MicrosoftSetupCancellationResult CancelMicrosoftSetupCandidate(
        Guid activationId,
        long expectedRevision,
        DateTimeOffset cancelledUtc,
        CancellationToken cancellationToken = default)
    {
        if (activationId == Guid.Empty)
        {
            throw new ArgumentException("The candidate activation ID is required.", nameof(activationId));
        }

        ValidateExpectedRevision(expectedRevision);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);
        using var connection = OpenConnectionCore();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = ReadMicrosoftSetupState(connection, transaction);
        if (current is null || current.ActivationId != activationId)
        {
            transaction.Commit();
            return new MicrosoftSetupCancellationResult(MicrosoftSetupCancellationOutcome.Replaced, current);
        }

        if (current.Lifecycle == MicrosoftSetupCandidateLifecycle.Activated)
        {
            transaction.Commit();
            return new MicrosoftSetupCancellationResult(MicrosoftSetupCancellationOutcome.AlreadyActivated, current);
        }

        if (current.Lifecycle == MicrosoftSetupCandidateLifecycle.Cancelled)
        {
            transaction.Commit();
            return new MicrosoftSetupCancellationResult(MicrosoftSetupCancellationOutcome.AlreadyCancelled, current);
        }

        if (current.Revision != expectedRevision)
        {
            transaction.Commit();
            return new MicrosoftSetupCancellationResult(MicrosoftSetupCancellationOutcome.Changed, current);
        }

        var cancelled = current with
        {
            Step = MicrosoftSetupStep.Welcome,
            IdentityValidated = false,
            ExchangeValidated = false,
            TestMessageAccepted = false,
            UpdatedUtc = cancelledUtc,
            Revision = checked(current.Revision + 1),
            Lifecycle = MicrosoftSetupCandidateLifecycle.Cancelled,
        };
        SaveMicrosoftSetupState(connection, transaction, cancelled);
        transaction.Commit();
        return new MicrosoftSetupCancellationResult(MicrosoftSetupCancellationOutcome.Cancelled, cancelled);
    }

    public MicrosoftSetupState ActivateNativeMicrosoftConfiguration(
        MicrosoftIdentityConfiguration configuration,
        NativeMicrosoftCandidateIdentity expected,
        NativeMicrosoftActivationEvidence evidence,
        DateTimeOffset completedUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(evidence);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);

        using var connection = OpenConnectionCore();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = ReadMicrosoftSetupState(connection, transaction);
        EnsureCandidateMatches(current, expected);
        if (current is null || current.Lifecycle != MicrosoftSetupCandidateLifecycle.Active ||
            !current.EntraResultValidated || !current.ExchangeResultValidated ||
            !current.IdentityValidated || current.Certificate is null || current.TenantId is null ||
            current.ClientId is null || current.ServicePrincipalObjectId is null ||
            evidence.ActivationId != expected.ActivationId ||
            !evidence.IdentityVerified || !evidence.FinalSmtpAcceptanceReceived ||
            !string.Equals(evidence.CandidateFingerprint, expected.ConfigurationFingerprint, StringComparison.Ordinal) ||
            !string.Equals(evidence.SenderMailbox, current.SenderMailbox, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                MicrosoftConfigurationFingerprint.Create(configuration, current.SenderMailbox),
                evidence.ConfigurationFingerprint,
                StringComparison.Ordinal))
        {
            throw new MicrosoftSetupConcurrencyException();
        }

        var completed = current with
        {
            Step = MicrosoftSetupStep.Complete,
            ExchangeValidated = true,
            TestMessageAccepted = true,
            UpdatedUtc = completedUtc,
            Revision = checked(current.Revision + 1),
            Lifecycle = MicrosoftSetupCandidateLifecycle.Activated,
        };

        UpsertActiveMicrosoftConfiguration(
            connection,
            transaction,
            configuration,
            current.SenderMailbox!,
            expected.ActivationId);
        SaveMicrosoftSetupState(connection, transaction, completed);
        transaction.Commit();
        return completed;
    }

    public void SaveMicrosoftSetupState(
        MicrosoftSetupState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);
        using var connection = OpenConnectionCore();
        SaveMicrosoftSetupState(connection, transaction: null, state);
    }

    public void ClearMicrosoftSetupState(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);
        using var connection = OpenConnectionCore();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM MicrosoftSetupState WHERE Id = 1;";
        command.ExecuteNonQuery();
    }

    public void ClearMicrosoftIdentityConfiguration(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);
        using var connection = OpenConnectionCore();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM MicrosoftIdentityConfiguration WHERE Id = 1;";
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<DeviceDefinition> GetDevices(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);

        using var connection = OpenConnectionCore();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, Name, Description, Enabled, AuthenticationMode, SmtpUsername, PasswordVerifier, CreatedUtc, Revision
            FROM Devices
            ORDER BY Name;
            """;

        var rows = new List<DeviceRow>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(new DeviceRow(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetBoolean(3),
                    Enum.Parse<DeviceAuthenticationMode>(reader.GetString(4), ignoreCase: false),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    ParseDate(reader.GetString(7)),
                    reader.GetInt64(8)));
            }
        }

        var devices = new List<DeviceDefinition>(rows.Count);
        foreach (var row in rows)
        {
            var networks = LoadStrings(connection, "DeviceAllowedNetworks", "Network", row.Id);
            var senders = LoadStrings(connection, "DeviceAllowedSenders", "Sender", row.Id);
            devices.Add(row.AuthenticationMode == DeviceAuthenticationMode.Authenticated
                ? DeviceDefinition.CreateAuthenticated(
                    row.Id,
                    row.Name,
                    row.Description,
                    row.Enabled,
                    row.SmtpUsername!,
                    row.PasswordVerifier!,
                    networks,
                    senders,
                    row.CreatedUtc,
                    row.Revision)
                : DeviceDefinition.CreateLegacy(
                    row.Id,
                    row.Name,
                    row.Description,
                    row.Enabled,
                    networks,
                    senders,
                    row.CreatedUtc,
                    row.Revision));
        }

        return devices;
    }

    public DeviceDefinition? GetDevice(Guid deviceId, CancellationToken cancellationToken = default)
    {
        if (deviceId == Guid.Empty)
        {
            return null;
        }

        return GetDevices(cancellationToken).SingleOrDefault(device => device.Id == deviceId);
    }

    public void UpdateDeviceConfiguration(
        DeviceDefinition device,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ValidateExpectedRevision(expectedRevision);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);

        using var connection = OpenConnectionCore();
        using var transaction = connection.BeginTransaction();
        using (var deviceCommand = connection.CreateCommand())
        {
            deviceCommand.Transaction = transaction;
            deviceCommand.CommandText =
                """
                UPDATE Devices
                SET Name = $name,
                    Description = $description,
                    Revision = Revision + 1
                WHERE Id = $id AND Revision = $expectedRevision AND Revision < $maximumRevision;
                """;
            deviceCommand.Parameters.AddWithValue("$id", device.Id.ToString("D"));
            deviceCommand.Parameters.AddWithValue("$name", device.Name);
            deviceCommand.Parameters.AddWithValue("$description", (object?)device.Description ?? DBNull.Value);
            deviceCommand.Parameters.AddWithValue("$expectedRevision", expectedRevision);
            deviceCommand.Parameters.AddWithValue("$maximumRevision", long.MaxValue);
            if (deviceCommand.ExecuteNonQuery() != 1)
            {
                throw new DeviceConcurrencyException();
            }
        }

        using (var clearNetworks = connection.CreateCommand())
        {
            clearNetworks.Transaction = transaction;
            clearNetworks.CommandText = "DELETE FROM DeviceAllowedNetworks WHERE DeviceId = $deviceId;";
            clearNetworks.Parameters.AddWithValue("$deviceId", device.Id.ToString("D"));
            clearNetworks.ExecuteNonQuery();
        }

        using (var clearSenders = connection.CreateCommand())
        {
            clearSenders.Transaction = transaction;
            clearSenders.CommandText = "DELETE FROM DeviceAllowedSenders WHERE DeviceId = $deviceId;";
            clearSenders.Parameters.AddWithValue("$deviceId", device.Id.ToString("D"));
            clearSenders.ExecuteNonQuery();
        }

        foreach (var network in device.AllowedNetworks)
        {
            using var networkCommand = connection.CreateCommand();
            networkCommand.Transaction = transaction;
            networkCommand.CommandText =
                "INSERT INTO DeviceAllowedNetworks (DeviceId, Network) VALUES ($deviceId, $network);";
            networkCommand.Parameters.AddWithValue("$deviceId", device.Id.ToString("D"));
            networkCommand.Parameters.AddWithValue("$network", network);
            networkCommand.ExecuteNonQuery();
        }

        foreach (var sender in device.AllowedSenders)
        {
            using var senderCommand = connection.CreateCommand();
            senderCommand.Transaction = transaction;
            senderCommand.CommandText =
                "INSERT INTO DeviceAllowedSenders (DeviceId, Sender) VALUES ($deviceId, $sender);";
            senderCommand.Parameters.AddWithValue("$deviceId", device.Id.ToString("D"));
            senderCommand.Parameters.AddWithValue("$sender", sender);
            senderCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void SetDeviceEnabled(
        Guid deviceId,
        bool enabled,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ValidateExpectedRevision(expectedRevision);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);
        using var connection = OpenConnectionCore();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE Devices
            SET Enabled = $enabled, Revision = Revision + 1
            WHERE Id = $id AND Revision = $expectedRevision AND Revision < $maximumRevision;
            """;
        command.Parameters.AddWithValue("$id", deviceId.ToString("D"));
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
        command.Parameters.AddWithValue("$maximumRevision", long.MaxValue);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new DeviceConcurrencyException();
        }
    }

    public void UpdateDevicePasswordVerifier(
        Guid deviceId,
        string passwordVerifier,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(passwordVerifier))
        {
            throw new ArgumentException("A password verifier is required.", nameof(passwordVerifier));
        }

        ValidateExpectedRevision(expectedRevision);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);
        using var connection = OpenConnectionCore();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE Devices
            SET PasswordVerifier = $verifier, Revision = Revision + 1
            WHERE Id = $id
              AND AuthenticationMode = 'Authenticated'
              AND Revision = $expectedRevision
              AND Revision < $maximumRevision;
            """;
        command.Parameters.AddWithValue("$id", deviceId.ToString("D"));
        command.Parameters.AddWithValue("$verifier", passwordVerifier);
        command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
        command.Parameters.AddWithValue("$maximumRevision", long.MaxValue);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new DeviceConcurrencyException();
        }
    }

    public IReadOnlyList<DeviceActivitySnapshot> GetDeviceActivities(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);
        using var connection = OpenConnectionCore();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH RankedMessages AS (
                SELECT DeviceId, State, LastErrorCategory, ReceivedUtc,
                       ROW_NUMBER() OVER (PARTITION BY DeviceId ORDER BY ReceivedUtc DESC, Id DESC) AS Position
                FROM QueueMessages
            ),
            MessageTotals AS (
                SELECT DeviceId,
                       MAX(ReceivedUtc) AS LastAcceptedUtc,
                       MAX(CASE WHEN State = 'Delivered' THEN CompletedUtc END) AS LastDeliveredUtc,
                       SUM(CASE WHEN ReceivedUtc >= $sinceUtc THEN 1 ELSE 0 END) AS MessagesSince
                FROM QueueMessages
                GROUP BY DeviceId
            )
            SELECT Devices.Id,
                   MessageTotals.LastAcceptedUtc,
                   MessageTotals.LastDeliveredUtc,
                   COALESCE(MessageTotals.MessagesSince, 0),
                   RankedMessages.State,
                   RankedMessages.LastErrorCategory
            FROM Devices
            LEFT JOIN MessageTotals ON MessageTotals.DeviceId = Devices.Id
            LEFT JOIN RankedMessages ON RankedMessages.DeviceId = Devices.Id AND RankedMessages.Position = 1
            ORDER BY Devices.Name;
            """;
        command.Parameters.AddWithValue("$sinceUtc", FormatDate(sinceUtc));
        using var reader = command.ExecuteReader();
        var activities = new List<DeviceActivitySnapshot>();
        while (reader.Read())
        {
            activities.Add(new DeviceActivitySnapshot(
                Guid.Parse(reader.GetString(0)),
                reader.IsDBNull(1) ? null : ParseDate(reader.GetString(1)),
                reader.IsDBNull(2) ? null : ParseDate(reader.GetString(2)),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : Enum.Parse<QueueState>(reader.GetString(4), ignoreCase: false),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return activities;
    }

    public MessageOutcomeCounts GetMessageOutcomeCounts(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);
        using var connection = OpenConnectionCore();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                SUM(CASE WHEN State = 'Delivered' AND CompletedUtc >= $sinceUtc THEN 1 ELSE 0 END),
                SUM(CASE WHEN State = 'PermanentFailure' AND CompletedUtc >= $sinceUtc THEN 1 ELSE 0 END)
            FROM QueueMessages;
            """;
        command.Parameters.AddWithValue("$sinceUtc", FormatDate(sinceUtc));
        using var reader = command.ExecuteReader();
        _ = reader.Read();
        return new MessageOutcomeCounts(
            reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1));
    }

    public void InsertQueuedMessage(QueuedMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);

        using var connection = OpenConnectionCore();
        using var transaction = connection.BeginTransaction();
        using (var messageCommand = connection.CreateCommand())
        {
            messageCommand.Transaction = transaction;
            messageCommand.CommandText =
                """
                INSERT INTO QueueMessages
                    (Id, DeviceId, EnvelopeFrom, ReceivedUtc, SizeBytes, SpoolFileName, State, RecipientCount,
                     AttemptCount, NextAttemptUtc, LastAttemptUtc, CompletedUtc, LastErrorCategory,
                     LastErrorMessage, PayloadPresent)
                VALUES
                    ($id, $deviceId, $from, $receivedUtc, $sizeBytes, $spoolFileName, $state, $recipientCount,
                     $attemptCount, $nextAttemptUtc, $lastAttemptUtc, $completedUtc, $lastErrorCategory,
                     $lastErrorMessage, $payloadPresent);
                """;
            messageCommand.Parameters.AddWithValue("$id", message.Id.ToString("D"));
            messageCommand.Parameters.AddWithValue("$deviceId", message.DeviceId.ToString("D"));
            messageCommand.Parameters.AddWithValue("$from", message.EnvelopeFrom);
            messageCommand.Parameters.AddWithValue("$receivedUtc", FormatDate(message.ReceivedUtc));
            messageCommand.Parameters.AddWithValue("$sizeBytes", message.SizeBytes);
            messageCommand.Parameters.AddWithValue("$spoolFileName", message.SpoolFileName);
            messageCommand.Parameters.AddWithValue("$state", message.State.ToString());
            messageCommand.Parameters.AddWithValue("$recipientCount", message.Recipients.Count);
            messageCommand.Parameters.AddWithValue("$attemptCount", message.AttemptCount);
            messageCommand.Parameters.AddWithValue("$nextAttemptUtc", FormatNullableDate(message.NextAttemptUtc));
            messageCommand.Parameters.AddWithValue("$lastAttemptUtc", FormatNullableDate(message.LastAttemptUtc));
            messageCommand.Parameters.AddWithValue("$completedUtc", FormatNullableDate(message.CompletedUtc));
            messageCommand.Parameters.AddWithValue("$lastErrorCategory", (object?)message.LastErrorCategory ?? DBNull.Value);
            messageCommand.Parameters.AddWithValue("$lastErrorMessage", (object?)message.LastErrorMessage ?? DBNull.Value);
            messageCommand.Parameters.AddWithValue("$payloadPresent", message.PayloadPresent ? 1 : 0);
            messageCommand.ExecuteNonQuery();
        }

        for (var index = 0; index < message.Recipients.Count; index++)
        {
            using var recipientCommand = connection.CreateCommand();
            recipientCommand.Transaction = transaction;
            recipientCommand.CommandText =
                """
                INSERT INTO QueueRecipients (MessageId, Ordinal, Recipient)
                VALUES ($messageId, $ordinal, $recipient);
                """;
            recipientCommand.Parameters.AddWithValue("$messageId", message.Id.ToString("D"));
            recipientCommand.Parameters.AddWithValue("$ordinal", index);
            recipientCommand.Parameters.AddWithValue("$recipient", message.Recipients[index]);
            recipientCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<QueuedMessage> GetQueuedMessages(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);

        using var connection = OpenConnectionCore();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, DeviceId, EnvelopeFrom, ReceivedUtc, SizeBytes, SpoolFileName, State,
                   AttemptCount, NextAttemptUtc, LastAttemptUtc, CompletedUtc,
                   LastErrorCategory, LastErrorMessage, PayloadPresent
            FROM QueueMessages
            ORDER BY ReceivedUtc, Id;
            """;

        var rows = new List<MessageRow>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(new MessageRow(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    ParseDate(reader.GetString(3)),
                    reader.GetInt64(4),
                    reader.GetString(5),
                    Enum.Parse<QueueState>(reader.GetString(6), ignoreCase: false),
                    reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : ParseDate(reader.GetString(8)),
                    reader.IsDBNull(9) ? null : ParseDate(reader.GetString(9)),
                    reader.IsDBNull(10) ? null : ParseDate(reader.GetString(10)),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.GetBoolean(13)));
            }
        }

        var messages = rows
            .Select(row => new QueuedMessage(
                row.Id,
                row.DeviceId,
                row.EnvelopeFrom,
                LoadRecipients(connection, row.Id),
                row.ReceivedUtc,
                row.SizeBytes,
                row.SpoolFileName,
                row.State,
                row.AttemptCount,
                row.NextAttemptUtc,
                row.LastAttemptUtc,
                row.CompletedUtc,
                row.LastErrorCategory,
                row.LastErrorMessage,
                row.PayloadPresent))
            .ToArray();

        return messages;
    }

    public QueuedMessage? ClaimNextEligible(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);
        using var connection = OpenConnectionCore();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE QueueMessages
            SET State = 'Delivering',
                AttemptCount = AttemptCount + 1,
                LastAttemptUtc = $nowUtc,
                NextAttemptUtc = NULL,
                CompletedUtc = NULL
            WHERE Id = (
                SELECT Id
                FROM QueueMessages
                WHERE PayloadPresent = 1
                  AND (State = 'Queued' OR (State = 'RetryScheduled' AND NextAttemptUtc <= $nowUtc))
                ORDER BY COALESCE(NextAttemptUtc, ReceivedUtc), ReceivedUtc, Id
                LIMIT 1
            )
            RETURNING Id, DeviceId, EnvelopeFrom, ReceivedUtc, SizeBytes, SpoolFileName, State,
                      AttemptCount, NextAttemptUtc, LastAttemptUtc, CompletedUtc,
                      LastErrorCategory, LastErrorMessage, PayloadPresent;
            """;
        command.Parameters.AddWithValue("$nowUtc", FormatDate(nowUtc));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var row = ReadMessageRow(reader);
        reader.Close();
        return CreateMessage(connection, row);
    }

    public bool MarkDelivered(
        Guid messageId,
        DateTimeOffset completedUtc,
        CancellationToken cancellationToken = default)
    {
        QueueStateMachine.RequireTransition(QueueState.Delivering, QueueState.Delivered);
        return TransitionDelivering(
            messageId,
            QueueState.Delivered,
            completedUtc,
            nextAttemptUtc: null,
            errorCategory: null,
            errorMessage: null,
            cancellationToken);
    }

    public bool ScheduleRetry(
        Guid messageId,
        DateTimeOffset nextAttemptUtc,
        string errorCategory,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        QueueStateMachine.RequireTransition(QueueState.Delivering, QueueState.RetryScheduled);
        return TransitionDelivering(
            messageId,
            QueueState.RetryScheduled,
            completedUtc: null,
            nextAttemptUtc,
            errorCategory,
            errorMessage,
            cancellationToken);
    }

    public bool MarkPermanentFailure(
        Guid messageId,
        DateTimeOffset completedUtc,
        string errorCategory,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        QueueStateMachine.RequireTransition(QueueState.Delivering, QueueState.PermanentFailure);
        return TransitionDelivering(
            messageId,
            QueueState.PermanentFailure,
            completedUtc,
            nextAttemptUtc: null,
            errorCategory,
            errorMessage,
            cancellationToken);
    }

    public bool RecoverInterruptedClaim(
        Guid messageId,
        string errorCategory,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        QueueStateMachine.RequireTransition(QueueState.Delivering, QueueState.Queued);
        return TransitionDelivering(
            messageId,
            QueueState.Queued,
            completedUtc: null,
            nextAttemptUtc: null,
            errorCategory,
            errorMessage,
            cancellationToken);
    }

    public int RecoverAllDelivering(
        string errorCategory,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        QueueStateMachine.RequireTransition(QueueState.Delivering, QueueState.Queued);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);
        using var connection = OpenConnectionCore();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE QueueMessages
            SET State = 'Queued',
                NextAttemptUtc = NULL,
                CompletedUtc = NULL,
                LastErrorCategory = $category,
                LastErrorMessage = $message
            WHERE State = 'Delivering';
            """;
        command.Parameters.AddWithValue("$category", errorCategory);
        command.Parameters.AddWithValue("$message", errorMessage);
        return command.ExecuteNonQuery();
    }

    public bool MarkMissingPayload(
        Guid messageId,
        DateTimeOffset detectedUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);
        using var connection = OpenConnectionCore();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE QueueMessages
            SET State = CASE WHEN State = 'Delivered' THEN State ELSE 'PermanentFailure' END,
                PayloadPresent = 0,
                NextAttemptUtc = NULL,
                CompletedUtc = COALESCE(CompletedUtc, $detectedUtc),
                LastErrorCategory = CASE WHEN State = 'Delivered' THEN LastErrorCategory ELSE 'MissingSpool' END,
                LastErrorMessage = CASE WHEN State = 'Delivered' THEN LastErrorMessage
                    ELSE 'The accepted message payload is missing from local storage.' END
            WHERE Id = $id AND PayloadPresent = 1;
            """;
        command.Parameters.AddWithValue("$id", messageId.ToString("D"));
        command.Parameters.AddWithValue("$detectedUtc", FormatDate(detectedUtc));
        return command.ExecuteNonQuery() == 1;
    }

    public bool MarkPayloadDeleted(Guid messageId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);
        using var connection = OpenConnectionCore();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE QueueMessages SET PayloadPresent = 0 WHERE Id = $id AND State = 'Delivered';";
        command.Parameters.AddWithValue("$id", messageId.ToString("D"));
        return command.ExecuteNonQuery() == 1;
    }

    public QueueCapacityUsage GetQueueCapacityUsage(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);
        using var connection = OpenConnectionCore();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*), COALESCE(SUM(SizeBytes), 0)
            FROM QueueMessages
            WHERE PayloadPresent = 1;
            """;
        using var reader = command.ExecuteReader();
        _ = reader.Read();
        return new QueueCapacityUsage(reader.GetInt32(0), reader.GetInt64(1));
    }

    public bool HasSpoolFile(string spoolFileName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);
        using var connection = OpenConnectionCore();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM QueueMessages WHERE SpoolFileName = $fileName LIMIT 1;";
        command.Parameters.AddWithValue("$fileName", spoolFileName);
        return command.ExecuteScalar() is not null;
    }

    public QueueMetrics GetQueueMetrics(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);
        using var connection = OpenConnectionCore();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                SUM(CASE WHEN State = 'Queued' THEN 1 ELSE 0 END),
                SUM(CASE WHEN State = 'RetryScheduled' THEN 1 ELSE 0 END),
                SUM(CASE WHEN State = 'Delivering' THEN 1 ELSE 0 END),
                SUM(CASE WHEN State = 'PermanentFailure' THEN 1 ELSE 0 END),
                MIN(CASE WHEN State IN ('Queued', 'RetryScheduled', 'Delivering') THEN ReceivedUtc END),
                COALESCE(SUM(CASE WHEN PayloadPresent = 1 THEN SizeBytes ELSE 0 END), 0)
            FROM QueueMessages;
            """;
        using var reader = command.ExecuteReader();
        _ = reader.Read();
        return new QueueMetrics(
            reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            reader.IsDBNull(4) ? null : ParseDate(reader.GetString(4)),
            reader.GetInt64(5));
    }

    public bool IsUsable(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureInitialized(cancellationToken);
            using var connection = OpenConnectionCore();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public SqliteConnection OpenConnectionForDiagnostics()
    {
        EnsureInitialized();
        return OpenConnectionCore();
    }

    private static IReadOnlyList<string> LoadStrings(
        SqliteConnection connection,
        string table,
        string column,
        Guid deviceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM {table} WHERE DeviceId = $deviceId ORDER BY {column};";
        command.Parameters.AddWithValue("$deviceId", deviceId.ToString("D"));
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static IReadOnlyList<string> LoadRecipients(SqliteConnection connection, Guid messageId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Recipient FROM QueueRecipients WHERE MessageId = $messageId ORDER BY Ordinal;";
        command.Parameters.AddWithValue("$messageId", messageId.ToString("D"));
        using var reader = command.ExecuteReader();
        var recipients = new List<string>();
        while (reader.Read())
        {
            recipients.Add(reader.GetString(0));
        }

        return recipients;
    }

    private bool TransitionDelivering(
        Guid messageId,
        QueueState nextState,
        DateTimeOffset? completedUtc,
        DateTimeOffset? nextAttemptUtc,
        string? errorCategory,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized(cancellationToken);
        using var connection = OpenConnectionCore();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE QueueMessages
            SET State = $nextState,
                NextAttemptUtc = $nextAttemptUtc,
                CompletedUtc = $completedUtc,
                LastErrorCategory = $errorCategory,
                LastErrorMessage = $errorMessage
            WHERE Id = $id AND State = 'Delivering';
            """;
        command.Parameters.AddWithValue("$nextState", nextState.ToString());
        command.Parameters.AddWithValue("$nextAttemptUtc", FormatNullableDate(nextAttemptUtc));
        command.Parameters.AddWithValue("$completedUtc", FormatNullableDate(completedUtc));
        command.Parameters.AddWithValue("$errorCategory", (object?)errorCategory ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", (object?)errorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", messageId.ToString("D"));
        return command.ExecuteNonQuery() == 1;
    }

    private static MessageRow ReadMessageRow(SqliteDataReader reader)
    {
        return new MessageRow(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            ParseDate(reader.GetString(3)),
            reader.GetInt64(4),
            reader.GetString(5),
            Enum.Parse<QueueState>(reader.GetString(6), ignoreCase: false),
            reader.GetInt32(7),
            reader.IsDBNull(8) ? null : ParseDate(reader.GetString(8)),
            reader.IsDBNull(9) ? null : ParseDate(reader.GetString(9)),
            reader.IsDBNull(10) ? null : ParseDate(reader.GetString(10)),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.GetBoolean(13));
    }

    private static QueuedMessage CreateMessage(SqliteConnection connection, MessageRow row)
    {
        return new QueuedMessage(
            row.Id,
            row.DeviceId,
            row.EnvelopeFrom,
            LoadRecipients(connection, row.Id),
            row.ReceivedUtc,
            row.SizeBytes,
            row.SpoolFileName,
            row.State,
            row.AttemptCount,
            row.NextAttemptUtc,
            row.LastAttemptUtc,
            row.CompletedUtc,
            row.LastErrorCategory,
            row.LastErrorMessage,
            row.PayloadPresent);
    }

    private SqliteConnection OpenConnectionCore()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                PRAGMA foreign_keys = ON;
                PRAGMA synchronous = FULL;
                PRAGMA busy_timeout = 5000;
                """;
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void EnsureInitialized(CancellationToken cancellationToken = default)
    {
        if (!_initialized)
        {
            Initialize(cancellationToken);
        }
    }

    private static string FormatDate(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static void ValidateExpectedRevision(long expectedRevision)
    {
        if (expectedRevision < 0 || expectedRevision == long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }
    }

    private static object FormatNullableDate(DateTimeOffset? value)
    {
        return value is null ? DBNull.Value : FormatDate(value.Value);
    }

    private static DateTimeOffset ParseDate(string value)
    {
        return DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private sealed record DeviceRow(
        Guid Id,
        string Name,
        string? Description,
        bool Enabled,
        DeviceAuthenticationMode AuthenticationMode,
        string? SmtpUsername,
        string? PasswordVerifier,
        DateTimeOffset CreatedUtc,
        long Revision);

    private sealed record MessageRow(
        Guid Id,
        Guid DeviceId,
        string EnvelopeFrom,
        DateTimeOffset ReceivedUtc,
        long SizeBytes,
        string SpoolFileName,
        QueueState State,
        int AttemptCount,
        DateTimeOffset? NextAttemptUtc,
        DateTimeOffset? LastAttemptUtc,
        DateTimeOffset? CompletedUtc,
        string? LastErrorCategory,
        string? LastErrorMessage,
        bool PayloadPresent);

    private static void EnsureQueueSchema(SqliteConnection connection)
    {
        var schemaVersion = GetSchemaVersion(connection);
        if (schemaVersion > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Database schema version {schemaVersion} is newer than supported version {CurrentSchemaVersion}.");
        }

        if (!TableExists(connection, "QueueMessages"))
        {
            Execute(connection, CurrentQueueSchema);
            Execute(connection, CurrentIdentitySchema);
            Execute(connection, CurrentSetupSchema);
            SetCurrentSchemaVersion(connection);
            return;
        }

        if (!ColumnExists(connection, "QueueMessages", "AttemptCount"))
        {
            MigrateMilestone1Queue(connection);
        }

        Execute(connection, CurrentQueueSchema);
        Execute(connection, CurrentIdentitySchema);
        if (schemaVersion < 5 || !ColumnExists(connection, "Devices", "Description"))
        {
            MigrateDeviceDescriptionToSchemaV5(connection);
            schemaVersion = 5;
        }
        if (schemaVersion < 6 || !ColumnExists(connection, "Devices", "Revision"))
        {
            MigrateDeviceRevisionToSchemaV6(connection);
            schemaVersion = 6;
        }
        if (!ColumnExists(connection, "MicrosoftIdentityConfiguration", "AuthorizedSender"))
        {
            Execute(connection, "ALTER TABLE MicrosoftIdentityConfiguration ADD COLUMN AuthorizedSender TEXT;");
        }
        Execute(connection, CurrentSetupSchema);
        if (schemaVersion < 7 ||
            !ColumnExists(connection, "MicrosoftIdentityConfiguration", "ActivationId") ||
            !ColumnExists(connection, "MicrosoftSetupState", "ActivationId"))
        {
            MigrateMicrosoftActivationToSchemaV7(connection);
            schemaVersion = 7;
        }
        if (schemaVersion < 8 || !ColumnExists(connection, "MicrosoftSetupState", "Revision"))
        {
            MigrateMicrosoftSetupRevisionToSchemaV8(connection);
            schemaVersion = 8;
        }
        if (schemaVersion < 9 || !ColumnExists(connection, "MicrosoftSetupState", "Lifecycle"))
        {
            MigrateMicrosoftSetupLifecycleToSchemaV9(connection);
        }
        SetCurrentSchemaVersion(connection);
    }

    internal static void MigrateDeviceDescriptionToSchemaV5(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var addColumn = !ColumnExists(connection, "Devices", "Description");
        using var transaction = connection.BeginTransaction();
        if (addColumn)
        {
            Execute(connection, "ALTER TABLE Devices ADD COLUMN Description TEXT;", transaction);
        }

        Execute(connection, "PRAGMA user_version = 5;", transaction);
        transaction.Commit();
    }

    internal static void MigrateDeviceRevisionToSchemaV6(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var addColumn = !ColumnExists(connection, "Devices", "Revision");
        using var transaction = connection.BeginTransaction();
        if (addColumn)
        {
            Execute(
                connection,
                "ALTER TABLE Devices ADD COLUMN Revision INTEGER NOT NULL DEFAULT 0 CHECK (Revision >= 0);",
                transaction);
        }

        Execute(connection, "PRAGMA user_version = 6;", transaction);
        transaction.Commit();
    }

    internal static void MigrateMicrosoftActivationToSchemaV7(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var transaction = connection.BeginTransaction();
        if (!ColumnExists(connection, "MicrosoftIdentityConfiguration", "ActivationId"))
        {
            Execute(connection, "ALTER TABLE MicrosoftIdentityConfiguration ADD COLUMN ActivationId TEXT;", transaction);
        }

        if (!ColumnExists(connection, "MicrosoftSetupState", "ActivationId"))
        {
            Execute(connection, "ALTER TABLE MicrosoftSetupState ADD COLUMN ActivationId TEXT;", transaction);
        }

        var activeActivationId = Guid.NewGuid().ToString("D");
        using (var identity = connection.CreateCommand())
        {
            identity.Transaction = transaction;
            identity.CommandText =
                "UPDATE MicrosoftIdentityConfiguration SET ActivationId = $activationId WHERE ActivationId IS NULL;";
            identity.Parameters.AddWithValue("$activationId", activeActivationId);
            identity.ExecuteNonQuery();
        }

        using (var setup = connection.CreateCommand())
        {
            setup.Transaction = transaction;
            setup.CommandText =
                """
                UPDATE MicrosoftSetupState
                SET ActivationId = CASE
                    WHEN Step = 'Complete' AND EXISTS (SELECT 1 FROM MicrosoftIdentityConfiguration)
                        THEN $activeActivationId
                    ELSE $candidateActivationId
                END
                WHERE ActivationId IS NULL;
                """;
            setup.Parameters.AddWithValue("$activeActivationId", activeActivationId);
            setup.Parameters.AddWithValue("$candidateActivationId", Guid.NewGuid().ToString("D"));
            setup.ExecuteNonQuery();
        }

        Execute(connection, "PRAGMA user_version = 7;", transaction);
        transaction.Commit();
    }

    internal static void MigrateMicrosoftSetupRevisionToSchemaV8(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var transaction = connection.BeginTransaction();
        if (!ColumnExists(connection, "MicrosoftSetupState", "Revision"))
        {
            Execute(
                connection,
                "ALTER TABLE MicrosoftSetupState ADD COLUMN Revision INTEGER NOT NULL DEFAULT 0 CHECK (Revision >= 0);",
                transaction);
        }

        Execute(connection, "PRAGMA user_version = 8;", transaction);
        transaction.Commit();
    }

    internal static void MigrateMicrosoftSetupLifecycleToSchemaV9(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var transaction = connection.BeginTransaction();
        if (!ColumnExists(connection, "MicrosoftSetupState", "Lifecycle"))
        {
            Execute(
                connection,
                "ALTER TABLE MicrosoftSetupState ADD COLUMN Lifecycle TEXT NOT NULL DEFAULT 'Active' CHECK (Lifecycle IN ('Active', 'Cancelled', 'Activated'));",
                transaction);
        }

        Execute(
            connection,
            """
            UPDATE MicrosoftSetupState
            SET Lifecycle = 'Activated'
            WHERE Step = 'Complete'
              AND ActivationId IS NOT NULL
              AND EXISTS (
                  SELECT 1
                  FROM MicrosoftIdentityConfiguration active
                  WHERE active.Id = 1 AND active.ActivationId = MicrosoftSetupState.ActivationId);
            """,
            transaction);
        Execute(connection, "PRAGMA user_version = 9;", transaction);
        transaction.Commit();
    }

    private static void SaveMicrosoftSetupState(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        MicrosoftSetupState state)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO MicrosoftSetupState
                (Id, Step, Mode, CertificateThumbprint, CertificateStoreLocation,
                 TenantId, ClientId, ServicePrincipalObjectId, SenderMailbox,
                 EntraResultValidated, ExchangeResultValidated, IdentityValidated,
                 ExchangeValidated, TestMessageAccepted, UpdatedUtc, ActivationId, Revision, Lifecycle)
            VALUES
                (1, $step, $mode, $thumbprint, $storeLocation,
                 $tenantId, $clientId, $servicePrincipalObjectId, $senderMailbox,
                 $entraValidated, $exchangeResultValidated, $identityValidated,
                 $exchangeValidated, $testAccepted, $updatedUtc, $activationId, $revision, $lifecycle)
            ON CONFLICT(Id) DO UPDATE SET
                Step = excluded.Step,
                Mode = excluded.Mode,
                CertificateThumbprint = excluded.CertificateThumbprint,
                CertificateStoreLocation = excluded.CertificateStoreLocation,
                TenantId = excluded.TenantId,
                ClientId = excluded.ClientId,
                ServicePrincipalObjectId = excluded.ServicePrincipalObjectId,
                SenderMailbox = excluded.SenderMailbox,
                EntraResultValidated = excluded.EntraResultValidated,
                ExchangeResultValidated = excluded.ExchangeResultValidated,
                IdentityValidated = excluded.IdentityValidated,
                ExchangeValidated = excluded.ExchangeValidated,
                TestMessageAccepted = excluded.TestMessageAccepted,
                UpdatedUtc = excluded.UpdatedUtc,
                ActivationId = excluded.ActivationId,
                Revision = excluded.Revision,
                Lifecycle = excluded.Lifecycle;
            """;
        command.Parameters.AddWithValue("$step", state.Step.ToString());
        command.Parameters.AddWithValue("$mode", state.Mode.ToString());
        command.Parameters.AddWithValue("$thumbprint", (object?)state.Certificate?.Thumbprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$storeLocation", (object?)state.Certificate?.StoreLocation.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$tenantId", state.TenantId is null ? DBNull.Value : state.TenantId.Value.ToString("D"));
        command.Parameters.AddWithValue("$clientId", state.ClientId is null ? DBNull.Value : state.ClientId.Value.ToString("D"));
        command.Parameters.AddWithValue("$servicePrincipalObjectId", state.ServicePrincipalObjectId is null
            ? DBNull.Value
            : state.ServicePrincipalObjectId.Value.ToString("D"));
        command.Parameters.AddWithValue("$senderMailbox", (object?)state.SenderMailbox ?? DBNull.Value);
        command.Parameters.AddWithValue("$entraValidated", state.EntraResultValidated ? 1 : 0);
        command.Parameters.AddWithValue("$exchangeResultValidated", state.ExchangeResultValidated ? 1 : 0);
        command.Parameters.AddWithValue("$identityValidated", state.IdentityValidated ? 1 : 0);
        command.Parameters.AddWithValue("$exchangeValidated", state.ExchangeValidated ? 1 : 0);
        command.Parameters.AddWithValue("$testAccepted", state.TestMessageAccepted ? 1 : 0);
        command.Parameters.AddWithValue("$updatedUtc", FormatDate(state.UpdatedUtc));
        command.Parameters.AddWithValue("$activationId", state.ActivationId is null
            ? DBNull.Value
            : state.ActivationId.Value.ToString("D"));
        command.Parameters.AddWithValue("$revision", state.Revision);
        command.Parameters.AddWithValue("$lifecycle", state.Lifecycle.ToString());
        command.ExecuteNonQuery();
    }

    private static void EnsureCandidateMatches(
        MicrosoftSetupState? current,
        NativeMicrosoftCandidateIdentity expected)
    {
        if (current is null || current.Lifecycle != MicrosoftSetupCandidateLifecycle.Active ||
            current.ActivationId != expected.ActivationId ||
            current.Revision != expected.Revision || current.Mode != expected.Mode ||
            !string.Equals(current.SenderMailbox, expected.SenderMailbox, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                MicrosoftSetupCandidateFingerprint.Create(current),
                expected.ConfigurationFingerprint,
                StringComparison.Ordinal))
        {
            throw new MicrosoftSetupConcurrencyException();
        }
    }

    private static void UpsertActiveMicrosoftConfiguration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MicrosoftIdentityConfiguration configuration,
        string authorizedSender,
        Guid activationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO MicrosoftIdentityConfiguration
                (Id, TenantId, ClientId, CertificateThumbprint, CertificateStoreName,
                 CertificateStoreLocation, AuthorizedSender, ActivationId)
            VALUES
                (1, $tenantId, $clientId, $thumbprint, $storeName, $storeLocation, $authorizedSender, $activationId)
            ON CONFLICT(Id) DO UPDATE SET
                TenantId = excluded.TenantId,
                ClientId = excluded.ClientId,
                CertificateThumbprint = excluded.CertificateThumbprint,
                CertificateStoreName = excluded.CertificateStoreName,
                CertificateStoreLocation = excluded.CertificateStoreLocation,
                AuthorizedSender = excluded.AuthorizedSender,
                ActivationId = excluded.ActivationId;
            """;
        command.Parameters.AddWithValue("$tenantId", configuration.TenantId.ToString("D"));
        command.Parameters.AddWithValue("$clientId", configuration.ClientId.ToString("D"));
        command.Parameters.AddWithValue("$thumbprint", configuration.Certificate.Thumbprint);
        command.Parameters.AddWithValue("$storeName", configuration.Certificate.StoreName);
        command.Parameters.AddWithValue("$storeLocation", configuration.Certificate.StoreLocation.ToString());
        command.Parameters.AddWithValue("$authorizedSender", authorizedSender);
        command.Parameters.AddWithValue("$activationId", activationId.ToString("D"));
        command.ExecuteNonQuery();
    }

    private static int GetSchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void SetCurrentSchemaVersion(SqliteConnection connection)
    {
        Execute(connection, $"PRAGMA user_version = {CurrentSchemaVersion};");
    }

    private static void MigrateMilestone1Queue(SqliteConnection connection)
    {
        Execute(connection, "PRAGMA foreign_keys = OFF;");
        try
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, "DROP INDEX IF EXISTS IX_QueueMessages_State_ReceivedUtc;", transaction);
            Execute(connection, "ALTER TABLE QueueRecipients RENAME TO QueueRecipients_Milestone1;", transaction);
            Execute(connection, "ALTER TABLE QueueMessages RENAME TO QueueMessages_Milestone1;", transaction);
            Execute(connection, CurrentQueueSchema, transaction);
            Execute(
                connection,
                """
                INSERT INTO QueueMessages
                    (Id, DeviceId, EnvelopeFrom, ReceivedUtc, SizeBytes, SpoolFileName, State,
                     RecipientCount, AttemptCount, PayloadPresent)
                SELECT Id, DeviceId, EnvelopeFrom, ReceivedUtc, SizeBytes, SpoolFileName, 'Queued',
                       RecipientCount, 0, 1
                FROM QueueMessages_Milestone1;

                INSERT INTO QueueRecipients (MessageId, Ordinal, Recipient)
                SELECT MessageId, Ordinal, Recipient
                FROM QueueRecipients_Milestone1;

                DROP TABLE QueueRecipients_Milestone1;
                DROP TABLE QueueMessages_Milestone1;
                """,
                transaction);
            transaction.Commit();
        }
        finally
        {
            Execute(connection, "PRAGMA foreign_keys = ON;");
        }
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

    private static void Execute(
        SqliteConnection connection,
        string commandText,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private const string BaseSchema =
        """
        CREATE TABLE IF NOT EXISTS Devices (
            Id TEXT PRIMARY KEY,
            Name TEXT NOT NULL,
            Description TEXT,
            Enabled INTEGER NOT NULL CHECK (Enabled IN (0, 1)),
            AuthenticationMode TEXT NOT NULL CHECK (AuthenticationMode IN ('Authenticated', 'Legacy')),
            SmtpUsername TEXT COLLATE NOCASE UNIQUE,
            PasswordVerifier TEXT,
            CreatedUtc TEXT NOT NULL,
            Revision INTEGER NOT NULL DEFAULT 0 CHECK (Revision >= 0),
            CHECK (
                (AuthenticationMode = 'Authenticated' AND SmtpUsername IS NOT NULL AND PasswordVerifier IS NOT NULL)
                OR
                (AuthenticationMode = 'Legacy' AND SmtpUsername IS NULL AND PasswordVerifier IS NULL)
            )
        );

        CREATE TABLE IF NOT EXISTS DeviceAllowedNetworks (
            DeviceId TEXT NOT NULL REFERENCES Devices(Id) ON DELETE CASCADE,
            Network TEXT NOT NULL,
            PRIMARY KEY (DeviceId, Network)
        );

        CREATE TABLE IF NOT EXISTS DeviceAllowedSenders (
            DeviceId TEXT NOT NULL REFERENCES Devices(Id) ON DELETE CASCADE,
            Sender TEXT NOT NULL COLLATE NOCASE,
            PRIMARY KEY (DeviceId, Sender)
        );
        """;

    private const string CurrentQueueSchema =
        """
        CREATE TABLE IF NOT EXISTS QueueMessages (
            Id TEXT PRIMARY KEY,
            DeviceId TEXT NOT NULL REFERENCES Devices(Id),
            EnvelopeFrom TEXT NOT NULL,
            ReceivedUtc TEXT NOT NULL,
            SizeBytes INTEGER NOT NULL CHECK (SizeBytes >= 0),
            SpoolFileName TEXT NOT NULL UNIQUE,
            State TEXT NOT NULL CHECK (State IN
                ('Queued', 'Delivering', 'RetryScheduled', 'Delivered', 'PermanentFailure')),
            RecipientCount INTEGER NOT NULL CHECK (RecipientCount > 0),
            AttemptCount INTEGER NOT NULL DEFAULT 0 CHECK (AttemptCount >= 0),
            NextAttemptUtc TEXT,
            LastAttemptUtc TEXT,
            CompletedUtc TEXT,
            LastErrorCategory TEXT,
            LastErrorMessage TEXT,
            PayloadPresent INTEGER NOT NULL DEFAULT 1 CHECK (PayloadPresent IN (0, 1))
        );

        CREATE TABLE IF NOT EXISTS QueueRecipients (
            MessageId TEXT NOT NULL REFERENCES QueueMessages(Id) ON DELETE CASCADE,
            Ordinal INTEGER NOT NULL CHECK (Ordinal >= 0),
            Recipient TEXT NOT NULL,
            PRIMARY KEY (MessageId, Ordinal)
        );

        CREATE INDEX IF NOT EXISTS IX_QueueMessages_State_NextAttempt_ReceivedUtc
            ON QueueMessages(State, NextAttemptUtc, ReceivedUtc);
        """;

    private const string CurrentIdentitySchema =
        """
        CREATE TABLE IF NOT EXISTS MicrosoftIdentityConfiguration (
            Id INTEGER PRIMARY KEY CHECK (Id = 1),
            TenantId TEXT NOT NULL,
            ClientId TEXT NOT NULL,
            CertificateThumbprint TEXT NOT NULL,
            CertificateStoreName TEXT NOT NULL CHECK (CertificateStoreName = 'My'),
            CertificateStoreLocation TEXT NOT NULL CHECK (CertificateStoreLocation IN ('LocalMachine', 'CurrentUser')),
            AuthorizedSender TEXT,
            ActivationId TEXT NOT NULL
        );
        """;

    private const string CurrentSetupSchema =
        """
        CREATE TABLE IF NOT EXISTS MicrosoftSetupState (
            Id INTEGER PRIMARY KEY CHECK (Id = 1),
            Step TEXT NOT NULL,
            Mode TEXT NOT NULL,
            CertificateThumbprint TEXT,
            CertificateStoreLocation TEXT,
            TenantId TEXT,
            ClientId TEXT,
            ServicePrincipalObjectId TEXT,
            SenderMailbox TEXT,
            EntraResultValidated INTEGER NOT NULL CHECK (EntraResultValidated IN (0, 1)),
            ExchangeResultValidated INTEGER NOT NULL CHECK (ExchangeResultValidated IN (0, 1)),
            IdentityValidated INTEGER NOT NULL CHECK (IdentityValidated IN (0, 1)),
            ExchangeValidated INTEGER NOT NULL CHECK (ExchangeValidated IN (0, 1)),
            TestMessageAccepted INTEGER NOT NULL CHECK (TestMessageAccepted IN (0, 1)),
            UpdatedUtc TEXT NOT NULL,
            ActivationId TEXT,
            Revision INTEGER NOT NULL DEFAULT 0 CHECK (Revision >= 0),
            Lifecycle TEXT NOT NULL DEFAULT 'Active' CHECK (Lifecycle IN ('Active', 'Cancelled', 'Activated')),
            CHECK (
                (CertificateThumbprint IS NULL AND CertificateStoreLocation IS NULL)
                OR
                (CertificateThumbprint IS NOT NULL AND CertificateStoreLocation IN ('LocalMachine', 'CurrentUser'))
            )
        );
        """;
}

public sealed class MicrosoftSetupConcurrencyException : InvalidOperationException
{
    public MicrosoftSetupConcurrencyException()
        : base("The Microsoft setup candidate changed. Review the current setup state and try again.")
    {
    }
}
