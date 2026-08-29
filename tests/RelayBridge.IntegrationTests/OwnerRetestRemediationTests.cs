// SPDX-License-Identifier: MPL-2.0

extern alias printerconfigurator;

using System.Runtime.Versioning;
using RelayBridge.Host.Services;
using PrinterApplyException = printerconfigurator::RelayBridge.PrinterConfigurator.PrinterApplyException;
using PrinterApplyOutcome = printerconfigurator::RelayBridge.PrinterConfigurator.PrinterApplyOutcome;
using PrinterApplyStage = printerconfigurator::RelayBridge.PrinterConfigurator.PrinterApplyStage;
using PrinterConfiguratorDialog = printerconfigurator::RelayBridge.PrinterConfigurator.PrinterConfiguratorDialog;
using IRelayBridgeProcessObservation = printerconfigurator::RelayBridge.PrinterConfigurator.IRelayBridgeProcessObservation;
using IRelayBridgeServiceControl = printerconfigurator::RelayBridge.PrinterConfigurator.IRelayBridgeServiceControl;
using RelayBridgeServiceSnapshot = printerconfigurator::RelayBridge.PrinterConfigurator.RelayBridgeServiceSnapshot;
using ServiceRestartException = printerconfigurator::RelayBridge.PrinterConfigurator.ServiceRestartException;
using ServiceStartResult = printerconfigurator::RelayBridge.PrinterConfigurator.ServiceStartResult;
using WindowsServiceRestarter = printerconfigurator::RelayBridge.PrinterConfigurator.WindowsServiceRestarter;
using Xunit;

namespace RelayBridge.IntegrationTests;

[SupportedOSPlatform("windows")]
public sealed class OwnerRetestRemediationTests
{
    [Fact]
    public void Host_only_treats_cancellation_as_expected_after_application_stopping()
    {
        using var stopping = new CancellationTokenSource();
        var cancellation = new OperationCanceledException();

        Assert.False(HostShutdownExceptionPolicy.IsExpected(cancellation, stopping.Token));
        Assert.False(HostShutdownExceptionPolicy.IsExpected(new InvalidOperationException(), stopping.Token));

        stopping.Cancel();
        Assert.True(HostShutdownExceptionPolicy.IsExpected(cancellation, stopping.Token));
        Assert.False(HostShutdownExceptionPolicy.IsExpected(new InvalidOperationException(), stopping.Token));
    }

    [Fact]
    public void Printer_restart_waits_for_the_exact_old_service_process_before_start()
    {
        var operations = new FakeServiceControl(
            new RelayBridgeServiceSnapshot(State: 4, ProcessId: 42, Win32ExitCode: 0),
            [new ServiceStartResult(true, null)]);

        WindowsServiceRestarter.RestartRelayBridge(operations, _ => { });

        Assert.Equal(
            ["Query", "Capture:42", "Stop", "WaitState:1", "WaitProcess", "Start", "WaitState:4"],
            operations.Events);
        Assert.True(operations.Process.Waited);
        Assert.False(operations.Process.Terminated);
    }

    [Fact]
    public void Printer_restart_retries_only_bounded_transient_start_failures()
    {
        var operations = new FakeServiceControl(
            new RelayBridgeServiceSnapshot(State: 1, ProcessId: 0, Win32ExitCode: 0),
            [
                new ServiceStartResult(false, 1061),
                new ServiceStartResult(false, 1061),
                new ServiceStartResult(true, null),
            ]);
        var delays = 0;

        WindowsServiceRestarter.RestartRelayBridge(operations, _ => delays++);

        Assert.Equal(3, operations.StartAttempts);
        Assert.Equal(2, delays);
    }

