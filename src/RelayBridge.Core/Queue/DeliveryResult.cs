// SPDX-License-Identifier: MPL-2.0

namespace RelayBridge.Core.Queue;

public enum DeliveryOutcome
{
    Success,
    TransientFailure,
    PermanentFailure,
}

public sealed record DeliveryResult
{
    private DeliveryResult(
        DeliveryOutcome outcome,
        string? errorCategory,
        string? safeMessage,
        TimeSpan? retryAfter)
    {
        if (errorCategory?.Length > 128)
        {
            throw new ArgumentException("Delivery error categories cannot exceed 128 characters.", nameof(errorCategory));
        }

        if (safeMessage?.Length > 1024)
        {
            throw new ArgumentException("Delivery messages cannot exceed 1024 characters.", nameof(safeMessage));
        }

        if (retryAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfter), "Retry-After must be positive.");
        }

        Outcome = outcome;
        ErrorCategory = errorCategory;
        SafeMessage = safeMessage;
        RetryAfter = retryAfter;
    }

    public DeliveryOutcome Outcome { get; }

    public string? ErrorCategory { get; }

    public string? SafeMessage { get; }

    public TimeSpan? RetryAfter { get; }

    public static DeliveryResult Succeeded()
    {
        return new DeliveryResult(DeliveryOutcome.Success, null, null, null);
    }

    public static DeliveryResult TransientFailure(
        string errorCategory,
        string safeMessage,
        TimeSpan? retryAfter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCategory);
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);
        return new DeliveryResult(DeliveryOutcome.TransientFailure, errorCategory, safeMessage, retryAfter);
    }

    public static DeliveryResult PermanentFailure(string errorCategory, string safeMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCategory);
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);
        return new DeliveryResult(DeliveryOutcome.PermanentFailure, errorCategory, safeMessage, null);
    }
}

public interface IMailDeliveryProvider
{
    Task<DeliveryResult> DeliverAsync(
        QueuedMessage message,
        Stream messageContent,
        CancellationToken cancellationToken);
}
