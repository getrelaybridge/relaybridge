// SPDX-License-Identifier: MPL-2.0

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using RelayBridge.Core.Microsoft;
using RelayBridge.Core.Queue;
using RelayBridge.Host.Components;
using RelayBridge.Host.Diagnostics;
using RelayBridge.Host.Services;
using RelayBridge.Infrastructure.Diagnostics;
using RelayBridge.Infrastructure.Microsoft;
using RelayBridge.Infrastructure.Queue;
using RelayBridge.Infrastructure.Smtp;
using RelayBridge.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);

var managementOptions = builder.Configuration.GetSection("Management").Get<ManagementOptions>() ?? new();
managementOptions.Validate();
ManagementBindingPolicy.Validate(builder.Configuration);
builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(managementOptions.Port));

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "RelayBridge";
});

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton(managementOptions);
builder.Services.AddHealthChecks().AddCheck<QueueHealthCheck>("queue");
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
builder.Services
    .AddOptions<RelayStorageOptions>()
    .Bind(builder.Configuration.GetSection("Storage"))
    .Validate(options => !string.IsNullOrWhiteSpace(options.DataDirectory), "A storage data directory is required.")
    .ValidateOnStart();
builder.Services
    .AddOptions<SmtpListenerOptions>()
    .Bind(builder.Configuration.GetSection("Smtp"))
    .ValidateOnStart();
builder.Services
    .AddOptions<QueueOptions>()
    .Bind(builder.Configuration.GetSection("Queue"))
    .Validate(options => ValidateQueueOptions(options), "Queue configuration is invalid.")
    .ValidateOnStart();
builder.Services
    .AddOptions<MicrosoftIdentityOptions>()
    .Bind(builder.Configuration.GetSection("MicrosoftIdentity"))
    .Validate(options => ValidateMicrosoftIdentityOptions(options), "Microsoft identity configuration is invalid.")
    .ValidateOnStart();
builder.Services
    .AddOptions<ExchangeSmtpOptions>()
    .Bind(builder.Configuration.GetSection("ExchangeSmtp"))
    .Validate(options => ValidateExchangeSmtpOptions(options), "Exchange SMTP configuration is invalid.")
    .ValidateOnStart();
builder.Services
    .AddOptions<NativeMicrosoftSetupOptions>()
    .Bind(builder.Configuration.GetSection("NativeMicrosoftSetup"))
    .Validate(options => ValidateNativeMicrosoftSetupOptions(options), "Native Microsoft setup configuration is invalid.")
    .ValidateOnStart();
builder.Services.AddSingleton(serviceProvider => new RelayDatabase(
    serviceProvider.GetRequiredService<IOptions<RelayStorageOptions>>().Value,
    AppContext.BaseDirectory));
builder.Services.AddSingleton(serviceProvider =>
    serviceProvider.GetRequiredService<IOptions<QueueOptions>>().Value);
builder.Services.AddSingleton(serviceProvider =>
    serviceProvider.GetRequiredService<IOptions<MicrosoftIdentityOptions>>().Value);
builder.Services.AddSingleton(serviceProvider =>
    serviceProvider.GetRequiredService<IOptions<ExchangeSmtpOptions>>().Value);
builder.Services.AddSingleton(serviceProvider =>
    serviceProvider.GetRequiredService<IOptions<NativeMicrosoftSetupOptions>>().Value);
builder.Services.AddSingleton<ISpoolFileSystem, PhysicalSpoolFileSystem>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<QueueWorkSignal>();
builder.Services.AddSingleton<QueueCapacityManager>();
builder.Services.AddSingleton<DeviceService>();
builder.Services.AddSingleton(serviceProvider => new DeviceOverviewService(
    serviceProvider.GetRequiredService<RelayDatabase>(),
    serviceProvider.GetRequiredService<TimeProvider>(),
    serviceProvider.GetRequiredService<IOptions<SmtpListenerOptions>>().Value,
    serviceProvider.GetRequiredService<MicrosoftIdentityRuntimeState>(),
    serviceProvider.GetRequiredService<ExchangeDeliveryRuntimeState>()));
builder.Services.AddSingleton<DurableMessageStore>();
builder.Services.AddSingleton<LocalQueuePreview>();
builder.Services.AddSingleton<QueueReconciler>();
builder.Services.AddSingleton<QueueWorker>();
builder.Services.AddSingleton<MicrosoftCertificateService>();
builder.Services.AddSingleton<IMicrosoftIdentityClientFactory, MsalMicrosoftIdentityClientFactory>();
builder.Services.AddSingleton<MicrosoftTokenProvider>();
builder.Services.AddSingleton<IMicrosoftTokenProvider>(serviceProvider =>
    serviceProvider.GetRequiredService<MicrosoftTokenProvider>());
