// SPDX-License-Identifier: MPL-2.0

using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RelayBridge.PrinterConfigurator;

[SupportedOSPlatform("windows")]
internal static partial class PrinterConfiguratorDialog
{
    private const uint MbYesNo = 0x00000004;
    private const uint MbIconWarning = 0x00000030;
    private const uint MbIconInformation = 0x00000040;
    private const uint MbIconError = 0x00000010;
    private const uint MbSetForeground = 0x00010000;
    private const int IdYes = 6;

    internal static bool Confirm(string address, int port) => MessageBox(
        IntPtr.Zero,
        $"Apply RelayBridge printer connectivity?\n\nListener: {address}:{port}\nQueue delivery: Enabled\n\nRelayBridge will update only its protected production configuration and restart only the RelayBridge service. Windows Firewall is not changed.",
        "RelayBridge — Apply printer connectivity",
        MbYesNo | MbIconWarning | MbSetForeground) == IdYes;

    internal static void ShowSuccess(string address, int port) => _ = MessageBox(
        IntPtr.Zero,
        $"Printer connectivity applied.\n\nListener: {address}:{port}\nQueue delivery: Enabled\nService: Healthy\n\nWindows Firewall may still require a narrowly scoped manual rule.",
        "RelayBridge",
        MbIconInformation | MbSetForeground);

    internal static void ShowFailure(PrinterApplyException failure) => ShowError(FormatFailure(failure));

    internal static string FormatFailure(PrinterApplyException failure)
    {
        var technical = $"\n\nStage: {failure.Stage}\nTimestamp (UTC): {failure.TimestampUtc:u}";
        if (failure.WindowsErrorCode is not null)
        {
            technical += $"\nWindows error: {failure.WindowsErrorCode.Value}";
        }

        if (failure.ServiceState is not null)
        {
            technical += $"\nService state: {failure.ServiceState.Value}";
        }

        return failure.Outcome switch
        {
            PrinterApplyOutcome.ConfigurationWriteFailed =>
                "Printer connectivity was not saved. RelayBridge's authoritative configuration was not changed." + technical,
            PrinterApplyOutcome.ConfigurationSavedVerificationFailed =>
                "Printer connectivity was written, but the saved file could not be verified. RelayBridge was not restarted. Review the protected configuration before starting the service." + technical,
            PrinterApplyOutcome.ConfigurationSavedRestartFailed =>
                "Printer connectivity was saved, but RelayBridge could not be restarted. Start or restart the RelayBridge service, then verify readiness." + technical,
            PrinterApplyOutcome.ServiceStartedReadinessFailed =>
                "Printer connectivity was saved and RelayBridge was started, but management or SMTP-listener readiness could not be confirmed. Verify RelayBridge service and listener status." + technical,
            _ => "Printer connectivity did not complete." + technical,
        };
    }

    internal static void ShowError(string message) => _ = MessageBox(
        IntPtr.Zero,
        message,
        "RelayBridge",
        MbIconError | MbSetForeground);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr window, string text, string caption, uint type);
}

