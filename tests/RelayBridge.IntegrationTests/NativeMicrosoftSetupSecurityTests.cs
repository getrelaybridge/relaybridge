// SPDX-License-Identifier: MPL-2.0

extern alias setup;
extern alias launcher;

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RelayBridge.Core.Microsoft;
using RelayBridge.Infrastructure.Microsoft;
using RelayBridge.StartupHookProbe;
using ProvisioningException = setup::RelayBridge.Setup.ProvisioningException;
using LauncherArguments = launcher::RelayBridge.SetupLauncher.LauncherArguments;
using BoundedFrameRelay = launcher::RelayBridge.SetupLauncher.BoundedFrameRelay;
using LauncherServerIdentityFacts = launcher::RelayBridge.SetupLauncher.LauncherServerIdentityFacts;
using LauncherServerIdentityVerifier = launcher::RelayBridge.SetupLauncher.LauncherServerIdentityVerifier;
using SetupLauncher = launcher::RelayBridge.SetupLauncher.SetupLauncher;
using PowerShellExecutionResult = setup::RelayBridge.Setup.PowerShellExecutionResult;
using PowerShellProcessRunner = setup::RelayBridge.Setup.PowerShellProcessRunner;
using PowerShellHostingMode = setup::RelayBridge.Setup.PowerShellHostingMode;
using ExchangeWamConsoleLease = setup::RelayBridge.Setup.ExchangeWamConsoleLease;
using IExchangeWamConsoleNative = setup::RelayBridge.Setup.IExchangeWamConsoleNative;
using ProvisioningScripts = setup::RelayBridge.Setup.ProvisioningScripts;
using SetupOrchestrator = setup::RelayBridge.Setup.SetupOrchestrator;
using ToolingFileEntry = setup::RelayBridge.Setup.ToolingFileEntry;
using ToolingIntegrityVerifier = setup::RelayBridge.Setup.ToolingIntegrityVerifier;
using ToolingManifest = setup::RelayBridge.Setup.ToolingManifest;
using ToolIntegrityException = setup::RelayBridge.Setup.ToolIntegrityException;
using VerifiedTooling = setup::RelayBridge.Setup.VerifiedTooling;
using WorkerOriginFacts = setup::RelayBridge.Setup.WorkerOriginFacts;
using WorkerOriginVerifier = setup::RelayBridge.Setup.WorkerOriginVerifier;
using Xunit;

namespace RelayBridge.IntegrationTests;

[SupportedOSPlatform("windows")]
public sealed class NativeMicrosoftSetupSecurityTests
{
    private static readonly object EnvironmentMutationLock = new();

    [Fact]
    public async Task Pipe_protocol_round_trips_strict_bounded_messages()
    {
        var session = Guid.NewGuid();
        var expected = new NativeSetupEnvelope(
            NativeMicrosoftSetupProtocol.Version,
            NativeSetupMessageKind.Stage,
            session,
            Stage: NativeSetupStage.ConfiguringExchange,
            SafeFailureDetails: new NativeSetupSafeFailureDetails(
                "Microsoft.Graph.SafeException",
                "SafeFailureId",
                "InvalidOperation",
                403));
        await using var stream = new MemoryStream();

        await NativeSetupPipeProtocol.WriteAsync(stream, expected, CancellationToken.None);
        stream.Position = 0;
        var actual = await NativeSetupPipeProtocol.ReadAsync<NativeSetupEnvelope>(stream, CancellationToken.None);

        Assert.Equal(expected, actual);
        Assert.True(stream.Length <= NativeMicrosoftSetupProtocol.MaximumMessageBytes + sizeof(int));
    }