    [Fact]
    public void Printer_restart_does_not_retry_non_transient_start_failure()
    {
        var operations = new FakeServiceControl(
            new RelayBridgeServiceSnapshot(State: 1, ProcessId: 0, Win32ExitCode: 0),
            [new ServiceStartResult(false, 5)]);

        var failure = Assert.Throws<ServiceRestartException>(() =>
            WindowsServiceRestarter.RestartRelayBridge(operations, _ => { }));

        Assert.Equal(PrinterApplyStage.ServiceStart, failure.Stage);
        Assert.Equal(5, failure.WindowsErrorCode);
        Assert.Equal(1, operations.StartAttempts);
    }

    [Fact]
    public void Printer_restart_fails_before_start_when_old_service_process_does_not_exit()
    {
        var operations = new FakeServiceControl(
            new RelayBridgeServiceSnapshot(State: 4, ProcessId: 42, Win32ExitCode: 0),
            [new ServiceStartResult(true, null)]);
        operations.Process.ExitObserved = false;

        var failure = Assert.Throws<ServiceRestartException>(() =>
            WindowsServiceRestarter.RestartRelayBridge(operations, _ => { }));

        Assert.Equal(PrinterApplyStage.PreviousProcessExit, failure.Stage);
        Assert.Equal(0, operations.StartAttempts);
    }

    [Theory]
    [InlineData(0, "was not saved")]
    [InlineData(1, "was written, but the saved file could not be verified")]
    [InlineData(2, "was saved, but RelayBridge could not be restarted")]
    [InlineData(3, "was saved and RelayBridge was started")]
    public void Printer_apply_failure_text_preserves_the_authoritative_completed_stage(
        int outcomeValue,
        string expected)
    {
        var outcome = (PrinterApplyOutcome)outcomeValue;
        var failure = new PrinterApplyException(
            outcome,
            outcome is PrinterApplyOutcome.ConfigurationWriteFailed or
                PrinterApplyOutcome.ConfigurationSavedVerificationFailed
                ? PrinterApplyStage.ConfigurationWrite
                : outcome == PrinterApplyOutcome.ConfigurationSavedRestartFailed
                    ? PrinterApplyStage.ServiceStart
                    : PrinterApplyStage.Readiness,
            new InvalidOperationException(),
            windowsErrorCode: 1056,
            serviceState: 1);

        var message = PrinterConfiguratorDialog.FormatFailure(failure);

        Assert.Contains(expected, message, StringComparison.Ordinal);
        Assert.Contains("Windows error: 1056", message, StringComparison.Ordinal);
        Assert.DoesNotContain("System.", message, StringComparison.Ordinal);
    }

    private sealed class FakeServiceControl(
        RelayBridgeServiceSnapshot initial,
        IReadOnlyList<ServiceStartResult> starts) : IRelayBridgeServiceControl
    {
        private int _startIndex;

        internal List<string> Events { get; } = [];

        internal FakeProcessObservation Process { get; } = new();

        internal int StartAttempts { get; private set; }

        public RelayBridgeServiceSnapshot Query()
        {
            Events.Add("Query");
            return initial;
        }

        public IRelayBridgeProcessObservation? CaptureProcess(uint processId)
        {
            Events.Add($"Capture:{processId}");
            Process.OnWait = () => Events.Add("WaitProcess");
            return Process;
        }

        public void RequestStop()
        {
            Events.Add("Stop");
        }

        public ServiceStartResult TryStart()
        {
            Events.Add("Start");
            StartAttempts++;
            return starts[Math.Min(_startIndex++, starts.Count - 1)];
        }

        public bool WaitForState(uint expected, TimeSpan timeout, out RelayBridgeServiceSnapshot observed)
        {
            Events.Add($"WaitState:{expected}");
            observed = new RelayBridgeServiceSnapshot(expected, expected == 4 ? 84u : 0u, 0);
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeProcessObservation : IRelayBridgeProcessObservation
    {
        internal bool ExitObserved { get; set; } = true;

        internal bool Waited { get; private set; }

        internal bool Terminated { get; private set; }

        internal Action? OnWait { get; set; }

        public bool WaitForExit(TimeSpan timeout)
        {
            Waited = true;
            OnWait?.Invoke();
            return ExitObserved;
        }

        public void Dispose()
        {
        }
    }
}
