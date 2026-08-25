// SPDX-License-Identifier: MPL-2.0

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using RelayBridge.Core.Diagnostics;

namespace RelayBridge.Host.Diagnostics;

public sealed record SupportBundle(string FileName, byte[] Content);

public sealed class SupportBundleService
{
    public const int BundleSchemaVersion = 1;
    public const int MaximumBundleBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly HashSet<string> AllowedSetupCodes = new(StringComparer.Ordinal)
    {
        "UserCancelled",
        "MicrosoftProvisioningFailed",
        "CloudObjectConflict",
        "EntraConnectionFailed",
        "EntraApplicationDiscoveryFailed",
        "EntraApplicationCreateFailed",
        "EntraServicePrincipalCreateFailed",
        "EntraCertificateCredentialFailed",
        "EntraApplicationVerificationFailed",
        "ExchangeWamConsoleUnavailable",
        "SessionTimeout",
    };
    private readonly RelayDiagnosticsService _diagnostics;
    private readonly TimeProvider _timeProvider;

    public SupportBundleService(RelayDiagnosticsService diagnostics, TimeProvider timeProvider)
    {
        _diagnostics = diagnostics;
        _timeProvider = timeProvider;
    }

    public SupportBundle Create(CancellationToken cancellationToken = default)
    {
        var snapshot = _diagnostics.GetSnapshot(cancellationToken);
        var generatedUtc = _timeProvider.GetUtcNow();
        var entries = new[]
        {
            "README.txt",
            "manifest.json",
            "runtime.json",
            "smtp.json",
            "queue.json",
            "microsoft.json",
            "certificate.json",
            "setup.json",
            "storage.json",
            "connectivity.json",
            "security.json",
        };

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteText(archive, "README.txt", Readme);
            WriteJson(archive, "manifest.json", new
            {
                bundleSchemaVersion = BundleSchemaVersion,
                generatedUtc,
                entries,
            });
            WriteJson(archive, "runtime.json", new
            {
                evidence = Export(snapshot.Runtime.Evidence),
                snapshot.Runtime.Version,
                snapshot.Runtime.InformationalVersion,
                uptimeSeconds = (long)Math.Max(0, snapshot.Runtime.Uptime.TotalSeconds),
                snapshot.Runtime.OperatingSystem,
                snapshot.Runtime.DotNetRuntime,
                snapshot.Runtime.HostingMode,
                snapshot.Runtime.ManagementBinding,
            });
            WriteJson(archive, "smtp.json", new
            {
                evidence = Export(snapshot.Smtp.Evidence),
                snapshot.Smtp.Enabled,
                snapshot.Smtp.Listening,
                snapshot.Smtp.BoundAddress,
                snapshot.Smtp.BoundPort,
                snapshot.Smtp.IntakeMode,
                snapshot.Smtp.EnabledDeviceCount,
                snapshot.Smtp.LastAcceptedMessageUtc,
                snapshot.Smtp.InboundStartTls,
                snapshot.Smtp.CleartextAuthenticationBoundary,
            });
            WriteJson(archive, "queue.json", new
            {
                evidence = Export(snapshot.Queue.Evidence),
                snapshot.Queue.ActiveCount,
                snapshot.Queue.ReadyCount,
                snapshot.Queue.RetryingCount,
                snapshot.Queue.DeliveringCount,
                snapshot.Queue.PermanentFailureCount,
                snapshot.Queue.OldestQueuedUtc,
                snapshot.Queue.NextRetryUtc,
                snapshot.Queue.WorkerExpected,
                snapshot.Queue.WorkerRunning,
            });
            WriteJson(archive, "microsoft.json", new
            {
                evidence = Export(snapshot.Microsoft.Evidence),
                snapshot.Microsoft.Configured,
                snapshot.Microsoft.ActiveConfigurationExists,
                snapshot.Microsoft.Readiness,
                snapshot.Microsoft.LastSuccessfulVerificationUtc,
                snapshot.Microsoft.LastCompletedVerificationUtc,
                snapshot.Microsoft.LastActivationUtc,
                snapshot.Microsoft.ActivationIdPresent,
            });
            WriteJson(archive, "certificate.json", new
            {
                evidence = Export(snapshot.Certificate.Evidence),
                snapshot.Certificate.Configured,
                snapshot.Certificate.Present,
                snapshot.Certificate.PrivateKeyAccessible,
                snapshot.Certificate.ValidFromUtc,
                snapshot.Certificate.ExpiresUtc,
                snapshot.Certificate.RemainingDays,
                snapshot.Certificate.Expired,
            });
            WriteJson(archive, "setup.json", new
            {
                evidence = Export(snapshot.Setup.Evidence),
                snapshot.Setup.Stage,
                snapshot.Setup.Category,
                safeCode = snapshot.Setup.SafeCode is not null && AllowedSetupCodes.Contains(snapshot.Setup.SafeCode)
                    ? snapshot.Setup.SafeCode
                    : null,
                snapshot.Setup.HttpStatusCode,
            });
            WriteJson(archive, "storage.json", new
            {
                evidence = Export(snapshot.Storage.Evidence),
                snapshot.Storage.DatabaseAccessible,
                snapshot.Storage.StorageDirectoryAccessible,
                snapshot.Storage.SchemaVersion,
                snapshot.Storage.FreeDiskBytes,
                quickCheck = Export(snapshot.Storage.QuickCheck),
            });
            WriteJson(archive, "connectivity.json", new
            {
                evidence = Export(snapshot.Connectivity.Evidence),
                stage = snapshot.Connectivity.Stage.ToString(),
                snapshot.Connectivity.Succeeded,
                elapsedMilliseconds = snapshot.Connectivity.Elapsed is null
                    ? null
                    : (long?)Math.Max(0, snapshot.Connectivity.Elapsed.Value.TotalMilliseconds),
            });
            WriteJson(archive, "security.json", new
            {
                evidence = Export(snapshot.Security.Evidence),
                snapshot.Security.ManagementLoopbackOnly,
                snapshot.Security.AuthenticatedCleartextSmtp,
                snapshot.Security.InboundStartTls,
                snapshot.Security.PrivateMicrosoftTooling,
                snapshot.Security.CertificatePrivateKey,
                snapshot.Security.ProvisioningScratch,
            });
        }

        var content = output.ToArray();
        if (content.Length > MaximumBundleBytes)
        {
            throw new InvalidOperationException("The support bundle exceeded its fixed size limit.");
        }

        return new SupportBundle(
            $"relaybridge-diagnostics-{generatedUtc:yyyyMMdd-HHmmss'Z'}.zip",
            content);
    }

    private static object Export(DiagnosticEvidence evidence) => new
    {
        status = evidence.Status.ToString(),
        observedUtc = evidence.ObservedUtc,
        source = evidence.Source.ToString(),
        evidence.Summary,
    };

    private static void WriteJson<T>(ZipArchive archive, string name, T value)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, value, JsonOptions);
    }

    private static void WriteText(ZipArchive archive, string name, string value)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(
            entry.Open(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            leaveOpen: false);
        writer.Write(value);
    }

    private const string Readme =
        """
        RelayBridge diagnostics bundle

        This bundle was generated locally by RelayBridge. RelayBridge did not upload or transmit it.
        Message content, mailbox addresses, credentials, tokens, private keys, raw configuration,
        databases, and logs are intentionally excluded.

        The bundle still contains software, runtime, local-listener, storage-capacity, and network
        diagnostic metadata. Review it before sharing it outside your organization.
        """;
}
