// SPDX-License-Identifier: MPL-2.0

using Microsoft.Win32;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace RelayBridge.Host.Services;

public sealed class ManagementEndpointPublisher : IHostedService
{
    internal const string RegistryPath = @"SOFTWARE\RelayBridge";
    internal const string RegistryValueName = "ManagementEndpoint";
    private readonly ManagementOptions _options;

    public ManagementEndpointPublisher(ManagementOptions options)
    {
        _options = options;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
        {
            using var key = Registry.LocalMachine.CreateSubKey(RegistryPath, writable: true)
                ?? throw new InvalidOperationException("RelayBridge could not publish its local management endpoint.");
            key.SetValue(
                RegistryValueName,
                $"http://localhost:{_options.Port}/",
                RegistryValueKind.String);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
