// SPDX-License-Identifier: MPL-2.0

using RelayBridge.PrinterConfigurator;

if (!OperatingSystem.IsWindows() || !PrinterConfiguratorArguments.TryParse(args, out var revision))
{
    return 2;
}

try
{
    return await PrinterConfigurator.RunAsync(revision, CancellationToken.None).ConfigureAwait(false);
}
catch (PrinterApplyException exception)
{
    PrinterConfiguratorDialog.ShowFailure(exception);
    return 1;
}
catch (Exception)
{
    PrinterConfiguratorDialog.ShowError(
        "Printer connectivity could not be prepared. No configuration write was confirmed. Return to the local RelayBridge page and try again.");
    return 1;
}
