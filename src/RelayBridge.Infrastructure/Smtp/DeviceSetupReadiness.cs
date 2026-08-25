// SPDX-License-Identifier: MPL-2.0

using RelayBridge.Core.Devices;

namespace RelayBridge.Infrastructure.Smtp;

public sealed record DeviceSetupReadiness(
    bool MicrosoftConfigured,
    bool MicrosoftReady,
    bool PrinterConnectivityReady,
    bool SmtpAuthenticationReady,
    bool InboundTlsAvailable)
{
    public DeviceSetupPrimaryAction PrimaryAction => !MicrosoftConfigured
        ? DeviceSetupPrimaryAction.SetUpMicrosoft365
        : !MicrosoftReady
            ? DeviceSetupPrimaryAction.RepairMicrosoft365
            : !PrinterConnectivityReady || !SmtpAuthenticationReady
                ? DeviceSetupPrimaryAction.PreparePrinterConnectivity
                : DeviceSetupPrimaryAction.AddDevice;

    public bool CanCreate(DeviceAuthenticationMode? authenticationMode)
    {
        return MicrosoftReady &&
            PrinterConnectivityReady &&
            authenticationMode is not null &&
            (authenticationMode == DeviceAuthenticationMode.Legacy || SmtpAuthenticationReady);
    }
}

public enum DeviceSetupPrimaryAction
{
    SetUpMicrosoft365,
    RepairMicrosoft365,
    PreparePrinterConnectivity,
    AddDevice,
}
