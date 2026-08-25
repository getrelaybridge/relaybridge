// SPDX-License-Identifier: MPL-2.0

extern alias provisioner;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AcquisitionLock = provisioner::RelayBridge.ToolingProvisioner.AcquisitionLock;
using AcquisitionPackage = provisioner::RelayBridge.ToolingProvisioner.AcquisitionPackage;
using AcceptanceState = provisioner::RelayBridge.ToolingProvisioner.AcceptanceState;
using PackageDependency = provisioner::RelayBridge.ToolingProvisioner.PackageDependency;
using PackageVerifier = provisioner::RelayBridge.ToolingProvisioner.PackageVerifier;
using ProvisionerArguments = provisioner::RelayBridge.ToolingProvisioner.ProvisionerArguments;
using SafePackageExtractor = provisioner::RelayBridge.ToolingProvisioner.SafePackageExtractor;
using ToolingProvisioningException = provisioner::RelayBridge.ToolingProvisioner.ToolingProvisioningException;
using Xunit;

namespace RelayBridge.IntegrationTests;

[SupportedOSPlatform("windows")]
public sealed class ToolingProvisionerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"relaybridge-tooling-tests-{Guid.NewGuid():N}");

    public ToolingProvisionerTests() => Directory.CreateDirectory(root);

    [Fact]
    public void Embedded_lock_is_exact_fixed_official_package_set()
    {
        var acquisitionLock = AcquisitionLock.LoadEmbedded();

        Assert.Equal(1, acquisitionLock.SchemaVersion);
        Assert.Equal(4, acquisitionLock.Packages.Count);
        Assert.Equal(2, acquisitionLock.Packages.Count(package => package.RequireLicenseAcceptance));
        Assert.All(acquisitionLock.Packages, package =>
        {
            Assert.Equal(Uri.UriSchemeHttps, package.DownloadUri.Scheme);
            Assert.Equal("www.powershellgallery.com", package.DownloadUri.Host);
            Assert.Equal(64, package.Sha256.Length);
            Assert.Equal(128, package.BurnSha512.Length);
            Assert.True(package.Size > 0);
        });
        Assert.Equal(
            [
                "Microsoft.Graph.Authentication@2.25.0",
                "Microsoft.Graph.Applications@2.25.0",
                "Microsoft.Entra.Authentication@1.3.0",
                "Microsoft.Entra.Applications@1.3.0",
            ],
            acquisitionLock.Packages.Select(package => $"{package.Id}@{package.Version}").ToArray());
    }

    [Theory]
    [InlineData("version", "2.26.0")]
    [InlineData("sha256", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("licenseUri", "https://example.invalid/terms")]
    public void Changed_package_or_license_identity_requires_a_reviewed_lock_update(string property, string value)
    {
        var node = JsonNode.Parse(File.ReadAllText(RepositoryPath("installer", "tooling-lock.json")))!;
        node["externalAcquisition"]!["packages"]![0]![property] = value;
        using var document = JsonDocument.Parse(node.ToJsonString());

        Assert.Throws<ToolingProvisioningException>(() => AcquisitionLock.Parse(document.RootElement));
    }

    [Fact]
    public void Acceptance_record_is_bound_to_every_exact_license_identity_field()
    {
        var acquisitionLock = AcquisitionLock.LoadEmbedded();
        var record = CreateAcceptanceRecord(acquisitionLock);
        using var valid = JsonDocument.Parse(record.ToJsonString());
        Assert.True(AcceptanceState.RecordMatches(valid.RootElement, acquisitionLock));

        foreach (var mutation in new Action<JsonNode>[]
                 {
                     node => node["accepted"] = false,
                     node => node["acceptanceIdentitySha256"] = new string('A', 64),
                     node => node["packages"]![0]!["version"] = "2.26.0",
                     node => node["packages"]![0]!["sha256"] = new string('B', 64),
                     node => node["packages"]![0]!["licenseUri"] = "https://example.invalid/terms",
                     node => node["packages"]![0]!["requireLicenseAcceptance"] = false,
                 })
        {
            var changed = JsonNode.Parse(record.ToJsonString())!;
            mutation(changed);
            using var document = JsonDocument.Parse(changed.ToJsonString());
            Assert.False(AcceptanceState.RecordMatches(document.RootElement, acquisitionLock));
        }

        using var malformed = JsonDocument.Parse("{\"schemaVersion\":1}");
        Assert.False(AcceptanceState.RecordMatches(malformed.RootElement, acquisitionLock));
    }

    [Theory]
    [InlineData("../outside.ps1")]
    [InlineData("/absolute.ps1")]
    [InlineData("C:/drive.ps1")]
    [InlineData("folder/file.ps1:stream")]
    [InlineData("folder/./file.ps1")]
    [InlineData("folder/../file.ps1")]
    public void Safe_extractor_rejects_traversal_rooted_ads_and_ambiguous_paths(string name)
    {
        var archivePath = CreateZip((name, "test", 0));
        using var archive = ZipFile.OpenRead(archivePath);

        Assert.Throws<ToolingProvisioningException>(() =>
            SafePackageExtractor.Extract(archive, Path.Combine(root, "extract")));
        Assert.False(File.Exists(Path.Combine(root, "outside.ps1")));
    }

    [Fact]
    public void Safe_extractor_rejects_case_collisions_and_links()
    {
        var collision = CreateZip(("Module/File.ps1", "a", 0), ("module/file.ps1", "b", 0));
        using (var archive = ZipFile.OpenRead(collision))
        {
            Assert.Throws<ToolingProvisioningException>(() =>
                SafePackageExtractor.Extract(archive, Path.Combine(root, "collision")));
        }

        var link = CreateZip(("module/link", "target", unchecked(0xA000 << 16)));
        using var linkArchive = ZipFile.OpenRead(link);
        Assert.Throws<ToolingProvisioningException>(() =>
            SafePackageExtractor.Extract(linkArchive, Path.Combine(root, "link")));
    }

    [Fact]
    public void Package_verifier_enforces_hash_size_id_version_license_signature_and_dependencies()
    {
        var packagePath = CreatePackage(
            "Example.Module",
            "1.2.3",
            requireLicenseAcceptance: true,
            "https://aka.ms/devservicesagreement",
            [new PackageDependency("Example.Dependency", "[4.5.6]")]);
        var expected = ExpectedPackage(packagePath, "Example.Module", "1.2.3", true,
            [new PackageDependency("Example.Dependency", "[4.5.6]")]);

        PackageVerifier.VerifyAndExtract(packagePath, expected, Path.Combine(root, "valid"));

        Assert.Throws<ToolingProvisioningException>(() => PackageVerifier.VerifyPackageBytes(
            packagePath,
            expected with { Sha256 = new string('0', 64) }));
        using var archive = ZipFile.OpenRead(packagePath);
        Assert.Throws<ToolingProvisioningException>(() => PackageVerifier.VerifyMetadata(
            archive,
            expected with { Id = "Unexpected.Module" }));
        Assert.Throws<ToolingProvisioningException>(() => PackageVerifier.VerifyMetadata(
            archive,
            expected with { Dependencies = [] }));
    }

    [Fact]
    public void Quiet_and_passive_require_explicit_acceptance_while_full_ui_is_affirmative()
    {
        var quiet = ProvisionerArguments.Parse(
            ["install", "--cache", @"C:\ProgramData\Package Cache\test", "--release", "0.9.1", "--ui-level", "2", "--accept-variable", "0"]);
        var passive = quiet with { UiLevel = 3 };
        var acceptedQuiet = quiet with { AcceptanceVariable = 1 };
        var interactive = quiet with { UiLevel = 4 };

        Assert.False(quiet.IsFreshAcceptance);
        Assert.False(passive.IsFreshAcceptance);
        Assert.True(acceptedQuiet.IsFreshAcceptance);
        Assert.True(interactive.IsFreshAcceptance);
        Assert.Throws<ToolingProvisioningException>(() => ProvisionerArguments.Parse(
            ["install", "--cache", @"C:\ProgramData\Package Cache\test", "--release", "0.9.1", "--ui-level", "2", "--accept-variable", "2"]));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    private string CreatePackage(
        string id,
        string version,
        bool requireLicenseAcceptance,
        string licenseUri,
        IReadOnlyList<PackageDependency> dependencies)
    {
        var dependencyXml = string.Join(string.Empty, dependencies.Select(dependency =>
            $"<dependency id=\"{dependency.Id}\" version=\"{dependency.VersionRange}\" />"));
        var nuspec = $"""
            <?xml version="1.0"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{id}</id>
                <version>{version}</version>
                <authors>Test</authors>
                <description>Test</description>
                <requireLicenseAcceptance>{requireLicenseAcceptance.ToString().ToLowerInvariant()}</requireLicenseAcceptance>
                <licenseUrl>{licenseUri}</licenseUrl>
                <dependencies>{dependencyXml}</dependencies>
              </metadata>
            </package>
            """;
        return CreateZip(($"{id}.nuspec", nuspec, 0), (".signature.p7s", "signature", 0), ("module.psd1", "test", 0));
    }

    private AcquisitionPackage ExpectedPackage(
        string packagePath,
        string id,
        string version,
        bool requireLicenseAcceptance,
        IReadOnlyList<PackageDependency> dependencies)
    {
        var bytes = File.ReadAllBytes(packagePath);
        return new AcquisitionPackage(
            id,
            version,
            new Uri($"https://www.powershellgallery.com/api/v2/package/{id}/{version}"),
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)),
            new string('A', 128),
            requireLicenseAcceptance,
            new Uri("https://aka.ms/devservicesagreement"),
            dependencies);
    }

    private string CreateZip(params (string Name, string Content, int ExternalAttributes)[] entries)
    {
        var path = Path.Combine(root, $"{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Name, CompressionLevel.NoCompression);
            entry.ExternalAttributes = item.ExternalAttributes;
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(item.Content);
        }
        return path;
    }

    private static JsonObject CreateAcceptanceRecord(AcquisitionLock acquisitionLock)
    {
        var packages = new JsonArray(acquisitionLock.Packages
            .Where(package => package.RequireLicenseAcceptance)
            .Select(package => (JsonNode)new JsonObject
            {
                ["id"] = package.Id,
                ["version"] = package.Version,
                ["sha256"] = package.Sha256,
                ["requireLicenseAcceptance"] = true,
                ["licenseUri"] = package.LicenseUri.AbsoluteUri,
            }).ToArray());
        return new JsonObject
        {
            ["schemaVersion"] = acquisitionLock.SchemaVersion,
            ["accepted"] = true,
            ["acceptanceIdentitySha256"] = acquisitionLock.AcceptanceIdentitySha256,
            ["toolingIdentitySha256"] = acquisitionLock.ToolingIdentitySha256,
            ["licenseUri"] = acquisitionLock.LicenseUri.AbsoluteUri,
            ["acceptedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["packages"] = packages,
        };
    }

    private static string RepositoryPath(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RelayBridge.sln")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return segments.Aggregate(directory!.FullName, Path.Combine);
    }
}
