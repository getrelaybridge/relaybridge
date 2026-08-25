// SPDX-License-Identifier: MPL-2.0

using RelayBridge.SetupLauncher;

if (!OperatingSystem.IsWindows() || !LauncherArguments.AreValid(args))
{
    return 2;
}

try
{
    return await SetupLauncher.RunAsync(CancellationToken.None).ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    return 1;
}
catch (Exception exception) when (exception is IOException or InvalidDataException or
    InvalidOperationException or System.ComponentModel.Win32Exception)
{
    return 2;
}
