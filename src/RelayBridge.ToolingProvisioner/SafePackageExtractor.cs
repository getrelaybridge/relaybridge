// SPDX-License-Identifier: MPL-2.0

using System.IO.Compression;

namespace RelayBridge.ToolingProvisioner;

internal static class SafePackageExtractor
{
    internal const int MaximumFileCount = 5_000;
    internal const long MaximumSingleFileBytes = 256L * 1024 * 1024;
    internal const long MaximumTotalBytes = 1024L * 1024 * 1024;
    internal const int MaximumRelativePathLength = 768;

    internal static void Extract(ZipArchive archive, string destinationRoot)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        var root = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(root);
        var boundary = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileCount = 0;
        long totalBytes = 0;

        foreach (var entry in archive.Entries)
        {
            var relative = ValidateEntryName(entry.FullName);
            if (!seen.Add(relative))
            {
                throw new ToolingProvisioningException("The package contains duplicate or case-colliding paths.");
            }

            if (IsLink(entry))
            {
                throw new ToolingProvisioningException("The package contains an unsupported link entry.");
            }

            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolingProvisioningException("The package contains a path outside the staging directory.");
            }

            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            fileCount++;
            totalBytes = checked(totalBytes + entry.Length);
            if (fileCount > MaximumFileCount || entry.Length > MaximumSingleFileBytes ||
                totalBytes > MaximumTotalBytes)
            {
                throw new ToolingProvisioningException("The package exceeds the approved extraction bounds.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            if (output.Length != entry.Length)
            {
                throw new ToolingProvisioningException("A package entry did not extract completely.");
            }
        }
    }

    private static string ValidateEntryName(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName) || entryName.IndexOf('\0') >= 0 ||
            entryName.Length > MaximumRelativePathLength || entryName.Contains(':', StringComparison.Ordinal))
        {
            throw new ToolingProvisioningException("The package contains an invalid archive path.");
        }

        var normalized = entryName.Replace('/', Path.DirectorySeparatorChar);
        if (normalized.EndsWith(Path.DirectorySeparatorChar))
        {
            normalized = normalized.TrimEnd(Path.DirectorySeparatorChar);
        }
        if (Path.IsPathFullyQualified(normalized) || Path.IsPathRooted(normalized) ||
            normalized.StartsWith(Path.DirectorySeparatorChar) ||
            normalized.StartsWith("\\", StringComparison.Ordinal))
        {
            throw new ToolingProvisioningException("The package contains a rooted archive path.");
        }

        var segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.None);
        if (segments.Any(segment => segment is "" or "." or ".." ||
            segment.EndsWith(' ') || segment.EndsWith('.')))
        {
            throw new ToolingProvisioningException("The package contains an ambiguous archive path.");
        }

        return normalized;
    }

    private static bool IsLink(ZipArchiveEntry entry)
    {
        const int unixFileTypeMask = 0xF000;
        const int unixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & unixFileTypeMask;
        return unixMode == unixSymbolicLink ||
            (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;
    }
}
