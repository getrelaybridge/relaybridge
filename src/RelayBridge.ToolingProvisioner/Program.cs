// SPDX-License-Identifier: MPL-2.0

using RelayBridge.ToolingProvisioner;

if (!OperatingSystem.IsWindows() || !ToolingInstaller.IsElevatedAdministrator())
{
    return 5;
}

try
{
    var arguments = ProvisionerArguments.Parse(args);
    var acquisitionLock = AcquisitionLock.LoadEmbedded();
    var installer = new ToolingInstaller(acquisitionLock);
    switch (arguments.Action)
    {
        case ProvisionerAction.Install:
        case ProvisionerAction.Repair:
            installer.InstallOrRepair(
                arguments.CacheRoot!,
                arguments.ReleaseIdentity!,
                arguments.IsFreshAcceptance);
            break;
        case ProvisionerAction.Uninstall:
            installer.Uninstall();
            break;
        default:
            return 64;
    }

    return 0;
}
catch (ToolingProvisioningException exception)
{
    Console.Error.WriteLine($"RelayBridge Microsoft prerequisite provisioning failed: {exception.Message}");
    return 1603;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"RelayBridge Microsoft prerequisite provisioning failed ({exception.GetType().Name}).");
    return 1603;
}
