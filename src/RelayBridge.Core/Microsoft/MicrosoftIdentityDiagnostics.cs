// SPDX-License-Identifier: MPL-2.0

namespace RelayBridge.Core.Microsoft;

public enum CertificateValidationStatus
{
    NotConfigured,
    Valid,
    ExpiringSoon,
    Expired,
    Missing,
    NoPrivateKey,
    PrivateKeyInaccessible,
    Unsupported,
    Invalid,
}

public sealed record MicrosoftCertificateMetadata(
    string Thumbprint,
    string Subject,
    string StoreName,
    CertificateStoreTarget StoreLocation,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    int KeySizeBits);

public sealed record CertificateValidationResult(
    CertificateValidationStatus Status,
    string Message,
    MicrosoftCertificateMetadata? Certificate)
{
    public bool IsUsable => Status is CertificateValidationStatus.Valid or CertificateValidationStatus.ExpiringSoon;
}

public enum MicrosoftIdentityErrorCategory
{
    InvalidConfiguration,
    CertificateMissing,
    CertificateInvalid,
    CertificateExpired,
    PrivateKeyUnavailable,
    TenantNotFound,
    ApplicationNotFound,
    CredentialRejected,
    NetworkFailure,
    MicrosoftServiceFailure,
    Cancelled,
    Unknown,
}

public sealed class MicrosoftIdentityException : Exception
{
    public MicrosoftIdentityException(
        MicrosoftIdentityErrorCategory category,
        string message,
        string? technicalCode = null,
        string? correlationId = null,
        DateTimeOffset? timestamp = null)
        : base(message)
    {
        Category = category;
        TechnicalCode = technicalCode;
        CorrelationId = correlationId;
        Timestamp = timestamp;
    }

    public MicrosoftIdentityErrorCategory Category { get; }

    public string? TechnicalCode { get; }

    public string? CorrelationId { get; }

    public DateTimeOffset? Timestamp { get; }
}

public sealed record MicrosoftAuthenticationTestResult(
    bool Succeeded,
    string Message,
    MicrosoftIdentityErrorCategory? ErrorCategory,
    CertificateValidationResult CertificateValidation,
    DateTimeOffset AttemptedAt,
    DateTimeOffset? TokenExpiresOn,
    string? TechnicalCode,
    string? CorrelationId)
{
    public bool SmtpAuthorizationTested => false;

    public bool MailDeliveryTested => false;
}

public enum MicrosoftIdentityHealthStatus
{
    NotConfigured,
    Checking,
    Healthy,
    Attention,
    Failed,
}

public sealed record MicrosoftIdentityHealthSnapshot(
    MicrosoftIdentityHealthStatus Status,
    DateTimeOffset? LastAttemptedAt,
    DateTimeOffset? LastSuccessfulAt,
    DateTimeOffset? CertificateExpiresOn,
    MicrosoftIdentityErrorCategory? LastErrorCategory)
{
    public string? ConfigurationFingerprint { get; init; }

    public Guid? AttemptId { get; init; }

    public DateTimeOffset? LastCompletedAt { get; init; }

    public long? CompletionSequence { get; init; }
}