builder.Services.AddSingleton<MicrosoftRuntimeEvidenceSequence>();
builder.Services.AddSingleton<MicrosoftIdentityRuntimeState>();
builder.Services.AddSingleton<MicrosoftAuthenticationTester>();
builder.Services.AddSingleton<ExchangeDeliveryRuntimeState>();
builder.Services.AddSingleton<ExchangeSmtpOAuthProvider>();
builder.Services.AddSingleton<ExchangeDeliveryTester>();
builder.Services.AddSingleton<MicrosoftSetupScriptGenerator>();
builder.Services.AddSingleton<MicrosoftSetupService>();
builder.Services.AddSingleton<NativeMicrosoftSetupRuntime>();
builder.Services.AddSingleton<NativeMicrosoftSetupServer>();
builder.Services.AddSingleton<LocalDiagnosticDataReader>();
builder.Services.AddSingleton<IExchangeConnectivityProbe, ExchangeConnectivityProbe>();
builder.Services.AddSingleton<DiagnosticsActionState>();
builder.Services.AddSingleton<RelayDiagnosticsService>();
builder.Services.AddSingleton<SupportBundleService>();
builder.Services.AddSingleton<ILanAddressDiscovery, SystemLanAddressDiscovery>();
builder.Services.AddSingleton(serviceProvider => new DeviceEndpointAdvisor(
    serviceProvider.GetRequiredService<IOptions<SmtpListenerOptions>>().Value,
    serviceProvider.GetRequiredService<ILanAddressDiscovery>()));
builder.Services.AddSingleton<IMailDeliveryProvider>(serviceProvider =>
    serviceProvider.GetRequiredService<ExchangeSmtpOAuthProvider>());
builder.Services.AddSingleton(serviceProvider => new SmtpListener(
    serviceProvider.GetRequiredService<IOptions<SmtpListenerOptions>>().Value,
    serviceProvider.GetRequiredService<RelayDatabase>(),
    serviceProvider.GetRequiredService<DeviceService>(),
    serviceProvider.GetRequiredService<DurableMessageStore>(),
    serviceProvider.GetRequiredService<ILogger<SmtpListener>>()));
builder.Services.AddHostedService<QueueHostedService>();
builder.Services.AddHostedService<SmtpHostedService>();
builder.Services.AddHostedService<NativeMicrosoftSetupHostedService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapHealthChecks("/health", new HealthCheckOptions
{
    AllowCachingResponses = false,
});
app.MapPost("/diagnostics/connectivity", async (
    DiagnosticsActionState actions,
    CancellationToken cancellationToken) =>
{
    var result = await actions.RunConnectivityAsync(cancellationToken);
    return Results.Json(result);
});
app.MapPost("/diagnostics/database-quick-check", async (
    DiagnosticsActionState actions,
    CancellationToken cancellationToken) =>
{
    var result = await actions.RunDatabaseQuickCheckAsync(cancellationToken);
    return Results.Json(result);
});
app.MapGet("/diagnostics/support-bundle", (
    SupportBundleService bundles,
    CancellationToken cancellationToken) =>
{
    try
    {
        var bundle = bundles.Create(cancellationToken);
        return Results.File(bundle.Content, "application/zip", bundle.FileName);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        return Results.StatusCode(StatusCodes.Status408RequestTimeout);
    }
    catch
    {
        return Results.Problem(
            "The local support bundle could not be generated.",
            statusCode: StatusCodes.Status500InternalServerError);
    }
});
app.MapGet("/setup/public-certificate", async (
    string thumbprint,
    CertificateStoreTarget store,
    MicrosoftCertificateService certificates,
    CancellationToken cancellationToken) =>
{
    try
    {
        var reference = MicrosoftCertificateReference.Create(thumbprint, store);
        var export = await certificates.ExportPublicCertificateAsync(reference, cancellationToken);
        return Results.File(export.FullPath, "application/pkix-cert", export.FileName);
    }
    catch (Exception exception) when (exception is ArgumentException or
        MicrosoftIdentityException or
        IOException or
        UnauthorizedAccessException or
        System.Security.Cryptography.CryptographicException)
    {
        return Results.BadRequest("The selected public certificate could not be exported.");
    }
});
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

static bool ValidateQueueOptions(QueueOptions options)
{
    try
    {
        options.Validate();
        return true;
    }
    catch (InvalidOperationException)
    {
        return false;
    }
}

static bool ValidateMicrosoftIdentityOptions(MicrosoftIdentityOptions options)
{
    try
    {
        options.Validate();
        return true;
    }
    catch (InvalidOperationException)
    {
        return false;
    }
}

static bool ValidateExchangeSmtpOptions(ExchangeSmtpOptions options)
{
    try
    {
        options.Validate();
        return true;
    }
    catch (InvalidOperationException)
    {
        return false;
    }
}

static bool ValidateNativeMicrosoftSetupOptions(NativeMicrosoftSetupOptions options)
{
    try
    {
        options.Validate();
        return true;
    }
    catch (InvalidOperationException)
    {
        return false;
    }
}

app.Run();

public partial class Program
{
}
