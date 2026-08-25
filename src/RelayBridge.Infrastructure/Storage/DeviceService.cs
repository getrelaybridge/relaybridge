// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using RelayBridge.Core.Devices;

namespace RelayBridge.Infrastructure.Storage;

public sealed class DeviceService
{
    private readonly RelayDatabase _database;
    private readonly Func<GeneratedDevicePassword> _passwordFactory;
    private readonly string _dummyVerifier;
    private readonly SemaphoreSlim _authenticationGate = new(1, 1);

    public DeviceService(RelayDatabase database)
        : this(database, DevicePassword.Generate)
    {
    }

    internal DeviceService(RelayDatabase database, Func<GeneratedDevicePassword> passwordFactory)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _passwordFactory = passwordFactory ?? throw new ArgumentNullException(nameof(passwordFactory));
        _dummyVerifier = DevicePassword.CreateVerifier("RelayBridge dummy verifier input");
    }

    public ProvisionedDevice AddAuthenticatedDevice(
        string name,
        string smtpUsername,
        IEnumerable<string> allowedNetworks,
        IEnumerable<string> allowedSenders,
        bool enabled = true,
        CancellationToken cancellationToken = default)
    {
        return AddAuthenticatedDeviceCore(
            Guid.CreateVersion7(),
            name,
            description: null,
            smtpUsername,
            allowedNetworks,
            allowedSenders,
            enabled,
            cancellationToken);
    }

    public ProvisionedDevice ProvisionAuthenticatedDevice(
        string name,
        string? description,
        IEnumerable<string> allowedNetworks,
        IEnumerable<string> allowedSenders,
        bool enabled = true,
        CancellationToken cancellationToken = default)
    {
        var username = CreateAvailableUsername(name, cancellationToken);
        return AddAuthenticatedDeviceCore(
            Guid.CreateVersion7(),
            name,
            description,
            username,
            allowedNetworks,
            allowedSenders,
            enabled,
            cancellationToken);
    }

    public ProvisionedDevice ProvisionAuthenticatedDeviceForActiveMicrosoftConfiguration(
        Guid deviceId,
        string name,
        string? description,
        IEnumerable<string> allowedNetworks,
        IEnumerable<string> allowedSenders,
        string expectedConfigurationFingerprint,
        string expectedSender,
        bool enabled = true,
        CancellationToken cancellationToken = default)
    {
        var username = CreateAvailableUsername(name, cancellationToken);
        return AddAuthenticatedDeviceCore(
            deviceId,
            name,
            description,
            username,
            allowedNetworks,
            allowedSenders,
            enabled,
            cancellationToken,
            expectedConfigurationFingerprint,
            expectedSender);
    }

    private ProvisionedDevice AddAuthenticatedDeviceCore(
        Guid deviceId,
        string name,
        string? description,
        string smtpUsername,
        IEnumerable<string> allowedNetworks,
        IEnumerable<string> allowedSenders,
        bool enabled,
        CancellationToken cancellationToken,
        string? expectedConfigurationFingerprint = null,
        string? expectedSender = null)
    {
        var generated = _passwordFactory();
        var device = DeviceDefinition.CreateAuthenticated(
            deviceId,
            name,
            description,
            enabled,
            smtpUsername,
            generated.Verifier,
            allowedNetworks,
            allowedSenders,
            DateTimeOffset.UtcNow);
        if (expectedConfigurationFingerprint is null)
        {
            _database.AddDevice(device, cancellationToken);
        }
        else
        {
            _database.AddDeviceForActiveMicrosoftConfiguration(
                device,
                expectedConfigurationFingerprint,
                expectedSender!,
                cancellationToken);
        }

        return new ProvisionedDevice(device, generated.Plaintext);
    }

    public DeviceDefinition AddLegacyDevice(
        string name,
        IEnumerable<string> allowedNetworks,
        IEnumerable<string> allowedSenders,
        bool enabled = true,
        CancellationToken cancellationToken = default)
    {
        return AddLegacyDevice(
            name,
            description: null,
            allowedNetworks,
            allowedSenders,
            enabled,
            cancellationToken);
    }

    public DeviceDefinition AddLegacyDevice(
        string name,
        string? description,
        IEnumerable<string> allowedNetworks,
        IEnumerable<string> allowedSenders,
        bool enabled = true,
        CancellationToken cancellationToken = default)
    {
        var device = DeviceDefinition.CreateLegacy(
            Guid.CreateVersion7(),
            name,
            description,
            enabled,
            allowedNetworks,
            allowedSenders,
            DateTimeOffset.UtcNow);
        _database.AddDevice(device, cancellationToken);
        return device;
    }

    public DeviceDefinition AddLegacyDeviceForActiveMicrosoftConfiguration(
        Guid deviceId,
        string name,
        string? description,
        IEnumerable<string> allowedNetworks,
        IEnumerable<string> allowedSenders,
        string expectedConfigurationFingerprint,
        string expectedSender,
        bool enabled = true,
        CancellationToken cancellationToken = default)
    {
        var device = DeviceDefinition.CreateLegacy(
            deviceId,
            name,
            description,
            enabled,
            allowedNetworks,
            allowedSenders,
            DateTimeOffset.UtcNow);
        _database.AddDeviceForActiveMicrosoftConfiguration(
            device,
            expectedConfigurationFingerprint,
            expectedSender,
            cancellationToken);
        return device;
    }

    public DeviceDefinition UpdateDevice(
        Guid deviceId,
        string name,
        string? description,
        IEnumerable<string> allowedNetworks,
        IEnumerable<string> allowedSenders,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var existing = GetRequiredDevice(deviceId, cancellationToken);
        var updated = existing.WithConfiguration(
            name,
            description,
            existing.Enabled,
            allowedNetworks,
            allowedSenders);
        _database.UpdateDeviceConfiguration(updated, expectedRevision, cancellationToken);
        return GetRequiredDevice(deviceId, cancellationToken);
    }

    public DeviceDefinition SetEnabled(
        Guid deviceId,
        bool enabled,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        _database.SetDeviceEnabled(deviceId, enabled, expectedRevision, cancellationToken);
        return GetRequiredDevice(deviceId, cancellationToken);
    }

    public ProvisionedDevice ResetPassword(
        Guid deviceId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var existing = GetRequiredDevice(deviceId, cancellationToken);
        if (existing.AuthenticationMode != DeviceAuthenticationMode.Authenticated)
        {
            throw new InvalidOperationException("Legacy devices do not use an SMTP password.");
        }

        var generated = _passwordFactory();
        _database.UpdateDevicePasswordVerifier(deviceId, generated.Verifier, expectedRevision, cancellationToken);
        return new ProvisionedDevice(GetRequiredDevice(deviceId, cancellationToken), generated.Plaintext);
    }

    private DeviceDefinition GetRequiredDevice(Guid deviceId, CancellationToken cancellationToken)
    {
        return _database.GetDevice(deviceId, cancellationToken)
            ?? throw new InvalidOperationException("The device no longer exists.");
    }

    private string CreateAvailableUsername(string name, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var previousWasSeparator = false;
        foreach (var character in name.Trim().ToLowerInvariant())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(character);
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator && builder.Length > 0)
            {
                builder.Append('-');
                previousWasSeparator = true;
            }

            if (builder.Length >= 40)
            {
                break;
            }
        }

        var baseName = builder.ToString().Trim('-');
        if (baseName.Length == 0)
        {
            baseName = "device";
        }

        var existing = _database.GetDevices(cancellationToken)
            .Select(device => device.SmtpUsername)
            .Where(username => username is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(baseName))
        {
            return baseName;
        }

        for (var suffix = 2; suffix < 10_000; suffix++)
        {
            var suffixText = $"-{suffix}";
            var prefixLength = Math.Min(baseName.Length, 48 - suffixText.Length);
            var candidate = baseName[..prefixLength] + suffixText;
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("RelayBridge could not generate a unique SMTP username.");
    }

    public async Task<DeviceDefinition?> AuthenticateAsync(
        string username,
        string password,
        IPAddress sourceAddress,
        CancellationToken cancellationToken = default)
    {
        // PBKDF2 is intentionally expensive. Serialize verification so unauthenticated
        // peers cannot fan password hashing out across all available CPU cores.
        await _authenticationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var device = _database
                .GetDevices(cancellationToken)
                .SingleOrDefault(candidate =>
                    candidate.AuthenticationMode == DeviceAuthenticationMode.Authenticated &&
                    string.Equals(candidate.SmtpUsername, username, StringComparison.OrdinalIgnoreCase));

            var verifier = device?.PasswordVerifier ?? _dummyVerifier;
            var passwordMatches = DevicePassword.Verify(password, verifier);
            return device is not null &&
                passwordMatches &&
                device.Enabled &&
                device.IsSourceAllowed(sourceAddress)
                    ? device
                    : null;
        }
        finally
        {
            _authenticationGate.Release();
        }
    }

    public bool TryResolveLegacyDevice(
        IPAddress sourceAddress,
        string sender,
        out DeviceDefinition? device,
        out bool sourceMatched,
        CancellationToken cancellationToken = default)
    {
        var candidates = _database
            .GetDevices(cancellationToken)
            .Where(device =>
                device.AuthenticationMode == DeviceAuthenticationMode.Legacy &&
                device.Enabled &&
                device.IsSourceAllowed(sourceAddress))
            .ToArray();

        sourceMatched = candidates.Length > 0;
        var senderMatches = candidates
            .Where(candidate => candidate.IsSenderAllowed(sender))
            .Take(2)
            .ToArray();
        device = senderMatches.Length == 1 ? senderMatches[0] : null;
        return device is not null;
    }
}

public sealed class DeviceConcurrencyException()
    : InvalidOperationException("This device changed while you were editing it. Reload the latest settings and try again.");

public sealed class MicrosoftConfigurationConcurrencyException()
    : InvalidOperationException("RelayBridge Microsoft configuration changed while you were reviewing this device. Review the latest settings and try again.");

public sealed class ProvisionedDevice(DeviceDefinition device, string plaintextPassword)
{
    public DeviceDefinition Device { get; } = device;

    [JsonIgnore]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string PlaintextPassword { get; } = plaintextPassword;

    public override string ToString() => $"{nameof(ProvisionedDevice)} {{ DeviceId = {Device.Id:D}, PlaintextPassword = [REDACTED] }}";
}
