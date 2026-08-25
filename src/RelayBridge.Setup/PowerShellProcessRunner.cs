// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using RelayBridge.Core.Microsoft;

namespace RelayBridge.Setup;

internal sealed record PowerShellExecutionResult(int ExitCode, string StandardOutput, string StandardError);

internal enum PowerShellHostingMode
{
    Hidden,
    InteractiveWamConsole,
}

internal sealed partial class PowerShellProcessRunner : IDisposable
{
    private const int MaximumCapturedCharacters = 32 * 1024;
    private readonly SafeJobHandle _job;

    internal PowerShellProcessRunner()
    {
        _job = NativeMethods.CreateKillOnCloseJob();
    }

    [SupportedOSPlatform("windows")]
    internal async Task<PowerShellExecutionResult> RunAsync(
        string powerShellPath,
        string workingDirectory,
        string scratchDirectory,
        string script,
        CancellationToken cancellationToken,
        PowerShellHostingMode hostingMode = PowerShellHostingMode.Hidden,
        int? expectedInteractiveSessionId = null)
    {
        if (!Path.IsPathFullyQualified(powerShellPath) || !File.Exists(powerShellPath) ||
            !Path.IsPathFullyQualified(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            throw new ToolIntegrityException();
        }

        using var consoleLease = hostingMode == PowerShellHostingMode.InteractiveWamConsole
            ? ExchangeWamConsoleLease.Acquire(expectedInteractiveSessionId ?? 0)
            : null;
        var start = CreateStartInfo(powerShellPath, workingDirectory, scratchDirectory, hostingMode);
        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException("The approved Microsoft setup process could not start.");
        }

        if (!NativeMethods.AssignProcessToJobObject(_job, process.SafeHandle))
        {
            var error = Marshal.GetLastWin32Error();
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw new System.ComponentModel.Win32Exception(error);
        }

        if (consoleLease is not null)
        {
            try
            {
                await consoleLease
                    .VerifyChildAttachedAsync(process.Id, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                TerminateProcessTree(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        using var registration = cancellationToken.Register(() => TerminateProcessTree(process));

        var stdout = ReadBoundedAsync(process.StandardOutput, CancellationToken.None);
        var stderr = ReadBoundedAsync(process.StandardError, CancellationToken.None);
        try
        {
            await process.StandardInput.WriteLineAsync(script.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.DisposeAsync().ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopAndDrainAsync(process, stdout, stderr).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await StopAndDrainAsync(process, stdout, stderr).ConfigureAwait(false);
            throw;
        }

        return new PowerShellExecutionResult(
            process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false));
    }

    internal static ProcessStartInfo CreateStartInfo(
        string powerShellPath,
        string workingDirectory,
        string scratchDirectory,
        PowerShellHostingMode hostingMode = PowerShellHostingMode.Hidden)
    {
        if (!Path.IsPathFullyQualified(powerShellPath) || !Path.IsPathFullyQualified(workingDirectory) ||
            !Path.IsPathFullyQualified(scratchDirectory))
        {
            throw new ToolIntegrityException();
        }

        var start = new ProcessStartInfo
        {
            FileName = powerShellPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = hostingMode == PowerShellHostingMode.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("& ([scriptblock]::Create([Console]::In.ReadToEnd()))");
        PrivilegedProcessEnvironment.Apply(start, scratchDirectory);
        start.Environment["PSModulePath"] = string.Empty;
        return start;
    }

    private void TerminateProcessTree(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        if (!NativeMethods.TerminateJobObject(_job, 1))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private async Task StopAndDrainAsync(Process process, Task<string> stdout, Task<string> stderr)
    {
        try
        {
            await process.StandardInput.DisposeAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
        }

        TerminateProcessTree(process);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _ = await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
        }
    }

    public void Dispose()
    {
        _job.Dispose();
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        var result = new System.Text.StringBuilder();
        var exceeded = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (exceeded)
                {
                    throw new InvalidDataException("Microsoft setup produced more diagnostic output than RelayBridge accepts.");
                }

                return result.ToString();
            }

            if (exceeded || result.Length + read > MaximumCapturedCharacters)
            {
                exceeded = true;
                continue;
            }

            result.Append(buffer, 0, read);
        }
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeJobHandle(IntPtr handle)
            : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle()
        {
            return NativeMethods.CloseHandle(handle);
        }
    }

    private static partial class NativeMethods
    {
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectExtendedLimitInformationClass = 9;

        internal static SafeJobHandle CreateKillOnCloseJob()
        {
            var rawJob = CreateJobObject(IntPtr.Zero, null);
            if (rawJob == IntPtr.Zero || rawJob == new IntPtr(-1))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            var job = new SafeJobHandle(rawJob);

            var information = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };
            var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var pointer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(information, pointer, fDeleteOld: false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, pointer, (uint)length))
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }

            return job;
        }

        [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr CreateJobObject(IntPtr attributes, string? name);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetInformationJobObject(
            SafeJobHandle job,
            int informationClass,
            IntPtr information,
            uint informationLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool TerminateJobObject(SafeJobHandle job, uint exitCode);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CloseHandle(IntPtr handle);

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
}
