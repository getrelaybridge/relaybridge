// SPDX-License-Identifier: MPL-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using RelayBridge.Core.Microsoft;
using RelayBridge.Infrastructure.Storage;

namespace RelayBridge.Infrastructure.Microsoft;

public sealed partial class NativeMicrosoftSetupServer : IAsyncDisposable
{
    private readonly NativeMicrosoftSetupOptions _options;
    private readonly NativeMicrosoftSetupRuntime _runtime;
    private readonly MicrosoftSetupService _setup;
    private readonly MicrosoftCertificateService _certificates;
    private readonly ILogger<NativeMicrosoftSetupServer> _logger;
    private readonly Func<NamedPipeServerStream> _pipeFactory;
    private readonly Func<NamedPipeServerStream, CancellationToken, Task> _connectionHandler;
    private readonly Func<NamedPipeServerStream, int, int, CancellationToken, Task<LauncherIdentity>> _identityValidator;
    private readonly Func<VerifiedHelperExecutionClosure> _helperClosureVerifier;
    private readonly TimeSpan _listenerRetryDelay;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _connectionLock = new();
    private NamedPipeServerStream? _activePipe;
    private Guid? _activeSessionId;
    private CancellationTokenSource? _activeOperationCancellation;
    private NativeMicrosoftCandidateIdentity? _activeCandidate;

    public NativeMicrosoftSetupServer(
        NativeMicrosoftSetupOptions options,
        NativeMicrosoftSetupRuntime runtime,
        MicrosoftSetupService setup,
        MicrosoftCertificateService certificates,
        ILogger<NativeMicrosoftSetupServer> logger)
        : this(
            options,
            runtime,
            setup,
            certificates,
            logger,
            CreatePipeForConfiguredPlatform,
            TimeSpan.FromSeconds(1),
            connectionHandler: null,
            identityValidator: null,
            helperClosureVerifier: null)
    {
    }

