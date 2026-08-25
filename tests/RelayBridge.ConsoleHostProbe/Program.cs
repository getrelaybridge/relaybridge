// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using RelayBridge.Setup;

namespace RelayBridge.ConsoleHostProbe;

[SupportedOSPlatform("windows")]
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 4)
        {
            return 2;
        }

        var powerShellPath = Path.GetFullPath(args[0]);
        var scratchDirectory = Path.GetFullPath(args[1]);
        var resultPath = Path.GetFullPath(args[2]);
        var mode = args[3];
        var initialWindow = ConsoleProbe.GetConsoleWindowValue();
        ProbeResult result;
        try
        {
            result = mode switch
            {
                "hidden" => await RunAsync(powerShellPath, scratchDirectory, PowerShellHostingMode.Hidden),
                "interactive" => await RunAsync(powerShellPath, scratchDirectory, PowerShellHostingMode.InteractiveWamConsole),
                "cancel" => await RunCancellationAsync(powerShellPath, scratchDirectory),
                _ => throw new InvalidDataException("Unknown probe mode."),
            };
        }
        catch (Exception exception)
        {
            result = new ProbeResult(
                initialWindow,
                ConsoleProbe.GetConsoleWindowValue(),
                null,
                null,
                null,
                exception.GetType().Name,
                null);
        }

        result = result with { InitialWindow = initialWindow };

        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result));
        return result.ErrorType is null ? 0 : 1;
    }

    private static async Task<ProbeResult> RunAsync(
        string powerShellPath,
        string scratchDirectory,
        PowerShellHostingMode mode)
    {
        var assemblyPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(typeof(ConsoleProbe).Assembly.Location));
        var script = $$"""
            $assemblyPath = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{assemblyPath}}'))
            $assembly = [Reflection.Assembly]::LoadFrom($assemblyPath)
            $probe = $assembly.GetType('RelayBridge.ConsoleHostProbe.ConsoleProbe')
            $handle = $probe.GetMethod('GetConsoleWindowValue').Invoke($null, @())
            [Console]::Out.WriteLine(('STDOUT_MARKER;HWND=' + $handle))
            [Console]::Error.WriteLine('STDERR_MARKER')
            """;
        using var runner = new PowerShellProcessRunner();
        var execution = await runner.RunAsync(
            powerShellPath,
            Path.GetDirectoryName(powerShellPath)!,
            scratchDirectory,
            script,
            CancellationToken.None,
            mode,
            mode == PowerShellHostingMode.InteractiveWamConsole
                ? Process.GetCurrentProcess().SessionId
                : null);
        var handleText = execution.StandardOutput.Split("HWND=", StringSplitOptions.None).Last().Trim();
        return new ProbeResult(
            0,
            ConsoleProbe.GetConsoleWindowValue(),
            long.Parse(handleText, System.Globalization.CultureInfo.InvariantCulture),
            execution.StandardOutput,
            execution.StandardError,
            null,
            null);
    }

    private static async Task<ProbeResult> RunCancellationAsync(string powerShellPath, string scratchDirectory)
    {
        var childPidPath = Path.Combine(scratchDirectory, $"child-{Guid.NewGuid():N}.pid");
        var encodedPidPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(childPidPath));
        var script = $$"""
            $pidPath = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{encodedPidPath}}'))
            [IO.File]::WriteAllText($pidPath, [string]$PID)
            while ($true) { Start-Sleep -Seconds 60 }
            """;
        using var cancellation = new CancellationTokenSource();
        using var runner = new PowerShellProcessRunner();
        var run = runner.RunAsync(
            powerShellPath,
            Path.GetDirectoryName(powerShellPath)!,
            scratchDirectory,
            script,
            cancellation.Token,
            PowerShellHostingMode.InteractiveWamConsole,
            Process.GetCurrentProcess().SessionId);

        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!File.Exists(childPidPath))
        {
            var delay = Task.Delay(20, wait.Token);
            if (await Task.WhenAny(run, delay) == run)
            {
                await run;
                throw new InvalidOperationException("PowerShell exited before the cancellation probe became ready.");
            }
        }

        var childPid = int.Parse(await File.ReadAllTextAsync(childPidPath, wait.Token), System.Globalization.CultureInfo.InvariantCulture);
        cancellation.Cancel();
        try
        {
            await run;
            throw new InvalidOperationException("Cancellation did not stop PowerShell.");
        }
        catch (OperationCanceledException)
        {
        }

        var childExited = true;
        try
        {
            using var child = Process.GetProcessById(childPid);
            childExited = child.HasExited;
        }
        catch (ArgumentException)
        {
        }

        return new ProbeResult(
            0,
            ConsoleProbe.GetConsoleWindowValue(),
            null,
            null,
            null,
            null,
            childExited);
    }
}

internal sealed record ProbeResult(
    long InitialWindow,
    long FinalWindow,
    long? ChildWindow,
    string? StandardOutput,
    string? StandardError,
    string? ErrorType,
    bool? ChildExited);

public static partial class ConsoleProbe
{
    public static long GetConsoleWindowValue() => GetConsoleWindow().ToInt64();

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetConsoleWindow();
}
