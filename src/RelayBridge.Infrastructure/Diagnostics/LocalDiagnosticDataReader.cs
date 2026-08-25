// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using Microsoft.Data.Sqlite;
using RelayBridge.Core.Diagnostics;
using RelayBridge.Core.Microsoft;
using RelayBridge.Infrastructure.Queue;
using RelayBridge.Infrastructure.Storage;

namespace RelayBridge.Infrastructure.Diagnostics;

public sealed record LocalQueueDiagnosticFacts(
    QueueMetrics Metrics,
    DateTimeOffset? NextRetryUtc,
    DateTimeOffset? LastAcceptedUtc);

public sealed record LocalStorageDiagnosticFacts(
    bool DatabaseAccessible,
    bool StorageDirectoryAccessible,
    int? SchemaVersion,
    long? FreeDiskBytes);

public sealed class LocalDiagnosticDataReader
{
    private readonly RelayDatabase _database;
    private readonly QueueCapacityManager _capacity;
    private readonly TimeProvider _timeProvider;

    public LocalDiagnosticDataReader(
        RelayDatabase database,
        QueueCapacityManager capacity,
        TimeProvider timeProvider)
    {
        _database = database;
        _capacity = capacity;
        _timeProvider = timeProvider;
    }

    public LocalQueueDiagnosticFacts ReadQueue(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var metrics = _capacity.GetMetrics(cancellationToken);
        using var connection = _database.OpenConnectionForDiagnostics();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                MIN(CASE WHEN State = 'RetryScheduled' THEN NextAttemptUtc END),
                MAX(ReceivedUtc)
            FROM QueueMessages;
            """;
        using var reader = command.ExecuteReader();
        _ = reader.Read();
        return new LocalQueueDiagnosticFacts(
            metrics,
            reader.IsDBNull(0) ? null : ParseDate(reader.GetString(0)),
            reader.IsDBNull(1) ? null : ParseDate(reader.GetString(1)));
    }

    public LocalStorageDiagnosticFacts ReadStorage(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var connection = _database.OpenConnectionForDiagnostics();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            var schemaVersion = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            var directoryAccessible = Directory.Exists(_database.DataDirectory);
            long? freeDiskBytes = null;
            if (directoryAccessible)
            {
                var root = Path.GetPathRoot(_database.DataDirectory);
                if (!string.IsNullOrWhiteSpace(root))
                {
                    freeDiskBytes = new DriveInfo(root).AvailableFreeSpace;
                }
            }

            return new LocalStorageDiagnosticFacts(
                true,
                directoryAccessible,
                schemaVersion,
                freeDiskBytes);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or
            UnauthorizedAccessException or ArgumentException)
        {
            return new LocalStorageDiagnosticFacts(false, false, null, null);
        }
    }

    public async Task<DiagnosticEvidence> RunQuickCheckAsync(
        CancellationToken cancellationToken = default)
    {
        var observedUtc = _timeProvider.GetUtcNow();
        try
        {
            using var connection = _database.OpenConnectionForDiagnostics();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            command.CommandTimeout = 10;
            using var cancellationRegistration = cancellationToken.Register(command.Cancel);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var resultCount = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                resultCount++;
                if (resultCount > 1 || !string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase))
                {
                    return new DiagnosticEvidence(
                        DiagnosticStatus.Attention,
                        observedUtc,
                        DiagnosticEvidenceSource.ActiveProbe,
                        "SQLite quick check reported a database integrity problem.");
                }
            }

            return new DiagnosticEvidence(
                resultCount == 1 ? DiagnosticStatus.Healthy : DiagnosticStatus.Unknown,
                observedUtc,
                DiagnosticEvidenceSource.ActiveProbe,
                resultCount == 1
                    ? "SQLite quick check completed successfully."
                    : "SQLite quick check returned no result.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            return new DiagnosticEvidence(
                DiagnosticStatus.Unavailable,
                observedUtc,
                DiagnosticEvidenceSource.ActiveProbe,
                "SQLite quick check could not be completed.");
        }
    }

    public DiagnosticEvidence ReadProvisioningScratch(bool nativeSetupConfigured)
    {
        var observedUtc = _timeProvider.GetUtcNow();
        if (!nativeSetupConfigured)
        {
            return new DiagnosticEvidence(
                DiagnosticStatus.NotConfigured,
                observedUtc,
                DiagnosticEvidenceSource.Configuration,
                "Native Microsoft setup is not configured.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return new DiagnosticEvidence(
                DiagnosticStatus.Unavailable,
                observedUtc,
                DiagnosticEvidenceSource.Runtime,
                "Protected provisioning scratch verification requires Windows.");
        }

        try
        {
            ProvisioningScratchDirectory.VerifyRoot(ProvisioningScratchDirectory.DefaultRoot);
            return new DiagnosticEvidence(
                DiagnosticStatus.Healthy,
                observedUtc,
                DiagnosticEvidenceSource.Runtime,
                "The protected provisioning scratch root passed its trust checks.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            TrustedWindowsPathException)
        {
            return new DiagnosticEvidence(
                DiagnosticStatus.Unavailable,
                observedUtc,
                DiagnosticEvidenceSource.Runtime,
                "The protected provisioning scratch root is missing or did not pass its trust checks.");
        }
    }

    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.ParseExact(
        value,
        "O",
        CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind);
}