    internal NativeMicrosoftSetupServer(
        NativeMicrosoftSetupOptions options,
        NativeMicrosoftSetupRuntime runtime,
        MicrosoftSetupService setup,
        MicrosoftCertificateService certificates,
        ILogger<NativeMicrosoftSetupServer> logger,
        Func<NamedPipeServerStream> pipeFactory,
        TimeSpan listenerRetryDelay,
        Func<NamedPipeServerStream, CancellationToken, Task>? connectionHandler = null,
        Func<NamedPipeServerStream, int, int, CancellationToken, Task<LauncherIdentity>>? identityValidator = null,
        Func<VerifiedHelperExecutionClosure>? helperClosureVerifier = null)
    {
        _options = options;
        _runtime = runtime;
        _setup = setup;
        _certificates = certificates;
        _logger = logger;
        _pipeFactory = pipeFactory;
        _connectionHandler = connectionHandler ?? HandleConnectionForConfiguredPlatformAsync;
        _identityValidator = identityValidator ?? ValidateLauncherIdentityForConfiguredPlatformAsync;
        _helperClosureVerifier = helperClosureVerifier ?? VerifyInstalledHelperExecutionClosure;
        _listenerRetryDelay = listenerRetryDelay;
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !OperatingSystem.IsWindows())
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            var retry = false;
            try
            {
                pipe = _pipeFactory();
                _runtime.ListenerReady();
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                await _connectionHandler(pipe, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                if (_runtime.Snapshot.FailureCategory != NativeSetupFailureCategory.Cancelled)
                {
                    _runtime.Fail(
                        NativeSetupFailureCategory.Timeout,
                        "Microsoft setup timed out. The candidate remains inactive and can be resumed.",
                        "SessionTimeout",
                        null);
                }
            }
            catch (TrustedWindowsPathException)
            {
                _runtime.Fail(
                    NativeSetupFailureCategory.ToolIntegrity,
                    "RelayBridge's Microsoft setup tools are not installed securely. Repair the RelayBridge installation.",
                    "UntrustedToolOwnership",
                    null);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or
                UnauthorizedAccessException or CryptographicException or MicrosoftSetupConcurrencyException or
                MicrosoftIdentityException or Win32Exception or SqliteException)
            {
                retry = pipe is null || exception is SqliteException;
                _runtime.Fail(
                    NativeSetupFailureCategory.InvalidHelper,
                    "RelayBridge rejected the local Microsoft setup launcher connection.",
                    exception.GetType().Name,
                    null);
                _logger.LogWarning(
                    "Native Microsoft setup launcher connection failed closed. Category: {Category}",
                    exception.GetType().Name);
                if (retry)
                {
                    _runtime.Unavailable(
                        "Native Microsoft setup is temporarily unavailable. Mail intake and delivery remain operational.",
                        exception.GetType().Name);
                }
            }
            finally
            {
                lock (_connectionLock)
                {
                    if (ReferenceEquals(_activePipe, pipe))
                    {
                        _activePipe = null;
                        _activeSessionId = null;
                        _activeOperationCancellation = null;
                        _activeCandidate = null;
                    }
                }

                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (retry)
            {
                try
                {
                    await Task.Delay(_listenerRetryDelay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    public void PrepareForLaunch()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Native Microsoft setup requires Windows.");
        }

        try
        {
            _ = _helperClosureVerifier();
        }
        catch (TrustedWindowsPathException exception)
        {
            const string message = "RelayBridge's Microsoft setup tools are not installed securely. Repair the RelayBridge installation.";
            _runtime.Fail(
                NativeSetupFailureCategory.ToolIntegrity,
                message,
                "UntrustedHelperClosure",
                null);
            throw new InvalidOperationException(message, exception);
        }

        _runtime.PrepareForLaunch();
    }

    [SupportedOSPlatform("windows")]
    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        using var bootstrapCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        bootstrapCancellation.CancelAfter(_options.BootstrapTimeout);
        var bootstrapToken = bootstrapCancellation.Token;
        var hello = await NativeSetupPipeProtocol.ReadAsync<NativeSetupEnvelope>(pipe, bootstrapToken)
            .ConfigureAwait(false);
        if (hello.Version != NativeMicrosoftSetupProtocol.Version ||
            hello.Kind != NativeSetupMessageKind.Hello ||
            hello.ProcessId is null || hello.WindowsSessionId is null)
        {
            throw new InvalidDataException("The launcher bootstrap message is invalid.");
        }

        var identity = await _identityValidator(
            pipe,
            hello.ProcessId.Value,
            hello.WindowsSessionId.Value,
            bootstrapToken).ConfigureAwait(false);
        NativeMicrosoftCandidateIdentity candidate;
        try
        {
            candidate = _setup.CaptureNativeCandidate(bootstrapToken);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("The Microsoft setup candidate is not ready for native provisioning.", exception);
        }

        var state = candidate.CapturedState;

        PublicCertificateExport export;
        try
        {
            export = await _certificates.ExportPublicCertificateAsync(state.Certificate!, bootstrapToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("The candidate certificate cannot be prepared for native setup.", exception);
        }

        var certificateBytes = await File.ReadAllBytesAsync(export.FullPath, bootstrapToken).ConfigureAwait(false);
        if (certificateBytes.Length is <= 0 or > 3072)
        {
            throw new InvalidDataException("The public certificate is outside the native setup protocol bound.");
        }

        var sessionId = Guid.NewGuid();
        using var scratch = ProvisioningScratchDirectory.Create(identity.Sid, sessionId);
        using var scratchLifetime = new LauncherScratchLifetime(pipe, hello.ProcessId.Value, scratch);
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        sessionCancellation.CancelAfter(_options.SessionTimeout);
        lock (_connectionLock)
        {
            _activePipe = pipe;
            _activeSessionId = sessionId;
            _activeOperationCancellation = sessionCancellation;
            _activeCandidate = candidate;
        }

        try
        {
            _runtime.Start(CancelCurrentAsync);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("Another native Microsoft setup session is already active.", exception);
        }

        var start = new NativeSetupStartRequest(
            NativeMicrosoftSetupProtocol.Version,
            sessionId,
            candidate.SenderMailbox,
            Convert.ToBase64String(certificateBytes),
            $"RelayBridge SMTP OAuth {state.Certificate!.Thumbprint[^12..]}",
            IsRepair: state.EntraResultValidated || state.ExchangeResultValidated,
            Path.GetFullPath(_options.InstallationRoot),
            Path.GetFullPath(_options.ToolingRoot),
            Path.GetFullPath(_options.ToolingManifestPath),
            _options.ExpectedToolingManifestSha256,
            scratch.Directory,
            hello.ProcessId.Value,
            identity.SessionId,
            identity.Sid.Value,
            Path.GetFullPath(_options.LauncherPath),
            Convert.ToHexString(_helperClosureVerifier().ExpectedLauncherHash),
            candidate.ActivationId,
            candidate.Revision,
            candidate.ConfigurationFingerprint,
            candidate.Mode);
        await WriteAsync(pipe, start, bootstrapToken).ConfigureAwait(false);
        bootstrapCancellation.CancelAfter(Timeout.InfiniteTimeSpan);

        var protocolState = new NativeSetupSessionProtocolState();
        while (true)
        {
            NativeSetupEnvelope message;
            try
            {
                message = await NativeSetupPipeProtocol.ReadAsync<NativeSetupEnvelope>(pipe, sessionCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested &&
                _runtime.Snapshot.FailureCategory == NativeSetupFailureCategory.Cancelled)
            {
                return;
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                _runtime.Fail(
                    NativeSetupFailureCategory.Timeout,
                    "Microsoft setup timed out. The candidate remains inactive and can be resumed.",
                    "SessionTimeout",
                    null);
                return;
            }

            ValidateSessionMessage(message, sessionId);
            protocolState.Accept(message.Kind);
            switch (message.Kind)
            {
                case NativeSetupMessageKind.Confirmed:
                    _runtime.Update(NativeSetupStage.VerifyingTools, "Preparing verified Microsoft setup tools.");
                    break;
                case NativeSetupMessageKind.Stage when message.Stage is not null:
                    _runtime.Update(message.Stage.Value, StageMessage(message.Stage.Value));
                    break;
                case NativeSetupMessageKind.EntraResult when message.Entra is not null:
                    candidate = ApplyEntraResult(candidate, message.Entra, sessionCancellation.Token);
                    SetActiveCandidate(candidate);
                    _runtime.Update(NativeSetupStage.WaitingForExchangeSignIn, "Microsoft application configured. Waiting for Exchange administrator sign-in.");
                    break;
                case NativeSetupMessageKind.ExchangeResult when message.Exchange is not null:
                    candidate = ApplyExchangeResult(candidate, message.Exchange, sessionCancellation.Token);
                    SetActiveCandidate(candidate);
                    _runtime.Update(NativeSetupStage.VerifyingIdentity, "Administrator setup complete. Verifying RelayBridge identity.");
                    break;
                case NativeSetupMessageKind.Completed:
                    await VerifyAndActivateAsync(candidate, sessionCancellation.Token).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Native Microsoft setup completed through an authenticated local launcher. Windows session: {SessionId}; user SID suffix: {SidSuffix}",
                        identity.SessionId,
                        identity.Sid.Value[^Math.Min(8, identity.Sid.Value.Length)..]);
                    return;
                case NativeSetupMessageKind.Cancelled:
                    _ = _setup.CancelNativeCandidate(candidate, stoppingToken);
                    _runtime.Fail(
                        NativeSetupFailureCategory.Cancelled,
                        "Microsoft setup was cancelled. No active RelayBridge configuration was changed.",
                        message.SafeCode,
                        message.SafeCorrelationId,
                        message.SafeFailureDetails);
                    return;
                case NativeSetupMessageKind.Failed:
                    _runtime.Fail(
                        message.FailureCategory,
                        FailureMessage(message.FailureCategory, message.SafeCode, protocolState.EntraApplied),
                        message.SafeCode,
                        message.SafeCorrelationId,
                        message.SafeFailureDetails);
                    return;
                default:
                    throw new InvalidDataException("The helper setup message arrived out of order.");
            }
        }
    }

    private NativeMicrosoftCandidateIdentity ApplyEntraResult(
        NativeMicrosoftCandidateIdentity candidate,
        EntraSetupResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            return _setup.ApplyNativeEntraResult(candidate, result, cancellationToken);
        }
        catch (MicrosoftSetupConcurrencyException)
        {
            throw;
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("The Microsoft application result is invalid.", exception);
        }
    }

    private NativeMicrosoftCandidateIdentity ApplyExchangeResult(
        NativeMicrosoftCandidateIdentity candidate,
        ExchangeSetupResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            return _setup.ApplyNativeExchangeResult(candidate, result, cancellationToken);
        }
        catch (MicrosoftSetupConcurrencyException)
        {
            throw;
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("The Exchange setup result is invalid.", exception);
        }
    }

    private Task HandleConnectionForConfiguredPlatformAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        return HandleConnectionAsync(pipe, cancellationToken);
    }

