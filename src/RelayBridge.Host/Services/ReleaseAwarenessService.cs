// SPDX-License-Identifier: MPL-2.0

using RelayBridge.Core.Release;
using RelayBridge.Infrastructure.Release;

namespace RelayBridge.Host.Services;

public sealed class ReleaseAwarenessService
{
    private readonly GitHubReleaseChecker _checker;
    private readonly ProductVersionService _product;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private ReleaseCheckResult? _lastResult;
    private bool _checking;

    public ReleaseAwarenessService(
        GitHubReleaseChecker checker,
        ProductVersionService product,
        TimeProvider timeProvider)
    {
        _checker = checker;
        _product = product;
        _timeProvider = timeProvider;
    }

    public ProductSemanticVersion CurrentVersion => _product.CurrentVersion;

    public ReleaseChannel Channel => _product.CurrentChannel;

    public ReleaseCheckStatus Status => _checking
        ? ReleaseCheckStatus.Checking
        : _lastResult?.Status ?? ReleaseCheckStatus.NotChecked;

    public ReleaseCheckResult? LastResult => _lastResult;

    public async Task<ReleaseCheckResult> CheckNowAsync(CancellationToken cancellationToken = default)
    {
        await _checkLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _checking = true;
            _lastResult = await _checker.CheckAsync(
                CurrentVersion,
                Channel,
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return _lastResult;
        }
        finally
        {
            _checking = false;
            _checkLock.Release();
        }
    }
}
