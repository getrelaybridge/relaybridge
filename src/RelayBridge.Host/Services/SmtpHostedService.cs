// SPDX-License-Identifier: MPL-2.0

using RelayBridge.Infrastructure.Smtp;

namespace RelayBridge.Host.Services;

public sealed class SmtpHostedService : IHostedService
{
    private readonly SmtpListener _listener;

    public SmtpHostedService(SmtpListener listener)
    {
        _listener = listener;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return _listener.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return _listener.StopAsync(cancellationToken);
    }
}