    private async Task VerifyAndActivateAsync(
        NativeMicrosoftCandidateIdentity candidate,
        CancellationToken cancellationToken)
    {
        _runtime.Update(NativeSetupStage.VerifyingIdentity, "Verifying the immutable candidate Microsoft identity.");
        var result = await _setup.VerifyAndActivateNativeAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            _runtime.Complete("Microsoft administrator setup and RelayBridge SMTP verification succeeded. Microsoft 365 accepted the verification message.");
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _runtime.Fail(
            NativeSetupFailureCategory.MicrosoftService,
            result.Message,
            result.TechnicalCode,
            result.CorrelationId);
    }

    [SupportedOSPlatform("windows")]
    private async Task<LauncherIdentity> ValidateLauncherIdentityAsync(
        NamedPipeServerStream pipe,
        int claimedProcessId,
        int claimedSessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var actualProcessId))
        {
            throw new InvalidDataException("The launcher process identity could not be verified.");
        }

        using var process = Process.GetProcessById(checked((int)actualProcessId));
        var actualPath = Path.GetFullPath(process.MainModule?.FileName
            ?? throw new InvalidDataException("The launcher executable path is unavailable."));
        var expectedPath = Path.GetFullPath(_options.LauncherPath);
        var verifiedClosure = _helperClosureVerifier();
        await using var executable = new FileStream(
            actualPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualHash = await SHA256.HashDataAsync(executable, cancellationToken).ConfigureAwait(false);
        var expectedHash = verifiedClosure.ExpectedLauncherHash;

        SecurityIdentifier? pipeSid = null;
        pipe.RunAsClient(() => pipeSid = WindowsIdentity.GetCurrent(TokenAccessLevels.Query).User);
        if (!OpenProcessToken(process.Handle, TokenQuery, out var processToken))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The launcher process token could not be opened.");
        }

        SecurityIdentifier? processSid;
        using (processToken)
        using (var processIdentity = new WindowsIdentity(processToken.DangerousGetHandle()))
        {
            processSid = processIdentity.User;
        }

        cancellationToken.ThrowIfCancellationRequested();

        return NativeSetupLauncherIdentityPolicy.Validate(new NativeSetupLauncherIdentityFacts(
            claimedProcessId,
            checked((int)actualProcessId),
            claimedSessionId,
            process.SessionId,
            actualPath,
            expectedPath,
            actualHash,
            expectedHash,
            pipeSid,
            processSid));
    }

    private VerifiedHelperExecutionClosure VerifyInstalledHelperExecutionClosure()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Native Microsoft setup requires Windows.");
        }

        return HelperExecutionClosureVerifier.Verify(
            _options.LauncherPath,
            _options.WorkerPath,
            _options.HelperManifestPath,
            _options.ExpectedHelperManifestSha256,
            _options.ExpectedLauncherSha256);
    }

    private Task<LauncherIdentity> ValidateLauncherIdentityForConfiguredPlatformAsync(
        NamedPipeServerStream pipe,
        int claimedProcessId,
        int claimedSessionId,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        return ValidateLauncherIdentityAsync(pipe, claimedProcessId, claimedSessionId, cancellationToken);
    }

    private async Task CancelCurrentAsync(CancellationToken cancellationToken)
    {
        NamedPipeServerStream? pipe;
        Guid? sessionId;
        CancellationTokenSource? operationCancellation;
        NativeMicrosoftCandidateIdentity? candidate;
        lock (_connectionLock)
        {
            pipe = _activePipe;
            sessionId = _activeSessionId;
            operationCancellation = _activeOperationCancellation;
            candidate = _activeCandidate;
        }

        if (candidate is not null)
        {
            var cancellation = _setup.CancelNativeCandidate(candidate, cancellationToken);
            if (cancellation.Outcome == MicrosoftSetupCancellationOutcome.AlreadyActivated)
            {
                _runtime.Complete("Microsoft 365 activation already completed before cancellation was committed.");
                return;
            }
        }

        operationCancellation?.Cancel();
        _runtime.Fail(
            NativeSetupFailureCategory.Cancelled,
            "Microsoft setup was cancelled. No active RelayBridge configuration was changed.",
            "Cancelled",
            null);

        if (pipe is null || sessionId is null || !pipe.IsConnected)
        {
            return;
        }

        try
        {
            await WriteAsync(
                pipe,
                new NativeSetupEnvelope(
                    NativeMicrosoftSetupProtocol.Version,
                    NativeSetupMessageKind.Cancelled,
                    sessionId,
                    FailureCategory: NativeSetupFailureCategory.Cancelled),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug("Native Microsoft setup cancellation closed an inactive helper connection.");
        }
    }

    private void SetActiveCandidate(NativeMicrosoftCandidateIdentity candidate)
    {
        lock (_connectionLock)
        {
            if (_activeSessionId is not null)
            {
                _activeCandidate = candidate;
            }
        }
    }

    private async Task WriteAsync<T>(NamedPipeServerStream pipe, T value, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await NativeSetupPipeProtocol.WriteAsync(pipe, value, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    [SupportedOSPlatform("windows")]
    internal static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        return security;
    }

    private static NamedPipeServerStream CreatePipeForConfiguredPlatform()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        return CreatePipe();
    }

    [SupportedOSPlatform("windows")]
    internal static NamedPipeServerStream CreatePipe()
    {
        var securityDescriptor = CreatePipeSecurity().GetSecurityDescriptorBinaryForm();
        var pinnedDescriptor = GCHandle.Alloc(securityDescriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = pinnedDescriptor.AddrOfPinnedObject(),
                InheritHandle = 0,
            };
            var pipeHandle = CreateNamedPipe(
                $@"\\.\pipe\{NativeMicrosoftSetupProtocol.BootstrapPipeName}",
                PipeAccessDuplex | FileFlagOverlapped | FileFlagWriteThrough | FileFlagFirstPipeInstance,
                PipeTypeByte | PipeReadModeByte | PipeWait | PipeRejectRemoteClients,
                1,
                4096,
                4096,
                0,
                ref attributes);
            if (pipeHandle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                pipeHandle.Dispose();
                throw new Win32Exception(error, "The local Microsoft setup pipe could not be created.");
            }

            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, pipeHandle);
        }
        finally
        {
            pinnedDescriptor.Free();
        }
    }

    private static void ValidateSessionMessage(NativeSetupEnvelope message, Guid sessionId)
    {
        if (message.Version != NativeMicrosoftSetupProtocol.Version || message.SessionId != sessionId)
        {
            throw new InvalidDataException("The helper session identity is invalid.");
        }
    }

    private static string StageMessage(NativeSetupStage stage) => stage switch
    {
        NativeSetupStage.VerifyingTools => "Verifying private Microsoft setup tools.",
        NativeSetupStage.WaitingForEntraSignIn => "Waiting for Microsoft Entra administrator sign-in.",
        NativeSetupStage.ConfiguringApplication => "Creating or verifying the dedicated Microsoft application.",
        NativeSetupStage.RegisteringCertificate => "Registering the public certificate.",
        NativeSetupStage.WaitingForExchangeSignIn => "Waiting for Exchange Online administrator sign-in.",
        NativeSetupStage.ConfiguringExchange => "Configuring Exchange Application RBAC.",
        NativeSetupStage.RestrictingSender => "Restricting Exchange permission to the selected sender.",
        NativeSetupStage.VerifyingAuthorization => "Verifying sender authorization.",
        _ => "Microsoft setup is running.",
    };

    internal static string FailureMessage(
        NativeSetupFailureCategory category,
        string? safeCode,
        bool entraApplied) => category switch
        {
            NativeSetupFailureCategory.Cancelled => "Microsoft setup was cancelled. No active RelayBridge configuration was changed.",
            NativeSetupFailureCategory.ToolIntegrity => "RelayBridge's Microsoft setup tools are not installed securely. Repair the RelayBridge installation.",
            NativeSetupFailureCategory.InsufficientPermission => "The signed-in administrator does not have permission to configure the required Microsoft 365 settings.",
            NativeSetupFailureCategory.ConditionalAccess => "Microsoft Conditional Access blocked this administrator sign-in. RelayBridge did not retry with a weaker sign-in mode.",
            NativeSetupFailureCategory.MicrosoftService when string.Equals(
                safeCode,
                "ExchangeWamConsoleUnavailable",
                StringComparison.Ordinal) => "Exchange administrator sign-in needs an interactive Windows desktop. Return to RelayBridge from the signed-in Windows user and try again.",
            NativeSetupFailureCategory.Conflict when string.Equals(
                safeCode,
                "UnexpectedExchangeAssignments",
                StringComparison.Ordinal) => "The RelayBridge Microsoft application has additional Exchange permissions that RelayBridge did not create. Review or remove them before continuing.",
            NativeSetupFailureCategory.Conflict => "RelayBridge found a conflicting Microsoft object and stopped without overwriting it.",
            _ when entraApplied => "RelayBridge created or verified the Microsoft application, but Exchange setup did not finish. You can safely resume setup.",
            _ => "Microsoft administrator setup did not finish. The candidate remains inactive.",
        };

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(
        IntPtr pipe,
        out int clientProcessId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

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

    private const uint TokenQuery = 0x0008;
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeTypeByte = 0x00000000;
    private const uint PipeReadModeByte = 0x00000000;
    private const uint PipeWait = 0x00000000;
    internal const uint PipeRejectRemoteClients = 0x00000008;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr SecurityDescriptor;

        internal int InheritHandle;
    }

    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed record NativeSetupLauncherIdentityFacts(
    int ClaimedProcessId,
    int ActualProcessId,
    int ClaimedSessionId,
    int ActualSessionId,
    string ActualPath,
    string ExpectedPath,
    byte[] ActualHash,
    byte[] ExpectedHash,
    SecurityIdentifier? PipeSid,
    SecurityIdentifier? ProcessSid);

