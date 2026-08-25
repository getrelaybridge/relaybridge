// SPDX-License-Identifier: MPL-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RelayBridge.Setup;

[SupportedOSPlatform("windows")]
internal sealed class ExchangeWamConsoleLease : IDisposable
{
    private const string ConsoleTitle = "RelayBridge — Microsoft setup";
    private const int DefaultAttachmentAttempts = 40;
    private static readonly TimeSpan DefaultAttachmentPollInterval = TimeSpan.FromMilliseconds(50);
    private readonly IExchangeWamConsoleNative _native;
    private bool _ownsConsole;
    private bool _disposed;

    private ExchangeWamConsoleLease(IExchangeWamConsoleNative native, bool ownsConsole, IntPtr windowHandle)
    {
        _native = native;
        _ownsConsole = ownsConsole;
        WindowHandle = windowHandle;
    }

    internal IntPtr WindowHandle { get; }

    internal bool OwnsConsole => _ownsConsole;

    internal static ExchangeWamConsoleLease Acquire(
        int expectedInteractiveSessionId,
        IExchangeWamConsoleNative? native = null)
    {
        native ??= WindowsExchangeWamConsoleNative.Instance;
        if (expectedInteractiveSessionId <= 0 || native.GetCurrentSessionId() != expectedInteractiveSessionId ||
            native.GetProcessWindowStation() == IntPtr.Zero ||
            native.GetThreadDesktop(native.GetCurrentThreadId()) == IntPtr.Zero)
        {
            throw Unavailable();
        }

        var window = native.GetConsoleWindow();
        if (window != IntPtr.Zero)
        {
            return new ExchangeWamConsoleLease(native, ownsConsole: false, window);
        }

        if (!native.AllocConsole())
        {
            throw Unavailable();
        }

        var ownsConsole = true;
        try
        {
            window = native.GetConsoleWindow();
            if (window == IntPtr.Zero || !native.SetConsoleTitle(ConsoleTitle))
            {
                throw Unavailable();
            }

            var lease = new ExchangeWamConsoleLease(native, ownsConsole, window);
            ownsConsole = false;
            return lease;
        }
        finally
        {
            if (ownsConsole)
            {
                _ = native.FreeConsole();
            }
        }
    }

    internal async Task VerifyChildAttachedAsync(
        int childProcessId,
        CancellationToken cancellationToken,
        int maximumAttempts = DefaultAttachmentAttempts,
        TimeSpan? pollInterval = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (childProcessId <= 0 || maximumAttempts <= 0 || _native.GetConsoleWindow() == IntPtr.Zero)
        {
            throw Unavailable();
        }

        var currentProcessId = Environment.ProcessId;
        var delay = pollInterval ?? DefaultAttachmentPollInterval;
        if (delay < TimeSpan.Zero)
        {
            throw Unavailable();
        }

        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processIds = _native.GetConsoleProcessIds();
            if (processIds.Contains((uint)currentProcessId) && processIds.Contains((uint)childProcessId))
            {
                return;
            }

            if (attempt + 1 < maximumAttempts)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw Unavailable();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsConsole)
        {
            _ownsConsole = false;
            _ = _native.FreeConsole();
        }
    }

    private static ProvisioningException Unavailable()
    {
        return new ProvisioningException(
            RelayBridge.Core.Microsoft.NativeSetupFailureCategory.MicrosoftService,
            "ExchangeWamConsoleUnavailable");
    }
}

internal interface IExchangeWamConsoleNative
{
    IntPtr GetConsoleWindow();

    bool AllocConsole();

    bool FreeConsole();

    bool SetConsoleTitle(string title);

    IntPtr GetProcessWindowStation();

    IntPtr GetThreadDesktop(uint threadId);

    uint GetCurrentThreadId();

    int GetCurrentSessionId();

    IReadOnlyList<uint> GetConsoleProcessIds();
}

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsExchangeWamConsoleNative : IExchangeWamConsoleNative
{
    internal static WindowsExchangeWamConsoleNative Instance { get; } = new();

    private WindowsExchangeWamConsoleNative()
    {
    }

    public IntPtr GetConsoleWindow() => NativeMethods.GetConsoleWindow();

    public bool AllocConsole() => NativeMethods.AllocConsole();

    public bool FreeConsole() => NativeMethods.FreeConsole();

    public bool SetConsoleTitle(string title) => NativeMethods.SetConsoleTitle(title);

    public IntPtr GetProcessWindowStation() => NativeMethods.GetProcessWindowStation();

    public IntPtr GetThreadDesktop(uint threadId) => NativeMethods.GetThreadDesktop(threadId);

    public uint GetCurrentThreadId() => NativeMethods.GetCurrentThreadId();

    public int GetCurrentSessionId()
    {
        using var current = Process.GetCurrentProcess();
        return current.SessionId;
    }

    public unsafe IReadOnlyList<uint> GetConsoleProcessIds()
    {
        var buffer = new uint[8];
        while (true)
        {
            uint count;
            fixed (uint* pointer = buffer)
            {
                count = NativeMethods.GetConsoleProcessList(pointer, (uint)buffer.Length);
            }

            if (count == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            if (count <= buffer.Length)
            {
                return buffer.AsSpan(0, checked((int)count)).ToArray();
            }

            buffer = new uint[checked((int)count)];
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool AllocConsole();

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool FreeConsole();

        [LibraryImport("kernel32.dll")]
        internal static partial IntPtr GetConsoleWindow();

        [LibraryImport("kernel32.dll", EntryPoint = "SetConsoleTitleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetConsoleTitle(string title);

        [LibraryImport("user32.dll")]
        internal static partial IntPtr GetProcessWindowStation();

        [LibraryImport("user32.dll")]
        internal static partial IntPtr GetThreadDesktop(uint threadId);

        [LibraryImport("kernel32.dll")]
        internal static partial uint GetCurrentThreadId();

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static unsafe partial uint GetConsoleProcessList(uint* processList, uint processCount);
    }
}
