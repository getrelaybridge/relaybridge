// SPDX-License-Identifier: MPL-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using RelayBridge.Core.Microsoft;
using RelayBridge.Core.PrinterConnectivity;

namespace RelayBridge.PrinterConfigurator;

internal enum PrinterApplyOutcome
{
    ConfigurationWriteFailed,
    ConfigurationSavedVerificationFailed,
    ConfigurationSavedRestartFailed,
    ServiceStartedReadinessFailed,
    Applied,
}

internal enum PrinterApplyStage
{
    ConfigurationWrite,
    ServiceStop,
    PreviousProcessExit,
    ServiceStart,
    ServiceRunning,
    Readiness,
    Complete,
}

internal sealed class PrinterApplyException : Exception
{
    internal PrinterApplyException(
        PrinterApplyOutcome outcome,
        PrinterApplyStage stage,
        Exception innerException,
        int? windowsErrorCode = null,
        uint? serviceState = null)
        : base("RelayBridge printer connectivity apply did not complete.", innerException)
    {
        Outcome = outcome;
        Stage = stage;
        WindowsErrorCode = windowsErrorCode;
        ServiceState = serviceState;
        TimestampUtc = DateTimeOffset.UtcNow;
    }

    internal PrinterApplyOutcome Outcome { get; }

    internal PrinterApplyStage Stage { get; }

    internal int? WindowsErrorCode { get; }

    internal uint? ServiceState { get; }

    internal DateTimeOffset TimestampUtc { get; }
}

