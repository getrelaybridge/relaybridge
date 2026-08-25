// SPDX-License-Identifier: MPL-2.0

using RelayBridge.Core.Diagnostics;
using RelayBridge.Core.Microsoft;
using Xunit;

namespace RelayBridge.Tests;

public sealed class DiagnosticsStatusTests
{
    [Fact]
    public void Fresh_installation_is_not_configured_when_local_runtime_is_healthy()
    {
        var result = Evaluate(microsoftConfigured: false);

        Assert.Equal(DiagnosticStatus.NotConfigured, result);
    }

    [Fact]
    public void Missing_connectivity_probe_does_not_make_configured_runtime_unhealthy()
    {
        var result = Evaluate(
            microsoftConfigured: true,
            connectivity: DiagnosticStatus.Unknown);

        Assert.Equal(DiagnosticStatus.Healthy, result);
    }

    [Fact]
    public void Verification_required_is_attention_and_not_live_connectivity_evidence()
    {
        var result = Evaluate(
            microsoftConfigured: true,
            microsoft: DiagnosticStatus.Attention,
            connectivity: DiagnosticStatus.Unknown);

        Assert.Equal(DiagnosticStatus.Attention, result);
    }

    [Fact]
    public void Required_local_component_unavailable_has_precedence()
    {
        var result = Evaluate(
            microsoftConfigured: false,
            smtp: DiagnosticStatus.Unavailable);

        Assert.Equal(DiagnosticStatus.Unavailable, result);
    }

    [Theory]
    [InlineData(CertificateValidationStatus.NotConfigured, DiagnosticStatus.NotConfigured)]
    [InlineData(CertificateValidationStatus.Valid, DiagnosticStatus.Healthy)]
    [InlineData(CertificateValidationStatus.ExpiringSoon, DiagnosticStatus.Attention)]
    [InlineData(CertificateValidationStatus.Expired, DiagnosticStatus.Unavailable)]
    [InlineData(CertificateValidationStatus.Missing, DiagnosticStatus.Unavailable)]
    [InlineData(CertificateValidationStatus.PrivateKeyInaccessible, DiagnosticStatus.Unavailable)]
    public void Certificate_presence_expiry_and_key_states_are_explicit(
        CertificateValidationStatus certificate,
        DiagnosticStatus expected)
    {
        Assert.Equal(expected, DiagnosticsItemStatusPolicy.Certificate(certificate));
    }

    [Theory]
    [InlineData(false, false, DiagnosticStatus.Attention)]
    [InlineData(true, false, DiagnosticStatus.Unavailable)]
    [InlineData(true, true, DiagnosticStatus.Healthy)]
    public void Listener_configuration_is_distinct_from_actual_runtime_binding(
        bool enabled,
        bool listening,
        DiagnosticStatus expected)
    {
        Assert.Equal(expected, DiagnosticsItemStatusPolicy.Listener(enabled, listening));
    }

    [Theory]
    [InlineData(false, false, 0, DiagnosticStatus.Healthy)]
    [InlineData(true, false, 0, DiagnosticStatus.Unavailable)]
    [InlineData(true, true, 1, DiagnosticStatus.Attention)]
    [InlineData(true, true, 0, DiagnosticStatus.Healthy)]
    public void Queue_aggregate_status_preserves_worker_and_permanent_failure_meaning(
        bool workerExpected,
        bool workerRunning,
        int permanentFailures,
        DiagnosticStatus expected)
    {
        Assert.Equal(
            expected,
            DiagnosticsItemStatusPolicy.Queue(workerExpected, workerRunning, permanentFailures));
    }

    private static DiagnosticStatus Evaluate(
        bool microsoftConfigured,
        DiagnosticStatus runtime = DiagnosticStatus.Healthy,
        DiagnosticStatus smtp = DiagnosticStatus.Healthy,
        DiagnosticStatus queue = DiagnosticStatus.Healthy,
        DiagnosticStatus microsoft = DiagnosticStatus.Healthy,
        DiagnosticStatus certificate = DiagnosticStatus.Healthy,
        DiagnosticStatus setup = DiagnosticStatus.Healthy,
        DiagnosticStatus connectivity = DiagnosticStatus.Unknown,
        DiagnosticStatus storage = DiagnosticStatus.Healthy,
        DiagnosticStatus security = DiagnosticStatus.Healthy) =>
        DiagnosticsOverallStatusPolicy.Evaluate(
            microsoftConfigured,
            runtime,
            smtp,
            queue,
            microsoft,
            certificate,
            setup,
            connectivity,
            storage,
            security);
}
