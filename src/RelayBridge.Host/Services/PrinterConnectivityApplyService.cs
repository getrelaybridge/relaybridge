// SPDX-License-Identifier: MPL-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;
using RelayBridge.Core.PrinterConnectivity;
using RelayBridge.Infrastructure.Smtp;

namespace RelayBridge.Host.Services;

public sealed class PrinterConnectivityApplyOptions
{
    public bool Enabled { get; set; }

    public string HelperPath { get; set; } = string.Empty;

    public string ExpectedHelperSha256 { get; set; } = string.Empty;

    public TimeSpan CandidateLifetime { get; set; } = TimeSpan.FromMinutes(2);

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (!Path.IsPathFullyQualified(HelperPath) ||
            ExpectedHelperSha256.Length != 64 || ExpectedHelperSha256.Any(character => !Uri.IsHexDigit(character)) ||
            CandidateLifetime < TimeSpan.FromSeconds(30) || CandidateLifetime > TimeSpan.FromMinutes(10))
        {
            throw new InvalidOperationException("Printer connectivity apply configuration is invalid.");
        }
    }
}

public sealed record PrinterConnectivityApplyPreparation(
    bool Succeeded,
    string Message,
    string? LaunchUri = null);

internal sealed record PendingPrinterConnectivityApply(
    Guid Revision,
    string ListenAddress,
    int SmtpPort,
    DateTimeOffset ExpiresUtc);

public sealed class PrinterConnectivityApplyCoordinator
{
    private readonly PrinterConnectivityApplyOptions _options;
    private readonly SmtpListenerOptions _listener;
    private readonly DeviceEndpointAdvisor _endpoints;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private PendingPrinterConnectivityApply? _pending;

    public PrinterConnectivityApplyCoordinator(
        PrinterConnectivityApplyOptions options,
        SmtpListenerOptions listener,
        DeviceEndpointAdvisor endpoints,
        TimeProvider timeProvider)
    {
        _options = options;
        _listener = listener;
        _endpoints = endpoints;
        _timeProvider = timeProvider;
    }

    public bool Available => _options.Enabled && OperatingSystem.IsWindows();

    public PrinterConnectivityApplyPreparation Prepare(string selectedAddress)
    {
        if (!Available)
        {
            return new(false, "Automatic apply is available only from an installed RelayBridge service. Use the advanced manual steps below.");
        }

        try
        {
            var validated = PrinterConnectivityConfiguration.Validate(selectedAddress, _listener.Port);
            var advice = _endpoints.GetAdvice();
            if (!advice.AvailableCandidates.Any(candidate => candidate.Address.Equals(validated)))
            {
                return new(false, "Configuration changed — regenerate before applying.");
            }

            var candidate = new PendingPrinterConnectivityApply(
                Guid.NewGuid(),
                validated.ToString(),
                _listener.Port,
                _timeProvider.GetUtcNow() + _options.CandidateLifetime);
            lock (_sync)
            {
                _pending = candidate;
            }

            return new(
                true,
                "Approve the Windows administrator confirmation to apply this exact listener configuration.",
                PrinterConnectivityApplyProtocol.UriPrefix + candidate.Revision.ToString("D"));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return new(false, "Configuration changed — regenerate before applying.");
        }
    }

    internal bool TryTake(Guid revision, out PendingPrinterConnectivityApply candidate)
    {
        lock (_sync)
        {
            candidate = _pending!;
            if (_pending is null || _pending.Revision != revision ||
                _pending.ExpiresUtc <= _timeProvider.GetUtcNow())
            {
                candidate = null!;
                return false;
            }

            var advice = _endpoints.GetAdvice();
            if (!advice.AvailableCandidates.Any(item =>
                    string.Equals(item.Address.ToString(), _pending.ListenAddress, StringComparison.Ordinal)) ||
                _listener.Port != _pending.SmtpPort)
            {
                _pending = null;
                candidate = null!;
                return false;
            }

            candidate = _pending;
            _pending = null;
            return true;
        }
    }
}

public sealed partial class PrinterConnectivityApplyHostedService : BackgroundService
{
    private readonly PrinterConnectivityApplyOptions _options;
    private readonly PrinterConnectivityApplyCoordinator _coordinator;
    private readonly ManagementOptions _management;
    private readonly ILogger<PrinterConnectivityApplyHostedService> _logger;