internal static class PrinterConfiguratorArguments
{
    internal static bool TryParse(string[] arguments, out Guid revision)
    {
        revision = Guid.Empty;
        if (arguments.Length != 1 ||
            !arguments[0].StartsWith(PrinterConnectivityApplyProtocol.UriPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = arguments[0][PrinterConnectivityApplyProtocol.UriPrefix.Length..].TrimEnd('/');
        return Guid.TryParseExact(value, "D", out revision) && revision != Guid.Empty;
    }
}

[SupportedOSPlatform("windows")]
internal static class PrinterConfigurator
{
    internal const string ServiceName = "RelayBridge";

    internal static async Task<int> RunAsync(Guid revision, CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            PrinterConnectivityApplyProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        PrinterConfiguratorServiceIdentity.Verify(pipe);

        using var current = Process.GetCurrentProcess();
        await PrinterConnectivityApplyPipeProtocol.WriteAsync(
            pipe,
            new PrinterConnectivityApplyEnvelope(
                PrinterConnectivityApplyProtocol.Version,
                PrinterConnectivityApplyMessageKind.Hello,
                revision,
                Environment.ProcessId,
                current.SessionId),
            timeout.Token).ConfigureAwait(false);
        var response = await PrinterConnectivityApplyPipeProtocol.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
        if (response.Version != PrinterConnectivityApplyProtocol.Version ||
            response.Revision != revision || response.Kind != PrinterConnectivityApplyMessageKind.Apply ||
            response.ListenAddress is null || response.SmtpPort is null || response.ManagementPort is null)
        {
            throw new InvalidDataException("RelayBridge rejected or returned an invalid printer-connectivity request.");
        }

        _ = PrinterConnectivityConfiguration.Validate(response.ListenAddress, response.SmtpPort.Value);
        if (response.ManagementPort is < 1 or > 65535)
        {
            throw new InvalidDataException("The local management endpoint is invalid.");
        }

        if (!PrinterConfiguratorDialog.Confirm(response.ListenAddress, response.SmtpPort.Value))
        {
            return 1;
        }

        var content = PrinterConnectivityConfiguration.CreateUtf8(
            response.ListenAddress,
            response.SmtpPort.Value);
        string target;
        try
        {
            target = WriteProtectedConfiguration(content);
        }
        catch (ProtectedConfigurationPersistedException exception)
        {
            throw new PrinterApplyException(
                PrinterApplyOutcome.ConfigurationSavedVerificationFailed,
                PrinterApplyStage.ConfigurationWrite,
                exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or CryptographicException or TrustedWindowsPathException)
        {
            throw new PrinterApplyException(
                PrinterApplyOutcome.ConfigurationWriteFailed,
                PrinterApplyStage.ConfigurationWrite,
                exception);
        }

        try
        {
            WindowsServiceRestarter.RestartRelayBridge();
        }
        catch (ServiceRestartException exception)
        {
            throw new PrinterApplyException(
                PrinterApplyOutcome.ConfigurationSavedRestartFailed,
                exception.Stage,
                exception,
                exception.WindowsErrorCode,
                exception.ServiceState);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new PrinterApplyException(
                PrinterApplyOutcome.ConfigurationSavedRestartFailed,
                PrinterApplyStage.ServiceStart,
                exception,
                exception is Win32Exception windows ? windows.NativeErrorCode : null);
        }

        try
        {
            await ConfirmReadinessAsync(
                response.ListenAddress,
                response.SmtpPort.Value,
                response.ManagementPort.Value,
                cancellationToken).ConfigureAwait(false);
            if (!File.ReadAllBytes(target).AsSpan().SequenceEqual(content))
            {
                throw new InvalidDataException("The applied printer configuration could not be verified.");
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
            InvalidOperationException or OperationCanceledException)
        {
            throw new PrinterApplyException(
                PrinterApplyOutcome.ServiceStartedReadinessFailed,
                PrinterApplyStage.Readiness,
                exception);
        }

        PrinterConfiguratorDialog.ShowSuccess(response.ListenAddress, response.SmtpPort.Value);
        return 0;
    }

    internal static string ResolveFixedTargetPath(string processPath, string programFiles)
    {
        var root = Path.GetFullPath(Path.Combine(programFiles, "RelayBridge"));
        var expectedHelper = Path.GetFullPath(Path.Combine(root, "Setup", "RelayBridge.PrinterConfigurator.exe"));
        if (!string.Equals(Path.GetFullPath(processPath), expectedHelper, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The printer configurator is not running from the approved installation path.");
        }

        return Path.GetFullPath(Path.Combine(root, "Host", "appsettings.Production.json"));
    }

    [SupportedOSPlatform("windows")]
    private static string WriteProtectedConfiguration(byte[] content)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidDataException("The printer configurator path is unavailable.");
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var target = ResolveFixedTargetPath(processPath, programFiles);
        var root = Path.GetFullPath(Path.Combine(programFiles, "RelayBridge"));
        var hostDirectory = Path.GetDirectoryName(target)!;
        TrustedWindowsPathVerifier.VerifyInstallationTree(
            root,
            [hostDirectory, processPath],
            recursivelyVerifyDirectories: false);
        if (File.Exists(target))
        {
            TrustedWindowsPathVerifier.VerifyInstallationTree(root, [target], recursivelyVerifyDirectories: false);
        }

        var temporary = Path.Combine(hostDirectory, $".relaybridge-printer-{Guid.NewGuid():N}.tmp");
        var committed = false;
        try
        {
            using (var output = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                output.Write(content);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporary, target, overwrite: true);
            committed = true;
            TrustedWindowsPathVerifier.VerifyInstallationTree(root, [target], recursivelyVerifyDirectories: false);
            if (!File.ReadAllBytes(target).AsSpan().SequenceEqual(content))
            {
                throw new InvalidDataException("The protected printer configuration did not round-trip exactly.");
            }

            return target;
        }
        catch (Exception exception) when (committed && exception is IOException or UnauthorizedAccessException or
            InvalidDataException or CryptographicException or TrustedWindowsPathException)
        {
            throw new ProtectedConfigurationPersistedException(exception);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task ConfirmReadinessAsync(
        string listenAddress,
        int smtpPort,
        int managementPort,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(45);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await client.GetAsync(
                    $"http://localhost:{managementPort}/health",
                    cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    using var smtp = new TcpClient();
                    await smtp.ConnectAsync(listenAddress, smtpPort, cancellationToken)
                        .AsTask().WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or SocketException or
                IOException or TimeoutException or TaskCanceledException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            "RelayBridge saved the configuration, but service/listener readiness was not confirmed in time.");
    }
}

internal sealed class ProtectedConfigurationPersistedException(Exception innerException)
    : IOException("The protected printer configuration was written but could not be verified.", innerException);
