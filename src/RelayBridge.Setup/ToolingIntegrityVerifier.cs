// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelayBridge.Core.Microsoft;

namespace RelayBridge.Setup;

internal sealed record ToolingFileEntry(
    string RelativePath,
    string Sha256,
    bool RequireMicrosoftSignature = false);

internal sealed record ToolingManifest(
    int Version,
    string PowerShellRelativePath,
    string GraphAuthenticationModuleRelativePath,
    string GraphAuthenticationModuleVersion,
    string GraphApplicationsModuleRelativePath,
    string GraphApplicationsModuleVersion,
    string EntraAuthenticationModuleRelativePath,
    string EntraAuthenticationModuleVersion,
    string EntraApplicationsModuleRelativePath,
    string EntraApplicationsModuleVersion,
    string ExchangeOnlineModuleRelativePath,
    string ExchangeOnlineModuleVersion,
    IReadOnlyList<ToolingFileEntry> Files);

internal sealed record VerifiedTooling(
    string PowerShellPath,
    string GraphAuthenticationModulePath,
    string GraphAuthenticationModuleVersion,
    string GraphApplicationsModulePath,
    string GraphApplicationsModuleVersion,
    string EntraAuthenticationModulePath,
    string EntraAuthenticationModuleVersion,
    string EntraApplicationsModulePath,
    string EntraApplicationsModuleVersion,
    string ExchangeOnlineModulePath,
    string ExchangeOnlineModuleVersion);

internal static partial class ToolingIntegrityVerifier
{
    internal const string RequiredGraphAuthenticationModuleVersion = "2.25.0";
    internal const string RequiredGraphApplicationsModuleVersion = "2.25.0";
    internal const string RequiredEntraAuthenticationModuleVersion = "1.3.0";
    internal const string RequiredEntraApplicationsModuleVersion = "1.3.0";
    internal const string RequiredExchangeOnlineModuleVersion = "3.9.2";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    [SupportedOSPlatform("windows")]
    internal static VerifiedTooling Verify(
        string installationRoot,
        string toolingRoot,
        string manifestPath,
        string expectedManifestSha256,
        Action<string, IEnumerable<string>, bool>? pathTrustVerifier = null)
    {
        var root = Path.GetFullPath(toolingRoot);
        var manifest = Path.GetFullPath(manifestPath);
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(manifest) ||
            !File.Exists(manifest) || !Directory.Exists(root) || !IsSha256(expectedManifestSha256))
        {
            throw new ToolIntegrityException();
        }

        try
        {
            (pathTrustVerifier ?? TrustedWindowsPathVerifier.VerifyInstallationTree)(
                installationRoot,
                [root, manifest],
                true);
        }
        catch (TrustedWindowsPathException)
        {
            throw new ToolIntegrityException();
        }

        ToolingManifest document;
        try
        {
            var bytes = File.ReadAllBytes(manifest);
            if (bytes.Length is <= 0 or > 256 * 1024)
            {
                throw new ToolIntegrityException();
            }

            var actualManifestHash = SHA256.HashData(bytes);
            if (!CryptographicOperations.FixedTimeEquals(
                    actualManifestHash,
                    Convert.FromHexString(expectedManifestSha256)))
            {
                throw new ToolIntegrityException();
            }

            document = JsonSerializer.Deserialize<ToolingManifest>(bytes, JsonOptions)
                ?? throw new ToolIntegrityException();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ToolIntegrityException();
        }

