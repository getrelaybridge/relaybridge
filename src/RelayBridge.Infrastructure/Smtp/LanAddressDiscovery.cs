// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RelayBridge.Core.PrinterConnectivity;

namespace RelayBridge.Infrastructure.Smtp;

public interface ILanAddressDiscovery
{
    LanAddressDiscoveryResult Discover();
}

public sealed class SystemLanAddressDiscovery : ILanAddressDiscovery
{
    private readonly ILanNetworkAdapterSource _adapterSource;

    public SystemLanAddressDiscovery()
        : this(new SystemLanNetworkAdapterSource())
    {
    }

    internal SystemLanAddressDiscovery(ILanNetworkAdapterSource adapterSource)
    {
        _adapterSource = adapterSource;
    }

    public LanAddressDiscoveryResult Discover()
    {
        IReadOnlyList<ILanNetworkAdapter> adapters;
        try
        {
            adapters = _adapterSource.GetAdapters();
        }
        catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
        {
            return LanAddressDiscoveryResult.Unavailable;
        }

        var results = new List<DeviceEndpointCandidate>();
        var incomplete = false;
        foreach (var adapter in adapters)
        {
            try
            {
                var status = adapter.OperationalStatus;
                var interfaceType = adapter.NetworkInterfaceType;
                var interfaceName = adapter.Name;
                if (status != OperationalStatus.Up ||
                    interfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (var address in adapter.UnicastAddresses)
                {
                    if (TrustedLanAddress.IsPrivateUnicast(address))
                    {
                        results.Add(new DeviceEndpointCandidate(
                            address,
                            interfaceName,
                            interfaceType));
                    }
                }
            }
            catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
            {
                incomplete = true;
            }
        }

        return new LanAddressDiscoveryResult(
            results
                .DistinctBy(candidate => candidate.Address)
                .OrderBy(candidate => candidate.Address.AddressFamily)
                .ThenBy(candidate => candidate.InterfaceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Address.ToString(), StringComparer.Ordinal)
                .ToArray(),
            incomplete);
    }
}

internal interface ILanNetworkAdapterSource
{
    IReadOnlyList<ILanNetworkAdapter> GetAdapters();
}

internal interface ILanNetworkAdapter
{
    string Name { get; }

    OperationalStatus OperationalStatus { get; }

    NetworkInterfaceType NetworkInterfaceType { get; }

    IReadOnlyList<IPAddress> UnicastAddresses { get; }
}

internal sealed class SystemLanNetworkAdapterSource : ILanNetworkAdapterSource
{
    public IReadOnlyList<ILanNetworkAdapter> GetAdapters()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Select(adapter => (ILanNetworkAdapter)new SystemLanNetworkAdapter(adapter))
            .ToArray();
    }
}

internal sealed class SystemLanNetworkAdapter(NetworkInterface adapter) : ILanNetworkAdapter
{
    public string Name => adapter.Name;

    public OperationalStatus OperationalStatus => adapter.OperationalStatus;

    public NetworkInterfaceType NetworkInterfaceType => adapter.NetworkInterfaceType;

    public IReadOnlyList<IPAddress> UnicastAddresses => adapter.GetIPProperties()
        .UnicastAddresses
        .Select(address => address.Address)
        .ToArray();
}

public sealed record LanAddressDiscoveryResult(
    IReadOnlyList<DeviceEndpointCandidate> Candidates,
    bool IsIncomplete)
{
    public static LanAddressDiscoveryResult Unavailable { get; } = new([], true);
}

public static class TrustedLanAddress
{
    public static bool IsPrivateUnicast(IPAddress address)
        => PrivateLanAddressPolicy.IsPrivateUnicast(address);
}
