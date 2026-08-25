// SPDX-License-Identifier: MPL-2.0

using System.Net;

namespace RelayBridge.Infrastructure.Smtp;

public sealed class SmtpListenerOptions
{
    public bool Enabled { get; set; } = true;

    public string ListenAddress { get; set; } = IPAddress.Loopback.ToString();

    public int Port { get; set; } = 2525;

    public string ServerName { get; set; } = "RelayBridge";

    public bool AllowCleartextAuthentication { get; set; }

    internal bool AllowInsecureLoopbackAuthenticationForTests { get; set; }

    internal bool AllowEphemeralPortForTests { get; set; }

    public int MaxConnections { get; set; } = 20;

    public int MaxConnectionsPerIp { get; set; } = 5;

    public int MaxCommandsPerSession { get; set; } = 200;

    public int MaxAuthenticationAttempts { get; set; } = 3;

    public int MaxCommandLength { get; set; } = 2048;

    public int MaxDataLineLength { get; set; } = 64 * 1024;

    public int MaxRecipients { get; set; } = 50;

    public long MaxMessageBytes { get; set; } = 35L * 1024 * 1024;

    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public IPAddress GetListenAddress()
    {
        return IPAddress.TryParse(ListenAddress, out var address)
            ? address
            : throw new InvalidOperationException($"SMTP listen address '{ListenAddress}' is invalid.");
    }

    public void Validate()
    {
        var listenAddress = GetListenAddress();
        if (AllowCleartextAuthentication &&
            !TrustedLanAddress.IsPrivateUnicast(listenAddress) &&
            !(AllowInsecureLoopbackAuthenticationForTests && IPAddress.IsLoopback(listenAddress)))
        {
            throw new InvalidOperationException(
                "Cleartext SMTP authentication requires one explicit RFC1918 IPv4 or IPv6 ULA listener address. Wildcard, loopback, link-local, multicast, and public bindings are not allowed.");
        }
        if ((Port is < 1 or > 65535) && !(AllowEphemeralPortForTests && Port == 0))
        {
            throw new InvalidOperationException("SMTP port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(ServerName) || ServerName.Any(character => character is < '!' or > '~'))
        {
            throw new InvalidOperationException("SMTP server name must contain printable ASCII characters.");
        }

        if (MaxConnections < 1 || MaxConnectionsPerIp < 1 || MaxConnectionsPerIp > MaxConnections)
        {
            throw new InvalidOperationException("SMTP connection limits are invalid.");
        }

        if (MaxCommandsPerSession < 10 || MaxAuthenticationAttempts < 1)
        {
            throw new InvalidOperationException("SMTP session limits are invalid.");
        }

        if (MaxCommandLength < 512 || MaxDataLineLength < 998)
        {
            throw new InvalidOperationException("SMTP line limits are too small for interoperable operation.");
        }

        if (MaxRecipients < 1 || MaxMessageBytes < 1024)
        {
            throw new InvalidOperationException("SMTP message limits are invalid.");
        }

        if (IdleTimeout < TimeSpan.FromSeconds(1) || IdleTimeout > TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException("SMTP idle timeout must be between one second and one hour.");
        }
    }
}
