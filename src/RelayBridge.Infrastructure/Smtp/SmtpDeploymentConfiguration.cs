// SPDX-License-Identifier: MPL-2.0

using RelayBridge.Core.PrinterConnectivity;

namespace RelayBridge.Infrastructure.Smtp;

public static class SmtpDeploymentConfiguration
{
    public static string Create(string listenAddress, int port)
        => PrinterConnectivityConfiguration.Create(listenAddress, port);

    public static string GetEnvironmentOverridePath(string contentRootPath, string environmentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        ValidateEnvironmentName(environmentName);
        return Path.Combine(
            Path.GetFullPath(contentRootPath),
            $"appsettings.{environmentName}.json");
    }

    public static string GetDownloadFileName(string environmentName)
    {
        ValidateEnvironmentName(environmentName);
        return $"RelayBridge-appsettings.{environmentName}.json";
    }

    public static string CreateAdministratorCommands(
        string destinationPath,
        string downloadFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadFileName);
        if (!Path.IsPathFullyQualified(destinationPath) ||
            !string.Equals(Path.GetFileName(downloadFileName), downloadFileName, StringComparison.Ordinal))
        {
            throw new ArgumentException("The configuration paths are invalid.");
        }

        var relativeDownloadPath = Path.Combine("Downloads", downloadFileName);
        return string.Join(
            Environment.NewLine,
            $"$relayBridgeConfigSource = Join-Path ([Environment]::GetFolderPath('UserProfile')) {QuotePowerShell(relativeDownloadPath)}",
            $"Copy-Item -LiteralPath $relayBridgeConfigSource -Destination {QuotePowerShell(destinationPath)} -Force",
            "Restart-Service -Name 'RelayBridge'");
    }

    public static string CreateFirewallCommand(
        string listenAddress,
        int port,
        string hostExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostExecutablePath);
        if (!Path.IsPathFullyQualified(hostExecutablePath))
        {
            throw new ArgumentException("The RelayBridge Host path must be absolute.", nameof(hostExecutablePath));
        }

        var options = new SmtpListenerOptions
        {
            Enabled = true,
            ListenAddress = listenAddress,
            Port = port,
            AllowCleartextAuthentication = true,
        };
        options.Validate();

        return string.Join(
            " ",
            "New-NetFirewallRule",
            $"-DisplayName {QuotePowerShell($"RelayBridge SMTP {options.ListenAddress}:{options.Port}")}",
            "-Direction Inbound",
            "-Action Allow",
            "-Enabled True",
            "-Profile Private",
            "-Protocol TCP",
            $"-Program {QuotePowerShell(Path.GetFullPath(hostExecutablePath))}",
            $"-LocalAddress {QuotePowerShell(options.ListenAddress)}",
            $"-LocalPort {options.Port}",
            "-RemoteAddress 'LocalSubnet'");
    }

    private static void ValidateEnvironmentName(string environmentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        if (environmentName.Length > 64 ||
            environmentName.Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
        {
            throw new ArgumentException("The host environment name is invalid.", nameof(environmentName));
        }
    }

    private static string QuotePowerShell(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