    [Fact]
    public async Task Native_launcher_hello_and_frame_relay_are_compatible_with_the_Host_protocol()
    {
        await using var helloStream = new MemoryStream();
        await BoundedFrameRelay.WriteHelloAsync(helloStream, 123, 4, CancellationToken.None);
        helloStream.Position = 0;
        var hello = await NativeSetupPipeProtocol.ReadAsync<NativeSetupEnvelope>(
            helloStream,
            CancellationToken.None);
        Assert.Equal(NativeSetupMessageKind.Hello, hello.Kind);
        Assert.Equal(123, hello.ProcessId);
        Assert.Equal(4, hello.WindowsSessionId);

        var payload = Encoding.UTF8.GetBytes("{\"bounded\":true}");
        await using var relay = new MemoryStream();
        await BoundedFrameRelay.WriteFrameAsync(relay, payload, CancellationToken.None);
        relay.Position = 0;
        Assert.Equal(payload, await BoundedFrameRelay.ReadFrameAsync(relay, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => BoundedFrameRelay.WriteFrameAsync(
            Stream.Null,
            new byte[NativeMicrosoftSetupProtocol.MaximumMessageBytes + 1],
            CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"version\":1,\"kind\":0,\"unexpected\":true}")]
    [InlineData("{\"version\":1,\"version\":1,\"kind\":0}")]
    [InlineData("{not-json}")]
    public async Task Pipe_protocol_rejects_unknown_duplicate_and_malformed_json(string json)
    {
        await using var stream = Framed(json);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NativeSetupPipeProtocol.ReadAsync<NativeSetupEnvelope>(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Pipe_protocol_rejects_oversized_length_before_allocation()
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(header, NativeMicrosoftSetupProtocol.MaximumMessageBytes + 1);
        await using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NativeSetupPipeProtocol.ReadAsync<NativeSetupEnvelope>(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Pipe_protocol_detects_helper_disconnect_during_a_message()
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(header, 128);
        await using var stream = new MemoryStream([.. header, (byte)'{']);

        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            NativeSetupPipeProtocol.ReadAsync<NativeSetupEnvelope>(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(6)]
    public async Task Pipe_bootstrap_timeout_bounds_silent_and_partial_frames(int bytesBeforeStall)
    {
        var frame = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(frame, 8);
        await using var stream = new PrefixThenStallStream(frame[..bytesBeforeStall]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NativeSetupPipeProtocol.ReadAsync<NativeSetupEnvelope>(stream, timeout.Token));
    }

    [Fact]
    public async Task Runtime_allows_one_session_and_cancellation_is_idempotent()
    {
        var runtime = new NativeMicrosoftSetupRuntime(
            new NativeMicrosoftSetupOptions { Enabled = true },
            TimeProvider.System);
        var cancellationCount = 0;
        runtime.Start(_ =>
        {
            Interlocked.Increment(ref cancellationCount);
            runtime.Fail(
                NativeSetupFailureCategory.Cancelled,
                "Cancelled",
                "Cancelled",
                null);
            return Task.CompletedTask;
        });

        Assert.Throws<InvalidOperationException>(() => runtime.Start(_ => Task.CompletedTask));
        await runtime.CancelAsync();
        await runtime.CancelAsync();

        Assert.Equal(1, cancellationCount);
        Assert.False(runtime.Snapshot.Running);
        Assert.Equal(NativeSetupFailureCategory.Cancelled, runtime.Snapshot.FailureCategory);
    }

    [Fact]
    public void Listener_readiness_preserves_the_last_sanitized_attempt_failure_until_a_new_launch()
    {
        var runtime = new NativeMicrosoftSetupRuntime(
            new NativeMicrosoftSetupOptions { Enabled = true },
            TimeProvider.System);
        runtime.Start(_ => Task.CompletedTask);
        runtime.Update(
            NativeSetupStage.ConfiguringApplication,
            "Creating or verifying the dedicated Microsoft application.");
        runtime.Fail(
            NativeSetupFailureCategory.MicrosoftService,
            "Microsoft administrator setup did not finish. The candidate remains inactive.",
            "MicrosoftProvisioningFailed",
            null,
            new NativeSetupSafeFailureDetails(
                "Microsoft.Graph.SafeException",
                "SafeFailureId",
                "InvalidOperation",
                403));
        var failure = runtime.Snapshot;

        runtime.ListenerReady();

        var listenerReady = runtime.Snapshot;
        Assert.True(listenerReady.Available);
        Assert.False(listenerReady.Running);
        Assert.Equal(failure.Stage, listenerReady.Stage);
        Assert.Equal(failure.FailureCategory, listenerReady.FailureCategory);
        Assert.Equal(failure.SafeCode, listenerReady.SafeCode);
        Assert.Equal(failure.Message, listenerReady.Message);
        Assert.Equal(failure.UpdatedUtc, listenerReady.UpdatedUtc);
        Assert.Equal(failure.SafeFailureDetails, listenerReady.SafeFailureDetails);

        runtime.PrepareForLaunch();

        Assert.Equal(NativeSetupFailureCategory.None, runtime.Snapshot.FailureCategory);
        Assert.Null(runtime.Snapshot.SafeCode);
        Assert.Null(runtime.Snapshot.SafeFailureDetails);
    }

    [Theory]
    [InlineData("Connect", "EntraConnectionFailed")]
    [InlineData("ApplicationDiscovery", "EntraApplicationDiscoveryFailed")]
    [InlineData("ApplicationCreate", "EntraApplicationCreateFailed")]
    [InlineData("ServicePrincipalCreate", "EntraServicePrincipalCreateFailed")]
    [InlineData("CertificateCredential", "EntraCertificateCredentialFailed")]
    [InlineData("ApplicationVerification", "EntraApplicationVerificationFailed")]
    public async Task Entra_failure_instrumentation_maps_substage_without_retaining_secret_text(
        string provisioningStage,
        string expectedCode)
    {
        using var fixture = PrivateEntraModuleFixture.Create();
        const string secretText = "Bearer eyJ-secret-token-value";
        var script = $$"""
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            {{ProvisioningScripts.EntraFailureInstrumentation}}
            $exception = [InvalidOperationException]::new('{{secretText}}')
            $exception | Add-Member -NotePropertyName StatusCode -NotePropertyValue 403
            $errorRecord = [Management.Automation.ErrorRecord]::new(
                $exception,
                'SafeFailureId',
                [Management.Automation.ErrorCategory]::InvalidOperation,
                $null)
            Write-RelayBridgeEntraFailure -ProvisioningStage '{{provisioningStage}}' -ErrorRecord $errorRecord
            exit 1
            """;

        var result = await RunPowerShell7ProductionScriptAsync(
            script,
            fixture.PoisonModulePath,
            fixture.PoisonMarkerPath);
        var exception = ProvisioningException.FromPowerShellFailure(result.StandardError);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(NativeSetupFailureCategory.MicrosoftService, exception.Category);
        Assert.Equal(expectedCode, exception.SafeCode);
        Assert.Equal("System.InvalidOperationException", exception.SafeFailureDetails?.PowerShellExceptionType);
        Assert.Equal("SafeFailureId", exception.SafeFailureDetails?.FullyQualifiedErrorId);
        Assert.Equal("InvalidOperation", exception.SafeFailureDetails?.PowerShellCategory);
        Assert.Equal(403, exception.SafeFailureDetails?.HttpStatusCode);
        Assert.DoesNotContain(secretText, result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(secretText, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            secretText,
            JsonSerializer.Serialize(exception.SafeFailureDetails),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Entra_failure_parser_discards_non_allowlisted_secret_shaped_metadata()
    {
        const string secretText = "Bearer eyJ-secret-token-value";
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Code = "EntraApplicationCreateFailed",
            ExceptionType = secretText,
            FullyQualifiedErrorId = "SafeFailureId",
            PowerShellCategory = "InvalidOperation",
            HttpStatusCode = 403,
        }));

        var exception = ProvisioningException.FromPowerShellFailure(
            "RELAYBRIDGE_ENTRA_FAILURE:" + payload);

        Assert.Equal(NativeSetupFailureCategory.MicrosoftService, exception.Category);
        Assert.Equal("EntraApplicationCreateFailed", exception.SafeCode);
        Assert.NotNull(exception.SafeFailureDetails);
        Assert.Null(exception.SafeFailureDetails.PowerShellExceptionType);
        Assert.Equal("SafeFailureId", exception.SafeFailureDetails.FullyQualifiedErrorId);
        Assert.DoesNotContain(secretText, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Native_options_require_absolute_launcher_worker_paths_and_manifest_hashes()
    {
        var valid = new NativeMicrosoftSetupOptions
        {
            Enabled = true,
            InstallationRoot = Path.GetFullPath("."),
            LauncherPath = Path.GetFullPath("RelayBridge.SetupLauncher.exe"),
            ExpectedLauncherSha256 = new string('A', 64),
            WorkerPath = Path.GetFullPath("RelayBridge.Setup.exe"),
            HelperManifestPath = Path.GetFullPath("helper-manifest.json"),
            ExpectedHelperManifestSha256 = new string('C', 64),
            ToolingRoot = Path.GetFullPath("Tools"),
            ToolingManifestPath = Path.GetFullPath("tooling-manifest.json"),
            ExpectedToolingManifestSha256 = new string('B', 64),
        };

        valid.Validate();
        Assert.Throws<InvalidOperationException>(() => withHelperHash("bad").Validate());
        Assert.Throws<InvalidOperationException>(() => withManifestHash("bad").Validate());
        var invalidBootstrap = withManifestHash(valid.ExpectedToolingManifestSha256);
        invalidBootstrap.BootstrapTimeout = TimeSpan.FromMinutes(3);
        Assert.Throws<InvalidOperationException>(invalidBootstrap.Validate);

        NativeMicrosoftSetupOptions withHelperHash(string value) => new()
        {
            Enabled = valid.Enabled,
            InstallationRoot = valid.InstallationRoot,
            LauncherPath = valid.LauncherPath,
            ExpectedLauncherSha256 = value,
            WorkerPath = valid.WorkerPath,
            HelperManifestPath = valid.HelperManifestPath,
            ExpectedHelperManifestSha256 = valid.ExpectedHelperManifestSha256,
            ToolingRoot = valid.ToolingRoot,
            ToolingManifestPath = valid.ToolingManifestPath,
            ExpectedToolingManifestSha256 = valid.ExpectedToolingManifestSha256,
        };
        NativeMicrosoftSetupOptions withManifestHash(string value) => new()
        {
            Enabled = valid.Enabled,
            InstallationRoot = valid.InstallationRoot,
            LauncherPath = valid.LauncherPath,
            ExpectedLauncherSha256 = valid.ExpectedLauncherSha256,
            WorkerPath = valid.WorkerPath,
            HelperManifestPath = valid.HelperManifestPath,
            ExpectedHelperManifestSha256 = valid.ExpectedHelperManifestSha256,
            ToolingRoot = valid.ToolingRoot,
            ToolingManifestPath = valid.ToolingManifestPath,
            ExpectedToolingManifestSha256 = value,
        };

        var invalidHelperManifest = withManifestHash(valid.ExpectedToolingManifestSha256);
        invalidHelperManifest.ExpectedHelperManifestSha256 = "bad";
        Assert.Throws<InvalidOperationException>(invalidHelperManifest.Validate);
    }

    [Fact]
    public async Task Launch_preflight_rejects_an_untrusted_helper_closure_before_a_session_starts()
    {
        var options = new NativeMicrosoftSetupOptions { Enabled = true };
        var runtime = new NativeMicrosoftSetupRuntime(options, TimeProvider.System);
        await using var server = new NativeMicrosoftSetupServer(
            options,
            runtime,
            setup: null!,
            certificates: null!,
            new TestLogger<NativeMicrosoftSetupServer>(),
            pipeFactory: null!,
            listenerRetryDelay: TimeSpan.Zero,
            helperClosureVerifier: () => throw new TrustedWindowsPathException());

        Assert.Throws<InvalidOperationException>(server.PrepareForLaunch);
        Assert.False(runtime.Snapshot.Running);
        Assert.Equal(NativeSetupStage.WaitingForHelper, runtime.Snapshot.Stage);
        Assert.Equal(NativeSetupFailureCategory.ToolIntegrity, runtime.Snapshot.FailureCategory);
        Assert.Equal("UntrustedHelperClosure", runtime.Snapshot.SafeCode);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Launcher_identity_requires_actual_pid_session_path_hash_and_interactive_sid()
    {
        var hash = SHA256.HashData("approved"u8);
        var sid = new SecurityIdentifier("S-1-5-21-1000-1000-1000-1001");
        var approved = new NativeSetupLauncherIdentityFacts(
            123, 123, 4, 4,
            Path.GetFullPath("RelayBridge.SetupLauncher.exe"),
            Path.GetFullPath("RelayBridge.SetupLauncher.exe"),
            hash, hash, sid, sid);

        Assert.Equal(4, NativeSetupLauncherIdentityPolicy.Validate(approved).SessionId);
        Assert.Throws<InvalidDataException>(() => NativeSetupLauncherIdentityPolicy.Validate(approved with { ActualProcessId = 124 }));
        Assert.Throws<InvalidDataException>(() => NativeSetupLauncherIdentityPolicy.Validate(approved with { ActualSessionId = 5 }));
        Assert.Throws<InvalidDataException>(() => NativeSetupLauncherIdentityPolicy.Validate(approved with { ActualPath = Path.GetFullPath("fake.exe") }));
        Assert.Throws<CryptographicException>(() => NativeSetupLauncherIdentityPolicy.Validate(approved with { ActualHash = SHA256.HashData("tampered"u8) }));
        Assert.Throws<InvalidDataException>(() => NativeSetupLauncherIdentityPolicy.Validate(approved with
        {
            PipeSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            ProcessSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        }));
        Assert.Throws<InvalidDataException>(() => NativeSetupLauncherIdentityPolicy.Validate(approved with
        {
            ProcessSid = new SecurityIdentifier("S-1-5-21-1000-1000-1000-2002"),
        }));
    }

    [Fact]
    public async Task Auxiliary_listener_contains_pipe_creation_failure_and_retries_without_host_failure()
    {
        var options = new NativeMicrosoftSetupOptions { Enabled = true };
        var runtime = new NativeMicrosoftSetupRuntime(options, TimeProvider.System);
        using var stop = new CancellationTokenSource();
        var calls = 0;
        await using var server = new NativeMicrosoftSetupServer(
            options,
            runtime,
            setup: null!,
            certificates: null!,
            new TestLogger<NativeMicrosoftSetupServer>(),
            () =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw new IOException("simulated pipe collision");
                }

                stop.Cancel();
                throw new OperationCanceledException(stop.Token);
            },
            TimeSpan.Zero);

        await server.RunAsync(stop.Token);

        Assert.Equal(2, calls);
        Assert.False(runtime.Snapshot.Available);
        Assert.Equal(NativeSetupFailureCategory.HelperFailed, runtime.Snapshot.FailureCategory);
    }

    [Fact]
    public async Task Bootstrap_timeout_bounds_identity_validation_and_releases_listener()
    {
        var pipeName = $"relaybridge-test-{Guid.NewGuid():N}";
        var options = new NativeMicrosoftSetupOptions
        {
            Enabled = true,
            BootstrapTimeout = TimeSpan.FromMilliseconds(100),
        };
        var runtime = new NativeMicrosoftSetupRuntime(options, TimeProvider.System);
        using var stop = new CancellationTokenSource();
        var calls = 0;
        await using var server = new NativeMicrosoftSetupServer(
            options,
            runtime,
            setup: null!,
            certificates: null!,
            new TestLogger<NativeMicrosoftSetupServer>(),
            () => Interlocked.Increment(ref calls) == 1
                ? new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous)
                : StopListener(stop),
            TimeSpan.Zero,
            identityValidator: async (_, _, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new UnreachableException();
            });

        var run = server.RunAsync(stop.Token);
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(5_000);
        await NativeSetupPipeProtocol.WriteAsync(
            client,
            new NativeSetupEnvelope(
                NativeMicrosoftSetupProtocol.Version,
                NativeSetupMessageKind.Hello,
                ProcessId: Environment.ProcessId,
                WindowsSessionId: Process.GetCurrentProcess().SessionId),
            CancellationToken.None);

        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, calls);
        Assert.Equal(NativeSetupFailureCategory.Timeout, runtime.Snapshot.FailureCategory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Auxiliary_listener_contains_helper_and_database_operational_failures(bool databaseFailure)
    {
        var exception = databaseFailure
            ? (Exception)new SqliteException("simulated database busy", 5)
            : new InvalidDataException("simulated invalid helper operation");

        var runtime = await RunContainedConnectionFailureAsync(exception);

        Assert.False(runtime.Snapshot.Running);
        Assert.Equal(
            databaseFailure ? NativeSetupFailureCategory.HelperFailed : NativeSetupFailureCategory.InvalidHelper,
            runtime.Snapshot.FailureCategory);
        Assert.Equal(databaseFailure, !runtime.Snapshot.Available);
    }

    [Fact]
    public void Bootstrap_pipe_acl_explicitly_denies_the_network_sid()
    {
        var rules = NativeMicrosoftSetupServer.CreatePipeSecurity().GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            typeof(SecurityIdentifier));

        Assert.Contains(
            rules.Cast<PipeAccessRule>(),
            rule => rule.AccessControlType == AccessControlType.Deny &&
                ((SecurityIdentifier)rule.IdentityReference).IsWellKnown(WellKnownSidType.NetworkSid));
        Assert.Equal(0x00000008u, NativeMicrosoftSetupServer.PipeRejectRemoteClients);
    }

    [Fact]
    public async Task Bootstrap_pipe_factory_accepts_a_local_client_with_the_hardened_mode()
    {
        await using var server = NativeMicrosoftSetupServer.CreatePipe();
        using var client = new NamedPipeClientStream(
            ".",
            NativeMicrosoftSetupProtocol.BootstrapPipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        var wait = server.WaitForConnectionAsync();

        await client.ConnectAsync(5_000);
        await wait.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(server.IsConnected);
        Assert.True(client.IsConnected);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Native_launcher_accepts_only_the_running_local_system_service_with_the_expected_host_image()
    {
        var approved = new LauncherServerIdentityFacts(
            123,
            123,
            0x00000010,
            0x00000004,
            "\"C:\\Program Files\\RelayBridge\\Host\\RelayBridge.Host.exe\"",
            "LocalSystem",
            @"C:\Program Files\RelayBridge\Host\RelayBridge.Host.exe");

        LauncherServerIdentityVerifier.Validate(approved);
        Assert.Throws<InvalidDataException>(() =>
            LauncherServerIdentityVerifier.Validate(approved with { PipeProcessId = 0 }));
        Assert.Throws<InvalidDataException>(() =>
            LauncherServerIdentityVerifier.Validate(approved with { ServiceProcessId = 124 }));
        Assert.Throws<InvalidDataException>(() =>
            LauncherServerIdentityVerifier.Validate(approved with { ServiceType = 0x00000020 }));
        Assert.Throws<InvalidDataException>(() =>
            LauncherServerIdentityVerifier.Validate(approved with { ServiceState = 0x00000001 }));
        Assert.Throws<InvalidDataException>(() =>
            LauncherServerIdentityVerifier.Validate(approved with { ServiceStartName = "LocalService" }));
        Assert.Throws<InvalidDataException>(() =>
            LauncherServerIdentityVerifier.Validate(approved with
            {
                ServiceBinaryPath = @"C:\Program Files\RelayBridge\Host\RelayBridge.Host.exe"
            }));
        Assert.Throws<InvalidDataException>(() =>
            LauncherServerIdentityVerifier.Validate(approved with
            {
                ServiceBinaryPath = "\"C:\\Program Files\\RelayBridge\\Host\\RelayBridge.Host.exe\" --unexpected"
            }));
        Assert.Throws<InvalidDataException>(() =>
            LauncherServerIdentityVerifier.Validate(approved with
            {
                ServiceBinaryPath = "\"C:\\Program Files\\RelayBridge\\Host\\RelayBridge.Host.exe\\\"\""
            }));
        Assert.Throws<InvalidDataException>(() =>
            LauncherServerIdentityVerifier.Validate(approved with
            {
                ServiceBinaryPath = "\"C:\\Program Files\\RelayBridge\\Host\\Other.exe\""
            }));
        Assert.Throws<InvalidDataException>(() =>
            LauncherServerIdentityVerifier.Validate(approved with
            {
                ServiceBinaryPath = "\"RelayBridge.Host.exe\""
            }));
        Assert.Throws<InvalidDataException>(() =>
            LauncherServerIdentityVerifier.Validate(approved with
            {
                ServiceBinaryPath = "\"%ProgramFiles%\\RelayBridge\\Host\\RelayBridge.Host.exe\""
            }));
        Assert.Throws<InvalidDataException>(() =>
            LauncherServerIdentityVerifier.Validate(approved with
            {
                ServiceBinaryPath = "\"\\\\server\\share\\RelayBridge.Host.exe\""
            }));
        Assert.Throws<InvalidDataException>(() =>
            LauncherServerIdentityVerifier.Validate(approved with
            {
                ServiceBinaryPath = "\"C:\\Program Files\\RelayBridge\\Host\\RelayBridge.Host.exe\" "
            }));

        LauncherServerIdentityVerifier.Validate(approved with
        {
            ServiceBinaryPath = "\"c:\\program files\\relaybridge\\host\\relaybridge.host.exe\""
        });
    }

    [Fact]
    public void Session_protocol_rejects_out_of_order_unknown_and_replayed_operations()
    {
        var state = new NativeSetupSessionProtocolState();
        Assert.Throws<InvalidDataException>(() => state.Accept(NativeSetupMessageKind.EntraResult));

        state = new NativeSetupSessionProtocolState();
        state.Accept(NativeSetupMessageKind.Confirmed);
        state.Accept(NativeSetupMessageKind.Stage);
        state.Accept(NativeSetupMessageKind.EntraResult);
        Assert.True(state.EntraApplied);
        Assert.Throws<InvalidDataException>(() => state.Accept(NativeSetupMessageKind.EntraResult));
        state.Accept(NativeSetupMessageKind.ExchangeResult);
        state.Accept(NativeSetupMessageKind.Completed);
        Assert.Throws<InvalidDataException>(() => state.Accept(NativeSetupMessageKind.Completed));
        Assert.Throws<InvalidDataException>(() => state.Accept(NativeSetupMessageKind.Hello));
    }

    [Fact]
    public void Session_protocol_allows_safe_terminal_cancel_without_advancing_cloud_state()
    {
        var beforeEntra = new NativeSetupSessionProtocolState();
        beforeEntra.Accept(NativeSetupMessageKind.Confirmed);
        beforeEntra.Accept(NativeSetupMessageKind.Cancelled);
        Assert.False(beforeEntra.EntraApplied);
        Assert.Throws<InvalidDataException>(() => beforeEntra.Accept(NativeSetupMessageKind.Confirmed));

        var afterEntra = new NativeSetupSessionProtocolState();
        afterEntra.Accept(NativeSetupMessageKind.Confirmed);
        afterEntra.Accept(NativeSetupMessageKind.EntraResult);
        afterEntra.Accept(NativeSetupMessageKind.Failed);
        Assert.True(afterEntra.EntraApplied);
        Assert.Throws<InvalidDataException>(() => afterEntra.Accept(NativeSetupMessageKind.ExchangeResult));
    }

    [Fact]
    public void Tooling_manifest_pins_complete_tree_hash_and_versions()
    {
        using var fixture = ToolingFixture.Create(protectRoot: true);

        var verified = ToolingIntegrityVerifier.Verify(
            fixture.InstallationRoot,
            fixture.Root,
            fixture.ManifestPath,
            fixture.ManifestHash,
            BypassPathTrust);

        Assert.Equal(fixture.PowerShellPath, verified.PowerShellPath);
        Assert.Equal("2.25.0", verified.GraphAuthenticationModuleVersion);
        Assert.Equal("2.25.0", verified.GraphApplicationsModuleVersion);
        Assert.Equal("1.3.0", verified.EntraAuthenticationModuleVersion);
        Assert.Equal("3.9.2", verified.ExchangeOnlineModuleVersion);
    }

    [Fact]
    public void Tooling_verification_requires_both_exact_Graph_2_25_0_trees()
    {
        using var missingAuthentication = ToolingFixture.Create();
        File.Delete(missingAuthentication.GraphAuthenticationModulePath);
        Assert.Throws<ToolIntegrityException>(() => ToolingIntegrityVerifier.Verify(
            missingAuthentication.InstallationRoot,
            missingAuthentication.Root,
            missingAuthentication.ManifestPath,
            missingAuthentication.ManifestHash,
            BypassPathTrust));

        using var missingApplications = ToolingFixture.Create();
        File.Delete(missingApplications.GraphApplicationsModulePath);
        Assert.Throws<ToolIntegrityException>(() => ToolingIntegrityVerifier.Verify(
            missingApplications.InstallationRoot,
            missingApplications.Root,
            missingApplications.ManifestPath,
            missingApplications.ManifestHash,
            BypassPathTrust));

        using var wrongAuthentication = ToolingFixture.Create(graphAuthenticationVersion: "2.24.0");
        Assert.Throws<ToolIntegrityException>(() => ToolingIntegrityVerifier.Verify(
            wrongAuthentication.InstallationRoot,
            wrongAuthentication.Root,
            wrongAuthentication.ManifestPath,
            wrongAuthentication.ManifestHash,
            BypassPathTrust));

        using var wrongApplications = ToolingFixture.Create(graphApplicationsVersion: "2.30.0");
        Assert.Throws<ToolIntegrityException>(() => ToolingIntegrityVerifier.Verify(
            wrongApplications.InstallationRoot,
            wrongApplications.Root,
            wrongApplications.ManifestPath,
            wrongApplications.ManifestHash,
            BypassPathTrust));
    }

    [Fact]
    public void Tooling_verification_rejects_modified_or_unexpected_Graph_tree_files()
    {
        using var modified = ToolingFixture.Create();
        File.AppendAllText(modified.GraphAuthenticationRuntimePath, "tampered");
        Assert.Throws<ToolIntegrityException>(() => ToolingIntegrityVerifier.Verify(
            modified.InstallationRoot,
            modified.Root,
            modified.ManifestPath,
            modified.ManifestHash,
            BypassPathTrust));

        using var unexpected = ToolingFixture.Create();
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(unexpected.GraphApplicationsModulePath)!, "unexpected.dll"),
            "unapproved");
        Assert.Throws<ToolIntegrityException>(() => ToolingIntegrityVerifier.Verify(
            unexpected.InstallationRoot,
            unexpected.Root,
            unexpected.ManifestPath,
            unexpected.ManifestHash,
            BypassPathTrust));
    }

    [Fact]
    public void Tooling_verification_rejects_manifest_file_and_tree_tampering()
    {
        using var fixture = ToolingFixture.Create();
        File.AppendAllText(fixture.PowerShellPath, "tampered");
        Assert.Throws<ToolIntegrityException>(() =>
            ToolingIntegrityVerifier.Verify(fixture.InstallationRoot, fixture.Root, fixture.ManifestPath, fixture.ManifestHash, BypassPathTrust));

        using var extraFixture = ToolingFixture.Create();
        File.WriteAllText(Path.Combine(extraFixture.Root, "unapproved.psm1"), "fake");
        Assert.Throws<ToolIntegrityException>(() =>
            ToolingIntegrityVerifier.Verify(extraFixture.InstallationRoot, extraFixture.Root, extraFixture.ManifestPath, extraFixture.ManifestHash, BypassPathTrust));

        using var manifestFixture = ToolingFixture.Create();
        File.AppendAllText(manifestFixture.ManifestPath, " ");
        Assert.Throws<ToolIntegrityException>(() =>
            ToolingIntegrityVerifier.Verify(manifestFixture.InstallationRoot, manifestFixture.Root, manifestFixture.ManifestPath, manifestFixture.ManifestHash, BypassPathTrust));
    }

    [Fact]
    public void Fixed_scripts_use_WAM_exact_modules_and_data_not_executable_input()
    {
        var tooling = CreateVerifiedTooling();
        var malicious = "scanner@example.com'; Remove-Item C:\\\\Windows -Recurse; #`n";
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            ClientId = Guid.NewGuid(),
            ServicePrincipalObjectId = Guid.NewGuid(),
            SenderMailbox = malicious,
        }));

        var exchange = ProvisioningScripts.CreateExchangeScript(
            tooling,
            payload,
            Path.GetFullPath(Path.GetTempPath()));
        var entra = ProvisioningScripts.CreateEntraScript(tooling, payload);

        Assert.DoesNotContain(malicious, exchange, StringComparison.Ordinal);
        Assert.Contains(
            "Connect-ExchangeOnline -ShowBanner:$false -EXOModuleBasePath $exchangeModuleBasePath",
            exchange,
            StringComparison.Ordinal);
        Assert.Contains("Application SMTP.SendAsApp", exchange, StringComparison.Ordinal);
        Assert.Contains("Test-ServicePrincipalAuthorization", exchange, StringComparison.Ordinal);
        Assert.Contains("Connect-Entra -Scopes 'Application.ReadWrite.All' -ContextScope Process", entra, StringComparison.Ordinal);
        Assert.Contains("RequiredResourceAccess @()", entra, StringComparison.Ordinal);
        Assert.Contains("CustomKeyIdentifier = [Convert]::ToBase64String($Certificate.GetCertHash())", entra, StringComparison.Ordinal);
        Assert.Contains("Key = [Convert]::ToBase64String($Certificate.RawData)", entra, StringComparison.Ordinal);
        Assert.DoesNotContain("Key = $certificate.RawData", entra, StringComparison.Ordinal);
        Assert.Contains("Invoke-MgGraphRequest", entra, StringComparison.Ordinal);
        Assert.Contains("requiredResourceAccess,keyCredentials,passwordCredentials,signInAudience", entra, StringComparison.Ordinal);
        Assert.DoesNotContain("$application.RequiredResourceAccess", entra, StringComparison.Ordinal);
        Assert.DoesNotContain("$_.ResourceAccess", entra, StringComparison.Ordinal);
        Assert.Contains("$members.Count -ne 1", exchange, StringComparison.Ordinal);
        Assert.Contains("$Assignments.Count -ne 1", exchange, StringComparison.Ordinal);
        Assert.Contains(".Trim().Equals($expectedScopeFilter", exchange, StringComparison.Ordinal);
        Assert.DoesNotContain("IndexOf('MemberOfGroup'", exchange, StringComparison.Ordinal);
        Assert.DoesNotContain("DisableWAM", exchange, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Device", exchange, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mail.Send", entra, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SMTP.SendAsApp", entra, StringComparison.OrdinalIgnoreCase);

        var graphAuthenticationImport = entra.IndexOf(
            "Import-Module $graphAuthentication",
            StringComparison.Ordinal);
        var graphApplicationsImport = entra.IndexOf(
            "Import-Module $graphApplications",
            StringComparison.Ordinal);
        var entraAuthenticationImport = entra.IndexOf(
            "Import-Module $entraAuthentication",
            StringComparison.Ordinal);
        var entraApplicationsImport = entra.IndexOf(
            "Import-Module $entraApplications",
            StringComparison.Ordinal);
        Assert.True(graphAuthenticationImport >= 0);
        Assert.True(graphAuthenticationImport < graphApplicationsImport);
        Assert.True(graphApplicationsImport < entraAuthenticationImport);
        Assert.True(entraAuthenticationImport < entraApplicationsImport);
        Assert.Contains("$env:PSModulePath = ''", entra, StringComparison.Ordinal);
        Assert.Contains("$PSModuleAutoLoadingPreference = 'None'", entra, StringComparison.Ordinal);
        Assert.Equal(2, exchange.Split("$env:PSModulePath = ''", StringSplitOptions.None).Length - 1);
        Assert.Contains("Assert-RelayBridgeExchangeModuleState", exchange, StringComparison.Ordinal);
        Assert.Contains("RELAYBRIDGE_TOOL_INTEGRITY:TemporaryModulePath", exchange, StringComparison.Ordinal);
        Assert.Contains("$PSModuleAutoLoadingPreference = 'None'", exchange, StringComparison.Ordinal);
        foreach (var provisioningStage in new[]
                 {
                     "Connect",
                     "ApplicationDiscovery",
                     "ApplicationCreate",
                     "ServicePrincipalCreate",
                     "CertificateCredential",
                     "ApplicationVerification",
                 })
        {
            Assert.Contains($"$provisioningStage = '{provisioningStage}'", entra, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Entra_import_preflight_loads_only_exact_private_modules_and_commands()
    {
        using var fixture = PrivateEntraModuleFixture.Create();
        var result = await RunPowerShell7ProductionScriptAsync(
            ProvisioningScripts.CreateEntraImportPreflightScript(fixture.Tooling),
            fixture.PoisonModulePath,
            fixture.PoisonMarkerPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.False(File.Exists(fixture.PoisonMarkerPath));
        Assert.StartsWith("RELAYBRIDGE_RESULT:", result.StandardOutput, StringComparison.Ordinal);
        var evidence = SetupOrchestrator.ParseResult<SetupOrchestrator.EntraImportPreflightResult>(result);
        Assert.Equal("2.25.0", evidence.GraphAuthenticationVersion);
        Assert.Equal("2.25.0", evidence.GraphApplicationsVersion);
        Assert.Equal("1.3.0", evidence.EntraAuthenticationVersion);
        Assert.Equal("1.3.0", evidence.EntraApplicationsVersion);
        Assert.True(evidence.GraphAuthenticationPathMatches);
        Assert.True(evidence.GraphApplicationsPathMatches);
        Assert.True(evidence.EntraAuthenticationPathMatches);
        Assert.True(evidence.EntraApplicationsPathMatches);
        Assert.True(evidence.ConnectMgGraphAvailable);
        Assert.True(evidence.ConnectEntraAvailable);
        Assert.True(evidence.GetMgApplicationAvailable);
        Assert.True(evidence.PSModulePathLocked);
        Assert.False(evidence.UnexpectedModuleDiscovery);
    }

    [Fact]
    public async Task Entra_import_preflight_rejects_a_wrong_private_module_identity()
    {
        using var fixture = PrivateEntraModuleFixture.Create();
        var wrong = fixture.Tooling with { GraphApplicationsModuleVersion = "2.30.0" };

        var result = await RunPowerShell7ProductionScriptAsync(
            ProvisioningScripts.CreateEntraImportPreflightScript(wrong),
            fixture.PoisonModulePath,
            fixture.PoisonMarkerPath);

        Assert.Contains("RELAYBRIDGE_TOOL_INTEGRITY:ModuleIdentity", result.StandardError, StringComparison.Ordinal);
        Assert.Throws<ProvisioningException>(() =>
            SetupOrchestrator.ParseResult<SetupOrchestrator.EntraImportPreflightResult>(result));
        Assert.False(File.Exists(fixture.PoisonMarkerPath));
    }

    [Fact]
    public void PowerShell_launch_policy_uses_only_the_absolute_approved_executable()
    {
        var approved = Path.GetFullPath(Path.Combine("private-tools", "PowerShell", "pwsh.exe"));
        var workingDirectory = Path.GetFullPath("private-tools");

        var start = PowerShellProcessRunner.CreateStartInfo(
            approved,
            workingDirectory,
            Path.GetFullPath(Path.GetTempPath()));

        Assert.Equal(approved, start.FileName);
        Assert.Equal(workingDirectory, start.WorkingDirectory);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.True(start.RedirectStandardInput);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.Equal(
            ["-NoLogo", "-NoProfile", "-Command", "& ([scriptblock]::Create([Console]::In.ReadToEnd()))"],
            start.ArgumentList);
        Assert.Equal(string.Empty, start.Environment["PSModulePath"]);
        Assert.Equal("0", start.Environment["DOTNET_EnableDiagnostics"]);
        AssertRuntimeInjectionEnvironmentIsAbsent(start.Environment);
        Assert.Throws<ToolIntegrityException>(() =>
            PowerShellProcessRunner.CreateStartInfo(
                "pwsh",
                workingDirectory,
                Path.GetFullPath(Path.GetTempPath())));

        var exchange = PowerShellProcessRunner.CreateStartInfo(
            approved,
            workingDirectory,
            Path.GetFullPath(Path.GetTempPath()),
            PowerShellHostingMode.InteractiveWamConsole);
        Assert.False(exchange.CreateNoWindow);
        Assert.False(exchange.UseShellExecute);
        Assert.True(exchange.RedirectStandardInput);
        Assert.True(exchange.RedirectStandardOutput);
        Assert.True(exchange.RedirectStandardError);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()), exchange.Environment["TEMP"]);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()), exchange.Environment["TMP"]);
        Assert.Equal(string.Empty, exchange.Environment["PSModulePath"]);
        Assert.Equal("0", exchange.Environment["DOTNET_EnableDiagnostics"]);
        Assert.DoesNotContain(exchange.Environment.Keys, key => key.Contains("PROXY", StringComparison.OrdinalIgnoreCase));
        AssertRuntimeInjectionEnvironmentIsAbsent(exchange.Environment);
    }

    [Fact]
    public void Exchange_console_lease_fails_closed_when_console_allocation_fails()
    {
        var native = new FakeExchangeWamConsoleNative
        {
            AllocConsoleResult = false,
        };

        var exception = Assert.Throws<ProvisioningException>(() =>
            ExchangeWamConsoleLease.Acquire(1, native));

        Assert.Equal(NativeSetupFailureCategory.MicrosoftService, exception.Category);
        Assert.Equal("ExchangeWamConsoleUnavailable", exception.SafeCode);
        Assert.Equal(1, native.AllocConsoleCalls);
        Assert.Equal(0, native.FreeConsoleCalls);

        native = new FakeExchangeWamConsoleNative
        {
            AllocConsoleResult = true,
        };
        Assert.Throws<ProvisioningException>(() => ExchangeWamConsoleLease.Acquire(2, native));
        Assert.Equal(0, native.AllocConsoleCalls);
    }

    [Fact]
    public void Exchange_console_lease_releases_only_a_console_that_it_allocated()
    {
        var native = new FakeExchangeWamConsoleNative
        {
            AllocConsoleResult = true,
            WindowAfterAllocation = new IntPtr(42),
        };

        using (var lease = ExchangeWamConsoleLease.Acquire(1, native))
        {
            Assert.True(lease.OwnsConsole);
            Assert.Equal(new IntPtr(42), lease.WindowHandle);
            Assert.Equal("RelayBridge — Microsoft setup", native.Title);
        }

        Assert.Equal(1, native.FreeConsoleCalls);

        native = new FakeExchangeWamConsoleNative
        {
            InitialWindow = new IntPtr(84),
        };
        using (var lease = ExchangeWamConsoleLease.Acquire(1, native))
        {
            Assert.False(lease.OwnsConsole);
            Assert.Equal(new IntPtr(84), lease.WindowHandle);
        }

        Assert.Equal(0, native.AllocConsoleCalls);
        Assert.Equal(0, native.FreeConsoleCalls);
    }

    [Fact]
    public async Task Exchange_console_attachment_poll_accepts_only_the_expected_child_when_it_appears()
    {
        const int expectedChild = 4242;
        var native = new FakeExchangeWamConsoleNative { InitialWindow = new IntPtr(84) };
        native.ConsoleProcessIdSnapshots.Enqueue([(uint)Environment.ProcessId]);
        native.ConsoleProcessIdSnapshots.Enqueue([(uint)Environment.ProcessId, 7777]);
        native.ConsoleProcessIdSnapshots.Enqueue([(uint)Environment.ProcessId, expectedChild]);

        using var lease = ExchangeWamConsoleLease.Acquire(1, native);
        await lease.VerifyChildAttachedAsync(
            expectedChild,
            CancellationToken.None,
            maximumAttempts: 3,
            pollInterval: TimeSpan.Zero);

        Assert.Equal(3, native.GetConsoleProcessIdsCalls);
    }

    [Fact]
    public async Task Exchange_console_attachment_poll_rejects_a_missing_or_wrong_child_after_the_bound()
    {
        const int expectedChild = 4242;
        var native = new FakeExchangeWamConsoleNative { InitialWindow = new IntPtr(84) };
        native.ConsoleProcessIdSnapshots.Enqueue([(uint)Environment.ProcessId, 7777]);
        native.ConsoleProcessIdSnapshots.Enqueue([(uint)Environment.ProcessId, 8888]);
        native.ConsoleProcessIdSnapshots.Enqueue([(uint)Environment.ProcessId]);

        using var lease = ExchangeWamConsoleLease.Acquire(1, native);
        var exception = await Assert.ThrowsAsync<ProvisioningException>(() =>
            lease.VerifyChildAttachedAsync(
                expectedChild,
                CancellationToken.None,
                maximumAttempts: 3,
                pollInterval: TimeSpan.Zero));

        Assert.Equal("ExchangeWamConsoleUnavailable", exception.SafeCode);
        Assert.Equal(3, native.GetConsoleProcessIdsCalls);
    }

    [Fact]
    public async Task Exchange_console_attachment_poll_honors_cancellation_before_polling()
    {
        var native = new FakeExchangeWamConsoleNative { InitialWindow = new IntPtr(84) };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        using var lease = ExchangeWamConsoleLease.Acquire(1, native);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            lease.VerifyChildAttachedAsync(
                4242,
                cancellation.Token,
                maximumAttempts: 3,
                pollInterval: TimeSpan.Zero));

        Assert.Equal(0, native.GetConsoleProcessIdsCalls);
    }

    [Fact]
    public async Task Real_PowerShell_has_no_console_when_hidden_and_a_console_with_redirected_pipes_in_Exchange_mode()
    {
        var hidden = await RunConsoleHostProbeAsync("hidden");
        Assert.Equal(0, hidden.InitialWindow);
        Assert.Equal(0, hidden.ChildWindow);
        Assert.Equal(0, hidden.FinalWindow);
        Assert.Contains("STDOUT_MARKER", hidden.StandardOutput, StringComparison.Ordinal);
        Assert.Equal("STDERR_MARKER", hidden.StandardError?.Trim());

        var exchange = await RunConsoleHostProbeAsync("interactive");
        Assert.Equal(0, exchange.InitialWindow);
        Assert.True(exchange.ChildWindow > 0);
        Assert.Equal(0, exchange.FinalWindow);
        Assert.Contains("STDOUT_MARKER", exchange.StandardOutput, StringComparison.Ordinal);
        Assert.Equal("STDERR_MARKER", exchange.StandardError?.Trim());
    }

    [Fact]
    public async Task Exchange_console_mode_keeps_job_cancellation_and_releases_console_after_child_exit()
    {
        var result = await RunConsoleHostProbeAsync("cancel");

        Assert.Equal(0, result.InitialWindow);
        Assert.Equal(0, result.FinalWindow);
        Assert.True(result.ChildExited);
        Assert.Null(result.ErrorType);
    }

    [Fact]
    public void Native_launcher_accepts_only_the_fixed_parameter_free_uri()
    {
        Assert.True(LauncherArguments.AreValid([]));
        Assert.True(LauncherArguments.AreValid(["relaybridge-setup://start"]));
        Assert.True(LauncherArguments.AreValid(["relaybridge-setup://start/"]));
        Assert.False(LauncherArguments.AreValid(["relaybridge-setup://start?command=anything"]));
        Assert.False(LauncherArguments.AreValid(["relaybridge-setup://start/?command=anything"]));
        Assert.False(LauncherArguments.AreValid(["relaybridge-setup://start/#fragment"]));
        Assert.False(LauncherArguments.AreValid(["relaybridge-setup://start/anything"]));
        Assert.False(LauncherArguments.AreValid(["C:\\untrusted.exe"]));
        Assert.False(LauncherArguments.AreValid(["relaybridge-setup://start", "extra"]));
    }

    [Fact]
    public void Worker_environment_is_an_explicit_runtime_injection_free_allowlist()
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "RelayBridge.Setup.exe");

        var start = SetupLauncher.CreateWorkerStartInfo(worker, Path.GetFullPath(Path.GetTempPath()));

        Assert.Equal(worker, start.FileName);
        Assert.Empty(start.ArgumentList);
        Assert.Equal("0", start.Environment["DOTNET_EnableDiagnostics"]);
        Assert.False(string.IsNullOrWhiteSpace(start.Environment["USERPROFILE"]));
        Assert.False(string.IsNullOrWhiteSpace(start.Environment["APPDATA"]));
        Assert.False(string.IsNullOrWhiteSpace(start.Environment["LOCALAPPDATA"]));
        Assert.False(string.IsNullOrWhiteSpace(start.Environment["TEMP"]));
        AssertRuntimeInjectionEnvironmentIsAbsent(start.Environment);
        Assert.DoesNotContain(start.Environment.Keys, key => key.Contains("PROXY", StringComparison.OrdinalIgnoreCase));
        Assert.All(start.Environment.Keys, key => Assert.True(IsApprovedEnvironmentKey(key), key));
    }

