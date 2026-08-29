// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;
using RelayBridge.Infrastructure.Microsoft;
using RelayBridge.Infrastructure.Queue;
using RelayBridge.Infrastructure.Storage;

namespace RelayBridge.Host.Services;

public sealed class QueueHostedService : IHostedService
{
    private readonly QueueReconciler _reconciler;
    private readonly QueueWorker _worker;
    private readonly QueueWorkSignal _workSignal;
    private readonly QueueDeliveryActivation _deliveryActivation;
    private readonly RelayDatabase _database;
    private readonly MicrosoftCertificateService _certificates;
    private readonly QueueOptions _options;
    private readonly ILogger<QueueHostedService> _logger;

    public QueueHostedService(
        QueueReconciler reconciler,
        QueueWorker worker,
        QueueWorkSignal workSignal,
        QueueDeliveryActivation deliveryActivation,
        RelayDatabase database,
        MicrosoftCertificateService certificates,
        QueueOptions options,
        ILogger<QueueHostedService> logger)
    {
        _reconciler = reconciler;
        _worker = worker;
        _workSignal = workSignal;
        _deliveryActivation = deliveryActivation;
        _database = database;
        _certificates = certificates;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _reconciler.Reconcile(cancellationToken);
        if (_options.Enabled)
        {
            var identity = _database.GetMicrosoftIdentityConfiguration(cancellationToken);
            if (identity is not null && _certificates.Validate(identity.Certificate, cancellationToken).IsUsable)
            {
                _deliveryActivation.Activate();
            }
            else
            {
                _logger.LogInformation(
                    "ExchangeQueueWorkerWaiting Reason=MicrosoftIdentityNotReady");
            }
        }

        var start = _worker.StartAsync(cancellationToken);
        _workSignal.Pulse();
        return start;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return _worker.StopAsync(cancellationToken);
    }
}
