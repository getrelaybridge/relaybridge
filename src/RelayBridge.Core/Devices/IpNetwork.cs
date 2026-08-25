// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Net.Sockets;

namespace RelayBridge.Core.Devices;

public sealed class IpNetwork
{
    private readonly byte[] _networkBytes;

    private IpNetwork(IPAddress address, int prefixLength)
    {
        AddressFamily = address.AddressFamily;
        PrefixLength = prefixLength;
        _networkBytes = address.GetAddressBytes();
        ClearHostBits(_networkBytes, prefixLength);
    }

    public AddressFamily AddressFamily { get; }

    public int PrefixLength { get; }

    public bool IsPrivateOrLocal
    {
        get
        {
            if (AddressFamily == AddressFamily.InterNetwork)
            {
                return
                    (PrefixLength >= 8 && _networkBytes[0] is 10 or 127) ||
                    (PrefixLength >= 12 && _networkBytes[0] == 172 && (_networkBytes[1] & 0xf0) == 16) ||
                    (PrefixLength >= 16 && _networkBytes[0] == 192 && _networkBytes[1] == 168) ||
                    (PrefixLength >= 16 && _networkBytes[0] == 169 && _networkBytes[1] == 254);
            }

            var uniqueLocal = PrefixLength >= 7 && (_networkBytes[0] & 0xfe) == 0xfc;
            var linkLocal = PrefixLength >= 10 &&
                _networkBytes[0] == 0xfe &&
                (_networkBytes[1] & 0xc0) == 0x80;
            var loopback = PrefixLength == 128 &&
                _networkBytes[..^1].All(value => value == 0) &&
                _networkBytes[^1] == 1;
            return uniqueLocal || linkLocal || loopback;
        }
    }

    public static IpNetwork Parse(string value)
    {
        if (!TryParse(value, out var network))
        {
            throw new FormatException($"'{value}' is not a valid IP address or CIDR network.");
        }

        return network;
    }

    public static bool TryParse(string? value, out IpNetwork network)
    {
        network = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split('/', 2, StringSplitOptions.TrimEntries);
        if (!IPAddress.TryParse(parts[0], out var address))
        {
            return false;
        }

        address = Normalize(address);
        var maximumPrefixLength = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefixLength = maximumPrefixLength;
        if (parts.Length == 2 &&
            (!int.TryParse(parts[1], out prefixLength) || prefixLength < 0 || prefixLength > maximumPrefixLength))
        {
            return false;
        }

        network = new IpNetwork(address, prefixLength);
        return true;
    }

    public bool Contains(IPAddress address)
    {
        address = Normalize(address);
        if (address.AddressFamily != AddressFamily)
        {
            return false;
        }

        var candidate = address.GetAddressBytes();
        var completeBytes = PrefixLength / 8;
        var remainingBits = PrefixLength % 8;

        for (var index = 0; index < completeBytes; index++)
        {
            if (candidate[index] != _networkBytes[index])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xff << (8 - remainingBits));
        return (candidate[completeBytes] & mask) == (_networkBytes[completeBytes] & mask);
    }

    public override string ToString()
    {
        return $"{new IPAddress(_networkBytes)}/{PrefixLength}";
    }

    private static IPAddress Normalize(IPAddress address)
    {
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    private static void ClearHostBits(byte[] bytes, int prefixLength)
    {
        var completeBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        if (remainingBits != 0)
        {
            bytes[completeBytes] &= (byte)(0xff << (8 - remainingBits));
            completeBytes++;
        }

        Array.Clear(bytes, completeBytes, bytes.Length - completeBytes);
    }
}
