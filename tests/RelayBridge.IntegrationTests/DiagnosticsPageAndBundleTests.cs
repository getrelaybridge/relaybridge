// SPDX-License-Identifier: MPL-2.0

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RelayBridge.Core.Diagnostics;
using RelayBridge.Core.Microsoft;
using RelayBridge.Core.Queue;
using RelayBridge.Host.Diagnostics;
using RelayBridge.Infrastructure.Diagnostics;
using RelayBridge.Infrastructure.Microsoft;
using RelayBridge.Infrastructure.Storage;
using Xunit;

namespace RelayBridge.IntegrationTests;

public sealed class DiagnosticsPageAndBundleTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DiagnosticsPageAndBundleTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Diagnostics_route_actions_and_bundle_remain_on_the_management_app()
    {
        var dataDirectory = CreateDataDirectory();
        try
        {
            using var factory = CreateFactory(dataDirectory, useFakeConnectivity: true);
            using var client = factory.CreateClient();

            using var pageResponse = await client.GetAsync("/diagnostics");
            var page = await pageResponse.Content.ReadAsStringAsync();
            using var connectivity = await client.PostAsync("/diagnostics/connectivity", content: null);
            using var quickCheck = await client.PostAsync("/diagnostics/database-quick-check", content: null);
            using var bundle = await client.GetAsync("/diagnostics/support-bundle");

            Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
            Assert.Contains("<h1>Diagnostics</h1>", page, StringComparison.Ordinal);
            Assert.Contains("Inbound STARTTLS", page, StringComparison.Ordinal);
            Assert.Contains("Not currently available", page, StringComparison.Ordinal);
            Assert.Contains("Evidence:", page, StringComparison.Ordinal);
            Assert.DoesNotContain("tenant ID", page, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(HttpStatusCode.OK, connectivity.StatusCode);
            Assert.Contains("Complete", await connectivity.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK, quickCheck.StatusCode);
            Assert.Contains("Healthy", await quickCheck.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK, bundle.StatusCode);
            Assert.Equal(new MediaTypeHeaderValue("application/zip"), bundle.Content.Headers.ContentType);
            Assert.Matches(
                "relaybridge-diagnostics-[0-9]{8}-[0-9]{6}Z\\.zip",
                bundle.Content.Headers.ContentDisposition?.FileNameStar ?? string.Empty);

            using var hostileHostRequest = new HttpRequestMessage(HttpMethod.Get, "/diagnostics");
            hostileHostRequest.Headers.Host = "192.168.20.20";
            using var hostileHostResponse = await client.SendAsync(hostileHostRequest);
            Assert.Equal(HttpStatusCode.BadRequest, hostileHostResponse.StatusCode);
        }
        finally
        {
            Cleanup(dataDirectory);
        }
    }

    [Fact]
    public void Support_bundle_uses_fixed_allowlisted_entries_and_excludes_hostile_private_data()
    {
        var dataDirectory = CreateDataDirectory();
        try
        {
            using var factory = CreateFactory(dataDirectory, useFakeConnectivity: false);
            _ = factory.CreateClient();
            var database = factory.Services.GetRequiredService<RelayDatabase>();
            var device = factory.Services.GetRequiredService<DeviceService>().ProvisionAuthenticatedDevice(
                "SUBJECT-MARKER",
                "BODY-MARKER",
                ["127.0.0.1"],
                ["sender-secret@example.invalid"]);
            var messageId = Guid.CreateVersion7();
            var spoolName = $"{messageId:N}.eml";
            database.InsertQueuedMessage(new QueuedMessage(
                messageId,
                device.Device.Id,
                "sender-secret@example.invalid",
                ["recipient-secret@example.invalid"],
                DateTimeOffset.UtcNow,
                128,
                spoolName,
                QueueState.Queued));
            File.WriteAllText(
                database.GetPendingPath(spoolName),
                "Subject: SUBJECT-MARKER\r\n\r\nBODY-MARKER\n-----BEGIN PRIVATE KEY-----\nTOKEN-MARKER");
            factory.Services.GetRequiredService<NativeMicrosoftSetupRuntime>().Fail(
                NativeSetupFailureCategory.MicrosoftService,
                "TOKEN-MARKER raw stderr bearer eyJ-secret",
                "TOKEN-MARKER",
                "AUTH-CODE-MARKER",
                new NativeSetupSafeFailureDetails(
                    "TOKEN-MARKER",
                    "PASSWORD-MARKER",
                    "REFRESH-TOKEN-MARKER",
                    400));

            var bundle = factory.Services.GetRequiredService<SupportBundleService>().Create();

            Assert.True(bundle.Content.Length < SupportBundleService.MaximumBundleBytes);
            Assert.Matches("^relaybridge-diagnostics-[0-9]{8}-[0-9]{6}Z\\.zip$", bundle.FileName);
            using var archive = new ZipArchive(new MemoryStream(bundle.Content), ZipArchiveMode.Read);
            var expected = new[]
            {
                "README.txt", "manifest.json", "runtime.json", "smtp.json", "queue.json",
                "microsoft.json", "certificate.json", "setup.json", "storage.json",
                "connectivity.json", "security.json",
            };
            Assert.Equal(expected.Order(), archive.Entries.Select(entry => entry.FullName).Order());
            Assert.All(archive.Entries, entry =>
            {
                Assert.Equal(Path.GetFileName(entry.FullName), entry.FullName);
                Assert.DoesNotContain("..", entry.FullName, StringComparison.Ordinal);
                Assert.False(entry.FullName.EndsWith(".db", StringComparison.OrdinalIgnoreCase));
                Assert.False(entry.FullName.EndsWith(".log", StringComparison.OrdinalIgnoreCase));
                Assert.False(entry.FullName.EndsWith(".eml", StringComparison.OrdinalIgnoreCase));
            });

            var text = string.Join("\n", archive.Entries.Select(ReadEntry));
            using var manifest = JsonDocument.Parse(ReadEntry(archive.GetEntry("manifest.json")!));
            Assert.Equal(1, manifest.RootElement.GetProperty("bundleSchemaVersion").GetInt32());
            foreach (var marker in new[]
            {
                "SUBJECT-MARKER",
                "BODY-MARKER",
                "TOKEN-MARKER",
                "PASSWORD-MARKER",
                "REFRESH-TOKEN-MARKER",
                "AUTH-CODE-MARKER",
                "sender-secret@example.invalid",
                "recipient-secret@example.invalid",
                "BEGIN PRIVATE KEY",
                device.PlaintextPassword,
                device.Device.PasswordVerifier!,
            })
            {
                Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
            }
            Assert.DoesNotContain("SQLite format 3", text, StringComparison.Ordinal);
            Assert.DoesNotContain("EnvironmentVariables", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CommandLine", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(dataDirectory);
        }
    }

    [Fact]
    public void Snapshot_distinguishes_configuration_readiness_connectivity_and_actual_listener_state()
    {
        var dataDirectory = CreateDataDirectory();
        try
        {
            using var factory = CreateFactory(dataDirectory, useFakeConnectivity: false);
            _ = factory.CreateClient();

            var snapshot = factory.Services.GetRequiredService<RelayDiagnosticsService>().GetSnapshot();

            Assert.False(snapshot.Smtp.Enabled);
            Assert.False(snapshot.Smtp.Listening);
            Assert.Equal(DiagnosticEvidenceSource.Runtime, snapshot.Smtp.Evidence.Source);
            Assert.False(snapshot.Microsoft.Configured);
            Assert.Equal("Not configured", snapshot.Microsoft.Readiness);
            Assert.Equal(ConnectivityProbeStage.NotRun, snapshot.Connectivity.Stage);
            Assert.Equal(DiagnosticEvidenceSource.Runtime, snapshot.Connectivity.Evidence.Source);
            Assert.Equal(9, snapshot.Storage.SchemaVersion);
            Assert.True(snapshot.Security.ManagementLoopbackOnly);
            Assert.Equal("Not configured", snapshot.Security.AuthenticatedCleartextSmtp);
        }
        finally
        {
            Cleanup(dataDirectory);
        }
    }

    [Fact]
    public void Snapshot_projects_queue_aggregates_and_sanitized_setup_failure_without_message_data()
    {
        var dataDirectory = CreateDataDirectory();
        try
        {
            using var factory = CreateFactory(dataDirectory, useFakeConnectivity: false);
            _ = factory.CreateClient();
            var database = factory.Services.GetRequiredService<RelayDatabase>();
            var device = factory.Services.GetRequiredService<DeviceService>().AddLegacyDevice(
                "Diagnostic queue device",
                ["127.0.0.1"],
                ["private-sender@example.invalid"]);
            var now = DateTimeOffset.UtcNow;
            Insert(QueueState.Queued, now.AddMinutes(-20));
            Insert(QueueState.RetryScheduled, now.AddMinutes(-15), now.AddMinutes(5));
            Insert(QueueState.Delivering, now.AddMinutes(-10));
            Insert(QueueState.PermanentFailure, now.AddMinutes(-5));
            factory.Services.GetRequiredService<NativeMicrosoftSetupRuntime>().Fail(
                NativeSetupFailureCategory.MicrosoftService,
                "A fixed user-facing setup message.",
                "EntraApplicationCreateFailed",
                correlationId: null,
                safeFailureDetails: new NativeSetupSafeFailureDetails(
                    "Microsoft.PowerShell.Commands.WriteErrorException",
                    "WriteErrorException,New-EntraApplication",
                    "NotSpecified",
                    400));

            var snapshot = factory.Services.GetRequiredService<RelayDiagnosticsService>().GetSnapshot();

            Assert.Equal(3, snapshot.Queue.ActiveCount);
            Assert.Equal(1, snapshot.Queue.ReadyCount);
            Assert.Equal(1, snapshot.Queue.RetryingCount);
            Assert.Equal(1, snapshot.Queue.DeliveringCount);
            Assert.Equal(1, snapshot.Queue.PermanentFailureCount);
            Assert.Equal(DiagnosticStatus.Attention, snapshot.Queue.Evidence.Status);
            Assert.NotNull(snapshot.Queue.OldestQueuedUtc);
            Assert.NotNull(snapshot.Queue.NextRetryUtc);
            Assert.Equal(NativeSetupStage.WaitingForHelper.ToString(), snapshot.Setup.Stage);
            Assert.Equal(NativeSetupFailureCategory.MicrosoftService.ToString(), snapshot.Setup.Category);
            Assert.Equal("EntraApplicationCreateFailed", snapshot.Setup.SafeCode);
            Assert.Equal(400, snapshot.Setup.HttpStatusCode);
            Assert.Equal(DiagnosticEvidenceSource.Runtime, snapshot.Setup.Evidence.Source);

            void Insert(QueueState state, DateTimeOffset receivedUtc, DateTimeOffset? nextAttemptUtc = null)
            {
                var id = Guid.CreateVersion7();
                database.InsertQueuedMessage(new QueuedMessage(
                    id,
                    device.Id,
                    "private-sender@example.invalid",
                    ["private-recipient@example.invalid"],
                    receivedUtc,
                    10,
                    $"{id:N}.eml",
                    state,
                    NextAttemptUtc: nextAttemptUtc,
                    LastErrorCategory: state == QueueState.PermanentFailure ? "PermanentServerFailure" : null,
                    LastErrorMessage: state == QueueState.PermanentFailure ? "MESSAGE-BODY-MARKER" : null));
            }
        }
        finally
        {
            Cleanup(dataDirectory);
        }
    }

    private WebApplicationFactory<Program> CreateFactory(string dataDirectory, bool useFakeConnectivity)
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Storage:DataDirectory", dataDirectory);
            builder.UseSetting("Smtp:Enabled", "false");
            builder.UseSetting("Smtp:ServerName", "ENVIRONMENT-TOKEN-MARKER");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services =>
            {
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                if (useFakeConnectivity)
                {
                    services.RemoveAll<IExchangeConnectivityProbe>();
                    services.AddSingleton<IExchangeConnectivityProbe>(new SuccessfulConnectivityProbe());
                }
            });
        });
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string CreateDataDirectory() => Path.Combine(
        Path.GetTempPath(),
        "RelayBridge.Tests",
        Guid.NewGuid().ToString("N"));

    private static void Cleanup(string dataDirectory)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(dataDirectory))
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private sealed class SuccessfulConnectivityProbe : IExchangeConnectivityProbe
    {
        public Task<ConnectivityDiagnosticSnapshot> RunAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConnectivityDiagnosticSnapshot(
                new DiagnosticEvidence(
                    DiagnosticStatus.Healthy,
                    DateTimeOffset.UtcNow,
                    DiagnosticEvidenceSource.ActiveProbe,
                    "The deterministic connectivity probe passed."),
                ConnectivityProbeStage.Complete,
                true,
                TimeSpan.FromMilliseconds(10)));
    }
}
