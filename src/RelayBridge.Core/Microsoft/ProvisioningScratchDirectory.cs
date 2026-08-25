// SPDX-License-Identifier: MPL-2.0

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace RelayBridge.Core.Microsoft;

[SupportedOSPlatform("windows")]
internal static class ProvisioningScratchDirectory
{
    private const string SessionPrefix = "session-";
    private const string SessionLockFileName = ".relaybridge-session.lock";
    private static readonly SecurityIdentifier LocalSystem =
        new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);

    internal static string DefaultRoot
    {
        get
        {
            var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return string.IsNullOrWhiteSpace(commonData)
                ? throw new TrustedWindowsPathException()
                : Path.GetFullPath(Path.Combine(commonData, "RelayBridge", "SetupScratch"));
        }
    }

    internal static ProvisioningScratchLease Create(SecurityIdentifier interactiveSid, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(interactiveSid);
        if (sessionId == Guid.Empty || interactiveSid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
            interactiveSid.IsWellKnown(WellKnownSidType.LocalServiceSid) ||
            interactiveSid.IsWellKnown(WellKnownSidType.NetworkServiceSid))
        {
            throw new TrustedWindowsPathException();
        }

        var root = Path.GetFullPath(DefaultRoot);
        VerifyRoot(root);
        CleanupStaleDirectories(root);

        var path = ResolveSessionPath(root, sessionId);
        if (Directory.Exists(path) || File.Exists(path))
        {
            throw new TrustedWindowsPathException();
        }

        Directory.CreateDirectory(path);
        FileStream? sessionLock = null;
        try
        {
            FileSystemAclExtensions.SetAccessControl(
                new DirectoryInfo(path),
                CreateSessionSecurity(interactiveSid));
            VerifySession(root, path, interactiveSid);
            sessionLock = new FileStream(
                Path.Combine(path, SessionLockFileName),
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.WriteThrough);
            WriteSessionIdentity(sessionLock, interactiveSid);
            return new ProvisioningScratchLease(root, path, interactiveSid, sessionLock);
        }
        catch
        {
            sessionLock?.Dispose();
            TryDeleteCreatedDirectory(root, path, interactiveSid);
            throw;
        }
    }

    internal static void VerifyRoot(string root)
    {
        var expected = Path.GetFullPath(DefaultRoot);
        var fullRoot = Path.GetFullPath(root);
        if (!string.Equals(expected, fullRoot, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(fullRoot))
        {
            throw new TrustedWindowsPathException();
        }

        var commonData = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        if (!IsWithin(commonData, fullRoot))
        {
            throw new TrustedWindowsPathException();
        }

        var current = new DirectoryInfo(commonData);
        TrustedWindowsPathVerifier.VerifyNoReparsePoint(current);
        TrustedWindowsPathVerifier.VerifyNoUntrustedDeleteChild(
            FileSystemAclExtensions.GetAccessControl(
                current,
                AccessControlSections.Owner | AccessControlSections.Access));

        var relative = Path.GetRelativePath(commonData, fullRoot);
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = new DirectoryInfo(Path.Combine(current.FullName, segment));
            if (!current.Exists)
            {
                throw new TrustedWindowsPathException();
            }

            TrustedWindowsPathVerifier.VerifyNoReparsePoint(current);
            TrustedWindowsPathVerifier.VerifySecurityDescriptor(
                FileSystemAclExtensions.GetAccessControl(
                    current,
                    AccessControlSections.Owner | AccessControlSections.Access));
        }
    }

    internal static void VerifySession(
        string root,
        string sessionDirectory,
        SecurityIdentifier interactiveSid)
    {
        VerifyRoot(root);
        var fullRoot = Path.GetFullPath(root);
        var fullSession = Path.GetFullPath(sessionDirectory);
        if (!IsWithin(fullRoot, fullSession) ||
            string.Equals(fullRoot, fullSession, StringComparison.OrdinalIgnoreCase) ||
            !IsSessionName(Path.GetFileName(fullSession)) ||
            !Directory.Exists(fullSession))
        {
            throw new TrustedWindowsPathException();
        }

        var directory = new DirectoryInfo(fullSession);
        TrustedWindowsPathVerifier.VerifyNoReparsePoint(directory);
        TrustedWindowsPathVerifier.VerifyScratchSecurityDescriptor(
            FileSystemAclExtensions.GetAccessControl(
                directory,
                AccessControlSections.Owner | AccessControlSections.Access),
            interactiveSid,
            requireInteractiveWrite: true);
    }

    internal static DirectorySecurity CreateSessionSecurity(SecurityIdentifier interactiveSid)
    {
        var security = new DirectorySecurity();
        security.SetOwner(LocalSystem);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            LocalSystem,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            Administrators,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            interactiveSid,
            FileSystemRights.Modify | FileSystemRights.Synchronize,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    internal static string ResolveSessionPath(string root, Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            throw new TrustedWindowsPathException();
        }

        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(fullRoot, SessionPrefix + sessionId.ToString("N")));
        if (!IsWithin(fullRoot, path) || string.Equals(fullRoot, path, StringComparison.OrdinalIgnoreCase))
        {
            throw new TrustedWindowsPathException();
        }

        return path;
    }

    internal static bool IsSessionName(string name) =>
        name.StartsWith(SessionPrefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(name[SessionPrefix.Length..], "N", out var id) && id != Guid.Empty;

    private static void CleanupStaleDirectories(string root)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories(SessionPrefix + "*"))
        {
            if (!IsSessionName(directory.Name) || directory.LastWriteTimeUtc >= cutoff)
            {
                continue;
            }

            TryDeleteTree(root, directory.FullName);
        }
    }

    private static void TryDeleteCreatedDirectory(
        string root,
        string path,
        SecurityIdentifier interactiveSid)
    {
        try
        {
            VerifySession(root, path, interactiveSid);
            TryDeleteTree(
                root,
                path,
                requireInactiveSessionLock: false,
                expectedInteractiveSid: interactiveSid);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            TrustedWindowsPathException)
        {
        }
    }

    internal static bool TryDeleteTree(
        string root,
        string sessionDirectory,
        Action<string>? rootVerifier = null,
        bool requireInactiveSessionLock = true,
        SecurityIdentifier? expectedInteractiveSid = null,
        Func<string, DirectorySecurity>? sessionSecurityReader = null)
    {
        try
        {
            (rootVerifier ?? VerifyRoot)(root);
            var fullRoot = Path.GetFullPath(root);
            var fullSession = Path.GetFullPath(sessionDirectory);
            if (!IsWithin(fullRoot, fullSession) ||
                string.Equals(fullRoot, fullSession, StringComparison.OrdinalIgnoreCase) ||
                !IsSessionName(Path.GetFileName(fullSession)) ||
                !Directory.Exists(fullSession))
            {
                return false;
            }

            var lockInteractiveSid = requireInactiveSessionLock
                ? ReadInactiveSessionIdentity(fullSession)
                : expectedInteractiveSid;
            if (lockInteractiveSid is null ||
                (expectedInteractiveSid is not null && !expectedInteractiveSid.Equals(lockInteractiveSid)))
            {
                return false;
            }

            var directory = new DirectoryInfo(fullSession);
            TrustedWindowsPathVerifier.VerifyNoReparsePoint(directory);
            var security = sessionSecurityReader?.Invoke(fullSession) ??
                FileSystemAclExtensions.GetAccessControl(
                    directory,
                    AccessControlSections.Owner | AccessControlSections.Access);
            TrustedWindowsPathVerifier.VerifyScratchSecurityDescriptor(
                security,
                lockInteractiveSid,
                requireInteractiveWrite: true);
            VerifyTreeContainsNoReparsePoints(fullRoot, directory);
            Directory.Delete(fullSession, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            TrustedWindowsPathException)
        {
            return false;
        }
    }

    private static void WriteSessionIdentity(FileStream stream, SecurityIdentifier interactiveSid)
    {
        var bytes = Encoding.UTF8.GetBytes(interactiveSid.Value);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        stream.Position = 0;
    }

    private static SecurityIdentifier? ReadInactiveSessionIdentity(string sessionDirectory)
    {
        var lockPath = Path.Combine(sessionDirectory, SessionLockFileName);
        if (!File.Exists(lockPath) || (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(
                lockPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                128,
                FileOptions.None);
            if (stream.Length is <= 0 or > 184)
            {
                return null;
            }

            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 128,
                leaveOpen: true);
            var value = reader.ReadToEnd();
            return string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
                ? null
                : new SecurityIdentifier(value);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            DecoderFallbackException or ArgumentException)
        {
            return null;
        }
    }

    private static void VerifyTreeContainsNoReparsePoints(string root, DirectoryInfo directory)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!IsWithin(root, current.FullName))
            {
                throw new TrustedWindowsPathException();
            }

            TrustedWindowsPathVerifier.VerifyNoReparsePoint(current);
            foreach (var entry in current.EnumerateFileSystemInfos())
            {
                if (!IsWithin(root, entry.FullName))
                {
                    throw new TrustedWindowsPathException();
                }

                TrustedWindowsPathVerifier.VerifyNoReparsePoint(entry);
                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static bool IsWithin(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);
        return string.Equals(normalizedRoot, normalizedPath, StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class ProvisioningScratchLease : IDisposable
{
    private readonly string _root;
    private readonly SecurityIdentifier _interactiveSid;
    private readonly FileStream _sessionLock;
    private int _cleanupAllowed = 1;
    private int _disposed;

    internal ProvisioningScratchLease(
        string root,
        string directory,
        SecurityIdentifier interactiveSid,
        FileStream sessionLock)
    {
        _root = root;
        Directory = directory;
        _interactiveSid = interactiveSid;
        _sessionLock = sessionLock;
    }

    internal string Directory { get; }

    internal void Verify() => ProvisioningScratchDirectory.VerifySession(
        _root,
        Directory,
        _interactiveSid);

    internal void Abandon() => Interlocked.Exchange(ref _cleanupAllowed, 0);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _sessionLock.Dispose();
            if (Volatile.Read(ref _cleanupAllowed) != 0)
            {
                ProvisioningScratchDirectory.TryDeleteTree(
                    _root,
                    Directory,
                    expectedInteractiveSid: _interactiveSid);
            }
        }
    }
}
