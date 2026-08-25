// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelayBridge.Core.Microsoft;

internal sealed record HelperExecutionFileEntry(string RelativePath, string Sha256);

internal sealed record HelperExecutionManifest(
    int Version,
    IReadOnlyList<HelperExecutionFileEntry> Files);

internal sealed record VerifiedHelperExecutionClosure(byte[] ExpectedLauncherHash);

internal static class HelperExecutionClosureVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    [SupportedOSPlatform("windows")]
    internal static VerifiedHelperExecutionClosure Verify(
        string launcherPath,
        string workerPath,
        string manifestPath,
        string expectedManifestSha256,
        string expectedLauncherSha256,
        Action<string, IEnumerable<string>, bool>? pathTrustVerifier = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var launcher = Path.GetFullPath(launcherPath);
        var worker = Path.GetFullPath(workerPath);
        var root = Path.GetDirectoryName(launcher) ?? throw new TrustedWindowsPathException();
        var manifest = Path.GetFullPath(manifestPath);
        if (!File.Exists(launcher) || !File.Exists(worker) || !File.Exists(manifest) ||
            !IsWithin(root, worker) ||
            !IsWithin(root, manifest) ||
            !IsSha256(expectedManifestSha256) ||
            !IsSha256(expectedLauncherSha256))
        {
            throw new TrustedWindowsPathException();
        }

        try
        {
            (pathTrustVerifier ?? TrustedWindowsPathVerifier.VerifyInstallationTree)(
                root,
                [root, manifest],
                true);

            var manifestBytes = File.ReadAllBytes(manifest);
            if (manifestBytes.Length is <= 0 or > 256 * 1024 ||
                !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(manifestBytes),
                    Convert.FromHexString(expectedManifestSha256)))
            {
                throw new TrustedWindowsPathException();
            }

            var document = JsonSerializer.Deserialize<HelperExecutionManifest>(manifestBytes, JsonOptions)
                ?? throw new TrustedWindowsPathException();
            if (document.Version != 1 || document.Files is null || document.Files.Count is <= 0 or > 256)
            {
                throw new TrustedWindowsPathException();
            }

            var approvedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var approvedHashes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in document.Files)
            {
                if (entry is null || !IsSha256(entry.Sha256))
                {
                    throw new TrustedWindowsPathException();
                }

                var fullPath = ResolveApprovedPath(root, entry.RelativePath);
                if (!approvedFiles.Add(fullPath) || !File.Exists(fullPath) ||
                    (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new TrustedWindowsPathException();
                }

                var expectedHash = Convert.FromHexString(entry.Sha256);
                using var input = File.OpenRead(fullPath);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(input), expectedHash))
                {
                    throw new TrustedWindowsPathException();
                }

                approvedHashes.Add(fullPath, expectedHash);
            }

            var actualFiles = EnumerateFiles(root);
            actualFiles.Remove(manifest);
            if (!actualFiles.SetEquals(approvedFiles))
            {
                throw new TrustedWindowsPathException();
            }

            RequireExecutionClosure(root, launcher, worker, approvedFiles);
            if (!approvedHashes.TryGetValue(launcher, out var expectedLauncherHash) ||
                !CryptographicOperations.FixedTimeEquals(
                    expectedLauncherHash,
                    Convert.FromHexString(expectedLauncherSha256)))
            {
                throw new TrustedWindowsPathException();
            }

            return new VerifiedHelperExecutionClosure(expectedLauncherHash);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or FormatException or ArgumentException)
        {
            throw new TrustedWindowsPathException();
        }
    }

    private static void RequireExecutionClosure(
        string root,
        string launcher,
        string worker,
        HashSet<string> approvedFiles)
    {
        var workerName = Path.GetFileNameWithoutExtension(worker);
        var required = new[]
        {
            launcher,
            worker,
            Path.Combine(root, workerName + ".dll"),
            Path.Combine(root, workerName + ".deps.json"),
            Path.Combine(root, workerName + ".runtimeconfig.json"),
            Path.Combine(root, "RelayBridge.Core.dll"),
        };
        if (required.Any(path => !approvedFiles.Contains(Path.GetFullPath(path))))
        {
            throw new TrustedWindowsPathException();
        }
    }

    private static HashSet<string> EnumerateFiles(string root)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            foreach (var entry in pending.Pop().EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new TrustedWindowsPathException();
                }

                if (entry is DirectoryInfo directory)
                {
                    pending.Push(directory);
                }
                else if (entry is FileInfo file)
                {
                    files.Add(Path.GetFullPath(file.FullName));
                }
            }
        }

        return files;
    }

    private static string ResolveApprovedPath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathFullyQualified(relativePath))
        {
            throw new TrustedWindowsPathException();
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        return IsWithin(root, fullPath) ? fullPath : throw new TrustedWindowsPathException();
    }

    private static bool IsWithin(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSha256(string? value) =>
        value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);
}
