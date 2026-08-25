// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Net.NetworkInformation;

namespace RelayBridge.Infrastructure.Smtp;

public sealed class DeviceEndpointAdvisor
{
    private readonly SmtpListenerOptions _options;
    private readonly ILanAddressDiscovery _discovery;

    public DeviceEndpointAdvisor(SmtpListenerOptions options, ILanAddressDiscovery? discovery = null)
    {
        _options = options;
        _discovery = discovery ?? new SystemLanAddressDiscovery();
    }

    public DeviceEndpointAdvice GetAdvice()
    {
        var listenAddress = _options.GetListenAddress();
        LanAddressDiscoveryResult discovery;
        try
        {
            discovery = _discovery.Discover();
        }
        catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
        {
            discovery = LanAddressDiscoveryResult.Unavailable;
        }

        var availableCandidates = discovery.Candidates
            .Where(candidate => TrustedLanAddress.IsPrivateUnicast(candidate.Address))
            .ToArray();
        if (!_options.Enabled)
        {
            return new DeviceEndpointAdvice(
                [],
                availableCandidates,
                _options.Port,
                false,
                false,
                _options.ListenAddress,
                "The SMTP listener is disabled. A printer cannot reach RelayBridge until an administrator enables it.");
        }

        if (IPAddress.IsLoopback(listenAddress))
        {
            return new DeviceEndpointAdvice(
                [],
                availableCandidates,
                _options.Port,
                false,
                false,
                _options.ListenAddress,
                "The SMTP listener is local-only. A printer on the LAN cannot reach RelayBridge until an administrator deliberately configures a trusted-LAN SMTP binding.");
        }

        if (listenAddress.Equals(IPAddress.Any) || listenAddress.Equals(IPAddress.IPv6Any))
        {
            return new DeviceEndpointAdvice(
                [],
                availableCandidates,
                _options.Port,
                false,
                false,
                _options.ListenAddress,
                "The SMTP listener uses a wildcard address. Bind one explicit trusted private address before configuring a printer.");
        }

        if (!TrustedLanAddress.IsPrivateUnicast(listenAddress))
        {
            return new DeviceEndpointAdvice(
                [],
                availableCandidates,
                _options.Port,
                false,
                false,
                _options.ListenAddress,
                "The configured SMTP address is not an acceptable private printer-network address.");
        }

        var candidates = availableCandidates
            .Where(candidate => candidate.Address.Equals(listenAddress))
            .ToArray();

        return candidates.Length == 0
            ? new DeviceEndpointAdvice(
                [],
                availableCandidates,
                _options.Port,
                false,
                false,
                _options.ListenAddress,
                discovery.IsIncomplete
                    ? "Printer network interfaces are currently unavailable. RelayBridge will not guess a printer address."
                    : "RelayBridge did not find the configured private LAN address on an active interface.")
            : new DeviceEndpointAdvice(
                candidates,
                availableCandidates,
                _options.Port,
                true,
                _options.AllowCleartextAuthentication,
                _options.ListenAddress,
                null);
    }

}

public sealed record DeviceEndpointAdvice(
    IReadOnlyList<DeviceEndpointCandidate> Candidates,
    IReadOnlyList<DeviceEndpointCandidate> AvailableCandidates,
    int Port,
    bool IsLanReachable,
    bool IsAuthenticatedSmtpAvailable,
    string ConfiguredAddress,
    string? Warning);

public sealed record DeviceEndpointCandidate(
    IPAddress Address,
    string InterfaceName,
    NetworkInterfaceType InterfaceType = NetworkInterfaceType.Unknown);
