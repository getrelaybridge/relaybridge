// SPDX-License-Identifier: MPL-2.0

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RelayBridge.ToolingProvisioner;

internal static partial class WindowsServiceControl
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceRunning = 0x00000004;
    private const int ErrorServiceDoesNotExist = 1060;

    internal static void StartRelayBridge()
    {
        WithService(ServiceQueryStatus | ServiceStart, allowMissing: false, service =>
        {
            var state = QueryState(service);
            if (state == ServiceRunning)
            {
                return;
            }

            if (state == ServiceStopPending)
            {
                WaitForState(service, ServiceStopped, TimeSpan.FromSeconds(30));
            }

            if (!StartService(service, 0, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RelayBridge could not start after tooling verification.");
            }

            WaitForState(service, ServiceRunning, TimeSpan.FromSeconds(30));
        });
    }

    internal static void StopRelayBridgeIfPresent()
    {
        WithService(ServiceQueryStatus | ServiceStop, allowMissing: true, service =>
        {
            var state = QueryState(service);
            if (state == ServiceStopped)
            {
                return;
            }

            if (state != ServiceStopPending)
            {
                if (!ControlService(service, ServiceControlStop, out _))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "RelayBridge could not stop for tooling removal.");
                }
            }

            WaitForState(service, ServiceStopped, TimeSpan.FromSeconds(30));
        });
    }

    private static void WithService(uint access, bool allowMissing, Action<IntPtr> action)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The Windows service manager is unavailable.");
        }

        try
        {
            var service = OpenService(manager, "RelayBridge", access);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (allowMissing && error == ErrorServiceDoesNotExist)
                {
                    return;
                }

                throw new Win32Exception(error, "The RelayBridge service is unavailable.");
            }

            try
            {
                action(service);
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static uint QueryState(IntPtr service)
    {
        if (!QueryServiceStatusEx(
                service,
                0,
                out var status,
                Marshal.SizeOf<ServiceStatusProcess>(),
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "RelayBridge service status is unavailable.");
        }

        return status.CurrentState;
    }

    private static void WaitForState(IntPtr service, uint expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (QueryState(service) == expected)
            {
                return;
            }

            using var delay = new ManualResetEvent(initialState: false);
            delay.WaitOne(TimeSpan.FromMilliseconds(100));
        }

        throw new ToolingProvisioningException("The RelayBridge service did not reach the required servicing state.");
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
}
