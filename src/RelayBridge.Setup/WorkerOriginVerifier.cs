// SPDX-License-Identifier: MPL-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using RelayBridge.Core.Microsoft;

namespace RelayBridge.Setup;

[SupportedOSPlatform("windows")]
internal static partial class WorkerOriginVerifier
{
    private const uint TokenQuery = 0x0008;

    internal static void Verify(NativeSetupStartRequest start)
    {
        ArgumentNullException.ThrowIfNull(start);
        var currentPath = Path.GetFullPath(Environment.ProcessPath
            ?? throw new InvalidDataException("The setup worker path is unavailable."));
        var expectedLauncherPath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(currentPath)
                ?? throw new InvalidDataException("The setup worker directory is unavailable."),
            "RelayBridge.SetupLauncher.exe"));
        var claimedLauncherPath = Path.GetFullPath(start.LauncherPath);
        if (!string.Equals(expectedLauncherPath, claimedLauncherPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The setup worker was not started by the approved launcher.");
        }

        var parentProcessId = GetParentProcessId();
        if (parentProcessId <= 0 || parentProcessId != start.LauncherProcessId)
        {
            throw new InvalidDataException("The setup worker parent process is not approved.");
        }

        using var current = Process.GetCurrentProcess();
        using var parent = Process.GetProcessById(parentProcessId);
        var actualLauncherPath = Path.GetFullPath(parent.MainModule?.FileName
            ?? throw new InvalidDataException("The setup launcher path is unavailable."));
        if (!string.Equals(actualLauncherPath, expectedLauncherPath, StringComparison.OrdinalIgnoreCase) ||
            parent.SessionId != start.LauncherWindowsSessionId ||
            current.SessionId != start.LauncherWindowsSessionId)
        {
            throw new InvalidDataException("The setup worker launcher identity is not approved.");
        }

        var expectedSid = new SecurityIdentifier(start.LauncherUserSid);
        var currentSid = WindowsIdentity.GetCurrent(TokenAccessLevels.Query).User;
        var parentSid = GetProcessSid(parent);
        if (currentSid is null)
        {
            throw new InvalidDataException("The setup worker Windows identity is not approved.");
        }

        if (start.LauncherSha256.Length != 64 || start.LauncherSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("The setup launcher hash is invalid.");
        }

        byte[] actualHash;
        byte[] expectedHash;
        using (var stream = new FileStream(
                   actualLauncherPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   64 * 1024,
                   FileOptions.SequentialScan))
        {
            actualHash = SHA256.HashData(stream);
            expectedHash = Convert.FromHexString(start.LauncherSha256);
        }

        Validate(new WorkerOriginFacts(
            current.Id,
            parentProcessId,
            start.LauncherProcessId,
            current.SessionId,
            parent.SessionId,
            start.LauncherWindowsSessionId,
            actualLauncherPath,
            expectedLauncherPath,
            currentSid,
            parentSid,
            expectedSid,
            actualHash,
            expectedHash));

        TrustedWindowsPathVerifier.VerifyInstallationTree(
            start.InstallationRoot,
            [actualLauncherPath, currentPath],
            recursivelyVerifyDirectories: false);
        ProvisioningScratchDirectory.VerifySession(
            ProvisioningScratchDirectory.DefaultRoot,
            start.ScratchDirectory,
            expectedSid);
    }

    internal static void Validate(WorkerOriginFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.WorkerProcessId <= 0 || facts.ParentProcessId <= 0 ||
            facts.ParentProcessId != facts.ExpectedLauncherProcessId ||
            facts.WorkerSessionId != facts.ExpectedSessionId ||
            facts.ParentSessionId != facts.ExpectedSessionId ||
            !string.Equals(Path.GetFullPath(facts.ParentPath), Path.GetFullPath(facts.ExpectedLauncherPath),
                StringComparison.OrdinalIgnoreCase) ||
            !facts.WorkerSid.Equals(facts.ExpectedSid) ||
            !facts.ParentSid.Equals(facts.ExpectedSid) ||
            facts.ParentHash.Length != SHA256.HashSizeInBytes ||
            facts.ExpectedLauncherHash.Length != SHA256.HashSizeInBytes ||
            !CryptographicOperations.FixedTimeEquals(facts.ParentHash, facts.ExpectedLauncherHash))
        {
            throw new InvalidDataException("The setup worker was not started by the approved launcher.");
        }
    }

    private static int GetParentProcessId()
    {
        var information = new ProcessBasicInformation();
        var status = NtQueryInformationProcess(
            Process.GetCurrentProcess().Handle,
            0,
            ref information,
            Marshal.SizeOf<ProcessBasicInformation>(),
            out _);
        if (status != 0)
        {
            throw new Win32Exception(status, "The setup worker parent process could not be determined.");
        }

        return checked((int)information.InheritedFromUniqueProcessId);
    }

    private static SecurityIdentifier GetProcessSid(Process process)
    {
        if (!OpenProcessToken(process.SafeHandle, TokenQuery, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The setup launcher token could not be opened.");
        }

        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            return identity.User
                ?? throw new InvalidDataException("The setup launcher Windows identity is unavailable.");
        }
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(
        SafeProcessHandle processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        internal IntPtr Reserved1;
        internal IntPtr PebBaseAddress;
        internal IntPtr Reserved2_0;
        internal IntPtr Reserved2_1;
        internal IntPtr UniqueProcessId;
        internal IntPtr InheritedFromUniqueProcessId;
    }
}

internal sealed record WorkerOriginFacts(
    int WorkerProcessId,
    int ParentProcessId,
    int ExpectedLauncherProcessId,
    int WorkerSessionId,
    int ParentSessionId,
    int ExpectedSessionId,
    string ParentPath,
    string ExpectedLauncherPath,
    SecurityIdentifier WorkerSid,
    SecurityIdentifier ParentSid,
    SecurityIdentifier ExpectedSid,
    byte[] ParentHash,
    byte[] ExpectedLauncherHash);
