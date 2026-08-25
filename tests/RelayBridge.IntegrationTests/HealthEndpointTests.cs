// SPDX-License-Identifier: MPL-2.0

using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelayBridge.Core.Microsoft;
using RelayBridge.Infrastructure.Storage;
using RelayBridge.Core.Queue;
using Xunit;

namespace RelayBridge.IntegrationTests;

public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("urls", "http://0.0.0.0:5080")]
    [InlineData("urls", "http://[::]:5080")]
    [InlineData("Kestrel:Endpoints:Hostile:Url", "http://192.168.10.20:5080")]
    [InlineData("Kestrel:Endpoints:Hostile:Url", "http://203.0.113.20:5080")]
    public void Host_rejects_non_loopback_management_overrides(string key, string value)
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting(key, value);
            builder.UseSetting("Smtp:Enabled", "false");
            builder.ConfigureLogging(logging => logging.ClearProviders());
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("loopback", FlattenMessages(exception), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("203.0.113.20")]
    public void Host_rejects_unsafe_cleartext_smtp_auth_bindings(string address)
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Smtp:ListenAddress", address);
            builder.UseSetting("Smtp:AllowCleartextAuthentication", "true");
            builder.ConfigureLogging(logging => logging.ClearProviders());
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Cleartext SMTP authentication", FlattenMessages(exception), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Host_validates_explicit_private_smtp_auth_configuration_without_exposing_management()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Smtp:Enabled", "false");
            builder.UseSetting("Smtp:ListenAddress", "192.168.10.20");
            builder.UseSetting("Smtp:AllowCleartextAuthentication", "true");
            builder.ConfigureLogging(logging => logging.ClearProviders());
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var management = factory.Services.GetRequiredService<RelayBridge.Host.Services.ManagementOptions>();
        Assert.Equal(5080, management.Port);
    }

    [Fact]
    public async Task Foundation_health_endpoint_returns_healthy()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var response = await client.GetAsync("/health", timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(timeout.Token));
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl.NoStore);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("[::1]")]
    public async Task Loopback_host_headers_are_accepted(string host)
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Host = host;

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("192.168.10.20")]
    [InlineData("example.com")]
    [InlineData("[fd00::20]")]
    public async Task Non_loopback_host_headers_are_rejected(string host)
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Host = host;

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Status_page_keeps_identity_and_exchange_delivery_readiness_distinct()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var response = await client.GetAsync("/", timeout.Token);
        var content = await response.Content.ReadAsStringAsync(timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Dashboard", content, StringComparison.Ordinal);
        Assert.Contains("Needs attention", content, StringComparison.Ordinal);
        Assert.Contains("SMTP listener is disabled", content, StringComparison.Ordinal);
        Assert.Contains("Microsoft 365", content, StringComparison.Ordinal);
        Assert.Contains("Not configured", content, StringComparison.Ordinal);
        Assert.Contains("Set up Microsoft 365", content, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/devices/add\">Add device", content, StringComparison.Ordinal);
        Assert.Contains("No devices yet", content, StringComparison.Ordinal);
        Assert.DoesNotContain("XOAUTH2", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RBAC", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Microsoft_setup_page_renders_guided_loopback_wizard()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var response = await client.GetAsync("/setup/microsoft", timeout.Token);
        var content = await response.Content.ReadAsStringAsync(timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Connect Microsoft 365", content, StringComparison.Ordinal);
        Assert.Contains("Set up a new RelayBridge application", content, StringComparison.Ordinal);
        Assert.Contains("Use an existing Microsoft application", content, StringComparison.Ordinal);
        Assert.DoesNotContain("XOAUTH2", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Device_management_pages_render_safe_accessible_configuration_without_secrets()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "RelayBridge.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var factory = _factory.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("Storage:DataDirectory", dataDirectory);
                builder.UseSetting("Smtp:Enabled", "false");
                builder.ConfigureLogging(logging => logging.ClearProviders());
                builder.ConfigureServices(services =>
                    services.AddDataProtection().UseEphemeralDataProtectionProvider());
            });
            var provisioned = factory.Services.GetRequiredService<DeviceService>().ProvisionAuthenticatedDevice(
                "Ricoh Reception",
                "Reception copier",
                ["127.0.0.1"],
                ["scanner@example.com"]);
            var reset = factory.Services.GetRequiredService<DeviceService>().ResetPassword(
                provisioned.Device.Id,
                provisioned.Device.Revision);
            using var client = factory.CreateClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            var dashboard = await client.GetStringAsync("/", timeout.Token);
            var devices = await client.GetStringAsync("/devices", timeout.Token);
            var add = await client.GetStringAsync("/devices/add", timeout.Token);
            var details = await client.GetStringAsync($"/devices/{provisioned.Device.Id:D}", timeout.Token);
            var settings = await client.GetStringAsync("/settings", timeout.Token);

            Assert.Contains("1 <span>configured</span>", dashboard, StringComparison.Ordinal);
            Assert.Contains("Ricoh Reception", devices, StringComparison.Ordinal);
            Assert.Contains("Search devices", devices, StringComparison.Ordinal);
            Assert.Contains("What should we call this device?", add, StringComparison.Ordinal);
            Assert.Contains("Last message accepted locally", details, StringComparison.Ordinal);
            Assert.Contains("SMTP username", details, StringComparison.Ordinal);
            Assert.Contains("listener is disabled", details, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Inbound STARTTLS", settings, StringComparison.Ordinal);
            Assert.Contains("Deployment configuration and restart required", settings, StringComparison.Ordinal);
            Assert.Contains("Available private interfaces", settings, StringComparison.Ordinal);
            foreach (var content in new[] { dashboard, devices, add, details, settings })
            {
                Assert.DoesNotContain(provisioned.PlaintextPassword, content, StringComparison.Ordinal);
                Assert.DoesNotContain(reset.PlaintextPassword, content, StringComparison.Ordinal);
                Assert.DoesNotContain(provisioned.Device.PasswordVerifier!, content, StringComparison.Ordinal);
                Assert.DoesNotContain("XOAUTH2", content, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("access token", content, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Smtp:Enabled", "false");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services =>
                services.AddDataProtection().UseEphemeralDataProtectionProvider());
        });
    }

    [Fact]
    public async Task Dashboard_reports_delivering_queue_work_instead_of_clear()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "RelayBridge.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var factory = _factory.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("Storage:DataDirectory", dataDirectory);
                builder.UseSetting("Smtp:Enabled", "false");
                builder.ConfigureLogging(logging => logging.ClearProviders());
                builder.ConfigureServices(services =>
                    services.AddDataProtection().UseEphemeralDataProtectionProvider());
            });
            var database = factory.Services.GetRequiredService<RelayDatabase>();
            var device = factory.Services.GetRequiredService<DeviceService>().AddLegacyDevice(
                "Queue device", ["127.0.0.1"], ["scanner@example.com"]);
            database.InsertQueuedMessage(new QueuedMessage(
                Guid.CreateVersion7(), device.Id, "scanner@example.com", ["recipient@example.net"],
                DateTimeOffset.UtcNow, 12, $"{Guid.NewGuid():N}.eml", QueueState.Delivering));
            using var client = factory.CreateClient();

            var dashboard = await client.GetStringAsync("/");

            Assert.Contains("> Delivering</p>", dashboard, StringComparison.Ordinal);
            Assert.DoesNotContain("> Clear</p>", dashboard, StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Restarted_host_does_not_call_persisted_microsoft_configuration_ready()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "RelayBridge.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var seed = new RelayDatabase(
                new RelayStorageOptions { DataDirectory = dataDirectory },
                AppContext.BaseDirectory);
            seed.Initialize();
            using (var connection = seed.OpenConnectionForDiagnostics())
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    INSERT INTO MicrosoftIdentityConfiguration
                        (Id, TenantId, ClientId, CertificateThumbprint, CertificateStoreName, CertificateStoreLocation, AuthorizedSender, ActivationId)
                    VALUES (1, $tenant, $client, $thumbprint, 'My', 'CurrentUser', 'scanner@example.com', $activationId);
                    """;
                command.Parameters.AddWithValue("$tenant", Guid.NewGuid().ToString("D"));
                command.Parameters.AddWithValue("$client", Guid.NewGuid().ToString("D"));
                command.Parameters.AddWithValue("$thumbprint", new string('A', 40));
                command.Parameters.AddWithValue("$activationId", Guid.NewGuid().ToString("D"));
                command.ExecuteNonQuery();
            }
            seed.SaveMicrosoftSetupState(new MicrosoftSetupState(
                MicrosoftSetupStep.Complete,
                MicrosoftSetupMode.ExistingApplication,
                MicrosoftCertificateReference.Create(
                    new string('A', 40),
                    CertificateStoreTarget.CurrentUser),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                "scanner@example.com",
                true,
                true,
                true,
                true,
                true,
                DateTimeOffset.UtcNow));
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            using var factory = _factory.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("Storage:DataDirectory", dataDirectory);
                builder.UseSetting("Smtp:Enabled", "false");
                builder.ConfigureLogging(logging => logging.ClearProviders());
                builder.ConfigureServices(services =>
                    services.AddDataProtection().UseEphemeralDataProtectionProvider());
            });
            using var client = factory.CreateClient();

            var dashboard = await client.GetStringAsync("/");
            var setup = await client.GetStringAsync("/setup/microsoft");

            Assert.Contains("Verification required", dashboard, StringComparison.Ordinal);
            Assert.Contains("have not succeeded during this service start", dashboard, StringComparison.Ordinal);
            Assert.Contains("Microsoft 365 verification required", setup, StringComparison.Ordinal);
            Assert.DoesNotContain("<h1>Microsoft 365 is ready</h1>", setup, StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private static string FlattenMessages(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }
}
