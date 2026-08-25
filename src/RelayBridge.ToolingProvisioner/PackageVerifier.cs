// SPDX-License-Identifier: MPL-2.0

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace RelayBridge.ToolingProvisioner;

internal static class PackageVerifier
{
    internal static void VerifyAndExtract(
        string packagePath,
        AcquisitionPackage expected,
        string destinationRoot)
    {
        VerifyPackageBytes(packagePath, expected);
        using var archive = ZipFile.OpenRead(packagePath);
        VerifyMetadata(archive, expected);
        SafePackageExtractor.Extract(archive, destinationRoot);

        foreach (var symbol in Directory.EnumerateFiles(destinationRoot, "*", SearchOption.AllDirectories)
                     .Where(path => Path.GetExtension(path) is ".pdb" or ".dbg"))
        {
            File.Delete(symbol);
        }
    }

    internal static void VerifyPackageBytes(string packagePath, AcquisitionPackage expected)
    {
        var file = new FileInfo(packagePath);
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length != expected.Size)
        {
            throw new ToolingProvisioningException("An acquired Microsoft package has an unexpected size.");
        }

        using var stream = file.OpenRead();
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        if (!hash.Equals(expected.Sha256, StringComparison.Ordinal))
        {
            throw new ToolingProvisioningException("An acquired Microsoft package failed independent SHA-256 verification.");
        }
    }

    internal static void VerifyMetadata(ZipArchive archive, AcquisitionPackage expected)
    {
        var nuspecs = archive.Entries.Where(entry =>
            entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) &&
            !entry.FullName.Contains('/') && !entry.FullName.Contains('\\')).ToArray();
        if (nuspecs.Length != 1)
        {
            throw new ToolingProvisioningException("The acquired package has an ambiguous package manifest.");
        }

        XDocument document;
        using (var stream = nuspecs[0].Open())
        {
            document = XDocument.Load(stream, LoadOptions.None);
        }

        var metadata = document.Root?.Elements().SingleOrDefault(element => element.Name.LocalName == "metadata")
            ?? throw new ToolingProvisioningException("The acquired package manifest is incomplete.");
        var id = RequiredValue(metadata, "id");
        var version = RequiredValue(metadata, "version");
        var requireLicenseAcceptance = bool.TryParse(RequiredValue(metadata, "requireLicenseAcceptance"), out var required) && required;
        var licenseUrl = RequiredValue(metadata, "licenseUrl");
        if (!id.Equals(expected.Id, StringComparison.Ordinal) ||
            !version.Equals(expected.Version, StringComparison.Ordinal) ||
            requireLicenseAcceptance != expected.RequireLicenseAcceptance ||
            !licenseUrl.Equals(expected.LicenseUri.AbsoluteUri, StringComparison.Ordinal))
        {
            throw new ToolingProvisioningException("The acquired package metadata does not match the approved identity.");
        }

        var dependencies = metadata.Descendants()
            .Where(element => element.Name.LocalName == "dependency")
            .Select(element => new PackageDependency(
                element.Attribute("id")?.Value ?? string.Empty,
                element.Attribute("version")?.Value ?? string.Empty))
            .OrderBy(dependency => dependency.Id, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.VersionRange, StringComparer.Ordinal)
            .ToArray();
        var expectedDependencies = expected.Dependencies
            .OrderBy(dependency => dependency.Id, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.VersionRange, StringComparer.Ordinal)
            .ToArray();
        if (!dependencies.SequenceEqual(expectedDependencies))
        {
            throw new ToolingProvisioningException("The acquired package dependency metadata is not the approved fixed set.");
        }

        if (expected.RequireLicenseAcceptance &&
            archive.Entries.Any(entry => entry.FullName.EndsWith("license.txt", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ToolingProvisioningException("The locked license identity expected no embedded license.txt.");
        }

        if (!archive.Entries.Any(entry => entry.FullName.Equals(".signature.p7s", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ToolingProvisioningException("The acquired Gallery package is missing its package signature payload.");
        }
    }

    private static string RequiredValue(XElement metadata, string name)
    {
        var value = metadata.Elements().SingleOrDefault(element => element.Name.LocalName == name)?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ToolingProvisioningException("The acquired package manifest is missing required metadata.");
        }

        return value;
    }
}

internal sealed class ToolingClosure
{
    private readonly IReadOnlyDictionary<string, string> expectedFiles;

    private ToolingClosure(IReadOnlyDictionary<string, string> expectedFiles)
    {
        this.expectedFiles = expectedFiles;
    }

    internal static ToolingClosure Load(string manifestPath, AcquisitionLock acquisitionLock)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16,
        });
        var root = document.RootElement;
        if (root.GetProperty("Version").GetInt32() != 2)
        {
            throw new ToolingProvisioningException("The tooling closure manifest schema is unsupported.");
        }

        var packageRoots = acquisitionLock.Packages
            .Select(package => package.ModuleRelativeRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .ToArray();
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in root.GetProperty("Files").EnumerateArray())
        {
            var relative = NormalizeRelative(entry.GetProperty("RelativePath").GetString() ?? string.Empty);
            if (!packageRoots.Any(packageRoot => relative.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var hash = entry.GetProperty("Sha256").GetString()?.ToUpperInvariant() ?? string.Empty;
            if (hash.Length != 64 || !expected.TryAdd(relative, hash))
            {
                throw new ToolingProvisioningException("The tooling closure manifest contains an invalid external module entry.");
            }
        }

        if (expected.Count == 0 || acquisitionLock.Packages.Any(package =>
                !expected.Keys.Any(path => path.StartsWith(
                    package.ModuleRelativeRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))))
        {
            throw new ToolingProvisioningException("The tooling closure manifest omits an external module package.");
        }

        return new ToolingClosure(expected);
    }

    internal void VerifyPackageDirectory(string packageDirectory, AcquisitionPackage package)
    {
        var root = Path.GetFullPath(packageDirectory);
        var expectedPrefix = package.ModuleRelativeRoot + Path.DirectorySeparatorChar;
        var expected = expectedFiles
            .Where(pair => pair.Key.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                pair => pair.Key[expectedPrefix.Length..],
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
        var actual = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase);
        if (actual.Count != expected.Count || !actual.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expected.Keys))
        {
            throw new ToolingProvisioningException("The extracted Microsoft module closure has missing or unexpected files.");
        }

        foreach (var pair in expected)
        {
            using var stream = File.OpenRead(actual[pair.Key]);
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!actualHash.Equals(pair.Value, StringComparison.Ordinal))
            {
                throw new ToolingProvisioningException("The extracted Microsoft module closure failed file verification.");
            }
        }
    }

    internal bool IsPackageDirectoryExact(string packageDirectory, AcquisitionPackage package)
    {
        try
        {
            VerifyPackageDirectory(packageDirectory, package);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (ToolingProvisioningException)
        {
            return false;
        }
    }

    private static string NormalizeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathFullyQualified(path) || path.Contains(':'))
        {
            throw new ToolingProvisioningException("The tooling closure manifest contains an unsafe path.");
        }

        return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }
}
