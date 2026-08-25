// SPDX-License-Identifier: MPL-2.0

namespace RelayBridge.Core.Queue;

public sealed class QueueRetryPolicy
{
    public QueueRetryPolicy(
        int maximumAttempts,
        TimeSpan maximumMessageAge,
        TimeSpan initialDelay,
        TimeSpan maximumDelay,
        double jitterFactor)
    {
        if (maximumAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        if (maximumMessageAge <= TimeSpan.Zero || initialDelay <= TimeSpan.Zero || maximumDelay < initialDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumMessageAge), "Retry durations are invalid.");
        }

        if (jitterFactor is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(jitterFactor));
        }

        MaximumAttempts = maximumAttempts;
        MaximumMessageAge = maximumMessageAge;
        InitialDelay = initialDelay;
        MaximumDelay = maximumDelay;
        JitterFactor = jitterFactor;
    }

    public int MaximumAttempts { get; }

    public TimeSpan MaximumMessageAge { get; }

    public TimeSpan InitialDelay { get; }

    public TimeSpan MaximumDelay { get; }

    public double JitterFactor { get; }

    public RetryDecision GetDecision(
        DateTimeOffset receivedUtc,
        int attemptCount,
        DateTimeOffset nowUtc,
        TimeSpan? retryAfter,
        double jitterSample)
    {
        if (attemptCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptCount));
        }

        if (jitterSample is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(jitterSample));
        }

        if (attemptCount >= MaximumAttempts || nowUtc - receivedUtc >= MaximumMessageAge)
        {
            return RetryDecision.Stop;
        }

        var delay = retryAfter is { } requested
            ? Clamp(requested, InitialDelay, MaximumDelay)
            : ApplyJitter(ExponentialDelay(attemptCount), jitterSample);
        var nextAttemptUtc = nowUtc + delay;
        if (nextAttemptUtc - receivedUtc >= MaximumMessageAge)
        {
            return RetryDecision.Stop;
        }

        return new RetryDecision(true, nextAttemptUtc, delay);
    }

    private TimeSpan ExponentialDelay(int attemptCount)
    {
        var multiplier = Math.Pow(2, Math.Min(attemptCount - 1, 30));
        var ticks = Math.Min(InitialDelay.Ticks * multiplier, MaximumDelay.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    private TimeSpan ApplyJitter(TimeSpan delay, double sample)
    {
        var multiplier = 1 + (((sample * 2) - 1) * JitterFactor);
        var ticks = Math.Clamp((long)(delay.Ticks * multiplier), 1, MaximumDelay.Ticks);
        return TimeSpan.FromTicks(ticks);
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum, TimeSpan maximum)
    {
        return value < minimum ? minimum : value > maximum ? maximum : value;
    }
}

public sealed record RetryDecision(bool ShouldRetry, DateTimeOffset? NextAttemptUtc, TimeSpan? Delay)
{
    public static RetryDecision Stop { get; } = new(false, null, null);
}
