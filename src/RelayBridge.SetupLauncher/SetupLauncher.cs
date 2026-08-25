// SPDX-License-Identifier: MPL-2.0

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using RelayBridge.Core.Microsoft;

namespace RelayBridge.SetupLauncher;

internal static class LauncherArguments
{
    internal static bool AreValid(string[] arguments) =>
        arguments.Length == 0 ||
        (arguments.Length == 1 &&
         (string.Equals(
              arguments[0],
              "relaybridge-setup://start",
              StringComparison.OrdinalIgnoreCase) ||
          string.Equals(
              arguments[0],
              "relaybridge-setup://start/",
              StringComparison.OrdinalIgnoreCase)));
}

internal static partial class SetupLauncher
{
    private const string WorkerFileName = "RelayBridge.Setup.exe";

    [SupportedOSPlatform("windows")]
    internal static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        using var host = new NamedPipeClientStream(
            ".",
            NativeMicrosoftSetupProtocol.BootstrapPipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        await host.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
        LauncherServerIdentityVerifier.Verify(host);

        using var current = Process.GetCurrentProcess();
        await BoundedFrameRelay.WriteHelloAsync(
            host,
            Environment.ProcessId,
            current.SessionId,
            cancellationToken).ConfigureAwait(false);
        var startFrame = await BoundedFrameRelay.ReadFrameAsync(host, cancellationToken).ConfigureAwait(false);
        var scratchDirectory = ReadScratchDirectory(startFrame);
        ProvisioningScratchDirectory.VerifySession(
            ProvisioningScratchDirectory.DefaultRoot,
            scratchDirectory,
            WindowsIdentity.GetCurrent(TokenAccessLevels.Query).User
                ?? throw new InvalidDataException("The interactive Windows identity is unavailable."));

        var workerPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, WorkerFileName));
        if (!File.Exists(workerPath) ||
            (File.GetAttributes(workerPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The approved RelayBridge setup worker is unavailable.");
        }

        using var job = LauncherJob.Create();
        using var worker = new Process { StartInfo = CreateWorkerStartInfo(workerPath, scratchDirectory) };
        if (!worker.Start())
        {
            throw new InvalidOperationException("The approved RelayBridge setup worker could not start.");
        }

        if (!AssignProcessToJobObject(job, worker.SafeHandle))
        {
            worker.Kill(entireProcessTree: true);
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        await BoundedFrameRelay.WriteFrameAsync(
            worker.StandardInput.BaseStream,
            startFrame,
            cancellationToken).ConfigureAwait(false);

        using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var hostToWorker = BoundedFrameRelay.CopyAsync(
            host,
            worker.StandardInput.BaseStream,
            relayCancellation.Token);
        var workerToHost = BoundedFrameRelay.CopyAsync(
            worker.StandardOutput.BaseStream,
            host,
            relayCancellation.Token);
        var stderr = DrainBoundedAsync(worker.StandardError, relayCancellation.Token);
        var exit = worker.WaitForExitAsync(cancellationToken);

        var first = await Task.WhenAny(exit, hostToWorker, workerToHost).ConfigureAwait(false);
        if (first == hostToWorker)
        {
            await hostToWorker.ConfigureAwait(false);
            relayCancellation.Cancel();
            TerminateJobObject(job, 1);
            await IgnoreCancellationAsync(exit).ConfigureAwait(false);
            return 1;
        }

        if (first == workerToHost)
        {
            await workerToHost.ConfigureAwait(false);
        }

        await exit.ConfigureAwait(false);
        await workerToHost.ConfigureAwait(false);
        relayCancellation.Cancel();
        await IgnoreCancellationAsync(hostToWorker).ConfigureAwait(false);
        await IgnoreCancellationAsync(stderr).ConfigureAwait(false);
        return worker.ExitCode;
    }

    internal static ProcessStartInfo CreateWorkerStartInfo(string workerPath, string scratchDirectory)
    {
        if (!Path.IsPathFullyQualified(workerPath))
        {
            throw new InvalidDataException("The setup worker path is not absolute.");
        }

        var start = new ProcessStartInfo
        {
            FileName = workerPath,
            WorkingDirectory = Path.GetDirectoryName(workerPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        PrivilegedProcessEnvironment.Apply(start, scratchDirectory);
        return start;
    }

    internal static string ReadScratchDirectory(ReadOnlySpan<byte> startFrame)
    {
        try
        {
            using var document = JsonDocument.Parse(startFrame.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("scratchDirectory", out var scratch) ||
                scratch.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("The provisioning scratch path is unavailable.");
            }

            var path = scratch.GetString();
            return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
                ? Path.GetFullPath(path)
                : throw new InvalidDataException("The provisioning scratch path is invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The setup start frame is malformed.", exception);
        }
    }

    private static async Task DrainBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        var total = 0;
        while (true)
        {
            var count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return;
            }

            total += count;
            if (total > NativeMicrosoftSetupProtocol.MaximumMessageBytes)
            {
                throw new InvalidDataException("The setup worker returned excessive diagnostic output.");
            }
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(LauncherJob job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(LauncherJob job, uint exitCode);
}

internal static class BoundedFrameRelay
{
    internal static async Task CopyAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        while (true)
        {
            byte[] frame;
            try
            {
                frame = await ReadFrameAsync(input, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                return;
            }

            await WriteFrameAsync(output, frame, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task<byte[]> ReadFrameAsync(Stream input, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(input, header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > NativeMicrosoftSetupProtocol.MaximumMessageBytes)
        {
            throw new InvalidDataException("The native Microsoft setup frame is invalid.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(input, payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    internal static async Task WriteFrameAsync(
        Stream output,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length <= 0 || payload.Length > NativeMicrosoftSetupProtocol.MaximumMessageBytes)
        {
            throw new InvalidDataException("The native Microsoft setup frame is invalid.");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static Task WriteHelloAsync(
        Stream output,
        int processId,
        int sessionId,
        CancellationToken cancellationToken)
    {
        var json = string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"version\":{NativeMicrosoftSetupProtocol.Version},\"kind\":{(int)NativeSetupMessageKind.Hello},\"processId\":{processId},\"windowsSessionId\":{sessionId},\"failureCategory\":0}}");
        return WriteFrameAsync(output, Encoding.UTF8.GetBytes(json), cancellationToken);
    }

    private static async Task ReadExactlyAsync(
        Stream input,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await input.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("The native Microsoft setup connection closed unexpectedly.");
            }

            read += count;
        }
    }
}

internal sealed partial class LauncherJob : SafeHandleZeroOrMinusOneIsInvalid
{
    private const uint JobObjectLimitKillOnClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    private LauncherJob(IntPtr handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    internal static LauncherJob Create()
    {
        var raw = CreateJobObject(IntPtr.Zero, null);
        if (raw == IntPtr.Zero || raw == new IntPtr(-1))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        var job = new LauncherJob(raw);
        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnClose,
            },
        };
        var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(information, pointer, fDeleteOld: false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, pointer, (uint)length))
            {
                var error = Marshal.GetLastWin32Error();
                job.Dispose();
                throw new System.ComponentModel.Win32Exception(error);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }

        return job;
    }

    protected override bool ReleaseHandle() => CloseHandle(handle);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateJobObject(IntPtr attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        LauncherJob job,
        int informationClass,
        IntPtr information,
        uint informationLength);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize;
        internal UIntPtr MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal UIntPtr ProcessMemoryLimit;
        internal UIntPtr JobMemoryLimit;
        internal UIntPtr PeakProcessMemoryUsed;
        internal UIntPtr PeakJobMemoryUsed;
    }
}

[SupportedOSPlatform("windows")]
internal static partial class LauncherServerIdentityVerifier
{
    private const string ServiceName = "RelayBridge";
    private const string LocalSystemAccount = "LocalSystem";
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceRunning = 0x00000004;
    private const int ScStatusProcessInfo = 0;
    private const int ErrorInsufficientBuffer = 122;

    internal static void Verify(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var processId) || processId == 0)
        {
            throw new InvalidDataException("The RelayBridge service identity could not be verified.");
        }

        Validate(ReadServiceIdentity(processId));
    }

    internal static void Validate(LauncherServerIdentityFacts facts)
    {
        if (facts.PipeProcessId <= 0 ||
            facts.PipeProcessId != facts.ServiceProcessId ||
            facts.ServiceState != ServiceRunning ||
            facts.ServiceType != ServiceWin32OwnProcess ||
            !string.Equals(facts.ServiceStartName, LocalSystemAccount, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                ParseServiceBinaryPath(facts.ServiceBinaryPath),
                Path.GetFullPath(facts.ExpectedHostPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The Microsoft setup launcher connected to an unapproved local service identity.");
        }
    }

    private static LauncherServerIdentityFacts ReadServiceIdentity(uint pipeProcessId)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var service = OpenService(
                manager,
                ServiceName,
                ServiceQueryConfig | ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var statusSize = Marshal.SizeOf<ServiceStatusProcess>();
                var statusBuffer = Marshal.AllocHGlobal(statusSize);
                try
                {
                    if (!QueryServiceStatusEx(
                            service,
                            ScStatusProcessInfo,
                            statusBuffer,
                            checked((uint)statusSize),
                            out _))
                    {
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    }

                    var status = Marshal.PtrToStructure<ServiceStatusProcess>(statusBuffer);
                    var config = ReadServiceConfiguration(service);
                    return new LauncherServerIdentityFacts(
                        checked((int)pipeProcessId),
                        checked((int)status.ProcessId),
                        status.ServiceType,
                        status.CurrentState,
                        config.BinaryPath,
                        config.StartName,
                        ExpectedHostPath());
                }
                finally
                {
                    Marshal.FreeHGlobal(statusBuffer);
                }
            }
            finally
            {
                _ = CloseServiceHandle(service);
            }
        }
        finally
        {
            _ = CloseServiceHandle(manager);
        }
    }

    private static (string BinaryPath, string StartName) ReadServiceConfiguration(IntPtr service)
    {
        _ = QueryServiceConfig(service, IntPtr.Zero, 0, out var required);
        if (required == 0 || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!QueryServiceConfig(service, buffer, required, out _))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            var config = Marshal.PtrToStructure<QueryServiceConfigData>(buffer);
            return (
                Marshal.PtrToStringUni(config.BinaryPathName)
                    ?? throw new InvalidDataException("The RelayBridge service image path is unavailable."),
                Marshal.PtrToStringUni(config.ServiceStartName)
                    ?? throw new InvalidDataException("The RelayBridge service account is unavailable."));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string ParseServiceBinaryPath(string configuredPath)
    {
        if (configuredPath.Length < 2 || configuredPath[0] != '"' || configuredPath[^1] != '"')
        {
            throw new InvalidDataException("The RelayBridge service image path is not approved.");
        }

        var value = configuredPath[1..^1];
        if (value.Contains('"') || !Path.IsPathFullyQualified(value))
        {
            throw new InvalidDataException("The RelayBridge service image path is not approved.");
        }

        return Path.GetFullPath(value);
    }

    private static string ExpectedHostPath() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Host", "RelayBridge.Host.exe"));

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        internal uint ServiceType;
        internal uint CurrentState;
        internal uint ControlsAccepted;
        internal uint Win32ExitCode;
        internal uint ServiceSpecificExitCode;
        internal uint CheckPoint;
        internal uint WaitHint;
        internal uint ProcessId;
        internal uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryServiceConfigData
    {
        internal uint ServiceType;
        internal uint StartType;
        internal uint ErrorControl;
        internal IntPtr BinaryPathName;
        internal IntPtr LoadOrderGroup;
        internal uint TagId;
        internal IntPtr Dependencies;
        internal IntPtr ServiceStartName;
        internal IntPtr DisplayName;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(
        IntPtr service,
        int infoLevel,
        IntPtr buffer,
        uint bufferSize,
        out uint bytesNeeded);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfig(
        IntPtr service,
        IntPtr config,
        uint bufferSize,
        out uint bytesNeeded);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr serviceHandle);
}

internal sealed record LauncherServerIdentityFacts(
    int PipeProcessId,
    int ServiceProcessId,
    uint ServiceType,
    uint ServiceState,
    string ServiceBinaryPath,
    string ServiceStartName,
    string ExpectedHostPath);
