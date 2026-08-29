// SPDX-License-Identifier: MPL-2.0

extern alias managementopener;
extern alias printerconfigurator;
extern alias setup;

using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Text.Json;
using RelayBridge.Core.Microsoft;
using RelayBridge.Core.PrinterConnectivity;
using RelayBridge.Host.Services;
using RelayBridge.Infrastructure.Microsoft;
using RelayBridge.Infrastructure.Smtp;
using ManagementDestination = managementopener::RelayBridge.ManagementOpener.ManagementDestination;
using ManagementOpener = managementopener::RelayBridge.ManagementOpener.ManagementOpener;
using ManagementOpenerArguments = managementopener::RelayBridge.ManagementOpener.ManagementOpenerArguments;
using PrinterConfigurator = printerconfigurator::RelayBridge.PrinterConfigurator.PrinterConfigurator;
using PrinterConfiguratorArguments = printerconfigurator::RelayBridge.PrinterConfigurator.PrinterConfiguratorArguments;
using SetupOrchestrator = setup::RelayBridge.Setup.SetupOrchestrator;
using Xunit;

namespace RelayBridge.IntegrationTests;

[SupportedOSPlatform("windows")]
public sealed class InstallerFirstRunUxTests
{
    [Fact]
    public async Task Completed_identical_entra_candidate_is_reused_but_exchange_remains_next_stage()
    {
        var state = CreateCompletedEntraState();
        var reusable = MicrosoftSetupService.GetReusableNativeEntraResult(state);
        Assert.NotNull(reusable);
        var executed = false;

        var resolved = await SetupOrchestrator.ResolveEntraResultAsync(
            CreateStart(state, reusable),
            _ =>
            {
                executed = true;
                return Task.FromResult(new EntraSetupResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0));
            },
            CancellationToken.None);