        if (document.Version != 2 || document.Files.Count == 0 || document.Files.Count > 4096 ||
            !IsVersion(document.GraphAuthenticationModuleVersion) ||
            !IsVersion(document.GraphApplicationsModuleVersion) ||
            !IsVersion(document.EntraAuthenticationModuleVersion) ||
            !IsVersion(document.EntraApplicationsModuleVersion) ||
            !IsVersion(document.ExchangeOnlineModuleVersion) ||
            !string.Equals(
                document.GraphAuthenticationModuleVersion,
                RequiredGraphAuthenticationModuleVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                document.GraphApplicationsModuleVersion,
                RequiredGraphApplicationsModuleVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                document.EntraAuthenticationModuleVersion,
                RequiredEntraAuthenticationModuleVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                document.EntraApplicationsModuleVersion,
                RequiredEntraApplicationsModuleVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                document.ExchangeOnlineModuleVersion,
                RequiredExchangeOnlineModuleVersion,
                StringComparison.Ordinal))
        {
            throw new ToolIntegrityException();
        }

        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.Files)
        {
            var fullPath = ResolveApprovedPath(root, item.RelativePath);
            if (!expected.Add(fullPath) || !File.Exists(fullPath) || !IsSha256(item.Sha256))
            {
                throw new ToolIntegrityException();
            }

            using var input = File.OpenRead(fullPath);
            var actual = Convert.ToHexString(SHA256.HashData(input));
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actual),
                    Convert.FromHexString(item.Sha256)))
            {
                throw new ToolIntegrityException();
            }

            if (item.RequireMicrosoftSignature)
            {
                VerifyMicrosoftSignature(fullPath);
            }
        }

        var actualFiles = EnumerateVerifiedFiles(root);
        if (manifest.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            actualFiles.Remove(manifest);
        }
        if (!actualFiles.SetEquals(expected))
        {
            throw new ToolIntegrityException();
        }

        return new VerifiedTooling(
            ResolveExpectedFile(root, document.PowerShellRelativePath, expected),
            ResolveExpectedFile(root, document.GraphAuthenticationModuleRelativePath, expected),
            document.GraphAuthenticationModuleVersion,
            ResolveExpectedFile(root, document.GraphApplicationsModuleRelativePath, expected),
            document.GraphApplicationsModuleVersion,
            ResolveExpectedFile(root, document.EntraAuthenticationModuleRelativePath, expected),
            document.EntraAuthenticationModuleVersion,
            ResolveExpectedFile(root, document.EntraApplicationsModuleRelativePath, expected),
            document.EntraApplicationsModuleVersion,
            ResolveExpectedFile(root, document.ExchangeOnlineModuleRelativePath, expected),
            document.ExchangeOnlineModuleVersion);
    }

    private static string ResolveExpectedFile(string root, string relativePath, HashSet<string> expected)
    {
        var path = ResolveApprovedPath(root, relativePath);
        if (!expected.Contains(path))
        {
            throw new ToolIntegrityException();
        }

        return path;
    }

    private static string ResolveApprovedPath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathFullyQualified(relativePath))
        {
            throw new ToolIntegrityException();
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolIntegrityException();
        }

        return fullPath;
    }

    [SupportedOSPlatform("windows")]
    private static HashSet<string> EnumerateVerifiedFiles(string root)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new ToolIntegrityException();
                }

                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                }
                else if (entry is FileInfo file)
                {
                    files.Add(Path.GetFullPath(file.FullName));
                }
            }
        }

        return files;
    }

    private static bool IsSha256(string value)
    {
        return value.Length == 64 && value.All(Uri.IsHexDigit);
    }

    private static bool IsVersion(string value)
    {
        return Version.TryParse(value, out var parsed) && parsed is not null && parsed.Major > 0;
    }

    private static void VerifyMicrosoftSignature(string path)
    {
        var fileInfo = new WinTrustFileInfo(path);
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        var trustDataPointer = IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            var trustData = new WinTrustData(fileInfoPointer);
            trustDataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(trustData, trustDataPointer, fDeleteOld: false);
            var action = WinTrustActionGenericVerifyV2;
            if (WinVerifyTrust(IntPtr.Zero, ref action, trustDataPointer) != 0)
            {
                throw new ToolIntegrityException();
            }

#pragma warning disable SYSLIB0057 // No modern API loads an Authenticode signer certificate from a PE file.
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            if (!string.Equals(
                    signer.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                    "Microsoft Corporation",
                    StringComparison.Ordinal))
            {
                throw new ToolIntegrityException();
            }
        }
        finally
        {
            if (trustDataPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(trustDataPointer);
            }

            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    [LibraryImport("wintrust.dll", SetLastError = true)]
    private static partial int WinVerifyTrust(IntPtr window, ref Guid actionId, IntPtr trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        internal WinTrustFileInfo(string path)
        {
            StructureSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            FilePath = path;
            FileHandle = IntPtr.Zero;
            KnownSubject = IntPtr.Zero;
        }

        internal uint StructureSize;

        [MarshalAs(UnmanagedType.LPWStr)]
        internal string FilePath;

        internal IntPtr FileHandle;
        internal IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        internal WinTrustData(IntPtr fileInfo)
        {
            StructureSize = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = 2;
            RevocationChecks = 1;
            UnionChoice = 1;
            FileInfo = fileInfo;
            StateAction = 0;
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            ProviderFlags = 0x00000040;
            UiContext = 0;
            SignatureSettings = IntPtr.Zero;
        }

        internal uint StructureSize;
        internal IntPtr PolicyCallbackData;
        internal IntPtr SipClientData;
        internal uint UiChoice;
        internal uint RevocationChecks;
        internal uint UnionChoice;
        internal IntPtr FileInfo;
        internal uint StateAction;
        internal IntPtr StateData;
        internal IntPtr UrlReference;
        internal uint ProviderFlags;
        internal uint UiContext;
        internal IntPtr SignatureSettings;
    }
}

internal sealed class ToolIntegrityException : Exception
{
    internal ToolIntegrityException()
        : base("RelayBridge's Microsoft setup tools are not installed securely. Repair the RelayBridge installation.")
    {
    }
}