    public PrinterConnectivityApplyHostedService(
        PrinterConnectivityApplyOptions options,
        PrinterConnectivityApplyCoordinator coordinator,
        ManagementOptions management,
        ILogger<PrinterConnectivityApplyHostedService> logger)
    {
        _options = options;
        _coordinator = coordinator;
        _management = management;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !OperatingSystem.IsWindows())
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = CreatePipe();
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                await HandleConnectionAsync(pipe, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or
                UnauthorizedAccessException or CryptographicException or Win32Exception)
            {
                _logger.LogWarning(
                    "Printer connectivity apply request failed closed. Category: {Category}",
                    exception.GetType().Name);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var hello = await PrinterConnectivityApplyPipeProtocol.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
        if (hello.Version != PrinterConnectivityApplyProtocol.Version ||
            hello.Kind != PrinterConnectivityApplyMessageKind.Hello || hello.Revision == Guid.Empty ||
            hello.ProcessId is null || hello.WindowsSessionId is null)
        {
            throw new InvalidDataException("The printer configurator hello message is invalid.");
        }

        ValidateHelperIdentity(pipe, hello.ProcessId.Value, hello.WindowsSessionId.Value);
        if (!_coordinator.TryTake(hello.Revision, out var candidate))
        {
            await PrinterConnectivityApplyPipeProtocol.WriteAsync(
                pipe,
                new PrinterConnectivityApplyEnvelope(
                    PrinterConnectivityApplyProtocol.Version,
                    PrinterConnectivityApplyMessageKind.Rejected,
                    hello.Revision,
                    SafeCode: "StaleRevision"),
                timeout.Token).ConfigureAwait(false);
            return;
        }

        await PrinterConnectivityApplyPipeProtocol.WriteAsync(
            pipe,
            new PrinterConnectivityApplyEnvelope(
                PrinterConnectivityApplyProtocol.Version,
                PrinterConnectivityApplyMessageKind.Apply,
                candidate.Revision,
                ListenAddress: candidate.ListenAddress,
                SmtpPort: candidate.SmtpPort,
                ManagementPort: _management.Port),
            timeout.Token).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private void ValidateHelperIdentity(NamedPipeServerStream pipe, int claimedProcessId, int claimedSessionId)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var actualProcessId) ||
            actualProcessId <= 0 || actualProcessId != claimedProcessId)
        {
            throw new InvalidDataException("The printer configurator process identity is invalid.");
        }

        using var process = Process.GetProcessById(actualProcessId);
        var actualPath = Path.GetFullPath(process.MainModule?.FileName
            ?? throw new InvalidDataException("The printer configurator path is unavailable."));
        var expectedPath = Path.GetFullPath(_options.HelperPath);
        if (process.SessionId != claimedSessionId || claimedSessionId <= 0 ||
            !string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The printer configurator executable is not approved.");
        }

        using var input = new FileStream(actualPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(input),
                Convert.FromHexString(_options.ExpectedHelperSha256)))
        {
            throw new CryptographicException("The printer configurator executable hash is not approved.");
        }
    }

    [SupportedOSPlatform("windows")]
    internal static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = pinned.AddrOfPinnedObject(),
            };
            var handle = CreateNamedPipe(
                $@"\\.\pipe\{PrinterConnectivityApplyProtocol.PipeName}",
                PipeAccessDuplex | FileFlagOverlapped | FileFlagWriteThrough | FileFlagFirstPipeInstance,
                PipeTypeByte | PipeReadModeByte | PipeWait | PipeRejectRemoteClients,
                1,
                2048,
                2048,
                0,
                ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "The printer connectivity apply pipe could not be created.");
            }

            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        finally
        {
            pinned.Free();
        }
    }

    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeTypeByte = 0x00000000;
    private const uint PipeReadModeByte = 0x00000000;
    private const uint PipeWait = 0x00000000;
    private const uint PipeRejectRemoteClients = 0x00000008;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr SecurityDescriptor;
        internal int InheritHandle;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(IntPtr pipe, out int clientProcessId);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafePipeHandle CreateNamedPipe(
        string name,
        uint openMode,
        uint pipeMode,
        uint maximumInstances,
        uint outputBufferSize,
        uint inputBufferSize,
        uint defaultTimeout,
        ref SecurityAttributes securityAttributes);
}
