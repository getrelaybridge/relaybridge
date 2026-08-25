// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;
using System.Net;
using RelayBridge.Core.Devices;
using Xunit;
using Xunit.Abstractions;

namespace RelayBridge.Tests;

public sealed class DeviceSecurityTests
{
    private readonly ITestOutputHelper _output;

    public DeviceSecurityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData("192.168.10.0/24", "192.168.10.31", true)]
    [InlineData("192.168.10.31", "192.168.10.32", false)]
    [InlineData("10.0.0.0/8", "11.0.0.1", false)]
    [InlineData("2001:db8::/32", "2001:db8:1::25", true)]
    [InlineData("2001:db8::/64", "2001:db9::1", false)]
    public void Ip_network_authorization_is_fail_closed(
        string network,
        string address,
        bool expected)
    {
        Assert.Equal(expected, IpNetwork.Parse(network).Contains(IPAddress.Parse(address)));
    }

    [Fact]
    public void IPv4_mapped_addresses_match_IPv4_networks()
    {
        var mapped = IPAddress.Parse("::ffff:192.168.10.31");

        Assert.True(IpNetwork.Parse("192.168.10.0/24").Contains(mapped));
    }

    [Fact]
    public void Legacy_device_without_network_restriction_is_rejected()
    {
        var exception = Assert.Throws<ArgumentException>(() => DeviceDefinition.CreateLegacy(
            Guid.NewGuid(),
            "Unsafe legacy device",
            enabled: true,
            allowedNetworks: [],
            allowedSenders: ["scanner@example.com"],
            DateTimeOffset.UtcNow));

        Assert.Contains("source IP", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("203.0.113.20")]
    public void Legacy_device_with_unrestricted_or_public_network_is_rejected(string network)
    {
        Assert.Throws<ArgumentException>(() => DeviceDefinition.CreateLegacy(
            Guid.NewGuid(),
            "Unsafe legacy device",
            enabled: true,
            allowedNetworks: [network],
            allowedSenders: ["scanner@example.com"],
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Device_without_sender_restriction_is_rejected()
    {
        var exception = Assert.Throws<ArgumentException>(() => DeviceDefinition.CreateLegacy(
            Guid.NewGuid(),
            "Unsafe legacy device",
            enabled: true,
            allowedNetworks: ["192.168.1.20"],
            allowedSenders: [],
            DateTimeOffset.UtcNow));

        Assert.Contains("allowed sender", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sender_authorization_is_exact_and_case_insensitive()
    {
        var device = DeviceDefinition.CreateLegacy(
            Guid.NewGuid(),
            "Scanner",
            enabled: true,
            allowedNetworks: ["127.0.0.1"],
            allowedSenders: ["Scanner@Example.com"],
            DateTimeOffset.UtcNow);

        Assert.True(device.IsSenderAllowed("scanner@example.com"));
        Assert.False(device.IsSenderAllowed("other@example.com"));
        Assert.False(device.IsSenderAllowed("Scanner <scanner@example.com>"));
    }

    [Fact]
    public void Generated_password_is_only_recoverable_from_one_time_result()
    {
        var generated = DevicePassword.Generate();

        Assert.Equal(32, generated.Plaintext.Length);
        Assert.DoesNotContain(generated.Plaintext, generated.Verifier, StringComparison.Ordinal);
        Assert.True(DevicePassword.Verify(generated.Plaintext, generated.Verifier));
        Assert.False(DevicePassword.Verify($"{generated.Plaintext}x", generated.Verifier));
    }

    [Fact]
    public void Pbkdf2_verification_cost_is_benchmarked()
    {
        const int sequentialAttempts = 10;
        const int parallelAttempts = 5;
        var generated = DevicePassword.Generate();

        var sequential = Stopwatch.StartNew();
        for (var attempt = 0; attempt < sequentialAttempts; attempt++)
        {
            Assert.False(DevicePassword.Verify("incorrect-device-password", generated.Verifier));
        }

        sequential.Stop();
        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var parallel = Stopwatch.StartNew();
        Parallel.For(0, parallelAttempts, _ =>
        {
            Assert.False(DevicePassword.Verify("incorrect-device-password", generated.Verifier));
        });
        parallel.Stop();
        var cpuUsed = process.TotalProcessorTime - cpuBefore;

        _output.WriteLine(
            "PBKDF2-HMAC-SHA256 (600,000): {0:N2} ms/verification sequential; " +
            "{1} parallel verifications: {2:N2} ms wall, {3:N2} ms process CPU.",
            sequential.Elapsed.TotalMilliseconds / sequentialAttempts,
            parallelAttempts,
            parallel.Elapsed.TotalMilliseconds,
            cpuUsed.TotalMilliseconds);
    }
}
