// SPDX-License-Identifier: MPL-2.0

using RelayBridge.Core.Queue;
using Xunit;

namespace RelayBridge.Tests;

public sealed class QueueReliabilityTests
{
    [Theory]
    [InlineData(QueueState.Queued, QueueState.Delivering)]
    [InlineData(QueueState.RetryScheduled, QueueState.Delivering)]
    [InlineData(QueueState.Delivering, QueueState.Queued)]
    [InlineData(QueueState.Delivering, QueueState.Delivered)]
    [InlineData(QueueState.Delivering, QueueState.RetryScheduled)]
    [InlineData(QueueState.Delivering, QueueState.PermanentFailure)]
    public void Required_queue_transitions_are_allowed(QueueState current, QueueState next)
    {
        Assert.True(QueueStateMachine.CanTransition(current, next));
        QueueStateMachine.RequireTransition(current, next);
    }

    [Theory]
    [InlineData(QueueState.Queued, QueueState.Delivered)]
    [InlineData(QueueState.Queued, QueueState.RetryScheduled)]
    [InlineData(QueueState.RetryScheduled, QueueState.Delivered)]
    [InlineData(QueueState.Delivered, QueueState.Queued)]
    [InlineData(QueueState.PermanentFailure, QueueState.Queued)]
    public void Illegal_queue_transitions_are_rejected(QueueState current, QueueState next)
    {
        Assert.False(QueueStateMachine.CanTransition(current, next));
        Assert.Throws<InvalidOperationException>(() => QueueStateMachine.RequireTransition(current, next));
    }

    [Fact]
    public void Retry_delay_increases_and_jitter_stays_bounded()
    {
        var policy = CreatePolicy();
        var received = new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);

        var firstLow = policy.GetDecision(received, 1, received, null, 0);
        var firstHigh = policy.GetDecision(received, 1, received, null, 1);
        var second = policy.GetDecision(received, 2, received, null, 0.5);

        Assert.Equal(TimeSpan.FromSeconds(8), firstLow.Delay);
        Assert.Equal(TimeSpan.FromSeconds(12), firstHigh.Delay);
        Assert.Equal(TimeSpan.FromSeconds(20), second.Delay);
    }

    [Fact]
    public void Retry_hint_is_honored_within_configured_bounds()
    {
        var policy = CreatePolicy();
        var now = new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            TimeSpan.FromSeconds(10),
            policy.GetDecision(now, 1, now, TimeSpan.FromSeconds(1), 0.5).Delay);
        Assert.Equal(
            TimeSpan.FromMinutes(2),
            policy.GetDecision(now, 1, now, TimeSpan.FromMinutes(2), 0.5).Delay);
        Assert.Equal(
            TimeSpan.FromMinutes(10),
            policy.GetDecision(now, 1, now, TimeSpan.FromHours(1), 0.5).Delay);
    }

    [Fact]
    public void Maximum_attempts_and_message_age_stop_retrying()
    {
        var policy = CreatePolicy();
        var received = new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);

        Assert.False(policy.GetDecision(received, 5, received, null, 0.5).ShouldRetry);
        Assert.False(policy.GetDecision(received, 1, received.AddHours(24), null, 0.5).ShouldRetry);
        Assert.False(policy.GetDecision(
            received,
            1,
            received.AddHours(23).AddMinutes(59),
            TimeSpan.FromMinutes(10),
            0.5).ShouldRetry);
    }

    [Fact]
    public void Attempt_age_and_retry_hint_boundaries_are_deterministic()
    {
        var policy = CreatePolicy();
        var received = new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);

        Assert.True(policy.GetDecision(received, 4, received, null, 0.5).ShouldRetry);
        Assert.False(policy.GetDecision(received, 5, received, null, 0.5).ShouldRetry);
        Assert.False(policy.GetDecision(received, 1, received.AddHours(24), null, 0.5).ShouldRetry);
        Assert.False(policy.GetDecision(
            received,
            1,
            received.AddHours(24).AddTicks(1),
            null,
            0.5).ShouldRetry);
        Assert.True(policy.GetDecision(
            received,
            1,
            received.AddHours(23).AddMinutes(49),
            TimeSpan.FromMinutes(10),
            0.5).ShouldRetry);
        Assert.False(policy.GetDecision(
            received,
            1,
            received.AddHours(23).AddMinutes(50),
            TimeSpan.FromMinutes(10),
            0.5).ShouldRetry);
    }

    private static QueueRetryPolicy CreatePolicy()
    {
        return new QueueRetryPolicy(
            maximumAttempts: 5,
            maximumMessageAge: TimeSpan.FromHours(24),
            initialDelay: TimeSpan.FromSeconds(10),
            maximumDelay: TimeSpan.FromMinutes(10),
            jitterFactor: 0.2);
    }
}
