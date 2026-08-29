// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.NetworkInformation;
using RelayBridge.Core.Devices;
using RelayBridge.Core.Microsoft;
using RelayBridge.Core.Queue;
using RelayBridge.Host.Components;
using RelayBridge.Host.Services;
using RelayBridge.Infrastructure.Queue;
using RelayBridge.Infrastructure.Smtp;
using RelayBridge.Infrastructure.Microsoft;
using Xunit;

namespace RelayBridge.IntegrationTests;

public sealed class DeviceUxPolicyTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5080", true)]
    [InlineData("http://[::1]:5080", true)]
    [InlineData("http://localhost:5080", true)]
    [InlineData("http://0.0.0.0:5080", false)]
    [InlineData("http://[::]:5080", false)]
    [InlineData("http://192.168.1.10:5080", false)]
    [InlineData("http://203.0.113.10:5080", false)]
    public void Management_binding_policy_accepts_only_loopback(string url, bool expected)
    {
        Assert.Equal(expected, ManagementBindingPolicy.IsLoopbackUrl(url));
    }

    [Fact]
    public void Wildcard_port_configuration_fails_management_binding_closed()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["http_ports"] = "5080" })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() => ManagementBindingPolicy.Validate(configuration));
        Assert.Contains("wildcard", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.10.20")]
    [InlineData("224.0.0.1")]
    [InlineData("203.0.113.10")]
    public void Cleartext_auth_rejects_non_private_explicit_bindings(string address)
    {
        var options = new SmtpListenerOptions
        {
            ListenAddress = address,
            AllowCleartextAuthentication = true,
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.2.3")]
    [InlineData("192.168.2.3")]
    [InlineData("fd00::10")]
    public void Cleartext_auth_accepts_explicit_private_unicast_binding(string address)
    {
        new SmtpListenerOptions
        {
            ListenAddress = address,
            AllowCleartextAuthentication = true,
        }.Validate();
    }

    [Fact]
    public void Production_smtp_configuration_rejects_ephemeral_port()
    {
        var options = new SmtpListenerOptions { Port = 0 };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("between 1 and 65535", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Older_identity_failure_then_newer_exchange_success_is_ready()
    {
        var older = new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);
        var newer = older.AddMinutes(5);
        var readiness = MicrosoftRuntimeReadinessPolicy.Evaluate(
            configured: true,
            "A",
            IdentitySnapshot(MicrosoftIdentityHealthStatus.Failed, older, null, "A"),
            ExchangeSnapshot(ExchangeDeliveryStatus.Healthy, newer, newer, newer, "A"));

        Assert.Equal(MicrosoftRuntimeReadiness.Ready, readiness);
    }

    [Fact]
    public void Older_exchange_success_then_newer_identity_failure_needs_attention()
    {
        var older = new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);
        var newer = older.AddMinutes(5);
        var readiness = MicrosoftRuntimeReadinessPolicy.Evaluate(
            configured: true,
            "A",
            IdentitySnapshot(MicrosoftIdentityHealthStatus.Failed, newer, null, "A"),
            ExchangeSnapshot(ExchangeDeliveryStatus.Healthy, older, older, older, "A"));

        Assert.Equal(MicrosoftRuntimeReadiness.NeedsAttention, readiness);
    }

    [Fact]
    public void Older_identity_success_then_newer_exchange_failure_needs_attention()
    {
        var older = new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);
        var newer = older.AddMinutes(5);
        var readiness = MicrosoftRuntimeReadinessPolicy.Evaluate(
            true,
            "A",
            IdentitySnapshot(MicrosoftIdentityHealthStatus.Healthy, older, older, "A"),
            ExchangeSnapshot(ExchangeDeliveryStatus.Failed, newer, null, newer, "A"));

        Assert.Equal(MicrosoftRuntimeReadiness.NeedsAttention, readiness);
    }

    [Fact]
    public void Older_failure_then_newer_identity_success_is_ready()
    {
        var older = new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);
        var newer = older.AddMinutes(5);
        var readiness = MicrosoftRuntimeReadinessPolicy.Evaluate(
            true,
            "A",
            IdentitySnapshot(MicrosoftIdentityHealthStatus.Healthy, newer, newer, "A"),
            ExchangeSnapshot(ExchangeDeliveryStatus.Failed, older, null, older, "A"));

        Assert.Equal(MicrosoftRuntimeReadiness.Ready, readiness);
    }

    [Fact]
    public void Evidence_from_replaced_configuration_does_not_establish_readiness()
    {
        var now = new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);
        var readiness = MicrosoftRuntimeReadinessPolicy.Evaluate(
            true,
            "B",
            IdentitySnapshot(MicrosoftIdentityHealthStatus.Attention, null, null, null),
            ExchangeSnapshot(ExchangeDeliveryStatus.Healthy, now, now, now, "A"));

        Assert.Equal(MicrosoftRuntimeReadiness.VerificationRequired, readiness);
    }

    [Fact]
    public void Current_configuration_success_establishes_readiness()
    {
        var now = new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);
        var readiness = MicrosoftRuntimeReadinessPolicy.Evaluate(
            true,
            "B",
            IdentitySnapshot(MicrosoftIdentityHealthStatus.Attention, null, null, null),
            ExchangeSnapshot(ExchangeDeliveryStatus.Healthy, now, now, now, "B"));

        Assert.Equal(MicrosoftRuntimeReadiness.Ready, readiness);
    }

    [Fact]
    public void Restart_without_current_runtime_evidence_requires_verification()
    {
        var readiness = MicrosoftRuntimeReadinessPolicy.Evaluate(
            true,
            "B",
            IdentitySnapshot(MicrosoftIdentityHealthStatus.Attention, null, null, null),
            ExchangeSnapshot(ExchangeDeliveryStatus.NotTested, null, null, null, null));

        Assert.Equal(MicrosoftRuntimeReadiness.VerificationRequired, readiness);
    }

    [Fact]
    public void Device_creation_review_detects_sender_listener_auth_and_interface_changes()
    {
        var original = new DeviceProvisioningReview(
            "scanner@example.com", "fingerprint", "192.168.1.10", 2525, true, true,
            MicrosoftRuntimeReadiness.Ready, ["192.168.1.10"]);

        Assert.False(original.MateriallyMatches(original with { Sender = "other@example.com" }));
        Assert.False(original.MateriallyMatches(original with { ConfigurationFingerprint = "replacement" }));
        Assert.False(original.MateriallyMatches(original with { IsAuthenticatedSmtpAvailable = false }));
        Assert.False(original.MateriallyMatches(original with { CandidateAddresses = [] }));
        Assert.True(original.MateriallyMatches(original with { }));
    }

    [Fact]
    public void Wildcard_never_uses_first_virtual_private_adapter_as_printer_binding()
    {
        var discovery = new StubLanDiscovery(new LanAddressDiscoveryResult(
            [
                new DeviceEndpointCandidate(IPAddress.Parse("172.20.0.1"), "vEthernet", NetworkInterfaceType.Ethernet),
                new DeviceEndpointCandidate(IPAddress.Parse("192.168.10.5"), "Printer LAN", NetworkInterfaceType.Ethernet),
                new DeviceEndpointCandidate(IPAddress.Parse("203.0.113.5"), "Public", NetworkInterfaceType.Ethernet),
            ],
            false));
        var advice = new DeviceEndpointAdvisor(
            new SmtpListenerOptions { ListenAddress = "0.0.0.0" },
            discovery).GetAdvice();

        Assert.False(advice.IsLanReachable);
        Assert.Empty(advice.Candidates);
        Assert.Equal(2, advice.AvailableCandidates.Count);
        Assert.DoesNotContain(advice.AvailableCandidates, item => item.Address.Equals(IPAddress.Parse("203.0.113.5")));
        Assert.Contains("wildcard", advice.Warning, StringComparison.OrdinalIgnoreCase);

        var settings = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "RelayBridge.Host", "Components", "Pages", "Settings.razor"));
        Assert.DoesNotContain("FirstOrDefault()?.Address", settings, StringComparison.Ordinal);
        Assert.Contains("_selectedAddress is null", settings, StringComparison.Ordinal);
        Assert.Contains("type=\"radio\" name=\"printer-interface\"", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void Network_discovery_failure_returns_safe_unavailable_advice()
    {
        var advice = new DeviceEndpointAdvisor(
            new SmtpListenerOptions { ListenAddress = "192.168.10.5" },
            new ThrowingLanDiscovery()).GetAdvice();

        Assert.False(advice.IsLanReachable);
        Assert.Empty(advice.Candidates);
        Assert.Contains("unavailable", advice.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Network_discovery_salvages_valid_adapter_after_another_adapter_fails()
    {
        var discovery = new SystemLanAddressDiscovery(new StubAdapterSource(
        [
            new ThrowingAdapter(),
            new StubAdapter(
                "Printer LAN",
                OperationalStatus.Up,
                NetworkInterfaceType.Ethernet,
                [IPAddress.Parse("192.168.50.10")]),
        ]));

        var result = discovery.Discover();

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(IPAddress.Parse("192.168.50.10"), candidate.Address);
        Assert.Equal("Printer LAN", candidate.InterfaceName);
        Assert.True(result.IsIncomplete);
    }

    [Fact]
    public void Generated_smtp_deployment_document_round_trips_through_configuration_binding()
    {
        var json = SmtpDeploymentConfiguration.Create("192.168.50.10", 2526);
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var options = configuration.GetSection("Smtp").Get<SmtpListenerOptions>();
        var queue = configuration.GetSection("Queue").Get<QueueOptions>();

        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.NotNull(options);
        Assert.True(options.Enabled);
        Assert.Equal("192.168.50.10", options.ListenAddress);
        Assert.Equal(2526, options.Port);
        Assert.True(options.AllowCleartextAuthentication);
        options.Validate();
        Assert.NotNull(queue);
        Assert.True(queue.Enabled);
        queue.Validate();
        Assert.False(configuration.GetSection("Management").Exists());
        Assert.Equal(2, document.RootElement.EnumerateObject().Count());
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Printer_connectivity_instructions_use_exact_override_path_and_scoped_manual_commands()
    {
        const string contentRoot = @"C:\Program Files\RelayBridge\Host";
        var destination = SmtpDeploymentConfiguration.GetEnvironmentOverridePath(contentRoot, "Production");
        var downloadName = SmtpDeploymentConfiguration.GetDownloadFileName("Production");
        var apply = SmtpDeploymentConfiguration.CreateAdministratorCommands(destination, downloadName);
        var firewall = SmtpDeploymentConfiguration.CreateFirewallCommand(
            "192.168.50.10",
            2525,
            Path.Combine(contentRoot, "RelayBridge.Host.exe"));

        Assert.Equal(@"C:\Program Files\RelayBridge\Host\appsettings.Production.json", destination);
        Assert.Equal("RelayBridge-appsettings.Production.json", downloadName);
        Assert.Contains("Copy-Item -LiteralPath", apply, StringComparison.Ordinal);
        Assert.Contains($"-Destination '{destination}'", apply, StringComparison.Ordinal);
        Assert.Contains("Restart-Service -Name 'RelayBridge'", apply, StringComparison.Ordinal);
        Assert.Contains("New-NetFirewallRule", firewall, StringComparison.Ordinal);
        Assert.Contains("-Direction Inbound", firewall, StringComparison.Ordinal);
        Assert.Contains("-Profile Private", firewall, StringComparison.Ordinal);
        Assert.Contains("-LocalAddress '192.168.50.10'", firewall, StringComparison.Ordinal);
        Assert.Contains("-LocalPort 2525", firewall, StringComparison.Ordinal);
        Assert.Contains("-RemoteAddress 'LocalSubnet'", firewall, StringComparison.Ordinal);
        Assert.Contains("RelayBridge.Host.exe", firewall, StringComparison.Ordinal);
        Assert.DoesNotContain("0.0.0.0", firewall, StringComparison.Ordinal);
    }

    [Fact]
    public void Printer_connectivity_page_exposes_an_actionable_unapplied_workflow()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "RelayBridge.Host", "Components", "Pages", "Settings.razor"));

        Assert.Contains("NOT YET APPLIED", source, StringComparison.Ordinal);
        Assert.Contains("Apply printer connectivity", source, StringComparison.Ordinal);
        Assert.Contains("Windows administrator confirmation", source, StringComparison.Ordinal);
        Assert.Contains("Download configuration", source, StringComparison.Ordinal);
        Assert.Contains("Copy configuration", source, StringComparison.Ordinal);
        Assert.Contains("Configuration destination", source, StringComparison.Ordinal);
        Assert.Contains("Queue processing after restart", source, StringComparison.Ordinal);
        Assert.Contains("Advanced manual apply", source, StringComparison.Ordinal);
        Assert.Contains("The installer intentionally does not create a firewall rule", source, StringComparison.Ordinal);
        Assert.Contains("management UI remains available only from this computer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("changes are applied", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_connection_ui_is_separate_from_repair()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "RelayBridge.Host", "Components", "Pages", "MicrosoftSetup.razor"));

        Assert.Contains("@onclick=\"VerifyActiveConnectionAsync\">Verify connection", source, StringComparison.Ordinal);
        Assert.Contains("@onclick=\"RepairAsync\">Repair connection", source, StringComparison.Ordinal);
        Assert.Contains(
            "VerifyActiveConnectionAsync() => RunOperationAsync(Setup.VerifyActiveConnectionAsync)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("@onclick=\"RepairAsync\">Verify connection", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("203.0.113.10")]
    public void Generated_smtp_deployment_document_rejects_unsafe_addresses(string address)
    {
        Assert.Throws<InvalidOperationException>(() =>
            SmtpDeploymentConfiguration.Create(address, 2525));
    }
    [Fact]
    public void Fresh_dashboard_prioritizes_microsoft_setup()
    {
        var readiness = CreateReadiness(microsoftConfigured: false, microsoftReady: false);

        Assert.Equal(DeviceSetupPrimaryAction.SetUpMicrosoft365, readiness.PrimaryAction);
    }

    [Fact]
    public void Configured_microsoft_with_unavailable_listener_prioritizes_connectivity()
    {
        var readiness = CreateReadiness(
            microsoftConfigured: true,
            microsoftReady: true,
            printerConnectivityReady: false,
            smtpAuthenticationReady: false);

        Assert.Equal(DeviceSetupPrimaryAction.PreparePrinterConnectivity, readiness.PrimaryAction);
    }

    [Fact]
    public void Ready_prerequisites_make_add_device_primary()
    {
        var readiness = CreateReadiness(
            microsoftConfigured: true,
            microsoftReady: true,
            printerConnectivityReady: true,
            smtpAuthenticationReady: true);

        Assert.Equal(DeviceSetupPrimaryAction.AddDevice, readiness.PrimaryAction);
    }

    [Fact]
    public void Authenticated_creation_is_blocked_when_authenticated_intake_is_unavailable()
    {
        var readiness = CreateReadiness(
            microsoftConfigured: true,
            microsoftReady: true,
            printerConnectivityReady: true,
            smtpAuthenticationReady: false);

        Assert.False(readiness.CanCreate(DeviceAuthenticationMode.Authenticated));
        Assert.True(readiness.CanCreate(DeviceAuthenticationMode.Legacy));
        Assert.False(readiness.CanCreate(authenticationMode: null));
    }

    [Fact]
    public void Legacy_creation_still_fails_closed_without_lan_connectivity()
    {
        var readiness = CreateReadiness(
            microsoftConfigured: true,
            microsoftReady: true,
            printerConnectivityReady: false,
            smtpAuthenticationReady: false);

        Assert.False(readiness.CanCreate(DeviceAuthenticationMode.Legacy));
    }

    [Fact]
    public void One_time_password_requires_saved_acknowledgement_before_leaving()
    {
        Assert.False(OneTimeSecretExitPolicy.CanLeave("transient-test-secret", savedAcknowledged: false));
        Assert.True(OneTimeSecretExitPolicy.CanLeave("transient-test-secret", savedAcknowledged: true));
        Assert.True(OneTimeSecretExitPolicy.CanLeave(plaintextSecret: null, savedAcknowledged: false));
    }

    [Theory]
    [InlineData(QueueState.Queued, "waiting for Microsoft 365 delivery", false, false)]
    [InlineData(QueueState.RetryScheduled, "retry automatically", false, false)]
    [InlineData(QueueState.PermanentFailure, "permanently rejected delivery", false, true)]
    [InlineData(QueueState.Delivered, "Microsoft 365 accepted the message", true, false)]
    public void Device_test_wording_matches_existing_queue_state(
        QueueState state,
        string expectedText,
        bool microsoftAccepted,
        bool isFailure)
    {
        var presentation = DeviceTestPresentation.From(localAccepted: true, state);

        Assert.True(presentation.LocalAccepted);
        Assert.Equal(microsoftAccepted, presentation.MicrosoftAccepted);
        Assert.Equal(isFailure, presentation.IsFailure);
        Assert.Contains(expectedText, presentation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Device_test_without_local_acceptance_stays_generic()
    {
        var presentation = DeviceTestPresentation.From(localAccepted: false, QueueState.PermanentFailure);

        Assert.False(presentation.LocalAccepted);
        Assert.False(presentation.MicrosoftAccepted);
        Assert.Equal("RelayBridge has not received a new message from this device yet.", presentation.Message);
    }

    [Fact]
    public async Task Shared_server_address_component_renders_every_usable_candidate()
    {
        var advice = new DeviceEndpointAdvice(
            [
                new DeviceEndpointCandidate(IPAddress.Parse("192.168.10.10"), "Printer LAN"),
                new DeviceEndpointCandidate(IPAddress.Parse("10.20.30.40"), "Scanner LAN"),
            ],
            [],
            2525,
            true,
            true,
            "0.0.0.0",
            null);
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<PrinterServerAddresses>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Advice"] = advice }));
            return rendered.ToHtmlString();
        });

        Assert.Contains("Use the address reachable from this printer", html, StringComparison.Ordinal);
        Assert.Contains("192.168.10.10", html, StringComparison.Ordinal);
        Assert.Contains("10.20.30.40", html, StringComparison.Ordinal);
        Assert.Contains("Printer LAN", html, StringComparison.Ordinal);
        Assert.Contains("Scanner LAN", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Device_confirmation_markup_uses_focused_inline_regions_not_false_modals()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "RelayBridge.Host",
            "Components",
            "Pages",
            "DeviceDetails.razor"));

        Assert.DoesNotContain("role=\"alertdialog\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-modal=\"true\"", source, StringComparison.Ordinal);
        Assert.Contains("tabindex=\"-1\"", source, StringComparison.Ordinal);
        Assert.Contains("FocusAsync", source, StringComparison.Ordinal);
        Assert.Contains("_editPanel.FocusAsync", source, StringComparison.Ordinal);
        Assert.Contains("_editTrigger.FocusAsync", source, StringComparison.Ordinal);
        Assert.Contains("Changing this address takes effect immediately", source, StringComparison.Ordinal);
        Assert.Contains("_editNetworks = [.. _item.Device.AllowedNetworks]", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Mutation_handlers_retain_supplemental_entry_guards_for_duplicate_events()
    {
        var root = FindRepositoryRoot();
        var add = File.ReadAllText(Path.Combine(root, "src", "RelayBridge.Host", "Components", "Pages", "AddDevice.razor"));
        var details = File.ReadAllText(Path.Combine(root, "src", "RelayBridge.Host", "Components", "Pages", "DeviceDetails.razor"));

        Assert.Contains("_busy || _created is not null || _step != 4", add, StringComparison.Ordinal);
        Assert.Contains("_mutationBusy || !_editing", details, StringComparison.Ordinal);
        Assert.Contains("_mutationBusy || !_confirmReset", details, StringComparison.Ordinal);
        Assert.Contains("_mutationBusy || !_confirmEnabledChange", details, StringComparison.Ordinal);
        Assert.Contains("GetCurrentReview", add, StringComparison.Ordinal);
        Assert.Contains("MateriallyMatches", add, StringComparison.Ordinal);
        Assert.Contains("Database.GetActiveMicrosoftConfiguration()", add, StringComparison.Ordinal);
        Assert.Contains("Endpoints.GetAdvice()", add, StringComparison.Ordinal);
    }

    private static MicrosoftIdentityHealthSnapshot IdentitySnapshot(
        MicrosoftIdentityHealthStatus status,
        DateTimeOffset? attemptedAt,
        DateTimeOffset? successfulAt,
        string? fingerprint) => new(status, attemptedAt, successfulAt, null, null)
        {
            ConfigurationFingerprint = fingerprint,
        };

    private static ExchangeDeliverySnapshot ExchangeSnapshot(
        ExchangeDeliveryStatus status,
        DateTimeOffset? attemptedAt,
        DateTimeOffset? successfulAt,
        DateTimeOffset? completedAt,
        string? fingerprint) => new(
            status, attemptedAt, successfulAt, null, null, false, false, false, false, false, false, false)
        {
            LastCompletedAt = completedAt,
            ConfigurationFingerprint = fingerprint,
        };

    private sealed class StubLanDiscovery(LanAddressDiscoveryResult result) : ILanAddressDiscovery
    {
        public LanAddressDiscoveryResult Discover() => result;
    }

    private sealed class ThrowingLanDiscovery : ILanAddressDiscovery
    {
        public LanAddressDiscoveryResult Discover() => throw new NetworkInformationException();
    }

    private sealed class StubAdapterSource(IReadOnlyList<ILanNetworkAdapter> adapters) : ILanNetworkAdapterSource
    {
        public IReadOnlyList<ILanNetworkAdapter> GetAdapters() => adapters;
    }

    private sealed class StubAdapter(
        string name,
        OperationalStatus operationalStatus,
        NetworkInterfaceType networkInterfaceType,
        IReadOnlyList<IPAddress> unicastAddresses) : ILanNetworkAdapter
    {
        public string Name => name;

        public OperationalStatus OperationalStatus => operationalStatus;

        public NetworkInterfaceType NetworkInterfaceType => networkInterfaceType;

        public IReadOnlyList<IPAddress> UnicastAddresses => unicastAddresses;
    }

    private sealed class ThrowingAdapter : ILanNetworkAdapter
    {
        public string Name => throw new NetworkInformationException();

        public OperationalStatus OperationalStatus => throw new NetworkInformationException();

        public NetworkInterfaceType NetworkInterfaceType => throw new NetworkInformationException();

        public IReadOnlyList<IPAddress> UnicastAddresses => throw new NetworkInformationException();
    }

    private static DeviceSetupReadiness CreateReadiness(
        bool microsoftConfigured,
        bool microsoftReady,
        bool printerConnectivityReady = false,
        bool smtpAuthenticationReady = false)
    {
        return new DeviceSetupReadiness(
            microsoftConfigured,
            microsoftReady,
            printerConnectivityReady,
            smtpAuthenticationReady,
            InboundTlsAvailable: false);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RelayBridge.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("RelayBridge repository root was not found.");
    }
}
