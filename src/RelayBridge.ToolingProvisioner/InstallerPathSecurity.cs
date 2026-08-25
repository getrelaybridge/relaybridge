// SPDX-License-Identifier: MPL-2.0

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using RelayBridge.Core.Microsoft;

namespace RelayBridge.ToolingProvisioner;

[SupportedOSPlatform("windows")]
internal static class InstallerPathSecurity
{
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier UsersSid = new(WellKnownSidType.BuiltinUsersSid, null);

    internal static void CreateProtectedDirectory(string path, bool allowUsersReadExecute)
    {
        var directory = Directory.CreateDirectory(Path.GetFullPath(path));
        TrustedWindowsPathVerifier.VerifyNoReparsePoint(directory);
        var security = new DirectorySecurity();
        security.SetOwner(AdministratorsSid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            SystemSid,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            AdministratorsSid,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        if (allowUsersReadExecute)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                UsersSid,
                FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        FileSystemAclExtensions.SetAccessControl(directory, security);
        VerifyDirectory(directory.FullName);
    }

    internal static void VerifyProtectedPath(string boundary, string target)
    {
        var root = Path.GetFullPath(boundary).TrimEnd(Path.DirectorySeparatorChar);
        var fullTarget = Path.GetFullPath(target);
        if (!IsWithin(root, fullTarget) || !Directory.Exists(root) || !Directory.Exists(fullTarget))
        {
            throw new ToolingProvisioningException("An installer-controlled path is outside its trusted boundary.");
        }

        var current = root;
        VerifyDirectory(current);
        foreach (var segment in Path.GetRelativePath(root, fullTarget).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            VerifyDirectory(current);
        }
    }

    internal static void VerifyCacheRoot(string cacheRoot)
    {
        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var packageCache = Path.Combine(commonData, "Package Cache");
        VerifyProtectedPath(packageCache, cacheRoot);
    }

    internal static void VerifyProgramDataRoot(string programDataRoot)
    {
        var commonData = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        var expectedRoot = Path.Combine(commonData, "RelayBridge");
        if (!Path.GetFullPath(programDataRoot).Equals(expectedRoot, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(commonData) || !Directory.Exists(expectedRoot))
        {
            throw new ToolingProvisioningException("The RelayBridge data root is unavailable.");
        }

        var commonDataDirectory = new DirectoryInfo(commonData);
        TrustedWindowsPathVerifier.VerifyNoReparsePoint(commonDataDirectory);
        var commonDataSecurity = FileSystemAclExtensions.GetAccessControl(
            commonDataDirectory,
            AccessControlSections.Owner | AccessControlSections.Access);
        TrustedWindowsPathVerifier.VerifyNoUntrustedDeleteChild(commonDataSecurity);

        // ProgramData intentionally lets ordinary users create unrelated children. The
        // RelayBridge child itself must be protected; requiring the entire ProgramData
        // boundary to be non-writable would reject a normal Windows installation.
        VerifyProtectedPath(expectedRoot, expectedRoot);
    }

    internal static void VerifyDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        TrustedWindowsPathVerifier.VerifyNoReparsePoint(directory);
        var security = FileSystemAclExtensions.GetAccessControl(
            directory,
            AccessControlSections.Owner | AccessControlSections.Access);
        TrustedWindowsPathVerifier.VerifySecurityDescriptor(security);
    }

    internal static void VerifyTreeHasNoReparsePoints(string root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            TrustedWindowsPathVerifier.VerifyNoReparsePoint(directory);
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                TrustedWindowsPathVerifier.VerifyNoReparsePoint(entry);
                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                }
            }
        }
    }

    internal static bool IsWithin(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