[SupportedOSPlatform("windows")]
internal static partial class WindowsServiceRestarter
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceRunning = 0x00000004;
    private const uint Synchronize = 0x00100000;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceCannotAcceptControl = 1061;
    private const int MaximumStartAttempts = 4;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PreviousProcessExitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartRetryDelay = TimeSpan.FromMilliseconds(500);

    internal static void RestartRelayBridge()
    {
        using var control = WindowsRelayBridgeServiceControl.Open();
        RestartRelayBridge(control, Delay);
    }

    internal static void RestartRelayBridge(
        IRelayBridgeServiceControl control,
        Action<TimeSpan>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(control);
        delay ??= Delay;
        var stage = PrinterApplyStage.ServiceStop;
        RelayBridgeServiceSnapshot? observed = null;
        try
        {
            observed = control.Query();
            using var previousProcess = observed.ProcessId == 0
                ? null
                : control.CaptureProcess(observed.ProcessId);

            if (observed.State != ServiceStopped && observed.State != ServiceStopPending)
            {
                control.RequestStop();
            }

            if (!control.WaitForState(ServiceStopped, StopTimeout, out observed))
            {
                throw new ServiceRestartException(stage, null, observed.State);
            }

            stage = PrinterApplyStage.PreviousProcessExit;
            if (previousProcess is not null && !previousProcess.WaitForExit(PreviousProcessExitTimeout))
            {
                throw new ServiceRestartException(stage, null, observed.State);
            }

            stage = PrinterApplyStage.ServiceStart;
            var started = false;
            for (var attempt = 1; attempt <= MaximumStartAttempts; attempt++)
            {
                var start = control.TryStart();
                if (start.Succeeded)
                {
                    started = true;
                    break;
                }

                if (start.WindowsErrorCode == ErrorServiceAlreadyRunning &&
                    control.Query().State == ServiceRunning)
                {
                    started = true;
                    break;
                }

                if (start.WindowsErrorCode is not ErrorServiceAlreadyRunning and not ErrorServiceCannotAcceptControl ||
                    attempt == MaximumStartAttempts)
                {
                    throw new ServiceRestartException(stage, start.WindowsErrorCode, observed.State);
                }

                delay(StartRetryDelay);
            }

            if (!started)
            {
                throw new ServiceRestartException(stage, null, observed.State);
            }

            stage = PrinterApplyStage.ServiceRunning;
            if (!control.WaitForState(ServiceRunning, StartTimeout, out observed))
            {
                throw new ServiceRestartException(stage, null, observed.State);
            }
        }
        catch (ServiceRestartException)
        {
            throw;
        }
        catch (Win32Exception exception)
        {
            throw new ServiceRestartException(stage, exception.NativeErrorCode, observed?.State, exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new ServiceRestartException(stage, null, observed?.State, exception);
        }
    }

    private static void Delay(TimeSpan duration)
    {
        using var delay = new ManualResetEvent(initialState: false);
        delay.WaitOne(duration);
    }

    private sealed class WindowsRelayBridgeServiceControl : IRelayBridgeServiceControl
    {
        private readonly IntPtr _manager;
        private readonly IntPtr _service;

        private WindowsRelayBridgeServiceControl(IntPtr manager, IntPtr service)
        {
            _manager = manager;
            _service = service;
        }

        internal static WindowsRelayBridgeServiceControl Open()
        {
            var manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The Windows service manager is unavailable.");
            }

            var service = OpenService(
                manager,
                PrinterConfigurator.ServiceName,
                ServiceQueryStatus | ServiceStart | ServiceStop);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                _ = CloseServiceHandle(manager);
                throw new Win32Exception(error, "The RelayBridge service is unavailable.");
            }

            return new WindowsRelayBridgeServiceControl(manager, service);
        }

        public RelayBridgeServiceSnapshot Query()
        {
            if (!QueryServiceStatusEx(
                    _service,
                    0,
                    out var status,
                    Marshal.SizeOf<ServiceStatusProcess>(),
                    out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RelayBridge service status is unavailable.");
            }

            return new RelayBridgeServiceSnapshot(status.CurrentState, status.ProcessId, status.Win32ExitCode);
        }

        public IRelayBridgeProcessObservation? CaptureProcess(uint processId)
        {
            var process = OpenProcess(Synchronize, inheritHandle: false, processId);
            if (process != IntPtr.Zero)
            {
                return new WindowsProcessObservation(process);
            }

            var error = Marshal.GetLastWin32Error();
            if (error == ErrorInvalidParameter)
            {
                return null;
            }

            throw new Win32Exception(error, "The existing RelayBridge service process could not be observed.");
        }

        public void RequestStop()
        {
            if (!ControlService(_service, ServiceControlStop, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RelayBridge could not stop for configuration apply.");
            }
        }

        public ServiceStartResult TryStart()
        {
            return StartService(_service, 0, IntPtr.Zero)
                ? new ServiceStartResult(true, null)
                : new ServiceStartResult(false, Marshal.GetLastWin32Error());
        }

        public bool WaitForState(uint expected, TimeSpan timeout, out RelayBridgeServiceSnapshot observed)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            observed = Query();
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (observed.State == expected)
                {
                    return true;
                }

                Delay(TimeSpan.FromMilliseconds(100));
                observed = Query();
            }

            return observed.State == expected;
        }

        public void Dispose()
        {
            _ = CloseServiceHandle(_service);
            _ = CloseServiceHandle(_manager);
        }
    }

    private sealed class WindowsProcessObservation(IntPtr process) : IRelayBridgeProcessObservation
    {
        public bool WaitForExit(TimeSpan timeout)
        {
            var milliseconds = checked((uint)Math.Clamp(timeout.TotalMilliseconds, 0, uint.MaxValue - 1));
            var result = WaitForSingleObject(process, milliseconds);
            return result switch
            {
                WaitObject0 => true,
                WaitTimeout => false,
                _ => throw new Win32Exception(Marshal.GetLastWin32Error(), "The existing RelayBridge process exit could not be observed."),
            };
        }

        public void Dispose()
        {
            _ = CloseHandle(process);
        }
    }

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
    private struct ServiceStatus
    {
        internal uint ServiceType;
        internal uint CurrentState;
        internal uint ControlsAccepted;
        internal uint Win32ExitCode;
        internal uint ServiceSpecificExitCode;
        internal uint CheckPoint;
        internal uint WaitHint;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartService(IntPtr service, uint argumentCount, IntPtr arguments);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlService", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(IntPtr service, uint control, out ServiceStatus status);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(
        IntPtr service,
        int infoLevel,
        out ServiceStatusProcess status,
        int bufferSize,
        out int bytesNeeded);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}

