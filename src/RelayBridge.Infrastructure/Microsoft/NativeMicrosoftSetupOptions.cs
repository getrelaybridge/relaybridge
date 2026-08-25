// SPDX-License-Identifier: MPL-2.0

namespace RelayBridge.Infrastructure.Microsoft;

public sealed class NativeMicrosoftSetupOptions
{
    public bool Enabled { get; set; }

    public string InstallationRoot { get; set; } = string.Empty;

    public string LauncherPath { get; set; } = string.Empty;

    public string ExpectedLauncherSha256 { get; set; } = string.Empty;

    public string WorkerPath { get; set; } = string.Empty;

    public string HelperManifestPath { get; set; } = string.Empty;

    public string ExpectedHelperManifestSha256 { get; set; } = string.Empty;

    public string ToolingRoot { get; set; } = string.Empty;

    public string ToolingManifestPath { get; set; } = string.Empty;

    public string ExpectedToolingManifestSha256 { get; set; } = string.Empty;

    public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromMinutes(20);

    public TimeSpan BootstrapTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (!Path.IsPathFullyQualified(InstallationRoot) ||
            !Path.IsPathFullyQualified(LauncherPath) ||
            !Path.IsPathFullyQualified(WorkerPath) ||
            !Path.IsPathFullyQualified(HelperManifestPath) ||
            !Path.IsPathFullyQualified(ToolingRoot) ||
            !Path.IsPathFullyQualified(ToolingManifestPath) ||
            ExpectedLauncherSha256.Length != 64 ||
            ExpectedLauncherSha256.Any(character => !Uri.IsHexDigit(character)) ||
            ExpectedHelperManifestSha256.Length != 64 ||
            ExpectedHelperManifestSha256.Any(character => !Uri.IsHexDigit(character)) ||
            ExpectedToolingManifestSha256.Length != 64 ||
            ExpectedToolingManifestSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException(
                "Native Microsoft setup requires absolute installer-owned paths and exact helper release hashes.");
        }

        var helperRoot = Path.GetDirectoryName(Path.GetFullPath(LauncherPath));
        if (helperRoot is null || !string.Equals(
                helperRoot,
                Path.GetDirectoryName(Path.GetFullPath(HelperManifestPath)),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                helperRoot,
                Path.GetDirectoryName(Path.GetFullPath(WorkerPath)),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetFileName(LauncherPath),
                "RelayBridge.SetupLauncher.exe",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetFileName(WorkerPath),
                "RelayBridge.Setup.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The native launcher, managed setup worker, and release manifest must share one dedicated installation directory.");
        }

        if (!IsWithin(Path.GetFullPath(InstallationRoot), helperRoot) ||
            !IsWithin(Path.GetFullPath(InstallationRoot), Path.GetFullPath(ToolingRoot)) ||
            !IsWithin(Path.GetFullPath(InstallationRoot), Path.GetFullPath(ToolingManifestPath)))
        {
            throw new InvalidOperationException(
                "Native Microsoft setup files must remain inside the installer-owned RelayBridge installation root.");
        }

        if (SessionTimeout < TimeSpan.FromMinutes(2) || SessionTimeout > TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException("Native Microsoft setup timeout must be between 2 minutes and 1 hour.");
        }


        if (BootstrapTimeout < TimeSpan.FromSeconds(5) || BootstrapTimeout > TimeSpan.FromMinutes(2))
        {
            throw new InvalidOperationException("Native Microsoft setup bootstrap timeout must be between 5 seconds and 2 minutes.");
        }
    }


    private static bool IsWithin(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);
        return string.Equals(normalizedRoot, normalizedPath, StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }
}
