// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Net.Sockets;

namespace RelayBridge.Core.PrinterConnectivity;

public static class PrivateLanAddressPolicy
{
    public static bool IsPrivateUnicast(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) ||
            address.IsIPv6LinkLocal ||
            address.IsIPv6Multicast)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes is [10, _, _, _] ||
                bytes is [172, >= 16 and <= 31, _, _] ||
                bytes is [192, 168, _, _];
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 &&
            (bytes[0] & 0xfe) == 0xfc;
    }
}
