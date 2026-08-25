// SPDX-License-Identifier: MPL-2.0

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RelayBridge.Core.Microsoft;

[SupportedOSPlatform("windows")]
internal static class TrustedWindowsPathVerifier
{
    private static readonly SecurityIdentifier LocalSystem =
        new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier BuiltinAdministrators =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly HashSet<string> TrustedOwnerSids = new(StringComparer.Ordinal)
    {
        LocalSystem.Value,
        BuiltinAdministrators.Value,
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", // TrustedInstaller
    };

    private const FileSystemRights MutationRights = FileSystemRights.WriteData |
        FileSystemRights.CreateFiles |
        FileSystemRights.CreateDirectories |
        FileSystemRights.AppendData |
        FileSystemRights.WriteAttributes |
        FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership;

    private const uint GenericWrite = 0x40000000;
    private const uint GenericAll = 0x10000000;
    private const uint MutationAccessMask = unchecked((uint)(int)MutationRights) |
        GenericWrite |
        GenericAll;

    [SupportedOSPlatform("windows")]
    internal static void VerifyInstallationTree(
        string installationRoot,
        IEnumerable<string> criticalPaths,
        bool recursivelyVerifyDirectories)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationRoot);
        ArgumentNullException.ThrowIfNull(criticalPaths);
        var root = Path.GetFullPath(installationRoot);
        if (!Directory.Exists(root) || !IsBeneathApprovedProgramFilesRoot(root, out var programFilesRoot))
        {
            throw new TrustedWindowsPathException();
        }

        VerifyPathFromBoundary(programFilesRoot, root);
        foreach (var path in criticalPaths)
        {
            var fullPath = Path.GetFullPath(path);
            if (!IsWithin(root, fullPath) || (!File.Exists(fullPath) && !Directory.Exists(fullPath)))
            {
                throw new TrustedWindowsPathException();
            }

            VerifyPathFromBoundary(root, fullPath);
            if (recursivelyVerifyDirectories && Directory.Exists(fullPath))
            {
                VerifyDirectoryTree(new DirectoryInfo(fullPath));
            }
        }
    }

    [SupportedOSPlatform("windows")]
    internal static void VerifySecurityDescriptor(FileSystemSecurity security)
    {
        VerifySecurityDescriptor(security, allowedMutationSid: null, allowInteractiveOwner: false);
    }

    [SupportedOSPlatform("windows")]
    internal static void VerifyNoUntrustedDeleteChild(FileSystemSecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        VerifyTrustedOwner(security, allowedOwnerSid: null);
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is null)
        {
            throw new TrustedWindowsPathException();
        }

        foreach (GenericAce ace in descriptor.DiscretionaryAcl)
        {
            if (ace is QualifiedAce
                {
                    AceQualifier: AceQualifier.AccessAllowed,
                    SecurityIdentifier: var sid,
                } qualifiedAce &&
                (qualifiedAce.AceFlags & AceFlags.InheritOnly) == 0 &&
                (unchecked((uint)qualifiedAce.AccessMask) &
                    (unchecked((uint)(int)FileSystemRights.DeleteSubdirectoriesAndFiles) | GenericAll)) != 0 &&
                !TrustedOwnerSids.Contains(sid.Value))
            {
                throw new TrustedWindowsPathException();
            }
        }
    }

    [SupportedOSPlatform("windows")]
    internal static void VerifyScratchSecurityDescriptor(
        FileSystemSecurity security,
        SecurityIdentifier interactiveSid,
        bool requireInteractiveWrite)
    {
        ArgumentNullException.ThrowIfNull(interactiveSid);
        VerifySecurityDescriptor(security, interactiveSid, allowInteractiveOwner: false);
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
            !owner.Equals(LocalSystem))
        {
            throw new TrustedWindowsPathException();
        }

        if (!security.AreAccessRulesProtected)
        {
            throw new TrustedWindowsPathException();
        }

        var allRules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        var explicitRules = allRules.Cast<FileSystemAccessRule>().Where(rule => !rule.IsInherited).ToArray();
        foreach (var rule in explicitRules)
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (rule.AccessControlType == AccessControlType.Allow &&
                !TrustedOwnerSids.Contains(sid.Value) && !sid.Equals(interactiveSid))
            {
                throw new TrustedWindowsPathException();
            }
        }

        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        if (explicitRules.Length != 3 ||
            !ContainsExactRule(explicitRules, LocalSystem, FileSystemRights.FullControl, inheritance) ||
            !ContainsExactRule(explicitRules, BuiltinAdministrators, FileSystemRights.FullControl, inheritance) ||
            !ContainsExactRule(
                explicitRules,
                interactiveSid,
                FileSystemRights.Modify | FileSystemRights.Synchronize,
                inheritance))
        {
            throw new TrustedWindowsPathException();
        }

        if (!requireInteractiveWrite)
        {
            return;
        }

        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier));
        var writable = rules.Cast<FileSystemAccessRule>().Any(rule =>
            rule.AccessControlType == AccessControlType.Allow &&
            ((SecurityIdentifier)rule.IdentityReference).Equals(interactiveSid) &&
            (rule.FileSystemRights & (FileSystemRights.WriteData | FileSystemRights.CreateFiles |
                FileSystemRights.CreateDirectories)) != 0 &&
            (rule.FileSystemRights & (FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) == 0);
        if (!writable)
        {
            throw new TrustedWindowsPathException();
        }
    }

    private static bool ContainsExactRule(
        IEnumerable<FileSystemAccessRule> rules,
        SecurityIdentifier sid,
        FileSystemRights rights,
        InheritanceFlags inheritance) =>
        rules.Any(rule =>
            rule.AccessControlType == AccessControlType.Allow &&
            ((SecurityIdentifier)rule.IdentityReference).Equals(sid) &&
            rule.FileSystemRights == rights &&
            rule.InheritanceFlags == inheritance &&
            rule.PropagationFlags == PropagationFlags.None);

    private static void VerifySecurityDescriptor(
        FileSystemSecurity security,
        SecurityIdentifier? allowedMutationSid,
        bool allowInteractiveOwner)
    {
        ArgumentNullException.ThrowIfNull(security);
        VerifyTrustedOwner(security, allowInteractiveOwner ? allowedMutationSid : null);

        var descriptorBytes = security.GetSecurityDescriptorBinaryForm();
        var descriptor = new RawSecurityDescriptor(descriptorBytes, 0);
        if (descriptor.DiscretionaryAcl is null)
        {
            throw new TrustedWindowsPathException();
        }

        foreach (GenericAce ace in descriptor.DiscretionaryAcl)
        {
            if (ace is not QualifiedAce qualifiedAce ||
                qualifiedAce.AceQualifier != AceQualifier.AccessAllowed ||
                (qualifiedAce.AceFlags & AceFlags.InheritOnly) != 0 ||
                (unchecked((uint)qualifiedAce.AccessMask) & MutationAccessMask) == 0)
            {
                continue;
            }

            var sid = qualifiedAce.SecurityIdentifier;
            if (!TrustedOwnerSids.Contains(sid.Value) &&
                (allowedMutationSid is null || !sid.Equals(allowedMutationSid)))
            {
                throw new TrustedWindowsPathException();
            }


            if (allowedMutationSid is not null && sid.Equals(allowedMutationSid) &&
                (unchecked((uint)qualifiedAce.AccessMask) &
                    (unchecked((uint)(int)(FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) |
                        GenericAll)) != 0)
            {
                throw new TrustedWindowsPathException();
            }
        }
    }

    private static void VerifyTrustedOwner(FileSystemSecurity security, SecurityIdentifier? allowedOwnerSid)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || (!TrustedOwnerSids.Contains(owner.Value) &&
            (allowedOwnerSid is null || !owner.Equals(allowedOwnerSid))))
        {
            throw new TrustedWindowsPathException();
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyPathFromBoundary(string boundary, string target)
    {
        if (!IsWithin(boundary, target))
        {
            throw new TrustedWindowsPathException();
        }

        var current = Path.GetFullPath(boundary);
        VerifyEntry(current);
        var relative = Path.GetRelativePath(current, target);
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            VerifyEntry(current);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyDirectoryTree(DirectoryInfo root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            VerifyEntry(directory.FullName);
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                VerifyEntry(entry.FullName);
                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyEntry(string path)
    {
        FileSystemInfo entry = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : File.Exists(path)
                ? new FileInfo(path)
                : throw new TrustedWindowsPathException();
        VerifyNoReparsePoint(entry);

        FileSystemSecurity security = entry switch
        {
            DirectoryInfo directory => FileSystemAclExtensions.GetAccessControl(
                directory,
                AccessControlSections.Owner | AccessControlSections.Access),
            FileInfo file => FileSystemAclExtensions.GetAccessControl(
                file,
                AccessControlSections.Owner | AccessControlSections.Access),
            _ => throw new TrustedWindowsPathException(),
        };
        VerifySecurityDescriptor(security);
    }

    [SupportedOSPlatform("windows")]
    internal static void VerifyNoReparsePoint(FileSystemInfo entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new TrustedWindowsPathException();
        }
    }

    private static bool IsBeneathApprovedProgramFilesRoot(string path, out string approvedRoot)
    {
        foreach (var candidate in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && IsWithin(candidate, path) &&
                !string.Equals(Path.GetFullPath(candidate), path, StringComparison.OrdinalIgnoreCase))
            {
                approvedRoot = Path.GetFullPath(candidate);
                return true;
            }
        }

        approvedRoot = string.Empty;
        return false;
    }

    private static bool IsWithin(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);
        return string.Equals(normalizedRoot, normalizedPath, StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class TrustedWindowsPathException : Exception
{
    internal TrustedWindowsPathException()
        : base("RelayBridge's Microsoft setup tools are not installed securely. Repair the RelayBridge installation.")
    {
    }
}
