// SPDX-License-Identifier: MPL-2.0

using System.Security.Principal;

namespace RelayBridge.ToolingProvisioner;

internal sealed class ToolingInstaller
{
    internal const string InstallationRoot = @"C:\Program Files\RelayBridge";
    internal const string ProgramDataRoot = @"C:\ProgramData\RelayBridge";
    internal const string ModuleRoot = @"C:\Program Files\RelayBridge\Tooling\Modules";
    internal const string StagingRoot = @"C:\ProgramData\RelayBridge\InstallerStaging";
    internal const string StateRoot = @"C:\ProgramData\RelayBridge\InstallerState";

    private readonly AcquisitionLock acquisitionLock;
    private readonly AcceptanceState acceptanceState;

    internal ToolingInstaller(AcquisitionLock acquisitionLock)
    {
        this.acquisitionLock = acquisitionLock;
        acceptanceState = new AcceptanceState(StateRoot);
    }

    internal static bool IsElevatedAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal void InstallOrRepair(string cacheRoot, string releaseIdentity, bool freshAcceptance)
    {
        var hadValidAcceptance = acceptanceState.IsAccepted(acquisitionLock);
        if (!freshAcceptance && !hadValidAcceptance)
        {
            throw new ToolingProvisioningException("Microsoft Graph package terms acceptance is required for this exact tooling identity.");
        }

        InstallerPathSecurity.VerifyCacheRoot(cacheRoot);
        var manifestPath = Path.Combine(cacheRoot, "Metadata", "tooling-manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new ToolingProvisioningException("The Burn-verified tooling closure manifest is unavailable.");
        }

        InstallerPathSecurity.VerifyProtectedPath(cacheRoot, Path.GetDirectoryName(manifestPath)!);
        var closure = ToolingClosure.Load(manifestPath, acquisitionLock);
        InstallerPathSecurity.VerifyProgramDataRoot(ProgramDataRoot);
        InstallerPathSecurity.CreateProtectedDirectory(StagingRoot, allowUsersReadExecute: false);
        var sessionRoot = Path.Combine(StagingRoot, $"session-{Guid.NewGuid():N}");
        InstallerPathSecurity.CreateProtectedDirectory(sessionRoot, allowUsersReadExecute: false);

        try
        {
            var extractedRoot = Path.Combine(sessionRoot, "extracted");
            Directory.CreateDirectory(extractedRoot);
            foreach (var package in acquisitionLock.Packages)
            {
                var packagePath = Path.Combine(cacheRoot, "Packages", package.FileName);
                var destination = Path.Combine(extractedRoot, package.Id, package.Version);
                Directory.CreateDirectory(destination);
                PackageVerifier.VerifyAndExtract(packagePath, package, destination);
                InstallerPathSecurity.VerifyTreeHasNoReparsePoints(destination);
                closure.VerifyPackageDirectory(destination, package);
            }

            Commit(extractedRoot, closure, releaseIdentity, freshAcceptance, hadValidAcceptance);
        }
        finally
        {
            DeleteProtectedSession(sessionRoot);
        }
    }

    internal void Uninstall()
    {
        WindowsServiceControl.StopRelayBridgeIfPresent();
        foreach (var package in acquisitionLock.Packages)
        {
            var packageRoot = Path.Combine(ModuleRoot, package.Id);
            DeleteOwnedModuleRoot(packageRoot, package);
        }

        acceptanceState.Remove();
        CleanupStaleSessions();
    }

    internal void CleanupStaleSessions()
    {
        if (!Directory.Exists(StagingRoot))
        {
            return;
        }

        InstallerPathSecurity.VerifyProtectedPath(ProgramDataRoot, StagingRoot);
        foreach (var directory in Directory.EnumerateDirectories(StagingRoot, "session-*", SearchOption.TopDirectoryOnly))
        {
            DeleteProtectedSession(directory);
        }
    }

