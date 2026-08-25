// SPDX-License-Identifier: MPL-2.0

using System.Net;

namespace RelayBridge.Host.Services;

public sealed class ManagementOptions
{
    public int Port { get; set; } = 5080;

    public void Validate()
    {
        if (Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("Management port must be between 1 and 65535.");
        }
    }
}

public static class ManagementBindingPolicy
{
    private static readonly string[] GenericUrlKeys = ["urls", "http_ports", "https_ports"];

    public static void Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        foreach (var key in GenericUrlKeys)
        {
            var value = configuration[key];
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (key is "http_ports" or "https_ports")
            {
                throw new InvalidOperationException(
                    $"Management binding '{key}' is not allowed because it creates a wildcard listener. Configure Management:Port instead.");
            }

            ValidateUrls(value, key);
        }

        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            var url = endpoint["Url"];
            if (!string.IsNullOrWhiteSpace(url))
            {
                ValidateUrls(url, $"Kestrel:Endpoints:{endpoint.Key}:Url");
            }
        }
    }

    public static bool IsLoopbackUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
    }

    private static void ValidateUrls(string value, string configurationKey)
    {
        var urls = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (urls.Length == 0 || urls.Any(url => !IsLoopbackUrl(url)))
        {
            throw new InvalidOperationException(
                $"Management binding '{configurationKey}' must contain only loopback HTTP listeners. Use Management:Port for the code-owned loopback listener.");
        }
    }
}
