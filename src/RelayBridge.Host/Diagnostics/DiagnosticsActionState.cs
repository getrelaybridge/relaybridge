// SPDX-License-Identifier: MPL-2.0

using RelayBridge.Core.Diagnostics;
using RelayBridge.Infrastructure.Diagnostics;

namespace RelayBridge.Host.Diagnostics;

public sealed class DiagnosticsActionState
{
    private readonly object _lock = new();
    private readonly SemaphoreSlim _connectivityGate = new(1, 1);
    private readonly SemaphoreSlim _databaseGate = new(1, 1);
    private readonly IExchangeConnectivityProbe _connectivity;
    private readonly LocalDiagnosticDataReader _localData;
    private readonly TimeProvider _timeProvider;
    private ConnectivityDiagnosticSnapshot _connectivitySnapshot;
    private DiagnosticEvidence _quickCheck;

    public DiagnosticsActionState(
        IExchangeConnectivityProbe connectivity,
        LocalDiagnosticDataReader localData,
        TimeProvider timeProvider)
    {
        _connectivity = connectivity;
        _localData = localData;
        _timeProvider = timeProvider;
        _connectivitySnapshot = new ConnectivityDiagnosticSnapshot(
            new DiagnosticEvidence(
                DiagnosticStatus.Unknown,
                timeProvider.GetUtcNow(),
                DiagnosticEvidenceSource.Runtime,
                "Connectivity check has not been run during this service start."),
            ConnectivityProbeStage.NotRun,
            null,
            null);
        _quickCheck = new DiagnosticEvidence(
            DiagnosticStatus.Unknown,
            timeProvider.GetUtcNow(),
            DiagnosticEvidenceSource.Runtime,
            "Database quick check has not been run during this service start.");
    }

    public ConnectivityDiagnosticSnapshot Connectivity
    {
        get
        {
            lock (_lock)
            {
                return _connectivitySnapshot;
            }
        }
    }

    public DiagnosticEvidence DatabaseQuickCheck
    {
        get
        {
            lock (_lock)
            {
                return _quickCheck;
            }
        }
    }

    public async Task<ConnectivityDiagnosticSnapshot> RunConnectivityAsync(
        CancellationToken cancellationToken = default)
    {
        await _connectivityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ConnectivityDiagnosticSnapshot result;
            try
            {
                result = await _connectivity.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                result = new ConnectivityDiagnosticSnapshot(
                    new DiagnosticEvidence(
                        DiagnosticStatus.Unavailable,
                        _timeProvider.GetUtcNow(),
                        DiagnosticEvidenceSource.ActiveProbe,
                        "The connectivity check failed unexpectedly."),
                    ConnectivityProbeStage.NotRun,
                    false,
                    null);
            }

            lock (_lock)
            {
                _connectivitySnapshot = result;
            }

            return result;
        }
        finally
        {
            _connectivityGate.Release();
        }
    }

    public async Task<DiagnosticEvidence> RunDatabaseQuickCheckAsync(
        CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DiagnosticEvidence result;
            try
            {
                result = await _localData.RunQuickCheckAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                result = new DiagnosticEvidence(
                    DiagnosticStatus.Unavailable,
                    _timeProvider.GetUtcNow(),
                    DiagnosticEvidenceSource.ActiveProbe,
                    "Database quick check failed unexpectedly.");
            }

            lock (_lock)
            {
                _quickCheck = result;
            }

            return result;
        }
        finally
        {
            _databaseGate.Release();
        }
    }
}