internal sealed record RelayBridgeServiceSnapshot(uint State, uint ProcessId, uint Win32ExitCode);

internal sealed record ServiceStartResult(bool Succeeded, int? WindowsErrorCode);

internal interface IRelayBridgeServiceControl : IDisposable
{
    RelayBridgeServiceSnapshot Query();

    IRelayBridgeProcessObservation? CaptureProcess(uint processId);

    void RequestStop();

    ServiceStartResult TryStart();

    bool WaitForState(uint expected, TimeSpan timeout, out RelayBridgeServiceSnapshot observed);
}

internal interface IRelayBridgeProcessObservation : IDisposable
{
    bool WaitForExit(TimeSpan timeout);
}

internal sealed class ServiceRestartException : Exception
{
    internal ServiceRestartException(
        PrinterApplyStage stage,
        int? windowsErrorCode,
        uint? serviceState,
        Exception? innerException = null)
        : base("RelayBridge service restart did not complete.", innerException)
    {
        Stage = stage;
        WindowsErrorCode = windowsErrorCode;
        ServiceState = serviceState;
    }

    internal PrinterApplyStage Stage { get; }

    internal int? WindowsErrorCode { get; }

    internal uint? ServiceState { get; }
}

[SupportedOSPlatform("windows")]
internal static partial class PrinterConfiguratorServiceIdentity
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceRunning = 0x00000004;
    private const int ErrorInsufficientBuffer = 122;

    internal static void Verify(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pipeProcessId) ||
            pipeProcessId == 0)
        {
            throw new InvalidDataException("The RelayBridge service identity could not be verified.");
        }

        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var service = OpenService(manager, PrinterConfigurator.ServiceName, ServiceQueryConfig | ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                if (!QueryServiceStatusEx(
                        service,
                        0,
                        out var status,
                        Marshal.SizeOf<ServiceStatusProcess>(),
                        out _) ||
                    status.ProcessId != pipeProcessId || status.ServiceType != ServiceWin32OwnProcess ||
                    status.CurrentState != ServiceRunning)
                {
                    throw new InvalidDataException("The printer configurator connected to an unapproved service process.");
                }

                var config = ReadConfiguration(service);
                var expectedHost = Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "Host",
                    "RelayBridge.Host.exe"));
                if (!string.Equals(config.StartName, "LocalSystem", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(ParseQuotedPath(config.BinaryPath), expectedHost, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The RelayBridge service configuration is not approved.");
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

    private static (string BinaryPath, string StartName) ReadConfiguration(IntPtr service)
    {
        _ = QueryServiceConfig(service, IntPtr.Zero, 0, out var required);
        if (required == 0 || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!QueryServiceConfig(service, buffer, required, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var value = Marshal.PtrToStructure<QueryServiceConfigData>(buffer);
            return (
                Marshal.PtrToStringUni(value.BinaryPathName) ?? throw new InvalidDataException(),
                Marshal.PtrToStringUni(value.ServiceStartName) ?? throw new InvalidDataException());
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static string ParseQuotedPath(string configuredPath)
    {
        if (configuredPath.Length < 2 || configuredPath[0] != '"' || configuredPath[^1] != '"')
        {
            throw new InvalidDataException("The RelayBridge service path is not approved.");
        }

        var value = configuredPath[1..^1];
        if (value.Contains('"') || !Path.IsPathFullyQualified(value))
        {
            throw new InvalidDataException("The RelayBridge service path is not approved.");
        }

        return Path.GetFullPath(value);
    }

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

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(
        IntPtr service,
        int infoLevel,
        out ServiceStatusProcess status,
        int bufferSize,
        out int bytesNeeded);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfig(IntPtr service, IntPtr config, uint bufferSize, out uint bytesNeeded);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);
}