        Assert.False(executed);
        Assert.Equal(state.TenantId, resolved.TenantId);
        Assert.Equal(state.ClientId, resolved.ClientId);
        Assert.Equal(state.ServicePrincipalObjectId, resolved.ServicePrincipalObjectId);
        Assert.Equal(MicrosoftSetupStep.ExchangePermission, state.Step);
        Assert.False(state.ExchangeResultValidated);
    }

    [Theory]
    [InlineData("changed-certificate")]
    [InlineData("changed-candidate")]
    [InlineData("changed-service-principal")]
    [InlineData("cancelled")]
    [InlineData("corrupt")]
    [InlineData("incomplete")]
    public async Task Changed_or_incomplete_candidate_requires_normal_entra_stage(string variation)
    {
        var state = CreateCompletedEntraState();
        state = variation switch
        {
            "changed-certificate" => state with
            {
                Certificate = MicrosoftCertificateReference.Create(
                    "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
                    CertificateStoreTarget.LocalMachine),
                EntraResultValidated = false,
                Step = MicrosoftSetupStep.MicrosoftApplication,
            },
            "changed-candidate" => state with
            {
                ActivationId = Guid.NewGuid(),
                EntraResultValidated = false,
                Step = MicrosoftSetupStep.MicrosoftApplication,
            },
            "changed-service-principal" => state with
            {
                ServicePrincipalObjectId = Guid.NewGuid(),
                EntraResultValidated = false,
                Step = MicrosoftSetupStep.MicrosoftApplication,
            },
            "cancelled" => state with { Lifecycle = MicrosoftSetupCandidateLifecycle.Cancelled },
            "corrupt" => state with { TenantId = Guid.Empty },
            "incomplete" => state with { ServicePrincipalObjectId = null },
            _ => throw new InvalidOperationException(),
        };
        Assert.Null(MicrosoftSetupService.GetReusableNativeEntraResult(state));
        var expected = new EntraSetupResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0);
        var executed = false;

        var resolved = await SetupOrchestrator.ResolveEntraResultAsync(
            CreateStart(state, reusable: null),
            _ =>
            {
                executed = true;
                return Task.FromResult(expected);
            },
            CancellationToken.None);

        Assert.True(executed);
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void Printer_apply_candidate_is_narrow_one_shot_and_stale_safe()
    {
        var discovery = new MutableDiscovery("192.168.50.10");
        var listener = new SmtpListenerOptions
        {
            Enabled = true,
            ListenAddress = IPAddress.Loopback.ToString(),
            Port = 2525,
        };
        var coordinator = new PrinterConnectivityApplyCoordinator(
            new PrinterConnectivityApplyOptions
            {
                Enabled = true,
                HelperPath = Path.GetFullPath("RelayBridge.PrinterConfigurator.exe"),
                ExpectedHelperSha256 = new string('A', 64),
            },
            listener,
            new DeviceEndpointAdvisor(listener, discovery),
            TimeProvider.System);

        var prepared = coordinator.Prepare("192.168.50.10");
        Assert.True(prepared.Succeeded);
        Assert.StartsWith(PrinterConnectivityApplyProtocol.UriPrefix, prepared.LaunchUri, StringComparison.Ordinal);
        var revision = Guid.Parse(prepared.LaunchUri![PrinterConnectivityApplyProtocol.UriPrefix.Length..]);
        Assert.False(coordinator.TryTake(Guid.NewGuid(), out _));
        Assert.True(coordinator.TryTake(revision, out var candidate));
        Assert.Equal("192.168.50.10", candidate.ListenAddress);
        Assert.Equal(2525, candidate.SmtpPort);
        Assert.False(coordinator.TryTake(revision, out _));

        prepared = coordinator.Prepare("192.168.50.10");
        revision = Guid.Parse(prepared.LaunchUri![PrinterConnectivityApplyProtocol.UriPrefix.Length..]);
        discovery.Address = IPAddress.Parse("192.168.50.11");
        Assert.False(coordinator.TryTake(revision, out _));
    }

    [Theory]
    [InlineData("0.0.0.0", 2525)]
    [InlineData("127.0.0.1", 2525)]
    [InlineData("203.0.113.5", 2525)]
    [InlineData("192.168.50.10", 0)]
    public void Printer_apply_rejects_unsafe_listener_inputs(string address, int port)
    {
        Assert.Throws<InvalidOperationException>(() => PrinterConnectivityConfiguration.Create(address, port));
    }

    [Fact]
    public void Printer_apply_content_has_only_the_approved_schema_and_queue_is_always_enabled()
    {
        using var json = JsonDocument.Parse(PrinterConnectivityConfiguration.Create("192.168.50.10", 2525));
        Assert.Equal(["Smtp", "Queue"], json.RootElement.EnumerateObject().Select(item => item.Name).ToArray());
        Assert.Equal(
            ["Enabled", "ListenAddress", "Port", "AllowCleartextAuthentication"],
            json.RootElement.GetProperty("Smtp").EnumerateObject().Select(item => item.Name).ToArray());
        Assert.True(json.RootElement.GetProperty("Queue").GetProperty("Enabled").GetBoolean());
    }

    [Fact]
    public void Printer_configurator_accepts_only_a_revision_uri_and_resolves_only_the_fixed_target()
    {
        var revision = Guid.NewGuid();
        Assert.True(PrinterConfiguratorArguments.TryParse(
            [$"{PrinterConnectivityApplyProtocol.UriPrefix}{revision:D}"], out var parsed));
        Assert.Equal(revision, parsed);
        Assert.False(PrinterConfiguratorArguments.TryParse([@"C:\temp\payload.json"], out _));
        Assert.False(PrinterConfiguratorArguments.TryParse(
            [$"{PrinterConnectivityApplyProtocol.UriPrefix}{revision:D}", @"C:\temp\payload.json"], out _));

        var expectedHelper = @"C:\Program Files\RelayBridge\Setup\RelayBridge.PrinterConfigurator.exe";
        Assert.Equal(
            @"C:\Program Files\RelayBridge\Host\appsettings.Production.json",
            PrinterConfigurator.ResolveFixedTargetPath(expectedHelper, @"C:\Program Files"));
        Assert.Throws<InvalidDataException>(() => PrinterConfigurator.ResolveFixedTargetPath(
            @"C:\temp\RelayBridge.PrinterConfigurator.exe", @"C:\Program Files"));
    }

    [Fact]
    public void Management_opener_accepts_no_url_and_only_validates_loopback_state()
    {
        Assert.True(ManagementOpenerArguments.TryParse([], out var dashboard));
        Assert.Equal(ManagementDestination.Dashboard, dashboard);
        Assert.True(ManagementOpenerArguments.TryParse(["--setup"], out var setupDestination));
        Assert.Equal(ManagementDestination.MicrosoftSetup, setupDestination);
        Assert.False(ManagementOpenerArguments.TryParse(["https://example.com"], out _));
        Assert.False(ManagementOpenerArguments.TryParse(["--setup", "http://localhost:5080"], out _));
        Assert.Equal("http://localhost:5080/", ManagementOpener.ValidateBaseEndpoint("http://localhost:5080/").AbsoluteUri);
        Assert.Equal("http://127.0.0.1:5080/", ManagementOpener.ValidateBaseEndpoint("http://127.0.0.1:5080/").AbsoluteUri);
        Assert.Throws<InvalidDataException>(() => ManagementOpener.ValidateBaseEndpoint(null));
        Assert.Throws<InvalidDataException>(() => ManagementOpener.ValidateBaseEndpoint("https://localhost:5080/"));
        Assert.Throws<InvalidDataException>(() => ManagementOpener.ValidateBaseEndpoint("http://example.com:5080/"));
        Assert.Throws<InvalidDataException>(() => ManagementOpener.ValidateBaseEndpoint("http://localhost:5080/?url=x"));
    }

    private static MicrosoftSetupState CreateCompletedEntraState() => new(
        MicrosoftSetupStep.ExchangePermission,
        MicrosoftSetupMode.NewApplication,
        MicrosoftCertificateReference.Create(
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            CertificateStoreTarget.LocalMachine),
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        "scanner@example.com",
        EntraResultValidated: true,
        ExchangeResultValidated: false,
        IdentityValidated: false,
        ExchangeValidated: false,
        TestMessageAccepted: false,
        DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
        Guid.Parse("44444444-4444-4444-4444-444444444444"),
        Revision: 7,
        MicrosoftSetupCandidateLifecycle.Active);

    private static NativeSetupStartRequest CreateStart(
        MicrosoftSetupState state,
        EntraSetupResult? reusable) => new(
        NativeMicrosoftSetupProtocol.Version,
        Guid.NewGuid(),
        state.SenderMailbox ?? "scanner@example.com",
        Convert.ToBase64String([1]),
        "RelayBridge test",
        IsRepair: reusable is not null,
        Path.GetFullPath("installation"),
        Path.GetFullPath("tooling"),
        Path.GetFullPath("tooling-manifest.json"),
        new string('A', 64),
        Path.GetFullPath("scratch"),
        10,
        1,
        "S-1-5-21-1-2-3-1001",
        Path.GetFullPath("RelayBridge.SetupLauncher.exe"),
        new string('B', 64),
        state.ActivationId ?? Guid.NewGuid(),
        state.Revision,
        state.ActivationId is null ? new string('C', 64) : MicrosoftSetupCandidateFingerprint.Create(state),
        state.Mode,
        reusable);

    private sealed class MutableDiscovery(string address) : ILanAddressDiscovery
    {
        internal IPAddress Address { get; set; } = IPAddress.Parse(address);

        public LanAddressDiscoveryResult Discover() => new(
            [new DeviceEndpointCandidate(Address, "Printer LAN", NetworkInterfaceType.Ethernet)],
            IsIncomplete: false);
    }
}
