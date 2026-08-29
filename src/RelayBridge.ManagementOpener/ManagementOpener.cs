// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace RelayBridge.ManagementOpener;

internal enum ManagementDestination
{
    Dashboard,
    MicrosoftSetup,
}

internal static class ManagementOpenerArguments
{
    internal static bool TryParse(string[] arguments, out ManagementDestination destination)
    {
        destination = ManagementDestination.Dashboard;
        if (arguments.Length == 0)
        {
            return true;
        }

        if (arguments.Length == 1 && string.Equals(arguments[0], "--setup", StringComparison.Ordinal))
        {
            destination = ManagementDestination.MicrosoftSetup;
            return true;
        }

        return false;
    }
}

[SupportedOSPlatform("windows")]
internal static partial class ManagementOpener
{
    internal const string RegistryPath = @"SOFTWARE\RelayBridge";
    internal const string RegistryValueName = "ManagementEndpoint";

    internal static async Task OpenAsync(
        ManagementDestination destination,
        CancellationToken cancellationToken = default)
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryPath, writable: false)
            ?? throw new InvalidOperationException("RelayBridge management endpoint state is unavailable.");
        var stored = key.GetValue(RegistryValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        var baseUri = ValidateBaseEndpoint(stored);
        await WaitForReadinessAsync(baseUri, cancellationToken).ConfigureAwait(false);
        var relative = destination == ManagementDestination.MicrosoftSetup ? "/setup/microsoft" : "/";
        var target = new Uri(baseUri, relative);
        _ = Process.Start(new ProcessStartInfo
        {
            FileName = target.AbsoluteUri,
            UseShellExecute = true,
        }) ?? throw new InvalidOperationException("The default browser could not be opened.");
    }

    internal static async Task WaitForReadinessAsync(
        Uri baseUri,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        TimeSpan? retryDelay = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(45));
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var health = new Uri(baseUri, "/health");
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await client.GetAsync(health, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
            }

            await Task.Delay(
                retryDelay ?? TimeSpan.FromMilliseconds(500),
                cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("RelayBridge management did not become ready in time.");
    }

    internal static Uri ValidateBaseEndpoint(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            uri.Port is < 1 or > 65535 ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/" ||
            !(string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
              IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address)))
        {
            throw new InvalidDataException("RelayBridge management endpoint state is invalid.");
        }

        return uri;
    }

    internal static void ShowError() => _ = MessageBox(
        IntPtr.Zero,
        "RelayBridge could not open the protected local management endpoint. Confirm the RelayBridge service is installed and running, then try again.",
        "RelayBridge",
        0x00000010 | 0x00010000);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr window, string text, string caption, uint type);
}
