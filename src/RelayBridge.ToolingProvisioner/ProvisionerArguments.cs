// SPDX-License-Identifier: MPL-2.0

namespace RelayBridge.ToolingProvisioner;

internal enum ProvisionerAction
{
    Install,
    Repair,
    Uninstall,
}

internal sealed record ProvisionerArguments(
    ProvisionerAction Action,
    string? CacheRoot,
    string? ReleaseIdentity,
    int UiLevel,
    int AcceptanceVariable)
{
    internal bool IsFreshAcceptance => UiLevel == 4 || AcceptanceVariable == 1;

    internal static ProvisionerArguments Parse(string[] args)
    {
        if (args.Length == 1 && args[0].Equals("uninstall", StringComparison.Ordinal))
        {
            return new ProvisionerArguments(ProvisionerAction.Uninstall, null, null, 0, 0);
        }

        if (args.Length != 9 ||
            (!args[0].Equals("install", StringComparison.Ordinal) &&
             !args[0].Equals("repair", StringComparison.Ordinal)) ||
            args[1] != "--cache" || args[3] != "--release" ||
            args[5] != "--ui-level" || args[7] != "--accept-variable" ||
            !Path.IsPathFullyQualified(args[2]) ||
            string.IsNullOrWhiteSpace(args[4]) || args[4].Length > 64 ||
            !int.TryParse(args[6], out var uiLevel) || uiLevel is < 2 or > 4 ||
            !int.TryParse(args[8], out var acceptance) || acceptance is < 0 or > 1)
        {
            throw new ToolingProvisioningException("The provisioning invocation is invalid.");
        }

        return new ProvisionerArguments(
            args[0] == "install" ? ProvisionerAction.Install : ProvisionerAction.Repair,
            Path.GetFullPath(args[2]),
            args[4],
            uiLevel,
            acceptance);
    }
}
