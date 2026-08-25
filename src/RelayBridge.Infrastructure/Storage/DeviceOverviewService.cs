// SPDX-License-Identifier: MPL-2.0

using RelayBridge.Core.Devices;
using RelayBridge.Core.Queue;
using RelayBridge.Infrastructure.Queue;
using RelayBridge.Infrastructure.Microsoft;
using RelayBridge.Infrastructure.Smtp;

namespace RelayBridge.Infrastructure.Storage;

public sealed class DeviceOverviewService
{
    private readonly RelayDatabase _database;
    private readonly TimeProvider _timeProvider;
    private readonly SmtpListenerOptions _listenerOptions;
    private readonly MicrosoftIdentityRuntimeState _identityState;
    private readonly ExchangeDeliveryRuntimeState _exchangeState;

    public DeviceOverviewService(
        RelayDatabase database,
        TimeProvider timeProvider,
        SmtpListenerOptions listenerOptions,
        MicrosoftIdentityRuntimeState identityState,
        ExchangeDeliveryRuntimeState exchangeState)
    {
        _database = database;
        _timeProvider = timeProvider;
        _listenerOptions = listenerOptions;
        _identityState = identityState;
        _exchangeState = exchangeState;
    }

    public DeviceOverviewSnapshot GetSnapshot(CancellationToken cancellationToken = default)
    {
        var startOfTodayUtc = GetStartOfLocalDayUtc(_timeProvider.GetUtcNow());
        var activities = _database.GetDeviceActivities(startOfTodayUtc, cancellationToken)
            .ToDictionary(activity => activity.DeviceId);
        var activeConfiguration = _database.GetActiveMicrosoftConfiguration(cancellationToken);
        var activeSender = activeConfiguration?.AuthorizedSender;
        var listenerCanServeLan = _listenerOptions.Enabled &&
            new DeviceEndpointAdvisor(_listenerOptions).GetAdvice().IsLanReachable;
        var microsoftReady = MicrosoftRuntimeReadinessPolicy.Evaluate(
            activeSender is not null,
            activeConfiguration?.Fingerprint,
            _identityState,
            _exchangeState) == MicrosoftRuntimeReadiness.Ready;
        var devices = _database.GetDevices(cancellationToken)
            .Select(device => CreateItem(device, activities[device.Id], activeSender, listenerCanServeLan, microsoftReady))
            .OrderByDescending(item => item.Activity.LastAcceptedUtc)
            .ThenBy(item => item.Device.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new DeviceOverviewSnapshot(
            devices,
            _database.GetQueueMetrics(cancellationToken),
            _database.GetMessageOutcomeCounts(startOfTodayUtc, cancellationToken),
            activeSender,
            activeConfiguration?.Fingerprint);
    }

    public DeviceOverviewItem? GetDevice(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var device = _database.GetDevice(deviceId, cancellationToken);
        if (device is null)
        {
            return null;
        }

        var startOfTodayUtc = GetStartOfLocalDayUtc(_timeProvider.GetUtcNow());
        var activity = _database.GetDeviceActivities(startOfTodayUtc, cancellationToken)
            .Single(item => item.DeviceId == deviceId);
        var listenerCanServeLan = _listenerOptions.Enabled &&
            new DeviceEndpointAdvisor(_listenerOptions).GetAdvice().IsLanReachable;
        var activeConfiguration = _database.GetActiveMicrosoftConfiguration(cancellationToken);
        var activeSender = activeConfiguration?.AuthorizedSender;
        var microsoftReady = MicrosoftRuntimeReadinessPolicy.Evaluate(
            activeSender is not null,
            activeConfiguration?.Fingerprint,
            _identityState,
            _exchangeState) == MicrosoftRuntimeReadiness.Ready;
        return CreateItem(device, activity, activeSender, listenerCanServeLan, microsoftReady);
    }

    private DeviceOverviewItem CreateItem(
        DeviceDefinition device,
        DeviceActivitySnapshot activity,
        string? activeSender,
        bool listenerCanServeLan,
        bool microsoftReady)
    {
        var senderMatches = !string.IsNullOrWhiteSpace(activeSender) &&
            device.AllowedSenders.Contains(activeSender, StringComparer.OrdinalIgnoreCase);
        var authenticationAvailable = device.AuthenticationMode == DeviceAuthenticationMode.Legacy ||
            _listenerOptions.AllowCleartextAuthentication;
        var status = !device.Enabled
            ? DeviceUiStatus.Disabled
            : !senderMatches || !microsoftReady || !listenerCanServeLan || !authenticationAvailable ||
                activity.LatestMessageState == QueueState.PermanentFailure
                ? DeviceUiStatus.NeedsAttention
                : DeviceUiStatus.Ready;
        return new DeviceOverviewItem(device, activity, status);
    }

    private static DateTimeOffset GetStartOfLocalDayUtc(DateTimeOffset nowUtc)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, TimeZoneInfo.Local);
        return new DateTimeOffset(local.Date, local.Offset).ToUniversalTime();
    }
}

public sealed record DeviceOverviewSnapshot(
    IReadOnlyList<DeviceOverviewItem> Devices,
    QueueMetrics Queue,
    MessageOutcomeCounts Today,
    string? ActiveSender,
    string? ActiveMicrosoftConfigurationFingerprint);

public sealed record DeviceOverviewItem(
    DeviceDefinition Device,
    DeviceActivitySnapshot Activity,
    DeviceUiStatus Status);

public enum DeviceUiStatus
{
    Ready,
    NeedsAttention,
    Disabled,
}