internal sealed record LauncherIdentity(SecurityIdentifier Sid, int SessionId);

[SupportedOSPlatform("windows")]
internal sealed class LauncherScratchLifetime : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly int _launcherProcessId;
    private readonly ProvisioningScratchLease _scratch;
    private int _disposed;

    internal LauncherScratchLifetime(
        NamedPipeServerStream pipe,
        int launcherProcessId,
        ProvisioningScratchLease scratch)
    {
        _pipe = pipe;
        _launcherProcessId = launcherProcessId;
        _scratch = scratch;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_pipe.IsConnected)
            {
                _pipe.Disconnect();
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }

        try
        {
            using var launcher = Process.GetProcessById(_launcherProcessId);
            if (!launcher.WaitForExit(5_000))
            {
                _scratch.Abandon();
            }
        }
        catch (ArgumentException)
        {
            // The authenticated launcher has already exited.
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            _scratch.Abandon();
        }
    }
}

internal static class NativeSetupLauncherIdentityPolicy
{
    [SupportedOSPlatform("windows")]
    internal static LauncherIdentity Validate(NativeSetupLauncherIdentityFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.ActualProcessId <= 0 || facts.ActualProcessId != facts.ClaimedProcessId)
        {
            throw new InvalidDataException("The launcher process identity could not be verified.");
        }