    [Fact]
    public void Privileged_environment_uses_the_supplied_scratch_and_omits_parent_proxy_and_low_value_state()
    {
        var scratch = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"RelayBridge.Scratch.{Guid.NewGuid():N}"));
        var environment = PrivilegedProcessEnvironment.Create(scratch);

        Assert.Equal(scratch, environment["TEMP"]);
        Assert.Equal(scratch, environment["TMP"]);
        Assert.DoesNotContain(environment.Keys, key => key.Contains("PROXY", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("USERDNSDOMAIN", environment.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("LOGONSERVER", environment.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("SESSIONNAME", environment.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("CLIENTNAME", environment.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("PUBLIC", environment.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            environment["USERPROFILE"]);
        Assert.Equal(
            Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
            environment["APPDATA"]);
        Assert.Equal(
            Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            environment["LOCALAPPDATA"]);
    }

    [Fact]
    public void Privileged_environment_rebuilds_profile_paths_instead_of_accepting_poisoned_parent_text()
    {
        lock (EnvironmentMutationLock)
        {
            var names = new[] { "USERPROFILE", "APPDATA", "LOCALAPPDATA", "HTTPS_PROXY", "hTtP_pRoXy" };
            var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var name in names)
                {
                    Environment.SetEnvironmentVariable(name, "C:\\attacker-controlled");
                }

                var environment = PrivilegedProcessEnvironment.Create(Path.GetFullPath(Path.GetTempPath()));

                Assert.NotEqual("C:\\attacker-controlled", environment["USERPROFILE"]);
                Assert.NotEqual("C:\\attacker-controlled", environment["APPDATA"]);
                Assert.NotEqual("C:\\attacker-controlled", environment["LOCALAPPDATA"]);
                Assert.DoesNotContain(environment.Keys, key => key.Contains("PROXY", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                foreach (var item in previous)
                {
                    Environment.SetEnvironmentVariable(item.Key, item.Value);
                }
            }
        }
    }

    [Fact]
    public async Task Sanitized_real_PowerShell_does_not_contact_an_inherited_proxy_listener()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        ProcessStartInfo start;
        lock (EnvironmentMutationLock)
        {
            var names = new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" };
            var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var name in names)
                {
                    Environment.SetEnvironmentVariable(name, $"http://127.0.0.1:{port}");
                }

                var powerShell = FindRealPowerShell();
                start = PowerShellProcessRunner.CreateStartInfo(
                    powerShell,
                    Path.GetDirectoryName(powerShell)!,
                    Path.GetFullPath(Path.GetTempPath()));
            }
            finally
            {
                foreach (var item in previous)
                {
                    Environment.SetEnvironmentVariable(item.Key, item.Value);
                }
            }
        }

        Assert.DoesNotContain(start.Environment.Keys, key => key.Contains("PROXY", StringComparison.OrdinalIgnoreCase));
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(
            "try { Invoke-WebRequest -Uri 'https://relaybridge.invalid' -TimeoutSec 1 | Out-Null } catch {}; exit 0");
        await process.StandardInput.DisposeAsync();
        var accept = listener.AcceptTcpClientAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        var contacted = await Task.WhenAny(accept, Task.Delay(500)) == accept;

        Assert.Equal(0, process.ExitCode);
        Assert.False(contacted, "The sanitized PowerShell child contacted the inherited audit proxy.");
    }

    [Fact]
    public async Task Startup_hook_probe_positive_control_executes_before_managed_worker_Main()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"RelayBridge.PositiveControl.{Guid.NewGuid():N}.marker");
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(AppContext.BaseDirectory, "RelayBridge.Setup.exe"),
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment["DOTNET_STARTUP_HOOKS"] = typeof(ProbeMarker).Assembly.Location;
            start.Environment["RELAYBRIDGE_STARTUP_HOOK_MARKER"] = marker;

            using var process = Process.Start(start)!;
            await process.StandardInput.DisposeAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(2, process.ExitCode);
            Assert.True(File.Exists(marker));
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Theory]
    [InlineData("Entra")]
    [InlineData("Exchange")]
    public async Task PowerShell_children_do_not_execute_an_inherited_startup_hook(string stage)
    {
        var marker = Path.Combine(Path.GetTempPath(), $"RelayBridge.{stage}.{Guid.NewGuid():N}.marker");
        var hook = typeof(ProbeMarker).Assembly.Location;
        var powerShell = FindRealPowerShell();
        var workingDirectory = Path.GetDirectoryName(powerShell)!;
        var scratch = Path.GetFullPath(Path.GetTempPath());
        var positive = new ProcessStartInfo
        {
            FileName = powerShell,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        positive.ArgumentList.Add("-NoLogo");
        positive.ArgumentList.Add("-NoProfile");
        positive.ArgumentList.Add("-Command");
        positive.ArgumentList.Add("-");
        positive.Environment["DOTNET_STARTUP_HOOKS"] = hook;
        positive.Environment["RELAYBRIDGE_STARTUP_HOOK_MARKER"] = marker;

        using (var positiveProcess = Process.Start(positive)!)
        {
            await positiveProcess.StandardInput.DisposeAsync();
            await positiveProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, positiveProcess.ExitCode);
        }
        Assert.True(File.Exists(marker), "The real PowerShell positive control did not execute the benign startup hook.");
        File.Delete(marker);

        var start = PowerShellProcessRunner.CreateStartInfo(
            powerShell,
            workingDirectory,
            scratch);
        start.Environment["RELAYBRIDGE_STARTUP_HOOK_MARKER"] = marker;
        Assert.False(start.Environment.ContainsKey("DOTNET_STARTUP_HOOKS"));

        using var process = Process.Start(start)!;
        await process.StandardInput.DisposeAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(0, process.ExitCode);
        Assert.False(File.Exists(marker), $"{stage} child executed a startup hook before Main.");
    }

    [Fact]
    public async Task Direct_worker_with_a_valid_frame_is_rejected_before_confirmation()
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "RelayBridge.Setup.exe");
        var launcher = Path.Combine(AppContext.BaseDirectory, "RelayBridge.SetupLauncher.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = worker,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(startInfo)!;
        var request = new NativeSetupStartRequest(
            NativeMicrosoftSetupProtocol.Version,
            Guid.NewGuid(),
            "scanner@example.com",
            "AQ==",
            "RelayBridge Test",
            false,
            AppContext.BaseDirectory,
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "tooling.json"),
            new string('A', 64),
            Path.GetFullPath(Path.GetTempPath()),
            Environment.ProcessId,
            Process.GetCurrentProcess().SessionId,
            WindowsIdentity.GetCurrent().User!.Value,
            launcher,
            new string('B', 64),
            Guid.NewGuid(),
            1,
            new string('C', 64),
            MicrosoftSetupMode.NewApplication);
        await NativeSetupPipeProtocol.WriteAsync(
            process.StandardInput.BaseStream,
            request,
            CancellationToken.None);
        await process.StandardInput.DisposeAsync();

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, process.ExitCode);
        Assert.Equal(string.Empty, await process.StandardOutput.ReadToEndAsync());
    }

    [Fact]
    public void Worker_origin_policy_requires_the_exact_parent_path_hash_session_and_sid()
    {
        var sid = WindowsIdentity.GetCurrent().User!;
        var hash = SHA256.HashData("approved-launcher"u8);
        var approved = new WorkerOriginFacts(
            200,
            100,
            100,
            7,
            7,
            7,
            Path.GetFullPath("RelayBridge.SetupLauncher.exe"),
            Path.GetFullPath("RelayBridge.SetupLauncher.exe"),
            sid,
            sid,
            sid,
            hash,
            hash);

        WorkerOriginVerifier.Validate(approved);
        Assert.Throws<InvalidDataException>(() => WorkerOriginVerifier.Validate(approved with { ParentProcessId = 101 }));
        Assert.Throws<InvalidDataException>(() => WorkerOriginVerifier.Validate(approved with { ParentPath = Path.GetFullPath("copied\\RelayBridge.SetupLauncher.exe") }));
        Assert.Throws<InvalidDataException>(() => WorkerOriginVerifier.Validate(approved with { ParentHash = SHA256.HashData("copied"u8) }));
    }

    [Fact]
    public async Task Managed_worker_starts_without_startup_hook_or_DOTNET_ROOT_injection()
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "RelayBridge.Setup.exe");
        var marker = Path.Combine(Path.GetTempPath(), $"RelayBridge.Worker.{Guid.NewGuid():N}.marker");
        var start = SetupLauncher.CreateWorkerStartInfo(worker, Path.GetFullPath(Path.GetTempPath()));
        start.Environment["RELAYBRIDGE_STARTUP_HOOK_MARKER"] = marker;
        Assert.False(start.Environment.ContainsKey("DOTNET_STARTUP_HOOKS"));
        Assert.DoesNotContain(
            start.Environment.Keys,
            key => key.StartsWith("DOTNET_ROOT", StringComparison.OrdinalIgnoreCase));

        using var process = Process.Start(start)!;
        await process.StandardInput.DisposeAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, process.ExitCode);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Direct_managed_worker_cannot_connect_to_the_privileged_Host_pipe()
    {
        await using var server = new NamedPipeServerStream(
            NativeMicrosoftSetupProtocol.BootstrapPipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        var waiting = server.WaitForConnectionAsync(wait.Token);
        var start = SetupLauncher.CreateWorkerStartInfo(
            Path.Combine(AppContext.BaseDirectory, "RelayBridge.Setup.exe"),
            Path.GetFullPath(Path.GetTempPath()));
        using var process = Process.Start(start)!;
        await process.StandardInput.DisposeAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(server.IsConnected);
        Assert.Equal(2, process.ExitCode);
    }

    [Fact]
    public async Task Published_NativeAot_launcher_ignores_managed_startup_and_runtime_injection()
    {
        var repository = FindRepositoryRoot();
        var publishDirectory = Path.Combine(
            Path.GetTempPath(),
            "RelayBridge.NativeAotTest",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(publishDirectory);
        try
        {
            var dotnet = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet",
                "dotnet.exe");
            var publish = new ProcessStartInfo
            {
                FileName = dotnet,
                WorkingDirectory = repository,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            publish.ArgumentList.Add("publish");
            publish.ArgumentList.Add(Path.Combine(
                repository,
                "src",
                "RelayBridge.SetupLauncher",
                "RelayBridge.SetupLauncher.csproj"));
            publish.ArgumentList.Add("-c");
            publish.ArgumentList.Add("Release");
            publish.ArgumentList.Add("-r");
            publish.ArgumentList.Add("win-x64");
            publish.ArgumentList.Add("--no-restore");
            publish.ArgumentList.Add("-o");
            publish.ArgumentList.Add(publishDirectory);
            using (var publisher = Process.Start(publish)!)
            {
                var stdout = publisher.StandardOutput.ReadToEndAsync();
                var stderr = publisher.StandardError.ReadToEndAsync();
                await publisher.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
                Assert.True(
                    publisher.ExitCode == 0,
                    $"NativeAOT publish failed. {await stdout} {await stderr}");
            }

            var launcher = Path.Combine(publishDirectory, "RelayBridge.SetupLauncher.exe");
            Assert.True(File.Exists(launcher));
            Assert.DoesNotContain(
                Directory.EnumerateFiles(publishDirectory),
                path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase));

            await using var server = new NamedPipeServerStream(
                NativeMicrosoftSetupProtocol.BootstrapPipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            var accepting = server.WaitForConnectionAsync();
            var marker = Path.Combine(publishDirectory, "startup-hook.marker");
            var start = new ProcessStartInfo
            {
                FileName = launcher,
                WorkingDirectory = publishDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.Environment["DOTNET_STARTUP_HOOKS"] = typeof(ProbeMarker).Assembly.Location;
            start.Environment["RELAYBRIDGE_STARTUP_HOOK_MARKER"] = marker;
            start.Environment["DOTNET_ROOT"] = Path.Combine(publishDirectory, "untrusted-runtime");
            start.Environment["DOTNET_ROOT_X64"] = Path.Combine(publishDirectory, "untrusted-runtime-x64");
            start.Environment["DOTNET_ADDITIONAL_DEPS"] = Path.Combine(publishDirectory, "untrusted-deps.json");
            start.Environment["CORECLR_ENABLE_PROFILING"] = "1";
            start.Environment["CORECLR_PROFILER"] = "{11111111-1111-1111-1111-111111111111}";
            start.Environment["CORECLR_PROFILER_PATH"] = Path.Combine(publishDirectory, "untrusted-profiler.dll");

            using var process = Process.Start(start)!;
            await accepting.WaitAsync(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(2, process.ExitCode);
            Assert.True(server.IsConnected);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            if (Directory.Exists(publishDirectory))
            {
                Directory.Delete(publishDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void Structured_result_rejects_noise_stderr_duplicate_markers_and_token_fields()
    {
        var valid = new EntraSetupResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0);
        var line = "RELAYBRIDGE_RESULT:" + JsonSerializer.Serialize(valid);
        Assert.Equal(valid, SetupOrchestrator.ParseResult<EntraSetupResult>(new(0, line, string.Empty)));

        Assert.Throws<InvalidDataException>(() =>
            SetupOrchestrator.ParseResult<EntraSetupResult>(new(0, "noise\n" + line, string.Empty)));
        Assert.Throws<InvalidDataException>(() =>
            SetupOrchestrator.ParseResult<EntraSetupResult>(new(0, line + "\n" + line, string.Empty)));
        Assert.Throws<InvalidDataException>(() =>
            SetupOrchestrator.ParseResult<EntraSetupResult>(new(0, line, "unexpected")));
        Assert.Throws<InvalidDataException>(() =>
            SetupOrchestrator.ParseResult<EntraSetupResult>(new(
                0,
                line[..^1] + ",\"AccessToken\":\"secret-token\"}",
                string.Empty)));
    }

    [Theory]
    [InlineData("RELAYBRIDGE_CANCELLED", NativeSetupFailureCategory.Cancelled)]
    [InlineData("RELAYBRIDGE_CA", NativeSetupFailureCategory.ConditionalAccess)]
    [InlineData("RELAYBRIDGE_PERMISSION", NativeSetupFailureCategory.InsufficientPermission)]
    [InlineData("RELAYBRIDGE_CONFLICT", NativeSetupFailureCategory.Conflict)]
    [InlineData("RELAYBRIDGE_EXCHANGE_ASSIGNMENT_CONFLICT", NativeSetupFailureCategory.Conflict)]
    [InlineData("RELAYBRIDGE_TOOL_INTEGRITY", NativeSetupFailureCategory.ToolIntegrity)]
    [InlineData("arbitrary authentication error", NativeSetupFailureCategory.MicrosoftService)]
    public void Only_recognized_failures_receive_special_classification(
        string stderr,
        NativeSetupFailureCategory expected)
    {
        var result = ProvisioningException.FromPowerShellFailure(stderr);
        Assert.Equal(expected, result.Category);
    }

    [Fact]
    public void Unexpected_Exchange_assignments_get_the_required_plain_language_failure()
    {
        Assert.Equal(
            "The RelayBridge Microsoft application has additional Exchange permissions that RelayBridge did not create. Review or remove them before continuing.",
            NativeMicrosoftSetupServer.FailureMessage(
                NativeSetupFailureCategory.Conflict,
                "UnexpectedExchangeAssignments",
                entraApplied: true));
    }

    [Fact]
    public void Missing_Exchange_WAM_console_gets_a_plain_language_compatibility_failure()
    {
        Assert.Equal(
            "Exchange administrator sign-in needs an interactive Windows desktop. Return to RelayBridge from the signed-in Windows user and try again.",
            NativeMicrosoftSetupServer.FailureMessage(
                NativeSetupFailureCategory.MicrosoftService,
                "ExchangeWamConsoleUnavailable",
                entraApplied: true));
    }

    private static MemoryStream Framed(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var bytes = new byte[payload.Length + sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, payload.Length);
        payload.CopyTo(bytes.AsSpan(sizeof(int)));
        return new MemoryStream(bytes);
    }

    private static async Task<NativeMicrosoftSetupRuntime> RunContainedConnectionFailureAsync(Exception exception)
    {
        var pipeName = $"relaybridge-test-{Guid.NewGuid():N}";
        var options = new NativeMicrosoftSetupOptions { Enabled = true };
        var runtime = new NativeMicrosoftSetupRuntime(options, TimeProvider.System);
        using var stop = new CancellationTokenSource();
        var calls = 0;
        await using var server = new NativeMicrosoftSetupServer(
            options,
            runtime,
            setup: null!,
            certificates: null!,
            new TestLogger<NativeMicrosoftSetupServer>(),
            () => Interlocked.Increment(ref calls) == 1
                ? new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous)
                : StopListener(stop),
            TimeSpan.Zero,
            connectionHandler: (_, _) => Task.FromException(exception));

        var run = server.RunAsync(stop.Token);
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(5_000);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, calls);
        return runtime;
    }

    private static NamedPipeServerStream StopListener(CancellationTokenSource stop)
    {
        stop.Cancel();
        throw new OperationCanceledException(stop.Token);
    }

    [Fact]
    public async Task Entra_key_credential_serializes_the_public_certificate_and_hash_as_base64_strings()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=RelayBridge Entra serialization test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        var publicCertificate = certificate.Export(X509ContentType.Cert);
        var publicCertificateBase64 = Convert.ToBase64String(publicCertificate);
        var script = $$"""
            $ErrorActionPreference='Stop'
            {{ProvisioningScripts.EntraApplicationPolicy}}
            $bytes=[Convert]::FromBase64String('{{publicCertificateBase64}}')
            $certificate=[Security.Cryptography.X509Certificates.X509Certificate2]::new($bytes)
            $credential=New-RelayBridgeEntraKeyCredential $certificate
            if ($credential.CustomKeyIdentifier -isnot [string] -or $credential.Key -isnot [string]) { throw 'not string' }
            if ($credential.CustomKeyIdentifier -ne [Convert]::ToBase64String($certificate.GetCertHash())) { throw 'hash mismatch' }
            if ($credential.Key -ne [Convert]::ToBase64String($certificate.RawData)) { throw 'certificate mismatch' }
            if ($credential.Type -ne 'AsymmetricX509Cert' -or $credential.Usage -ne 'Verify') { throw 'shape mismatch' }
            [Console]::Out.Write('PASS')
            """;

        var result = await RunLocalPowerShellAsync(script);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Equal("PASS", result.StandardOutput);
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("permissions", false)]
    [InlineData("missing-key", false)]
    [InlineData("missing-key-property", false)]
    [InlineData("extra-key", false)]
    [InlineData("password", false)]
    [InlineData("audience", false)]
    public async Task Raw_Graph_Entra_application_policy_executes_fail_closed(string scenario, bool succeeds)
    {
        var mutation = scenario switch
        {
            "valid" => string.Empty,
            "permissions" => "$raw.requiredResourceAccess=@([pscustomobject]@{resourceAppId=[Guid]::NewGuid()})",
            "missing-key" => "$raw.keyCredentials=@()",
            "missing-key-property" => "$raw.PSObject.Properties.Remove('keyCredentials')",
            "extra-key" => "$raw.keyCredentials=@($key,$otherKey)",
            "password" => "$raw.passwordCredentials=@([pscustomobject]@{displayName='secret'})",
            "audience" => "$raw.signInAudience='AzureADMultipleOrgs'",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var applicationObjectId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var script = $$"""
            $ErrorActionPreference='Stop'
            {{ProvisioningScripts.EntraApplicationPolicy}}
            $key=[pscustomobject]@{customKeyIdentifier=[Convert]::ToBase64String([byte[]](0xAA,0xBB));type='AsymmetricX509Cert';usage='Verify'}
            $otherKey=[pscustomobject]@{customKeyIdentifier=[Convert]::ToBase64String([byte[]](0xCC,0xDD));type='AsymmetricX509Cert';usage='Verify'}
            $raw=[pscustomobject]@{
                id=[Guid]'{{applicationObjectId:D}}'
                appId=[Guid]'{{clientId:D}}'
                requiredResourceAccess=@()
                keyCredentials=@($key)
                passwordCredentials=@()
                signInAudience='AzureADMyOrg'
            }
            {{mutation}}
            Assert-RelayBridgeRawEntraApplication -Application $raw -ExpectedApplicationObjectId ([Guid]'{{applicationObjectId:D}}') -ExpectedClientId ([Guid]'{{clientId:D}}') -ExpectedThumbprint 'AABB'
            [Console]::Out.Write('PASS')
            """;

        var result = await RunLocalPowerShellAsync(script);

        Assert.True(succeeds == (result.ExitCode == 0), result.StandardError);
        Assert.Equal(succeeds ? "PASS" : string.Empty, result.StandardOutput);
    }

    [Theory]
    [InlineData("$null")]
    [InlineData("@([pscustomobject]@{})")]
    public async Task Entra_wrapper_permission_projection_does_not_influence_raw_Graph_verification(
        string wrapperValue)
    {
        var applicationObjectId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var script = $$"""
            $ErrorActionPreference='Stop'
            Set-StrictMode -Version Latest
            {{ProvisioningScripts.EntraApplicationPolicy}}
            $wrapper=[pscustomobject]@{RequiredResourceAccess={{wrapperValue}}}
            $key=[pscustomobject]@{customKeyIdentifier=[Convert]::ToBase64String([byte[]](0xAA,0xBB));type='AsymmetricX509Cert';usage='Verify'}
            $raw=[pscustomobject]@{id=[Guid]'{{applicationObjectId:D}}';appId=[Guid]'{{clientId:D}}';requiredResourceAccess=@();keyCredentials=@($key);passwordCredentials=@();signInAudience='AzureADMyOrg'}
            Assert-RelayBridgeRawEntraApplication -Application $raw -ExpectedApplicationObjectId ([Guid]'{{applicationObjectId:D}}') -ExpectedClientId ([Guid]'{{clientId:D}}') -ExpectedThumbprint 'AABB'
            [Console]::Out.Write('PASS')
            """;

        var result = await RunLocalPowerShellAsync(script);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Equal("PASS", result.StandardOutput);
    }

    [Theory]
    [InlineData("$members=@($sender);$actual=$expectedFilter;$assignments=@($assignment)", true)]
    [InlineData("$members=@($sender,$other);$actual=$expectedFilter;$assignments=@($assignment)", false)]
    [InlineData("$members=@($sender);$actual=$expectedFilter + \" -or Alias -like '*'\";$assignments=@($assignment)", false)]
    [InlineData("$members=@($sender);$actual=\"MemberOfGroup -eq 'CN=Other'\";$assignments=@($assignment)", false)]
    [InlineData("$members=@($sender);$actual=$expectedFilter;$assignments=@($assignment,$broad)", false)]
    [InlineData("$members=@($sender);$actual=$expectedFilter;$assignments=@($wrongScope)", false)]
    public async Task Exchange_scope_policy_executes_fail_closed(string arrangement, bool succeeds)
    {
        var script = $$"""
            $ErrorActionPreference='Stop'
            {{ProvisioningScripts.ExchangeScopePolicy}}
            $sender=[pscustomobject]@{PrimarySmtpAddress='scanner@example.com'}
            $other=[pscustomobject]@{PrimarySmtpAddress='other@example.com'}
            $expectedFilter="MemberOfGroup -eq 'CN=RelayBridge'"
            $assignment=[pscustomobject]@{Name='RelayBridge SMTP SendAs';Role='Application SMTP.SendAsApp';RoleAssigneeType='ServicePrincipal';CustomResourceScope='RelayBridge Scope'}
            $broad=[pscustomobject]@{Name='Broad';Role='Application Mail.Read';RoleAssigneeType='ServicePrincipal';CustomResourceScope='Organization'}
            $wrongScope=[pscustomobject]@{Name='RelayBridge SMTP SendAs';Role='Application SMTP.SendAsApp';RoleAssigneeType='ServicePrincipal';CustomResourceScope='Other Scope'}
            {{arrangement}}
            Assert-RelayBridgeExchangeScope -Members $members -SenderAddress 'scanner@example.com' -ActualFilter $actual -ExpectedFilter $expectedFilter -Assignments $assignments -ExpectedAssignmentName 'RelayBridge SMTP SendAs' -ExpectedScopeName 'RelayBridge Scope' -ExpectedRoleName 'Application SMTP.SendAsApp'
            [Console]::Out.Write('PASS')
            """;

        var result = await RunLocalPowerShellAsync(script);

        Assert.Equal(succeeds, result.ExitCode == 0);
        Assert.Equal(succeeds ? "PASS" : string.Empty, result.StandardOutput);
    }

    [Theory]
    [InlineData("expected", true)]
    [InlineData("extra-scoped", false)]
    [InlineData("extra-unscoped", false)]
    [InlineData("different-role", false)]
    [InlineData("custom-role", false)]
    [InlineData("other-app", false)]
    [InlineData("wrong-scope", false)]
    [InlineData("missing-after-create", false)]
    public async Task Production_Exchange_script_discovers_all_assignments_by_exact_assignee(
        string scenario,
        bool succeeds)
    {
        using var fixture = FakeExchangeModuleFixture.Create();
        var clientId = Guid.NewGuid();
        var servicePrincipalId = Guid.NewGuid();
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            ClientId = clientId,
            ServicePrincipalObjectId = servicePrincipalId,
            SenderMailbox = "scanner@example.com",
        }));
        var tooling = CreateVerifiedTooling() with
        {
            ExchangeOnlineModulePath = fixture.ManifestPath,
            ExchangeOnlineModuleVersion = "3.9.2",
        };
        var scratch = Path.Combine(fixture.Root, "scratch");
        Directory.CreateDirectory(scratch);

        var result = await RunLocalPowerShellAsync(
            ProvisioningScripts.CreateExchangeScript(
                tooling,
                payload,
                scratch),
            new Dictionary<string, string>
            {
                ["RB_SCENARIO"] = scenario,
                ["RB_CLIENT_ID"] = clientId.ToString("D"),
                ["RB_SP_ID"] = servicePrincipalId.ToString("D"),
                ["RB_QUERY_LOG"] = fixture.QueryLogPath,
                ["RB_POISON_MODULE_PATH"] = Path.Combine(fixture.Root, "poison-modules"),
            });

        Assert.True(
            succeeds == (result.ExitCode == 0),
            $"ExitCode={result.ExitCode}; stdout={result.StandardOutput}; stderr={result.StandardError}");
        Assert.True(
            File.Exists(fixture.QueryLogPath),
            $"The production assignment query did not run. stderr={result.StandardError}");
        Assert.Equal(
            $"RoleAssignee:{servicePrincipalId:D}",
            File.ReadAllText(fixture.QueryLogPath),
            ignoreCase: true);
        if (succeeds)
        {
            Assert.StartsWith("RELAYBRIDGE_RESULT:", result.StandardOutput, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("RELAYBRIDGE_EXCHANGE_ASSIGNMENT_CONFLICT", result.StandardError, StringComparison.Ordinal);
        }

        if (succeeds)
        {
            var outsideQueryLog = fixture.QueryLogPath + ".outside";
            var outsideResult = await RunLocalPowerShellAsync(
                ProvisioningScripts.CreateExchangeScript(tooling, payload, scratch),
                new Dictionary<string, string>
                {
                    ["RB_SCENARIO"] = "outside-tmp-module",
                    ["RB_CLIENT_ID"] = clientId.ToString("D"),
                    ["RB_SP_ID"] = servicePrincipalId.ToString("D"),
                    ["RB_QUERY_LOG"] = outsideQueryLog,
                    ["RB_OUTSIDE_TMP"] = Path.Combine(fixture.Root, "outside"),
                    ["RB_POISON_MODULE_PATH"] = Path.Combine(fixture.Root, "poison-modules"),
                });

            Assert.NotEqual(0, outsideResult.ExitCode);
            Assert.Equal(string.Empty, outsideResult.StandardOutput);
            Assert.Contains(
                "RELAYBRIDGE_TOOL_INTEGRITY",
                outsideResult.StandardError,
                StringComparison.Ordinal);
            Assert.False(File.Exists(outsideQueryLog));
        }
    }

    [Fact]
    public void Tooling_is_revalidated_and_rejects_post_verification_tamper_or_writable_root()
    {
        using var fixture = ToolingFixture.Create(protectRoot: true);
        _ = ToolingIntegrityVerifier.Verify(
            fixture.InstallationRoot,
            fixture.Root,
            fixture.ManifestPath,
            fixture.ManifestHash,
            BypassPathTrust);

        var writable = new DirectorySecurity();
        writable.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        writable.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(fixture.Root), writable);
        File.AppendAllText(fixture.PowerShellPath, "post-verification-tamper");

        var exception = Assert.Throws<ToolIntegrityException>(() =>
            ToolingIntegrityVerifier.Verify(fixture.InstallationRoot, fixture.Root, fixture.ManifestPath, fixture.ManifestHash));
        Assert.Equal(
            "RelayBridge's Microsoft setup tools are not installed securely. Repair the RelayBridge installation.",
            exception.Message);
    }

    [Fact]
    public void Tooling_verification_rejects_a_user_writable_tree_even_when_hashes_match()
    {
        using var fixture = ToolingFixture.Create();

        Assert.Throws<ToolIntegrityException>(() =>
            ToolingIntegrityVerifier.Verify(fixture.InstallationRoot, fixture.Root, fixture.ManifestPath, fixture.ManifestHash));
    }

    [Fact]
    public void Trusted_path_policy_accepts_only_trusted_owners_and_read_only_interactive_access()
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var user = WindowsIdentity.GetCurrent().User!;

        TrustedWindowsPathVerifier.VerifySecurityDescriptor(CreateSecurity(system, user, FileSystemRights.ReadAndExecute));
        TrustedWindowsPathVerifier.VerifySecurityDescriptor(CreateSecurity(administrators, user, FileSystemRights.ReadAndExecute));

        Assert.Throws<TrustedWindowsPathException>(() =>
            TrustedWindowsPathVerifier.VerifySecurityDescriptor(CreateSecurity(user, user, FileSystemRights.ReadAndExecute)));
    }

    [Theory]
    [InlineData(WellKnownSidType.BuiltinUsersSid, FileSystemRights.WriteData)]
    [InlineData(WellKnownSidType.AuthenticatedUserSid, FileSystemRights.Modify)]
    [InlineData(WellKnownSidType.BuiltinUsersSid, FileSystemRights.DeleteSubdirectoriesAndFiles)]
    public void Trusted_path_policy_rejects_unprivileged_mutation_and_parent_replacement_rights(
        WellKnownSidType sidType,
        FileSystemRights rights)
    {
        var security = CreateSecurity(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            new SecurityIdentifier(sidType, null),
            rights);

        Assert.Throws<TrustedWindowsPathException>(() =>
            TrustedWindowsPathVerifier.VerifySecurityDescriptor(security));
    }

    [Fact]
    public void Provisioning_scratch_acl_allows_only_SYSTEM_Administrators_and_the_exact_interactive_user()
    {
        var interactive = WindowsIdentity.GetCurrent().User!;
        var approved = ProvisioningScratchDirectory.CreateSessionSecurity(interactive);

        TrustedWindowsPathVerifier.VerifyScratchSecurityDescriptor(
            approved,
            interactive,
            requireInteractiveWrite: true);

        var otherUser = new SecurityIdentifier("S-1-5-21-1000-1000-1000-4242");
        approved.AddAccessRule(new FileSystemAccessRule(
            otherUser,
            FileSystemRights.ReadAndExecute,
            AccessControlType.Allow));
        Assert.Throws<TrustedWindowsPathException>(() =>
            TrustedWindowsPathVerifier.VerifyScratchSecurityDescriptor(
                approved,
                interactive,
                requireInteractiveWrite: true));
    }

    [Theory]
    [InlineData(WellKnownSidType.BuiltinUsersSid, FileSystemRights.WriteData)]
    [InlineData(WellKnownSidType.AuthenticatedUserSid, FileSystemRights.FullControl)]
    public void Provisioning_scratch_acl_rejects_broad_mutation(
        WellKnownSidType sidType,
        FileSystemRights rights)
    {
        var interactive = WindowsIdentity.GetCurrent().User!;
        var security = ProvisioningScratchDirectory.CreateSessionSecurity(interactive);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(sidType, null),
            rights,
            AccessControlType.Allow));

        Assert.Throws<TrustedWindowsPathException>(() =>
            TrustedWindowsPathVerifier.VerifyScratchSecurityDescriptor(
                security,
                interactive,
                requireInteractiveWrite: true));
    }

    [Fact]
    public void Provisioning_scratch_acl_rejects_interactive_change_permissions_and_untrusted_owner()
    {
        var interactive = WindowsIdentity.GetCurrent().User!;
        var security = ProvisioningScratchDirectory.CreateSessionSecurity(interactive);
        security.AddAccessRule(new FileSystemAccessRule(
            interactive,
            FileSystemRights.ChangePermissions,
            AccessControlType.Allow));
        Assert.Throws<TrustedWindowsPathException>(() =>
            TrustedWindowsPathVerifier.VerifyScratchSecurityDescriptor(
                security,
                interactive,
                requireInteractiveWrite: true));

        var untrustedOwner = ProvisioningScratchDirectory.CreateSessionSecurity(interactive);
        untrustedOwner.SetOwner(interactive);
        Assert.Throws<TrustedWindowsPathException>(() =>
            TrustedWindowsPathVerifier.VerifyScratchSecurityDescriptor(
                untrustedOwner,
                interactive,
                requireInteractiveWrite: true));
    }

    [Fact]
    public void Provisioning_scratch_parent_replacement_rights_and_invalid_session_names_fail_closed()
    {
        var parent = CreateSecurity(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.DeleteSubdirectoriesAndFiles);
        Assert.Throws<TrustedWindowsPathException>(() =>
            TrustedWindowsPathVerifier.VerifyNoUntrustedDeleteChild(parent));

        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RelayBridge", "SetupScratch"));
        var session = Guid.NewGuid();
        Assert.Equal(
            Path.Combine(root, "session-" + session.ToString("N")),
            ProvisioningScratchDirectory.ResolveSessionPath(root, session));
        Assert.False(ProvisioningScratchDirectory.IsSessionName("session-not-a-guid"));
        Assert.Throws<TrustedWindowsPathException>(() =>
            ProvisioningScratchDirectory.ResolveSessionPath(root, Guid.Empty));
    }

    [Fact]
    public void Provisioning_scratch_cleanup_requires_containment_expected_name_and_no_reparse_points()
    {
        var interactive = WindowsIdentity.GetCurrent().User!;
        var expectedSecurity = ProvisioningScratchDirectory.CreateSessionSecurity(interactive);
        var root = Path.Combine(Path.GetTempPath(), "RelayBridge.ScratchCleanup", Guid.NewGuid().ToString("N"));
        var valid = Path.Combine(root, "session-" + Guid.NewGuid().ToString("N"));
        var invalid = Path.Combine(root, "unexpected");
        Directory.CreateDirectory(valid);
        Directory.CreateDirectory(invalid);
        File.WriteAllText(Path.Combine(valid, "generated-module.psm1"), "safe-test-content");
        File.WriteAllText(Path.Combine(valid, ".relaybridge-session.lock"), interactive.Value);
        try
        {
            Assert.False(ProvisioningScratchDirectory.TryDeleteTree(
                root,
                invalid,
                _ => { },
                requireInactiveSessionLock: false,
                expectedInteractiveSid: interactive,
                sessionSecurityReader: _ => expectedSecurity));
            Assert.True(Directory.Exists(invalid));

            var unexpectedOwner = ProvisioningScratchDirectory.CreateSessionSecurity(interactive);
            unexpectedOwner.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            Assert.False(ProvisioningScratchDirectory.TryDeleteTree(
                root,
                valid,
                _ => { },
                requireInactiveSessionLock: false,
                expectedInteractiveSid: interactive,
                sessionSecurityReader: _ => unexpectedOwner));
            Assert.True(Directory.Exists(valid));

            var broadened = ProvisioningScratchDirectory.CreateSessionSecurity(interactive);
            broadened.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.WriteData,
                AccessControlType.Allow));
            Assert.False(ProvisioningScratchDirectory.TryDeleteTree(
                root,
                valid,
                _ => { },
                requireInactiveSessionLock: false,
                expectedInteractiveSid: interactive,
                sessionSecurityReader: _ => broadened));
            Assert.True(Directory.Exists(valid));

            Assert.True(ProvisioningScratchDirectory.TryDeleteTree(
                root,
                valid,
                _ => { },
                expectedInteractiveSid: interactive,
                sessionSecurityReader: _ => expectedSecurity));
            Assert.False(Directory.Exists(valid));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Provisioning_scratch_cleanup_refuses_a_reparse_child()
    {
        var interactive = WindowsIdentity.GetCurrent().User!;
        var expectedSecurity = ProvisioningScratchDirectory.CreateSessionSecurity(interactive);
        var root = Path.Combine(Path.GetTempPath(), "RelayBridge.ScratchReparse", Guid.NewGuid().ToString("N"));
        var session = Path.Combine(root, "session-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target");
        var junction = Path.Combine(session, "tmpEXO_test");
        Directory.CreateDirectory(session);
        Directory.CreateDirectory(target);
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("mklink");
            start.ArgumentList.Add("/J");
            start.ArgumentList.Add(junction);
            start.ArgumentList.Add(target);
            using var process = Process.Start(start)!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);

            Assert.False(ProvisioningScratchDirectory.TryDeleteTree(
                root,
                session,
                _ => { },
                requireInactiveSessionLock: false,
                expectedInteractiveSid: interactive,
                sessionSecurityReader: _ => expectedSecurity));
            Assert.True(Directory.Exists(target));
        }
        finally
        {
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction);
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("S-1-5-32-545", "0x10000000")]
    [InlineData("S-1-5-11", "0x40000000")]
    public void Trusted_path_policy_rejects_raw_generic_mutation_masks(string sid, string accessMask)
    {
        var security = CreateRawSecurity($"(A;;{accessMask};;;{sid})");

        Assert.Throws<TrustedWindowsPathException>(() =>
            TrustedWindowsPathVerifier.VerifySecurityDescriptor(security));
    }

    [Theory]
    [InlineData("0x10000000")]
    [InlineData("0x40000000")]
    public void Trusted_path_policy_rejects_current_user_raw_generic_mutation_masks(string accessMask)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var security = CreateRawSecurity($"(A;;{accessMask};;;{user.Value})");

        Assert.Throws<TrustedWindowsPathException>(() =>
            TrustedWindowsPathVerifier.VerifySecurityDescriptor(security));
    }

    [Fact]
    public void Trusted_path_policy_rejects_effective_inherited_generic_write()
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var security = CreateRawSecurity($"(A;ID;0x40000000;;;{user.Value})");

        Assert.Throws<TrustedWindowsPathException>(() =>
            TrustedWindowsPathVerifier.VerifySecurityDescriptor(security));
    }

    [Theory]
    [InlineData("0x80000000")]
    [InlineData("0x20000000")]
    public void Trusted_path_policy_allows_raw_generic_read_or_execute(string accessMask)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var security = CreateRawSecurity($"(A;;{accessMask};;;{user.Value})");

        TrustedWindowsPathVerifier.VerifySecurityDescriptor(security);
    }

    [Fact]
    public void Trusted_path_policy_ignores_inherit_only_generic_write_on_the_current_object()
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var security = CreateRawSecurity($"(A;OIIO;0x40000000;;;{user.Value})");

        TrustedWindowsPathVerifier.VerifySecurityDescriptor(security);
    }

    [Fact]
    public void Trusted_path_policy_does_not_treat_a_deny_ace_as_a_mutation_grant()
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var security = CreateRawSecurity($"(D;;0x10000000;;;{user.Value})");

        TrustedWindowsPathVerifier.VerifySecurityDescriptor(security);
    }

    [Fact]
    public void Helper_execution_closure_accepts_only_the_exact_release_manifest()
    {
        using var fixture = HelperClosureFixture.Create();

        var result = fixture.Verify();

        Assert.Equal(fixture.LauncherHash, Convert.ToHexString(result.ExpectedLauncherHash));
    }

    [Theory]
    [InlineData("RelayBridge.SetupLauncher.exe")]
    [InlineData("RelayBridge.Setup.exe")]
    [InlineData("RelayBridge.Setup.dll")]
    [InlineData("RelayBridge.Core.dll")]
    [InlineData("RelayBridge.Setup.deps.json")]
    [InlineData("RelayBridge.Setup.runtimeconfig.json")]
    public void Helper_execution_closure_rejects_a_modified_sidecar(string relativePath)
    {
        using var fixture = HelperClosureFixture.Create();
        File.AppendAllText(Path.Combine(fixture.Root, relativePath), "tampered");

        Assert.Throws<TrustedWindowsPathException>(() => fixture.Verify());
    }

    [Fact]
    public void Helper_execution_closure_rejects_unapproved_extra_files()
    {
        using var fixture = HelperClosureFixture.Create();
        File.WriteAllText(Path.Combine(fixture.Root, "Injected.Dependency.dll"), "unapproved");

        Assert.Throws<TrustedWindowsPathException>(() => fixture.Verify());
    }

    [Fact]
    public void Helper_execution_closure_rejects_an_untrusted_file_hierarchy_even_when_hashes_match()
    {
        using var fixture = HelperClosureFixture.Create();

        Assert.Throws<TrustedWindowsPathException>(() => fixture.Verify((_, _, recursive) =>
        {
            Assert.True(recursive);
            throw new TrustedWindowsPathException();
        }));
    }

    [Fact]
    public void Trusted_path_policy_rejects_a_user_owned_module_below_a_trusted_root()
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var user = WindowsIdentity.GetCurrent().User!;
        var trustedRoot = CreateSecurity(administrators, user, FileSystemRights.ReadAndExecute);
        var userOwnedModule = CreateSecurity(user, user, FileSystemRights.ReadAndExecute);

        TrustedWindowsPathVerifier.VerifySecurityDescriptor(trustedRoot);
        Assert.Throws<TrustedWindowsPathException>(() =>
            TrustedWindowsPathVerifier.VerifySecurityDescriptor(userOwnedModule));
    }

    [Fact]
    public void Production_path_policy_rejects_a_helper_under_a_current_user_owned_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "RelayBridge.UntrustedHelper", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var helper = Path.Combine(root, "RelayBridge.Setup.exe");
        File.WriteAllText(helper, "not-an-executable");
        try
        {
            var exception = Assert.Throws<TrustedWindowsPathException>(() =>
                TrustedWindowsPathVerifier.VerifyInstallationTree(root, [helper], recursivelyVerifyDirectories: false));
            Assert.Equal(
                "RelayBridge's Microsoft setup tools are not installed securely. Repair the RelayBridge installation.",
                exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Trusted_path_policy_rejects_directory_junctions()
    {
        var root = Path.Combine(Path.GetTempPath(), "RelayBridge.Reparse", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target");
        var junction = Path.Combine(root, "junction");
        Directory.CreateDirectory(target);
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("mklink");
            start.ArgumentList.Add("/J");
            start.ArgumentList.Add(junction);
            start.ArgumentList.Add(target);
            using var process = Process.Start(start)!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);

            Assert.Throws<TrustedWindowsPathException>(() =>
                TrustedWindowsPathVerifier.VerifyNoReparsePoint(new DirectoryInfo(junction)));
        }
        finally
        {
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Real_Windows_system_executable_has_a_trusted_owner_and_non_user_writable_acl()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var security = FileSystemAclExtensions.GetAccessControl(
            new FileInfo(path),
            AccessControlSections.Owner | AccessControlSections.Access);

        TrustedWindowsPathVerifier.VerifySecurityDescriptor(security);
    }

    private static FileSecurity CreateSecurity(
        SecurityIdentifier owner,
        SecurityIdentifier subject,
        FileSystemRights rights)
    {
        var security = new FileSecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(subject, rights, AccessControlType.Allow));
        return security;
    }

    private static FileSecurity CreateRawSecurity(string additionalAce)
    {
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm(
            "O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)" + additionalAce,
            AccessControlSections.All);
        return security;
    }

    private sealed class PrefixThenStallStream : Stream
    {
        private readonly byte[] _prefix;
        private int _offset;

        internal PrefixThenStallStream(byte[] prefix) => _prefix = prefix;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_offset < _prefix.Length)
            {
                var count = Math.Min(buffer.Length, _prefix.Length - _offset);
                _prefix.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private static VerifiedTooling CreateVerifiedTooling()
    {
        var root = Path.GetFullPath("approved-tools");
        return new VerifiedTooling(
            Path.Combine(root, "pwsh.exe"),
            Path.Combine(root, "Microsoft.Graph.Authentication.psd1"),
            "2.25.0",
            Path.Combine(root, "Microsoft.Graph.Applications.psd1"),
            "2.25.0",
            Path.Combine(root, "Microsoft.Entra.Authentication.psd1"),
            "1.3.0",
            Path.Combine(root, "Microsoft.Entra.Applications.psd1"),
            "1.3.0",
            Path.Combine(root, "ExchangeOnlineManagement.psd1"),
            "3.9.2");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RelayBridge.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("The RelayBridge repository root is unavailable.");
    }

    private static string FindRealPowerShell()
    {
        var candidates = new List<string>
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "PowerShell",
                "7",
                "pwsh.exe"),
        };
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), "pwsh.exe")));
        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("A real PowerShell 7 executable is required for the startup-hook security test.");
    }

    private static async Task<ConsoleHostProbeResult> RunConsoleHostProbeAsync(string mode)
    {
        var probe = Path.Combine(AppContext.BaseDirectory, "RelayBridge.ConsoleHostProbe.exe");
        Assert.True(File.Exists(probe), "The compiled no-console hosting probe is unavailable.");

        var privatePowerShell = Path.Combine(
            FindRepositoryRoot(),
            ".local",
            "m51-harmless-wam-validation",
            "staging",
            "Tools",
            "PowerShell",
            "7.6.4",
            "pwsh.exe");
        var powerShell = File.Exists(privatePowerShell) ? privatePowerShell : FindRealPowerShell();
        var scratch = Path.Combine(Path.GetTempPath(), "RelayBridge.ConsoleHost", Guid.NewGuid().ToString("N"));
        var resultPath = Path.Combine(scratch, "result.json");
        Directory.CreateDirectory(scratch);
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = probe,
                WorkingDirectory = Path.GetDirectoryName(probe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add(powerShell);
            start.ArgumentList.Add(scratch);
            start.ArgumentList.Add(resultPath);
            start.ArgumentList.Add(mode);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var diagnostic = (await stdout) + (await stderr);
            Assert.True(File.Exists(resultPath), diagnostic);
            var result = JsonSerializer.Deserialize<ConsoleHostProbeResult>(await File.ReadAllTextAsync(resultPath))
                ?? throw new InvalidDataException("The console hosting probe returned no result.");
            Assert.Null(result.ErrorType);
            Assert.Equal(0, process.ExitCode);
            return result;
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static void AssertRuntimeInjectionEnvironmentIsAbsent(
        IDictionary<string, string?> environment)
    {
        foreach (var key in environment.Keys)
        {
            Assert.False(key.Equals("DOTNET_STARTUP_HOOKS", StringComparison.OrdinalIgnoreCase), key);
            Assert.False(key.StartsWith("DOTNET_ROOT", StringComparison.OrdinalIgnoreCase), key);
            Assert.False(key.Equals("DOTNET_ADDITIONAL_DEPS", StringComparison.OrdinalIgnoreCase), key);
            Assert.False(key.Equals("DOTNET_SHARED_STORE", StringComparison.OrdinalIgnoreCase), key);
            Assert.False(key.Equals("DOTNET_HOST_PATH", StringComparison.OrdinalIgnoreCase), key);
            Assert.False(key.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase), key);
            Assert.False(key.StartsWith("COR_", StringComparison.OrdinalIgnoreCase), key);
            Assert.False(key.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase), key);
            Assert.False(
                key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("DOTNET_EnableDiagnostics", StringComparison.OrdinalIgnoreCase),
                key);
        }
    }

    private static bool IsApprovedEnvironmentKey(string key)
    {
        return key.Equals("SystemRoot", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("WINDIR", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("SystemDrive", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("ComSpec", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("PATH", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("PATHEXT", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("USERPROFILE", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("APPDATA", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("LOCALAPPDATA", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("ProgramData", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("ALLUSERSPROFILE", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("TEMP", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("TMP", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("HOMEDRIVE", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("HOMEPATH", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("USERNAME", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("USERDOMAIN", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("ProgramFiles", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("ProgramFiles(x86)", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("ProgramW6432", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("CommonProgramFiles", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("CommonProgramFiles(x86)", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("CommonProgramW6432", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("DOTNET_EnableDiagnostics", StringComparison.OrdinalIgnoreCase);
    }

    private static void BypassPathTrust(string installationRoot, IEnumerable<string> paths, bool recursive)
    {
    }

    private sealed record ConsoleHostProbeResult(
        long InitialWindow,
        long FinalWindow,
        long? ChildWindow,
        string? StandardOutput,
        string? StandardError,
        string? ErrorType,
        bool? ChildExited);

    private sealed class FakeExchangeWamConsoleNative : IExchangeWamConsoleNative
    {
        internal IntPtr InitialWindow { get; init; }
        internal IntPtr WindowAfterAllocation { get; init; }
        internal bool AllocConsoleResult { get; init; }
        internal int AllocConsoleCalls { get; private set; }
        internal int FreeConsoleCalls { get; private set; }
        internal int GetConsoleProcessIdsCalls { get; private set; }
        internal string? Title { get; private set; }
        internal Queue<IReadOnlyList<uint>> ConsoleProcessIdSnapshots { get; } = new();
        private bool _allocated;

        public IntPtr GetConsoleWindow() => _allocated ? WindowAfterAllocation : InitialWindow;

        public bool AllocConsole()
        {
            AllocConsoleCalls++;
            _allocated = AllocConsoleResult;
            return AllocConsoleResult;
        }

        public bool FreeConsole()
        {
            FreeConsoleCalls++;
            _allocated = false;
            return true;
        }

        public bool SetConsoleTitle(string title)
        {
            Title = title;
            return true;
        }

        public IntPtr GetProcessWindowStation() => new(1);

        public IntPtr GetThreadDesktop(uint threadId) => new(1);

        public uint GetCurrentThreadId() => 1;

        public int GetCurrentSessionId() => 1;

        public IReadOnlyList<uint> GetConsoleProcessIds()
        {
            GetConsoleProcessIdsCalls++;
            return ConsoleProcessIdSnapshots.Count > 0
                ? ConsoleProcessIdSnapshots.Dequeue()
                : [(uint)Environment.ProcessId];
        }
    }

    private static async Task<PowerShellExecutionResult> RunLocalPowerShellAsync(
        string script,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var powerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Assert.True(File.Exists(powerShell), "Windows PowerShell is required for deterministic provisioning-policy tests.");
        var start = new ProcessStartInfo
        {
            FileName = powerShell,
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        if (environment is not null)
        {
            foreach (var item in environment)
            {
                start.Environment[item.Key] = item.Value;
            }
        }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new PowerShellExecutionResult(process.ExitCode, await stdout, await stderr);
    }

    private static async Task<PowerShellExecutionResult> RunPowerShell7ProductionScriptAsync(
        string script,
        string poisonModulePath,
        string poisonMarkerPath)
    {
        var powerShell = FindRealPowerShell();
        var scratch = Path.Combine(Path.GetTempPath(), "RelayBridge.EntraPreflight", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var prefix = $$"""
                $env:PSModulePath = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{Convert.ToBase64String(Encoding.UTF8.GetBytes(poisonModulePath))}}'))
                $env:RB_POISON_MARKER = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{Convert.ToBase64String(Encoding.UTF8.GetBytes(poisonMarkerPath))}}'))
                """;
            using var runner = new PowerShellProcessRunner();
            return await runner.RunAsync(
                powerShell,
                Path.GetDirectoryName(powerShell)!,
                scratch,
                prefix + Environment.NewLine + script,
                CancellationToken.None);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private sealed class PrivateEntraModuleFixture : IDisposable
    {
        private PrivateEntraModuleFixture(
            string root,
            VerifiedTooling tooling,
            string poisonModulePath,
            string poisonMarkerPath)
        {
            Root = root;
            Tooling = tooling;
            PoisonModulePath = poisonModulePath;
            PoisonMarkerPath = poisonMarkerPath;
        }

        internal string Root { get; }
        internal VerifiedTooling Tooling { get; }
        internal string PoisonModulePath { get; }
        internal string PoisonMarkerPath { get; }

        internal static PrivateEntraModuleFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "RelayBridge.PrivateEntra", Guid.NewGuid().ToString("N"));
            var approved = Path.Combine(root, "approved");
            var poison = Path.Combine(root, "poison");
            var marker = Path.Combine(root, "poison-loaded.txt");
            Directory.CreateDirectory(approved);
            Directory.CreateDirectory(poison);

            var graphAuthentication = CreateModule(
                approved,
                "Microsoft.Graph.Authentication",
                "2.25.0",
                ["Connect-MgGraph", "Invoke-MgGraphRequest"]);
            var graphApplications = CreateModule(
                approved,
                "Microsoft.Graph.Applications",
                "2.25.0",
                ["Get-MgApplication"],
                "Microsoft.Graph.Authentication",
                "2.25.0",
                requireExactDependency: true);
            var entraAuthentication = CreateModule(
                approved,
                "Microsoft.Entra.Authentication",
                "1.3.0",
                ["Connect-Entra"],
                "Microsoft.Graph.Authentication",
                "2.25.0",
                requireExactDependency: false);
            var entraApplications = CreateModule(
                approved,
                "Microsoft.Entra.Applications",
                "1.3.0",
                ["Get-EntraApplication"],
                "Microsoft.Graph.Applications",
                "2.25.0",
                requireExactDependency: false);

            CreatePoisonModule(poison, "Microsoft.Graph.Authentication", "2.30.0");
            CreatePoisonModule(poison, "Microsoft.Graph.Applications", "2.30.0");

            return new PrivateEntraModuleFixture(
                root,
                new VerifiedTooling(
                    FindRealPowerShell(),
                    graphAuthentication,
                    "2.25.0",
                    graphApplications,
                    "2.25.0",
                    entraAuthentication,
                    "1.3.0",
                    entraApplications,
                    "1.3.0",
                    Path.Combine(approved, "ExchangeOnlineManagement.psd1"),
                    "3.9.2"),
                poison,
                marker);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static string CreateModule(
            string root,
            string name,
            string version,
            IReadOnlyList<string> commands,
            string? dependencyName = null,
            string? dependencyVersion = null,
            bool requireExactDependency = false)
        {
            var directory = Path.Combine(root, name, version);
            Directory.CreateDirectory(directory);
            var manifestPath = Path.Combine(directory, $"{name}.psd1");
            var modulePath = Path.Combine(directory, $"{name}.psm1");
            var functions = string.Join(",", commands.Select(command => $"'{command}'"));
            var dependency = dependencyName is null
                ? "@()"
                : requireExactDependency
                    ? $"@(@{{ModuleName='{dependencyName}';RequiredVersion='{dependencyVersion}'}})"
                    : $"@(@{{ModuleName='{dependencyName}';ModuleVersion='{dependencyVersion}'}})";
            File.WriteAllText(
                manifestPath,
                $"@{{RootModule='{name}.psm1';ModuleVersion='{version}';GUID='{Guid.NewGuid():D}';FunctionsToExport=@({functions});RequiredModules={dependency}}}");
            File.WriteAllText(
                modulePath,
                string.Join(Environment.NewLine, commands.Select(command => $"function {command} {{ }}")));
            return manifestPath;
        }

        private static void CreatePoisonModule(string root, string name, string version)
        {
            var directory = Path.Combine(root, name, version);
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, $"{name}.psd1"),
                $"@{{RootModule='{name}.psm1';ModuleVersion='{version}';GUID='{Guid.NewGuid():D}'}}");
            File.WriteAllText(
                Path.Combine(directory, $"{name}.psm1"),
                "[IO.File]::WriteAllText($env:RB_POISON_MARKER, 'loaded')");
        }
    }

    private sealed class HelperClosureFixture : IDisposable
    {
        private HelperClosureFixture(
            string root,
            string launcherPath,
            string launcherHash,
            string workerPath,
            string manifestPath,
            string manifestHash)
        {
            Root = root;
            LauncherPath = launcherPath;
            LauncherHash = launcherHash;
            WorkerPath = workerPath;
            ManifestPath = manifestPath;
            ManifestHash = manifestHash;
        }

        internal string Root { get; }
        internal string LauncherPath { get; }
        internal string LauncherHash { get; }
        internal string WorkerPath { get; }
        internal string ManifestPath { get; }
        internal string ManifestHash { get; }

        internal static HelperClosureFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "RelayBridge.HelperClosure", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var entries = new[]
            {
                CreateFile(root, "RelayBridge.SetupLauncher.exe", "native-aot-launcher"),
                CreateFile(root, "RelayBridge.Setup.exe", "apphost"),
                CreateFile(root, "RelayBridge.Setup.dll", "setup-managed"),
                CreateFile(root, "RelayBridge.Core.dll", "core-managed"),
                CreateFile(root, "RelayBridge.Setup.deps.json", "{}"),
                CreateFile(root, "RelayBridge.Setup.runtimeconfig.json", "{}"),
            };
            var manifest = new HelperExecutionManifest(1, entries);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
            var manifestPath = Path.Combine(root, "helper-execution-manifest.json");
            File.WriteAllBytes(manifestPath, manifestBytes);
            return new HelperClosureFixture(
                root,
                Path.Combine(root, entries[0].RelativePath),
                entries[0].Sha256,
                Path.Combine(root, entries[1].RelativePath),
                manifestPath,
                Convert.ToHexString(SHA256.HashData(manifestBytes)));
        }

        internal VerifiedHelperExecutionClosure Verify(
            Action<string, IEnumerable<string>, bool>? pathTrustVerifier = null) =>
            HelperExecutionClosureVerifier.Verify(
                LauncherPath,
                WorkerPath,
                ManifestPath,
                ManifestHash,
                LauncherHash,
                pathTrustVerifier ?? BypassPathTrust);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static HelperExecutionFileEntry CreateFile(string root, string relativePath, string content)
        {
            var path = Path.Combine(root, relativePath);
            File.WriteAllText(path, content);
            return new HelperExecutionFileEntry(
                relativePath,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
    }

    private sealed class ToolingFixture : IDisposable
    {
        private ToolingFixture(
            string root,
            string manifestPath,
            string manifestHash,
            string powerShellPath,
            string graphAuthenticationModulePath,
            string graphApplicationsModulePath,
            string graphAuthenticationRuntimePath)
        {
            Root = root;
            ManifestPath = manifestPath;
            ManifestHash = manifestHash;
            PowerShellPath = powerShellPath;
            GraphAuthenticationModulePath = graphAuthenticationModulePath;
            GraphApplicationsModulePath = graphApplicationsModulePath;
            GraphAuthenticationRuntimePath = graphAuthenticationRuntimePath;
        }

        internal string Root { get; }
        internal string InstallationRoot => Directory.GetParent(Root)!.FullName;
        internal string ManifestPath { get; }
        internal string ManifestHash { get; }
        internal string PowerShellPath { get; }
        internal string GraphAuthenticationModulePath { get; }
        internal string GraphApplicationsModulePath { get; }
        internal string GraphAuthenticationRuntimePath { get; }

        internal static ToolingFixture Create(
            bool protectRoot = false,
            string graphAuthenticationVersion = "2.25.0",
            string graphApplicationsVersion = "2.25.0")
        {
            var parent = Path.Combine(Path.GetTempPath(), "RelayBridge.NativeSetupTests", Guid.NewGuid().ToString("N"));
            var root = Path.Combine(parent, "Tools");
            Directory.CreateDirectory(root);
            var entries = new[]
            {
                CreateFile(root, "PowerShell\\pwsh.exe", "pwsh"),
                CreateFile(root, $"Modules\\Microsoft.Graph.Authentication\\{graphAuthenticationVersion}\\Microsoft.Graph.Authentication.psd1", "graph-auth"),
                CreateFile(root, $"Modules\\Microsoft.Graph.Authentication\\{graphAuthenticationVersion}\\Dependencies\\Microsoft.Graph.Core.dll", "graph-auth-runtime"),
                CreateFile(root, $"Modules\\Microsoft.Graph.Applications\\{graphApplicationsVersion}\\Microsoft.Graph.Applications.psd1", "graph-apps"),
                CreateFile(root, $"Modules\\Microsoft.Graph.Applications\\{graphApplicationsVersion}\\bin\\Microsoft.Graph.Applications.private.dll", "graph-apps-runtime"),
                CreateFile(root, "Modules\\Microsoft.Entra.Authentication\\1.3.0\\Microsoft.Entra.Authentication.psd1", "entra-auth"),
                CreateFile(root, "Modules\\Microsoft.Entra.Applications\\1.3.0\\Microsoft.Entra.Applications.psd1", "entra-apps"),
                CreateFile(root, "Modules\\ExchangeOnlineManagement\\3.9.2\\ExchangeOnlineManagement.psd1", "exchange"),
            };
            var manifest = new ToolingManifest(
                2,
                entries[0].RelativePath,
                entries[1].RelativePath,
                graphAuthenticationVersion,
                entries[3].RelativePath,
                graphApplicationsVersion,
                entries[5].RelativePath,
                "1.3.0",
                entries[6].RelativePath,
                "1.3.0",
                entries[7].RelativePath,
                "3.9.2",
                entries);
            var manifestPath = Path.Combine(parent, "tooling-manifest.json");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
            File.WriteAllBytes(manifestPath, bytes);
            if (protectRoot)
            {
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    FileSystemRights.FullControl,
                    inheritance,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    FileSystemRights.FullControl,
                    inheritance,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(
                    WindowsIdentity.GetCurrent().User!,
                    FileSystemRights.ReadAndExecute,
                    inheritance,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(root), security);
                var manifestSecurity = new FileSecurity();
                manifestSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                manifestSecurity.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
                manifestSecurity.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
                manifestSecurity.AddAccessRule(new FileSystemAccessRule(
                    WindowsIdentity.GetCurrent().User!,
                    FileSystemRights.ReadAndExecute,
                    AccessControlType.Allow));
                FileSystemAclExtensions.SetAccessControl(new FileInfo(manifestPath), manifestSecurity);
            }
            return new ToolingFixture(
                root,
                manifestPath,
                Convert.ToHexString(SHA256.HashData(bytes)),
                Path.Combine(root, entries[0].RelativePath),
                Path.Combine(root, entries[1].RelativePath),
                Path.Combine(root, entries[3].RelativePath),
                Path.Combine(root, entries[2].RelativePath));
        }

        public void Dispose()
        {
            var parent = Directory.GetParent(Root)!.FullName;
            if (Directory.Exists(parent))
            {
                if (Directory.Exists(Root))
                {
                    var security = new DirectorySecurity();
                    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                    security.AddAccessRule(new FileSystemAccessRule(
                        WindowsIdentity.GetCurrent().User!,
                        FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        AccessControlType.Allow));
                    FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(Root), security);
                }
                if (File.Exists(ManifestPath))
                {
                    var security = new FileSecurity();
                    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                    security.AddAccessRule(new FileSystemAccessRule(
                        WindowsIdentity.GetCurrent().User!,
                        FileSystemRights.FullControl,
                        AccessControlType.Allow));
                    FileSystemAclExtensions.SetAccessControl(new FileInfo(ManifestPath), security);
                }
                Directory.Delete(parent, recursive: true);
            }
        }

        private static ToolingFileEntry CreateFile(string root, string relativePath, string content)
        {
            var path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return new ToolingFileEntry(
                relativePath,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
    }

    private sealed class FakeExchangeModuleFixture : IDisposable
    {
        private FakeExchangeModuleFixture(string root, string manifestPath, string queryLogPath)
        {
            Root = root;
            ManifestPath = manifestPath;
            QueryLogPath = queryLogPath;
        }

        internal string Root { get; }
        internal string ManifestPath { get; }
        internal string QueryLogPath { get; }

        internal static FakeExchangeModuleFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "RelayBridge.ExchangeMock", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var modulePath = Path.Combine(root, "ExchangeOnlineManagement.psm1");
            var manifestPath = Path.Combine(root, "ExchangeOnlineManagement.psd1");
            var queryLogPath = Path.Combine(root, "query.txt");
            File.WriteAllText(
                manifestPath,
                "@{ RootModule='ExchangeOnlineManagement.psm1'; ModuleVersion='3.9.2'; GUID='" +
                Guid.NewGuid().ToString("D") +
                "'; FunctionsToExport='*' }");
            File.WriteAllText(modulePath, FakeExchangeModuleSource);
            return new FakeExchangeModuleFixture(root, manifestPath, queryLogPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private const string FakeExchangeModuleSource = """
            function Connect-ExchangeOnline {
                [CmdletBinding()] param([switch]$ShowBanner,[string]$EXOModuleBasePath)
                if ([string]::IsNullOrWhiteSpace($EXOModuleBasePath)) { throw 'missing EXOModuleBasePath' }
                $moduleRoot = if ($env:RB_SCENARIO -eq 'outside-tmp-module') { $env:RB_OUTSIDE_TMP } else { $EXOModuleBasePath }
                $temporaryModule = Join-Path $moduleRoot 'tmpEXO_relaybridge_test'
                [IO.Directory]::CreateDirectory($temporaryModule) | Out-Null
                $temporaryModulePath = Join-Path $temporaryModule 'tmpEXO_relaybridge_test.psm1'
                [IO.File]::WriteAllText($temporaryModulePath, "function Get-RelayBridgeTemporaryModuleMarker { 'ok' }")
                Import-Module $temporaryModulePath -Force -Global -ErrorAction Stop
                $env:PSModulePath = $env:RB_POISON_MODULE_PATH
            }
            function Disconnect-ExchangeOnline { [CmdletBinding()] param([switch]$Confirm) }
            function Get-Recipient {
                [CmdletBinding()] param([string]$Identity)
                if (-not [string]::IsNullOrEmpty($env:PSModulePath)) { throw 'PSModulePath was not constrained after Connect-ExchangeOnline' }
                [pscustomobject]@{ Identity='sender-id'; PrimarySmtpAddress='scanner@example.com' }
            }
            function Get-ServicePrincipal {
                [CmdletBinding()] param([object]$Identity)
                if ($Identity -and [string]$Identity -eq 'other-principal') {
                    return [pscustomobject]@{ ObjectId=[Guid]::NewGuid(); AppId=[Guid]::NewGuid() }
                }
                [pscustomobject]@{ ObjectId=[Guid]$env:RB_SP_ID; AppId=[Guid]$env:RB_CLIENT_ID }
            }
            function New-ServicePrincipal { [CmdletBinding()] param([Guid]$AppId,[Guid]$ObjectId,[string]$DisplayName); throw 'unexpected create principal' }
            function Get-DistributionGroup {
                [CmdletBinding()] param([object]$Identity)
                [pscustomobject]@{
                    Identity='group-id'
                    DistinguishedName='CN=RelayBridge,OU=Tenant,DC=example,DC=com'
                    CustomAttribute15=('RelayBridge:' + $env:RB_CLIENT_ID)
                }
            }
            function New-DistributionGroup { [CmdletBinding()] param([string]$Name,[string]$DisplayName,[string]$Alias,[string]$Type); throw 'unexpected create group' }
            function Set-DistributionGroup { [CmdletBinding()] param([object]$Identity,[string]$CustomAttribute15); throw 'unexpected set group' }
            function Get-DistributionGroupMember {
                [CmdletBinding()] param([object]$Identity,[object]$ResultSize)
                @([pscustomobject]@{ PrimarySmtpAddress='scanner@example.com' })
            }
            function Add-DistributionGroupMember { [CmdletBinding()] param([object]$Identity,[object]$Member,[switch]$BypassSecurityGroupManagerCheck); throw 'unexpected add member' }
            function Get-ManagementScope {
                [CmdletBinding()] param([object]$Identity)
                [pscustomobject]@{ RecipientFilter="MemberOfGroup -eq 'CN=RelayBridge,OU=Tenant,DC=example,DC=com'" }
            }
            function New-ManagementScope { [CmdletBinding()] param([string]$Name,[string]$RecipientRestrictionFilter); throw 'unexpected create scope' }
            function New-ManagementRoleAssignment {
                [CmdletBinding()] param([string]$Name,[string]$Role,[Guid]$App,[string]$CustomResourceScope)
                [pscustomobject]@{ Name=$Name;Role=$Role;RoleAssigneeType='ServicePrincipal';RoleAssignee=$env:RB_SP_ID;CustomResourceScope=$CustomResourceScope }
            }
            function Get-ManagementRoleAssignment {
                [CmdletBinding()] param([object]$Identity,[object]$RoleAssignee,[object]$Role)
                $expected=[pscustomobject]@{
                    Name=('RelayBridge SMTP SendAs ' + ([Guid]$env:RB_CLIENT_ID).ToString('N').Substring(0,8))
                    Role='Application SMTP.SendAsApp'
                    RoleAssigneeType='ServicePrincipal'
                    RoleAssignee=$env:RB_SP_ID
                    CustomResourceScope=('RelayBridge Allowed Senders Scope ' + ([Guid]$env:RB_CLIENT_ID).ToString('N').Substring(0,8))
                }
                if ($PSBoundParameters.ContainsKey('Identity')) {
                    if ($env:RB_SCENARIO -eq 'missing-after-create') { return $null }
                    return $expected
                }
                if ($PSBoundParameters.ContainsKey('Role')) { [IO.File]::WriteAllText($env:RB_QUERY_LOG,'Role'); return @($expected) }
                [IO.File]::WriteAllText($env:RB_QUERY_LOG,('RoleAssignee:' + ([Guid]$RoleAssignee).ToString('D')))
                switch ($env:RB_SCENARIO) {
                    'extra-scoped' { return @($expected,[pscustomobject]@{Name='Extra';Role='Application Mail.Read';RoleAssigneeType='ServicePrincipal';RoleAssignee=$env:RB_SP_ID;CustomResourceScope=$expected.CustomResourceScope}) }
                    'extra-unscoped' { return @($expected,[pscustomobject]@{Name='Broad';Role='Application Mail.Read';RoleAssigneeType='ServicePrincipal';RoleAssignee=$env:RB_SP_ID;CustomResourceScope=$null}) }
                    'different-role' { return @($expected,[pscustomobject]@{Name='Other role';Role='Application Mail.Send';RoleAssigneeType='ServicePrincipal';RoleAssignee=$env:RB_SP_ID;CustomResourceScope=$expected.CustomResourceScope}) }
                    'custom-role' { return @($expected,[pscustomobject]@{Name='Derived';Role='RelayBridge Derived SMTP';RoleAssigneeType='ServicePrincipal';RoleAssignee=$env:RB_SP_ID;CustomResourceScope=$expected.CustomResourceScope}) }
                    'other-app' { return @([pscustomobject]@{Name=$expected.Name;Role=$expected.Role;RoleAssigneeType='ServicePrincipal';RoleAssignee='other-principal';CustomResourceScope=$expected.CustomResourceScope}) }
                    'wrong-scope' { $expected.CustomResourceScope='Wrong Scope'; return @($expected) }
                    'missing-after-create' { return @() }
                    default { return @($expected) }
                }
            }
            function Test-ServicePrincipalAuthorization {
                [CmdletBinding()] param([object]$Identity,[string]$Resource)
                [pscustomobject]@{ RoleName='Application SMTP.SendAsApp'; InScope=$true }
            }
            Export-ModuleMember -Function *
            """;
    }
}
