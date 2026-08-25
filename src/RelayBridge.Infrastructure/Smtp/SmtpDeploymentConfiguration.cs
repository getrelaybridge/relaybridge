// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;

namespace RelayBridge.Infrastructure.Smtp;

public static class SmtpDeploymentConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string Create(string listenAddress, int port)
    {
        var options = new SmtpListenerOptions
        {
            Enabled = true,
            ListenAddress = listenAddress,
            Port = port,
            AllowCleartextAuthentication = true,
        };
        options.Validate();

        return JsonSerializer.Serialize(
            new
            {
                Smtp = new
                {
                    options.Enabled,
                    options.ListenAddress,
                    options.Port,
                    options.AllowCleartextAuthentication,
                },
            },
            JsonOptions);
    }
}