    private void Commit(
        string extractedRoot,
        ToolingClosure closure,
        string releaseIdentity,
        bool freshAcceptance,
        bool hadValidAcceptance)
    {
        RelayBridge.Core.Microsoft.TrustedWindowsPathVerifier.VerifyInstallationTree(
            InstallationRoot,
            [Path.Combine(InstallationRoot, "Tooling")],
            recursivelyVerifyDirectories: false);
        InstallerPathSecurity.CreateProtectedDirectory(ModuleRoot, allowUsersReadExecute: true);

        if (acquisitionLock.Packages.All(package => closure.IsPackageDirectoryExact(
                Path.Combine(ModuleRoot, package.Id, package.Version), package)))
        {
            if (freshAcceptance || !acceptanceState.IsAccepted(acquisitionLock))
            {
                acceptanceState.WriteAccepted(acquisitionLock, releaseIdentity);
            }
            else
            {
                AcceptanceState.WriteReleaseMarker(acquisitionLock, releaseIdentity);
            }
            WindowsServiceControl.StartRelayBridge();
            return;
        }

        var transactionId = Guid.NewGuid().ToString("N");
        var incomingRoot = Path.Combine(ModuleRoot, $".relaybridge-incoming-{transactionId}");
        var backupRoot = Path.Combine(ModuleRoot, $".relaybridge-backup-{transactionId}");
        InstallerPathSecurity.CreateProtectedDirectory(incomingRoot, allowUsersReadExecute: true);
        InstallerPathSecurity.CreateProtectedDirectory(backupRoot, allowUsersReadExecute: true);
        var backedUp = new List<AcquisitionPackage>();
        try
        {
            foreach (var package in acquisitionLock.Packages)
            {
                var source = Path.Combine(extractedRoot, package.Id, package.Version);
                var incomingVersion = Path.Combine(incomingRoot, package.Id, package.Version);
                CopyDirectory(source, incomingVersion);
                closure.VerifyPackageDirectory(incomingVersion, package);
            }

            foreach (var package in acquisitionLock.Packages)
            {
                var destinationPackage = Path.Combine(ModuleRoot, package.Id);
                var backupPackage = Path.Combine(backupRoot, package.Id);
                if (Directory.Exists(destinationPackage))
                {
                    Directory.Move(destinationPackage, backupPackage);
                    backedUp.Add(package);
                }

                Directory.Move(Path.Combine(incomingRoot, package.Id), destinationPackage);
            }

            foreach (var package in acquisitionLock.Packages)
            {
                closure.VerifyPackageDirectory(Path.Combine(ModuleRoot, package.Id, package.Version), package);
            }

            if (freshAcceptance || !hadValidAcceptance)
            {
                acceptanceState.WriteAccepted(acquisitionLock, releaseIdentity);
            }
            else
            {
                AcceptanceState.WriteReleaseMarker(acquisitionLock, releaseIdentity);
            }
            WindowsServiceControl.StartRelayBridge();
            DeleteTrustedDirectory(backupRoot);
        }
        catch
        {
            foreach (var package in acquisitionLock.Packages.Reverse())
            {
                var destinationPackage = Path.Combine(ModuleRoot, package.Id);
                if (Directory.Exists(destinationPackage))
                {
                    DeleteTrustedDirectory(destinationPackage);
                }

                var backupPackage = Path.Combine(backupRoot, package.Id);
                if (backedUp.Contains(package) && Directory.Exists(backupPackage))
                {
                    Directory.Move(backupPackage, destinationPackage);
                }
            }

            if (hadValidAcceptance)
            {
                // A previously valid record remains authoritative for the restored closure.
            }
            else
            {
                acceptanceState.Remove();
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(incomingRoot))
            {
                DeleteTrustedDirectory(incomingRoot);
            }
            if (Directory.Exists(backupRoot) && !Directory.EnumerateFileSystemEntries(backupRoot).Any())
            {
                Directory.Delete(backupRoot);
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        InstallerPathSecurity.VerifyTreeHasNoReparsePoints(source);
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static void DeleteOwnedModuleRoot(string packageRoot, AcquisitionPackage package)
    {
        var expected = Path.Combine(ModuleRoot, package.Id);
        if (!Path.GetFullPath(packageRoot).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(packageRoot))
        {
            return;
        }
        DeleteTrustedDirectory(packageRoot);
    }

    private static void DeleteProtectedSession(string sessionRoot)
    {
        if (!Directory.Exists(sessionRoot))
        {
            return;
        }
        var name = Path.GetFileName(sessionRoot);
        if (!name.StartsWith("session-", StringComparison.Ordinal) || name.Length != "session-".Length + 32 ||
            !Guid.TryParseExact(name["session-".Length..], "N", out _) ||
            !InstallerPathSecurity.IsWithin(StagingRoot, sessionRoot))
        {
            throw new ToolingProvisioningException("Refusing to clean an unexpected installer staging directory.");
        }
        InstallerPathSecurity.VerifyProtectedPath(StagingRoot, sessionRoot);
        DeleteTrustedDirectory(sessionRoot);
    }

    private static void DeleteTrustedDirectory(string path)
    {
        InstallerPathSecurity.VerifyTreeHasNoReparsePoints(path);
        Directory.Delete(path, recursive: true);
    }
}
