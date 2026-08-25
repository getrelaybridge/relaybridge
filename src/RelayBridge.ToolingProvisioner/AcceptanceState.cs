// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace RelayBridge.ToolingProvisioner;

internal sealed class AcceptanceState
{
    internal const string RegistryKeyPath = @"SOFTWARE\RelayBridge\Installer";
    internal const string RegistryValueName = "ExternalToolingIdentity";
    internal const string RegistryReleaseValueName = "ExternalToolingRelease";
    internal const string RecordFileName = "microsoft-graph-terms.json";

    private readonly string stateRoot;

    internal AcceptanceState(string stateRoot)
    {
        this.stateRoot = Path.GetFullPath(stateRoot);
    }

    internal string RecordPath => Path.Combine(stateRoot, RecordFileName);

    internal bool IsAccepted(AcquisitionLock acquisitionLock)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath, writable: false);
            if (key?.GetValue(RegistryValueName) is not string marker ||
                !marker.Equals(acquisitionLock.ToolingIdentitySha256, StringComparison.Ordinal))
            {
                return false;
            }

            if (!File.Exists(RecordPath))
            {
                return false;
            }

            InstallerPathSecurity.VerifyProtectedPath(
                ToolingInstaller.ProgramDataRoot,
                stateRoot);
            using var document = JsonDocument.Parse(File.ReadAllText(RecordPath), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 12,
            });
            return RecordMatches(document.RootElement, acquisitionLock);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          JsonException or InvalidOperationException or ToolingProvisioningException)
        {
            return false;
        }
    }

    internal static bool RecordMatches(JsonElement root, AcquisitionLock acquisitionLock)
    {
        try
        {
            if (root.GetProperty("schemaVersion").GetInt32() != acquisitionLock.SchemaVersion ||
                !root.GetProperty("accepted").GetBoolean() ||
                root.GetProperty("acceptanceIdentitySha256").GetString() != acquisitionLock.AcceptanceIdentitySha256 ||
                root.GetProperty("toolingIdentitySha256").GetString() != acquisitionLock.ToolingIdentitySha256 ||
                root.GetProperty("licenseUri").GetString() != acquisitionLock.LicenseUri.AbsoluteUri ||
                !DateTimeOffset.TryParse(
                    root.GetProperty("acceptedAtUtc").GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out _))
            {
                return false;
            }

            var actualPackages = root.GetProperty("packages").EnumerateArray().ToArray();
            var expectedPackages = acquisitionLock.Packages.Where(package => package.RequireLicenseAcceptance).ToArray();
            if (actualPackages.Length != expectedPackages.Length)
            {
                return false;
            }

            for (var index = 0; index < expectedPackages.Length; index++)
            {
                var actual = actualPackages[index];
                var expected = expectedPackages[index];
                if (actual.GetProperty("id").GetString() != expected.Id ||
                    actual.GetProperty("version").GetString() != expected.Version ||
                    actual.GetProperty("sha256").GetString() != expected.Sha256 ||
                    actual.GetProperty("requireLicenseAcceptance").GetBoolean() != expected.RequireLicenseAcceptance ||
                    actual.GetProperty("licenseUri").GetString() != expected.LicenseUri.AbsoluteUri)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
                                          FormatException or KeyNotFoundException)
        {
            return false;
        }
    }

    internal void WriteAccepted(AcquisitionLock acquisitionLock, string releaseIdentity)
    {
        InstallerPathSecurity.CreateProtectedDirectory(stateRoot, allowUsersReadExecute: true);
        var temporaryPath = Path.Combine(stateRoot, $".{RecordFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", acquisitionLock.SchemaVersion);
                writer.WriteBoolean("accepted", true);
                writer.WriteString("acceptanceIdentitySha256", acquisitionLock.AcceptanceIdentitySha256);
                writer.WriteString("toolingIdentitySha256", acquisitionLock.ToolingIdentitySha256);
                writer.WriteString("licenseUri", acquisitionLock.LicenseUri.AbsoluteUri);
                writer.WriteString("acceptedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("relayBridgeRelease", releaseIdentity);
                writer.WriteStartArray("packages");
                foreach (var package in acquisitionLock.Packages.Where(package => package.RequireLicenseAcceptance))
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", package.Id);
                    writer.WriteString("version", package.Version);
                    writer.WriteString("sha256", package.Sha256);
                    writer.WriteBoolean("requireLicenseAcceptance", package.RequireLicenseAcceptance);
                    writer.WriteString("licenseUri", package.LicenseUri.AbsoluteUri);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            File.Move(temporaryPath, RecordPath, overwrite: true);
            WriteReleaseMarker(acquisitionLock, releaseIdentity);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    internal static void WriteReleaseMarker(AcquisitionLock acquisitionLock, string releaseIdentity)
    {
        using var key = Registry.LocalMachine.CreateSubKey(RegistryKeyPath, writable: true)
            ?? throw new ToolingProvisioningException("The machine installer state could not be created.");
        key.SetValue(RegistryValueName, acquisitionLock.ToolingIdentitySha256, RegistryValueKind.String);
        key.SetValue(RegistryReleaseValueName, releaseIdentity, RegistryValueKind.String);
    }

    internal void Remove()
    {
        if (File.Exists(RecordPath))
        {
            File.Delete(RecordPath);
        }

        using var key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath, writable: true);
        key?.DeleteValue(RegistryValueName, throwOnMissingValue: false);
        key?.DeleteValue(RegistryReleaseValueName, throwOnMissingValue: false);
    }
}
