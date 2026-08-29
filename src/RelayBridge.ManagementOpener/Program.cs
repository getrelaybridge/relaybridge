// SPDX-License-Identifier: MPL-2.0

using RelayBridge.ManagementOpener;

if (!OperatingSystem.IsWindows() || !ManagementOpenerArguments.TryParse(args, out var destination))
{
    return 2;
}

try
{
    await ManagementOpener.OpenAsync(destination, CancellationToken.None).ConfigureAwait(false);
    return 0;
}
catch (Exception)
{
    ManagementOpener.ShowError();
    return 1;
}
