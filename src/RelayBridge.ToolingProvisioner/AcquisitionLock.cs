// SPDX-License-Identifier: MPL-2.0

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RelayBridge.ToolingProvisioner;

internal sealed record PackageDependency(string Id, string VersionRange);

internal sealed record AcquisitionPackage(
    string Id,
    string Version,
    Uri DownloadUri,
    long Size,
    string Sha256,
    string BurnSha512,
    bool RequireLicenseAcceptance,
    Uri LicenseUri,
    IReadOnlyList<PackageDependency> Dependencies)
{
    internal string FileName => $"{Id}.{Version}.nupkg";
    internal string ModuleRelativeRoot => Path.Combine("Modules", Id, Version);
}

internal sealed record AcquisitionLock(
    int SchemaVersion,
    string ToolingIdentitySha256,
    string AcceptanceIdentitySha256,
    Uri LicenseUri,
    IReadOnlyList<AcquisitionPackage> Packages)
{
    internal const string ResourceName = "RelayBridge.ToolingProvisioner.tooling-lock.json";

    internal static AcquisitionLock LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new ToolingProvisioningException("The compiled tooling lock is unavailable.");
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16,
        });
        return Parse(document.RootElement);
    }

    internal static AcquisitionLock Parse(JsonElement root)
    {
        var external = root.GetProperty("externalAcquisition");
        var packages = external.GetProperty("packages").EnumerateArray().Select(ParsePackage).ToArray();
        var result = new AcquisitionLock(
            external.GetProperty("schemaVersion").GetInt32(),
            RequiredHash(external, "toolingIdentitySha256", 64),
            RequiredHash(external, "acceptanceIdentitySha256", 64),
            RequiredHttpsUri(external, "licenseUri"),
            packages);
        result.Validate();
        return result;
    }

    internal void Validate()
    {
        if (SchemaVersion != 1 || Packages.Count != 4 ||
            Packages.Select(package => package.Id).Distinct(StringComparer.Ordinal).Count() != Packages.Count)
        {
            throw new ToolingProvisioningException("The external tooling lock has an unsupported identity.");
        }

        foreach (var package in Packages)
        {
            if (package.Size <= 0 || package.Version.Length == 0 ||
                package.DownloadUri.Scheme != Uri.UriSchemeHttps ||
                !package.DownloadUri.Host.Equals("www.powershellgallery.com", StringComparison.OrdinalIgnoreCase) ||
                package.DownloadUri.AbsolutePath != $"/api/v2/package/{package.Id}/{package.Version}" ||
                package.LicenseUri != LicenseUri)
            {
                throw new ToolingProvisioningException("The external tooling lock contains an invalid package authority.");
            }
        }

        var acceptancePackages = Packages.Where(package => package.RequireLicenseAcceptance).ToArray();
        if (acceptancePackages.Length != 2 ||
            !acceptancePackages.All(package => package.Id.StartsWith("Microsoft.Graph.", StringComparison.Ordinal)))
        {
            throw new ToolingProvisioningException("The external tooling license identity is invalid.");
        }

        if (!ComputeAcceptanceIdentity(acceptancePackages).Equals(AcceptanceIdentitySha256, StringComparison.Ordinal) ||
            !ComputeToolingIdentity(Packages).Equals(ToolingIdentitySha256, StringComparison.Ordinal))
        {
            throw new ToolingProvisioningException("The external tooling lock identity hashes do not match its contents.");
        }
    }

    internal AcquisitionPackage FindPackage(string id) =>
        Packages.Single(package => package.Id.Equals(id, StringComparison.Ordinal));

    internal static string ComputeAcceptanceIdentity(IEnumerable<AcquisitionPackage> packages)
    {
        var lines = packages.Select(package => string.Join('|',
            package.Id,
            package.Version,
            package.Sha256,
            package.RequireLicenseAcceptance ? "true" : "false",
            package.LicenseUri.AbsoluteUri));
        return ComputeIdentity(lines);
    }

    internal static string ComputeToolingIdentity(IEnumerable<AcquisitionPackage> packages)
    {
        var lines = packages.Select(package => string.Join('|', package.Id, package.Version, package.Sha256));
        return ComputeIdentity(lines);
    }

    private static string ComputeIdentity(IEnumerable<string> lines)
    {
        var value = "1\n" + string.Join('\n', lines);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static AcquisitionPackage ParsePackage(JsonElement element) => new(
        element.GetProperty("id").GetString() ?? string.Empty,
        element.GetProperty("version").GetString() ?? string.Empty,
        RequiredHttpsUri(element, "downloadUrl"),
        element.GetProperty("size").GetInt64(),
        RequiredHash(element, "sha256", 64),
        RequiredHash(element, "burnSha512", 128),
        element.GetProperty("requireLicenseAcceptance").GetBoolean(),
        RequiredHttpsUri(element, "licenseUri"),
        element.GetProperty("dependencies").EnumerateArray()
            .Select(dependency => new PackageDependency(
                dependency.GetProperty("id").GetString() ?? string.Empty,
                dependency.GetProperty("versionRange").GetString() ?? string.Empty))
            .ToArray());

    private static string RequiredHash(JsonElement element, string property, int length)
    {
        var value = element.GetProperty(property).GetString()?.ToUpperInvariant() ?? string.Empty;
        if (value.Length != length || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ToolingProvisioningException("The external tooling lock contains an invalid hash.");
        }

        return value;
    }

    private static Uri RequiredHttpsUri(JsonElement element, string property)
    {
        var value = element.GetProperty(property).GetString();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ToolingProvisioningException("The external tooling lock contains an invalid URI.");
        }

        return uri;
    }
}

internal sealed class ToolingProvisioningException : Exception
{
    internal ToolingProvisioningException(string message) : base(message) { }
    internal ToolingProvisioningException(string message, Exception innerException) : base(message, innerException) { }
}