        if (facts.ActualSessionId <= 0 || facts.ActualSessionId != facts.ClaimedSessionId)
        {
            throw new InvalidDataException("The launcher is not in the claimed interactive Windows session.");
        }

        if (!string.Equals(
                Path.GetFullPath(facts.ActualPath),
                Path.GetFullPath(facts.ExpectedPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The launcher executable path is not approved.");
        }

        if (facts.ActualHash.Length != SHA256.HashSizeInBytes ||
            facts.ExpectedHash.Length != SHA256.HashSizeInBytes ||
            !CryptographicOperations.FixedTimeEquals(facts.ActualHash, facts.ExpectedHash))
        {
            throw new CryptographicException("The launcher executable hash is not approved.");
        }

        if (facts.PipeSid is null || facts.ProcessSid is null ||
            !facts.PipeSid.Equals(facts.ProcessSid) ||
            facts.PipeSid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
            facts.PipeSid.IsWellKnown(WellKnownSidType.LocalServiceSid) ||
            facts.PipeSid.IsWellKnown(WellKnownSidType.NetworkServiceSid))
        {
            throw new InvalidDataException("The launcher Windows identity is not an interactive user.");
        }

        return new LauncherIdentity(facts.PipeSid, facts.ActualSessionId);
    }
}

internal sealed class NativeSetupSessionProtocolState
{
    private NativeSetupSessionState _state = NativeSetupSessionState.AwaitingConfirmation;

