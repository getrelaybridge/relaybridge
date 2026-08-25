// SPDX-License-Identifier: MPL-2.0

using RelayBridge.Infrastructure.Microsoft;

namespace RelayBridge.Host.Services;

public sealed class NativeMicrosoftSetupHostedService : BackgroundService
{
    private readonly NativeMicrosoftSetupServer _server;

    public NativeMicrosoftSetupHostedService(NativeMicrosoftSetupServer server)
    {
        _server = server;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return _server.RunAsync(stoppingToken);
    }
}
