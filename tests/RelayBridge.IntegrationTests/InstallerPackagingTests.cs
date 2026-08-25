// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace RelayBridge.IntegrationTests;

public sealed class InstallerPackagingTests
{
    private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";
    private static readonly XNamespace WixUtil = "http://wixtoolset.org/schemas/v4/wxs/util";

    [Fact]
    public void Msi_uses_fixed_per_machine_x64_layout_and_standard_service_facilities()
    {
        var document = LoadXml("installer", "Package.wxs");
        var package = document.Root!.Element(Wix + "Package")!;

        Assert.Equal("perMachine", package.Attribute("Scope")?.Value);
        Assert.Equal("500", package.Attribute("InstallerVersion")?.Value);
        Assert.NotNull(document.Descendants(Wix + "StandardDirectory")
            .Single(element => (string?)element.Attribute("Id") == "ProgramFiles64Folder"));
        Assert.Equal(
            ["Host", "Setup", "Tooling"],
            document.Descendants(Wix + "Directory")
                .Where(element => new[] { "HOSTDIR", "SETUPDIR", "TOOLINGDIR" }
                    .Contains((string?)element.Attribute("Id"), StringComparer.Ordinal))
                .Select(element => (string)element.Attribute("Name")!)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray());

        var service = Assert.Single(document.Descendants(Wix + "ServiceInstall"));
        Assert.Equal("RelayBridge", service.Attribute("Name")?.Value);
        Assert.Equal("auto", service.Attribute("Start")?.Value);
        Assert.Null(service.Attribute("Account"));
        var recovery = Assert.Single(service.Elements(WixUtil + "ServiceConfig"));
        Assert.Equal("restart", recovery.Attribute("FirstFailureActionType")?.Value);
        Assert.Equal("restart", recovery.Attribute("SecondFailureActionType")?.Value);
        Assert.Equal("none", recovery.Attribute("ThirdFailureActionType")?.Value);
        Assert.Equal("1", recovery.Attribute("ResetPeriodInDays")?.Value);
        Assert.Equal("15", recovery.Attribute("RestartServiceDelayInSeconds")?.Value);

        var control = document.Descendants(Wix + "ServiceControl")
            .Single(element => (string?)element.Attribute("Id") == "RelayBridgeServiceControl");
        Assert.Null(control.Attribute("Start"));
        Assert.Equal("both", control.Attribute("Stop")?.Value);
        Assert.Equal("uninstall", control.Attribute("Remove")?.Value);
        Assert.Equal("yes", control.Attribute("Wait")?.Value);
        Assert.Empty(document.Descendants(Wix + "CustomAction"));
        Assert.DoesNotContain("Firewall", document.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProgramData_acls_are_protected_and_uninstall_preserves_durable_state()
    {
        var document = LoadXml("installer", "Package.wxs");
        var components = document.Descendants(Wix + "Component")
            .Where(component => new[]
            {
                "ProgramDataRootComponent",
                "ProgramDataDataComponent",
                "ProgramDataScratchComponent"
            }.Contains((string?)component.Attribute("Id"), StringComparer.Ordinal))
            .ToDictionary(component => (string)component.Attribute("Id")!, StringComparer.Ordinal);

        Assert.Equal(3, components.Count);
        Assert.All(components.Values, component => Assert.Equal("yes", component.Attribute("Permanent")?.Value));
        Assert.All(components.Values, component => Assert.Equal("yes", component.Attribute("NeverOverwrite")?.Value));

        const string readExecute = "O:SYG:SYD:P(A;OICI;GA;;;SY)(A;OICI;GA;;;BA)(A;OICI;GRGX;;;BU)";
        const string systemOnly = "O:SYG:SYD:P(A;OICI;GA;;;SY)(A;OICI;GA;;;BA)";
        Assert.Equal(readExecute, PermissionSddl(components["ProgramDataRootComponent"]));
        Assert.Equal(systemOnly, PermissionSddl(components["ProgramDataDataComponent"]));
        Assert.Equal(readExecute, PermissionSddl(components["ProgramDataScratchComponent"]));
        Assert.DoesNotContain(";;;WD", document.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(";;;AU", document.ToString(), StringComparison.Ordinal);
        Assert.Empty(document.Descendants(Wix + "RemoveFolder"));
    }

    [Fact]
    public void Msi_owns_only_the_fixed_uri_handler_and_has_safe_servicing_contracts()
    {
        var document = LoadXml("installer", "Package.wxs");
        var protocol = document.Descendants(Wix + "RegistryKey")
            .Single(element => (string?)element.Attribute("Key") == "Software\\Classes\\relaybridge-setup");

        Assert.Equal("HKLM", protocol.Attribute("Root")?.Value);
        Assert.Equal("yes", protocol.Attribute("ForceDeleteOnUninstall")?.Value);
        var command = protocol.Descendants(Wix + "RegistryValue")
            .Single(element => (string?)element.Parent?.Attribute("Key") == "shell\\open\\command");
        Assert.Equal("\"[SETUPDIR]RelayBridge.SetupLauncher.exe\" \"%1\"", command.Attribute("Value")?.Value);

        var upgrade = Assert.Single(document.Descendants(Wix + "MajorUpgrade"));
        Assert.Equal("afterInstallInitialize", upgrade.Attribute("Schedule")?.Value);
        Assert.False(string.IsNullOrWhiteSpace(upgrade.Attribute("DowngradeErrorMessage")?.Value));

        var propertyIds = document.Descendants(Wix + "Property")
            .Select(element => (string)element.Attribute("Id")!)
            .ToArray();
        Assert.DoesNotContain(propertyIds, id =>
            id.Contains("PATH", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("DIRECTORY", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("SCRATCH", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Bootstrapper_pins_prerequisites_msi_and_external_module_provisioner_order()
    {
        var document = LoadXml("installer", "Bundle.wxs");
        var exePackages = document.Descendants(Wix + "ExePackage").ToArray();
        var msi = Assert.Single(document.Descendants(Wix + "MsiPackage"));

        Assert.Equal(2, exePackages.Length);
        Assert.Contains(exePackages, package =>
            (string?)package.Attribute("SourceFile") ==
            "$(var.PrerequisiteRoot)\\dotnet-runtime-10.0.11-win-x64.exe");
        Assert.Contains(exePackages, package =>
            (string?)package.Attribute("SourceFile") ==
            "$(var.PrerequisiteRoot)\\aspnetcore-runtime-10.0.11-win-x64.exe");
        Assert.All(exePackages, package =>
        {
            Assert.Equal("yes", package.Attribute("Permanent")?.Value);
            Assert.Equal("yes", package.Attribute("Vital")?.Value);
            Assert.Contains(">= v10.0.11", package.Attribute("DetectCondition")?.Value, StringComparison.Ordinal);
        });
        Assert.Equal(
            "$(var.PackageRoot)\\RelayBridge-$(var.ProductVersion)-win-x64.msi",
            msi.Attribute("SourceFile")?.Value);
        var chainItems = document.Descendants(Wix + "Chain").Single().Elements().ToArray();
        Assert.Equal("RelayBridgeMsi", chainItems[^2].Attribute("Id")?.Value);
        Assert.Equal("RelayBridgeExternalMicrosoftModules", chainItems[^1].Attribute("Id")?.Value);
        Assert.Equal("PackageGroupRef", chainItems[^1].Name.LocalName);
        Assert.Equal(
            "D037C6938C7389DFFCB2360899E7D715ADE0EBAE9EF515FC69AFA339B033CCF5",
            msi.Elements(Wix + "MsiProperty").Single().Attribute("Value")?.Value);
    }

    [Fact]
    public void Wix_standard_ba_requires_explicit_graph_terms_acceptance_before_acquisition()
    {
        var document = LoadXml("installer", "Bundle.wxs");
        XNamespace Bal = "http://wixtoolset.org/schemas/v4/wxs/bal";
        var ba = document.Descendants(Bal + "WixStandardBootstrapperApplication").Single();
        Assert.Equal("hyperlinkLicense", ba.Attribute("Theme")?.Value);
        Assert.Equal("https://aka.ms/devservicesagreement", ba.Attribute("LicenseUrl")?.Value);

        var variable = document.Descendants(Wix + "Variable")
            .Single(element => (string?)element.Attribute("Name") == "RelayBridgeAcceptMicrosoftGraphTerms");
        Assert.Equal("0", variable.Attribute("Value")?.Value);
        Assert.Equal("yes", variable.Attribute(Bal + "Overridable")?.Value);
        var condition = document.Descendants(Bal + "Condition").Single().Attribute("Condition")?.Value;
        Assert.Contains("WixBundleUILevel = 4", condition, StringComparison.Ordinal);
        Assert.Contains("RelayBridgeAcceptMicrosoftGraphTerms = 1", condition, StringComparison.Ordinal);

        var localization = File.ReadAllText(RepositoryPath("installer", "Bundle.en-us.wxl"));
        Assert.Contains("Microsoft.Graph.Authentication 2.25.0", localization, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Graph.Applications 2.25.0", localization, StringComparison.Ordinal);
        Assert.Contains("I have reviewed and accept", localization, StringComparison.Ordinal);
        Assert.Contains("View Microsoft license terms", localization, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_inputs_pin_wix_powershell_modules_and_upstream_package_hashes()
    {
        var lockPath = RepositoryPath("installer", "tooling-lock.json");
        using var lockDocument = JsonDocument.Parse(File.ReadAllText(lockPath));
        var root = lockDocument.RootElement;

        Assert.Equal(2, root.GetProperty("version").GetInt32());
        Assert.Equal("7.6.4", root.GetProperty("powerShell").GetProperty("version").GetString());
        Assert.Equal(64, root.GetProperty("powerShell").GetProperty("sha256").GetString()!.Length);

        var expectedModules = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Microsoft.Graph.Authentication"] = "2.25.0",
            ["Microsoft.Graph.Applications"] = "2.25.0",
            ["Microsoft.Entra.Authentication"] = "1.3.0",
            ["Microsoft.Entra.Applications"] = "1.3.0",
            ["ExchangeOnlineManagement"] = "3.9.2"
        };
        var actualModules = root.GetProperty("modules").EnumerateArray().ToDictionary(
            module => module.GetProperty("name").GetString()!,
            module => module.GetProperty("version").GetString()!,
            StringComparer.Ordinal);
        Assert.Equal(expectedModules, actualModules);

        foreach (var externalPackage in root.GetProperty("modules").EnumerateArray()
                     .Append(root.GetProperty("powerShell")))
        {
            Assert.StartsWith("https://", externalPackage.GetProperty("source").GetString(), StringComparison.Ordinal);
            Assert.Equal(64, externalPackage.GetProperty("sha256").GetString()!.Length);
        }

        foreach (var prerequisite in root.GetProperty("dotNetPrerequisites").EnumerateArray())
        {
            Assert.Equal("10.0.11", prerequisite.GetProperty("version").GetString());
            Assert.Equal(128, prerequisite.GetProperty("sha512").GetString()!.Length);
        }

        var acquisition = root.GetProperty("externalAcquisition");
        Assert.Equal(1, acquisition.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(64, acquisition.GetProperty("toolingIdentitySha256").GetString()!.Length);
        Assert.Equal(64, acquisition.GetProperty("acceptanceIdentitySha256").GetString()!.Length);
        Assert.Equal("https://aka.ms/devservicesagreement", acquisition.GetProperty("licenseUri").GetString());
        var acquisitionPackages = acquisition.GetProperty("packages").EnumerateArray().ToArray();
        Assert.Equal(4, acquisitionPackages.Length);
        Assert.Equal(2, acquisitionPackages.Count(package => package.GetProperty("requireLicenseAcceptance").GetBoolean()));
        Assert.All(acquisitionPackages, package =>
        {
            Assert.Equal(64, package.GetProperty("sha256").GetString()!.Length);
            Assert.Equal(128, package.GetProperty("burnSha512").GetString()!.Length);
            Assert.True(package.GetProperty("size").GetInt64() > 0);
        });

        var installerProject = File.ReadAllText(RepositoryPath("installer", "RelayBridge.Installer.wixproj"));
        var bundleProject = File.ReadAllText(RepositoryPath("installer", "RelayBridge.Bundle.wixproj"));
        Assert.Contains("WixToolset.Sdk/6.0.2", installerProject, StringComparison.Ordinal);
        Assert.Contains("WixToolset.Sdk/6.0.2", bundleProject, StringComparison.Ordinal);
        Assert.Contains("WixToolset.Util.wixext", installerProject, StringComparison.Ordinal);

        var buildScript = File.ReadAllText(RepositoryPath("installer", "build-installer.ps1"));
        Assert.DoesNotContain("Install-Module", buildScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-RequiredVersion latest", buildScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "RelayBridgeExternalToolingIdentity = &quot;{0}&quot; AND RelayBridgeExternalToolingRelease = &quot;$(var.ProductVersion)&quot;",
            buildScript,
            StringComparison.Ordinal);
        Assert.Contains("$symbolsRoot = Join-Path $artifactRoot 'symbols'", buildScript, StringComparison.Ordinal);
        Assert.Contains("Move-Item -Destination $symbolsRoot", buildScript, StringComparison.Ordinal);

        var validationScript = File.ReadAllText(RepositoryPath("installer", "validate-installer.ps1"));
        Assert.Contains("'.pdb', '.wixpdb', '.dbg'", validationScript, StringComparison.Ordinal);

        Assert.True(File.Exists(RepositoryPath("installer", "generate-sbom.ps1")));
        var sbomScript = File.ReadAllText(RepositoryPath("installer", "generate-sbom.ps1"));
        Assert.Contains("specVersion = '1.6'", sbomScript, StringComparison.Ordinal);
        Assert.Contains("externalAcquisition.packages", sbomScript, StringComparison.Ordinal);
        Assert.Contains("lock.modules", sbomScript, StringComparison.Ordinal);
        Assert.Contains("'WiX Toolset SDK and compiler' '6.0.2' 'application' 'excluded'", sbomScript, StringComparison.Ordinal);
        Assert.Contains("'WiX Burn engine' '6.0.2' 'application' 'required'", sbomScript, StringComparison.Ordinal);
        Assert.Contains("'WiX Standard Bootstrapper Application' '6.0.2' 'application' 'required'", sbomScript, StringComparison.Ordinal);
        Assert.Contains("'WiX NetFx extension runtime and custom actions' '6.0.2' 'library' 'required'", sbomScript, StringComparison.Ordinal);
        Assert.Contains("'WiX Util extension runtime and custom actions' '6.0.2' 'library' 'required'", sbomScript, StringComparison.Ordinal);
        Assert.Contains("'MS-RL' $wixSource", sbomScript, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users\\", sbomScript, StringComparison.OrdinalIgnoreCase);
    }

    private static string PermissionSddl(XElement component) =>
        (string)component.Descendants(Wix + "PermissionEx").Single().Attribute("Sddl")!;

    private static XDocument LoadXml(params string[] segments) =>
        XDocument.Load(RepositoryPath(segments), LoadOptions.PreserveWhitespace);

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