    internal bool EntraApplied => _state is NativeSetupSessionState.AwaitingExchange or
        NativeSetupSessionState.AwaitingCompletion or NativeSetupSessionState.TerminalAfterEntra;

    internal void Accept(NativeSetupMessageKind kind)
    {
        switch (kind)
        {
            case NativeSetupMessageKind.Confirmed when _state == NativeSetupSessionState.AwaitingConfirmation:
                _state = NativeSetupSessionState.AwaitingEntra;
                return;
            case NativeSetupMessageKind.Stage when _state is NativeSetupSessionState.AwaitingEntra or
                NativeSetupSessionState.AwaitingExchange or NativeSetupSessionState.AwaitingCompletion:
                return;
            case NativeSetupMessageKind.EntraResult when _state == NativeSetupSessionState.AwaitingEntra:
                _state = NativeSetupSessionState.AwaitingExchange;
                return;
            case NativeSetupMessageKind.ExchangeResult when _state == NativeSetupSessionState.AwaitingExchange:
                _state = NativeSetupSessionState.AwaitingCompletion;
                return;
            case NativeSetupMessageKind.Completed when _state == NativeSetupSessionState.AwaitingCompletion:
                _state = NativeSetupSessionState.TerminalAfterEntra;
                return;
            case NativeSetupMessageKind.Cancelled or NativeSetupMessageKind.Failed
                when _state != NativeSetupSessionState.TerminalAfterEntra:
                _state = EntraApplied
                    ? NativeSetupSessionState.TerminalAfterEntra
                    : NativeSetupSessionState.Terminal;
                return;
            default:
                throw new InvalidDataException("The helper setup message arrived out of order or was replayed.");
        }
    }

    private enum NativeSetupSessionState
    {
        AwaitingConfirmation,
        AwaitingEntra,
        AwaitingExchange,
        AwaitingCompletion,
        Terminal,
        TerminalAfterEntra,
    }
}
