// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;

namespace RelayBridge.Core.Microsoft;

public static class PrivilegedProcessEnvironment
{
    public static IReadOnlyDictionary<string, string> Create(string scratchDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Native Microsoft setup requires Windows.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(scratchDirectory);
        if (!Path.IsPathFullyQualified(scratchDirectory))
        {
            throw new InvalidDataException("The provisioning scratch path is invalid.");
        }

        var windows = RequiredFolder(Environment.SpecialFolder.Windows, "Windows");
        var system = Environment.SystemDirectory;
        var userProfile = RequiredFolder(Environment.SpecialFolder.UserProfile, "user profile");
        var applicationData = RequiredFolder(Environment.SpecialFolder.ApplicationData, "application data");
        var localApplicationData = RequiredFolder(Environment.SpecialFolder.LocalApplicationData, "local application data");
        var commonApplicationData = RequiredFolder(Environment.SpecialFolder.CommonApplicationData, "common application data");
        var programFiles = RequiredFolder(Environment.SpecialFolder.ProgramFiles, "Program Files");
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var commonProgramFiles = RequiredFolder(Environment.SpecialFolder.CommonProgramFiles, "Common Program Files");
        var commonProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);
        var homeRoot = Path.GetPathRoot(userProfile) ?? throw new InvalidOperationException("The user profile root is unavailable.");
        var homePath = userProfile[homeRoot.Length..];

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows,
            ["WINDIR"] = windows,
            ["SystemDrive"] = Path.GetPathRoot(windows) ?? "C:\\",
            ["ComSpec"] = Path.Combine(system, "cmd.exe"),
            ["PATH"] = string.Join(
                Path.PathSeparator,
                system,
                windows,
                Path.Combine(system, "Wbem")),
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
            ["USERPROFILE"] = userProfile,
            ["APPDATA"] = applicationData,
            ["LOCALAPPDATA"] = localApplicationData,
            ["ProgramData"] = commonApplicationData,
            ["ALLUSERSPROFILE"] = commonApplicationData,
            ["TEMP"] = Path.GetFullPath(scratchDirectory),
            ["TMP"] = Path.GetFullPath(scratchDirectory),
            ["HOMEDRIVE"] = homeRoot.TrimEnd(Path.DirectorySeparatorChar),
            ["HOMEPATH"] = Path.DirectorySeparatorChar + homePath.TrimStart(Path.DirectorySeparatorChar),
            ["USERNAME"] = Environment.UserName,
            ["USERDOMAIN"] = Environment.UserDomainName,
            ["ProgramFiles"] = programFiles,
            ["ProgramW6432"] = programFiles,
            ["CommonProgramFiles"] = commonProgramFiles,
            ["DOTNET_EnableDiagnostics"] = "0",
        };

        AddIfPresent(values, "ProgramFiles(x86)", programFilesX86);
        AddIfPresent(values, "CommonProgramFiles(x86)", commonProgramFilesX86);
        AddIfPresent(values, "CommonProgramW6432", commonProgramFiles);
        return values;
    }

    public static void Apply(ProcessStartInfo startInfo, string scratchDirectory)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.Environment.Clear();
        foreach (var pair in Create(scratchDirectory))
        {
            startInfo.Environment.Add(pair.Key, pair.Value);
        }
    }

    private static string RequiredFolder(Environment.SpecialFolder folder, string description)
    {
        var path = Environment.GetFolderPath(folder);
        return string.IsNullOrWhiteSpace(path)
            ? throw new InvalidOperationException($"The Windows {description} path is unavailable.")
            : Path.GetFullPath(path);
    }

    private static void AddIfPresent(IDictionary<string, string> values, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values[name] = value;
        }
    }
}
