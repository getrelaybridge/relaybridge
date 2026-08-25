// SPDX-License-Identifier: MPL-2.0

using RelayBridge.Core.Microsoft;

namespace RelayBridge.Infrastructure.Microsoft;

public sealed class ExchangeSmtpOptions
{
    public const string ProductionHost = "smtp.office365.com";
    public const int ProductionPort = 587;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan TlsTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan DataTerminationTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan MinimumDataTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan DataTimeoutPerMiB { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaximumDataTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan ConfigurationFailureRetryAfter { get; set; } = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        RequirePositive(ConnectTimeout, nameof(ConnectTimeout));
        RequirePositive(TlsTimeout, nameof(TlsTimeout));
        RequirePositive(CommandTimeout, nameof(CommandTimeout));
        RequirePositive(DataTerminationTimeout, nameof(DataTerminationTimeout));
        RequirePositive(MinimumDataTimeout, nameof(MinimumDataTimeout));
        RequirePositive(DataTimeoutPerMiB, nameof(DataTimeoutPerMiB));
        RequirePositive(MaximumDataTimeout, nameof(MaximumDataTimeout));
        RequirePositive(ConfigurationFailureRetryAfter, nameof(ConfigurationFailureRetryAfter));
        if (MaximumDataTimeout < MinimumDataTimeout)
        {
            throw new InvalidOperationException("MaximumDataTimeout cannot be shorter than MinimumDataTimeout.");
        }
    }

    internal TimeSpan GetDataTimeout(long sizeBytes)
    {
        const long bytesPerMebibyte = 1024L * 1024;
        var mebibytes = Math.Max(
            1,
            (sizeBytes / bytesPerMebibyte) + (sizeBytes % bytesPerMebibyte == 0 ? 0 : 1));
        var scaledTicks = mebibytes >= MaximumDataTimeout.Ticks / DataTimeoutPerMiB.Ticks
            ? MaximumDataTimeout.Ticks
            : mebibytes * DataTimeoutPerMiB.Ticks;
        var ticks = Math.Min(
            MaximumDataTimeout.Ticks,
            Math.Max(MinimumDataTimeout.Ticks, scaledTicks));
        return TimeSpan.FromTicks(ticks);
    }

    private static void RequirePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{name} must be positive.");
        }
    }
}

public enum ExchangeDeliveryStatus
{
    NotTested,
    Healthy,
    Failed,
    Ambiguous,
}

public sealed record ExchangeDeliverySnapshot(
    ExchangeDeliveryStatus Status,
    DateTimeOffset? LastAttemptedAt,
    DateTimeOffset? LastSuccessfulAt,
    string? LastStage,
    string? LastErrorCategory,
    bool DnsResolved,
    bool TcpConnected,
    bool TlsEstablished,
    bool TokenAcquired,
    bool XOAuth2Authenticated,
    bool SenderAuthorized,
    bool MessageAccepted)
{
    public string? ConfigurationFingerprint { get; init; }

    public Guid? AttemptId { get; init; }

    public long? StartSequence { get; init; }

    public long? CompletionSequence { get; init; }

    public int? GreetingResponseCode { get; init; }

    public int? StartTlsResponseCode { get; init; }

    public int? AuthenticationResponseCode { get; init; }

    public int? MailFromResponseCode { get; init; }

    public IReadOnlyList<int> RecipientResponseCodes { get; init; } = Array.Empty<int>();

    public int? DataResponseCode { get; init; }

    public DateTimeOffset? LastCompletedAt { get; init; }

    public bool DataStreamingStarted { get; init; }

    public DateTimeOffset? DataStreamingStartedAt { get; init; }

    public long PayloadBytesRead { get; init; }

    public bool SpoolEofReached { get; init; }

    public DateTimeOffset? SpoolEofReachedAt { get; init; }

    public bool DataTerminatorWriteStarted { get; init; }

    public DateTimeOffset? DataTerminatorWriteStartedAt { get; init; }

    public bool DataTerminatorFlushed { get; init; }

    public DateTimeOffset? DataTerminatorFlushedAt { get; init; }

    public bool FinalResponseWaitStarted { get; init; }

    public DateTimeOffset? FinalResponseWaitStartedAt { get; init; }

    public bool FinalResponseReceived { get; init; }

    public DateTimeOffset? FinalResponseReceivedAt { get; init; }

    public int? FinalResponseCode { get; init; }

    public string? FinalResponseEnhancedStatusCode { get; init; }

    public string? FinalResponseSafeSummary { get; init; }

    public string? LastExceptionType { get; init; }

    public string? LastSocketError { get; init; }
}

public sealed class ExchangeDeliveryRuntimeState
{
    private readonly object _lock = new();
    private readonly MicrosoftRuntimeEvidenceSequence _sequence;
    private readonly Dictionary<Guid, ExchangeDeliverySnapshot> _attempts = [];
    private readonly Dictionary<string, ExchangeDeliverySnapshot> _completedByFingerprint =
        new(StringComparer.Ordinal);
    private ExchangeDeliverySnapshot _snapshot = CreateEmptySnapshot();
    private Guid? _visibleAttemptId;

    public ExchangeDeliveryRuntimeState(MicrosoftRuntimeEvidenceSequence sequence)
    {
        _sequence = sequence;
    }

    internal ExchangeDeliveryRuntimeState()
        : this(new MicrosoftRuntimeEvidenceSequence())
    {
    }

    public ExchangeDeliverySnapshot Snapshot
    {
        get
        {
            lock (_lock)
            {
                return _snapshot;
            }
        }
    }

    public ExchangeDeliverySnapshot GetCompletedSnapshot(string? configurationFingerprint)
    {
        lock (_lock)
        {
            return configurationFingerprint is not null &&
                _completedByFingerprint.TryGetValue(configurationFingerprint, out var snapshot)
                    ? snapshot
                    : CreateEmptySnapshot();
        }
    }

    internal MicrosoftAttemptContext BeginAttempt(
        DateTimeOffset attemptedAt,
        MicrosoftIdentityConfiguration? capturedConfiguration,
        string? configurationFingerprint)
    {
        var attempt = MicrosoftAttemptContext.Create(
            _sequence,
            attemptedAt,
            capturedConfiguration,
            configurationFingerprint);
        lock (_lock)
        {
            var snapshot = CreateEmptySnapshot() with
            {
                AttemptId = attempt.AttemptId,
                StartSequence = attempt.StartSequence,
                LastAttemptedAt = attemptedAt,
                ConfigurationFingerprint = configurationFingerprint,
                LastStage = "Connecting",
            };
            _attempts.Add(attempt.AttemptId, snapshot);
            _visibleAttemptId = attempt.AttemptId;
            _snapshot = snapshot;
        }

        return attempt;
    }

    internal void RecordProtocolResponse(
        MicrosoftAttemptContext attempt,
        string stage,
        SmtpResponse response)
    {
        lock (_lock)
        {
            UpdateAttempt(attempt, snapshot => stage switch
            {
                "Greeting" => snapshot with { GreetingResponseCode = response.Code },
                "STARTTLS" => snapshot with { StartTlsResponseCode = response.Code },
                "AUTH" => snapshot with { AuthenticationResponseCode = response.Code },
                "MAIL FROM" => snapshot with { MailFromResponseCode = response.Code },
                "RCPT TO" => snapshot with
                {
                    RecipientResponseCodes = [.. snapshot.RecipientResponseCodes, response.Code],
                },
                "DATA" => snapshot with { DataResponseCode = response.Code },
                _ => snapshot,
            });
        }
    }

    internal void RecordDataProgress(
        MicrosoftAttemptContext attempt,
        SmtpDataProgress progress,
        DateTimeOffset timestamp)
    {
        lock (_lock)
        {
            UpdateAttempt(attempt, snapshot => progress.Stage switch
            {
                SmtpDataProgressStage.StreamingStarted => snapshot with
                {
                    LastStage = "DATA streaming",
                    DataStreamingStarted = true,
                    DataStreamingStartedAt = timestamp,
                },
                SmtpDataProgressStage.PayloadRead => snapshot with
                {
                    PayloadBytesRead = progress.PayloadBytesRead,
                },
                SmtpDataProgressStage.SpoolEofReached => snapshot with
                {
                    LastStage = "Spool EOF",
                    PayloadBytesRead = progress.PayloadBytesRead,
                    SpoolEofReached = true,
                    SpoolEofReachedAt = timestamp,
                },
                SmtpDataProgressStage.TerminatorWriteStarted => snapshot with
                {
                    LastStage = "DATA terminator write",
                    DataTerminatorWriteStarted = true,
                    DataTerminatorWriteStartedAt = timestamp,
                },
                SmtpDataProgressStage.TerminatorFlushed => snapshot with
                {
                    LastStage = "DATA terminator flushed",
                    DataTerminatorFlushed = true,
                    DataTerminatorFlushedAt = timestamp,
                },
                _ => snapshot,
            });
        }
    }

    internal void RecordFinalResponseWait(MicrosoftAttemptContext attempt, DateTimeOffset timestamp)
    {
        lock (_lock)
        {
            UpdateAttempt(attempt, snapshot => snapshot with
            {
                LastStage = "Final DATA response wait",
                FinalResponseWaitStarted = true,
                FinalResponseWaitStartedAt = timestamp,
            });
        }
    }

    internal void RecordFinalResponse(
        MicrosoftAttemptContext attempt,
        SmtpResponse response,
        DateTimeOffset timestamp)
    {
        lock (_lock)
        {
            UpdateAttempt(attempt, snapshot => snapshot with
            {
                LastStage = "Final DATA response received",
                FinalResponseReceived = true,
                FinalResponseReceivedAt = timestamp,
                FinalResponseCode = response.Code,
                FinalResponseEnhancedStatusCode = response.EnhancedStatusCode,
                FinalResponseSafeSummary = response.SafeSummary,
            });
        }
    }

    internal void RecordException(MicrosoftAttemptContext attempt, Exception exception)
    {
        lock (_lock)
        {
            var socketException = FindSocketException(exception);
            UpdateAttempt(attempt, snapshot => snapshot with
            {
                LastExceptionType = exception.GetType().Name,
                LastSocketError = socketException is not null
                    ? socketException.SocketErrorCode.ToString()
                    : null,
            });
        }
    }

    private static System.Net.Sockets.SocketException? FindSocketException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is System.Net.Sockets.SocketException socketException)
            {
                return socketException;
            }
        }

        return null;
    }

    internal void RecordStage(MicrosoftAttemptContext attempt, string stage)
    {
        lock (_lock)
        {
            UpdateAttempt(attempt, snapshot => stage switch
            {
                "TCP connection" => snapshot with
                {
                    LastStage = stage,
                    DnsResolved = true,
                    TcpConnected = true,
                },
                "STARTTLS" => snapshot with { LastStage = stage, TlsEstablished = true },
                "Token acquisition" => snapshot with { LastStage = stage, TokenAcquired = true },
                "XOAUTH2" => snapshot with { LastStage = stage, XOAuth2Authenticated = true },
                "Sender authorization" => snapshot with { LastStage = stage, SenderAuthorized = true },
                "Accepted" => snapshot with { LastStage = stage, MessageAccepted = true },
                _ => snapshot with { LastStage = stage },
            });
        }
    }

    internal void RecordResult(
        MicrosoftAttemptContext attempt,
        DateTimeOffset completedAt,
        Core.Queue.DeliveryResult result)
    {
        lock (_lock)
        {
            if (!_attempts.Remove(attempt.AttemptId, out var attemptSnapshot))
            {
                return;
            }

            var completionSequence = _sequence.Next();
            var completed = result.Outcome == Core.Queue.DeliveryOutcome.Success
                ? attemptSnapshot with
                {
                    Status = ExchangeDeliveryStatus.Healthy,
                    LastSuccessfulAt = completedAt,
                    LastCompletedAt = completedAt,
                    LastStage = "Accepted",
                    LastErrorCategory = null,
                    MessageAccepted = true,
                    CompletionSequence = completionSequence,
                }
                : attemptSnapshot with
                {
                    LastCompletedAt = completedAt,
                    Status = string.Equals(
                        result.ErrorCategory,
                        ExchangeSmtpErrorCategories.AmbiguousAcceptance,
                        StringComparison.Ordinal)
                            ? ExchangeDeliveryStatus.Ambiguous
                            : ExchangeDeliveryStatus.Failed,
                    LastStage = attemptSnapshot.LastStage,
                    LastErrorCategory = result.ErrorCategory,
                    CompletionSequence = completionSequence,
                };

            if (attempt.ConfigurationFingerprint is not null)
            {
                _completedByFingerprint[attempt.ConfigurationFingerprint] = completed;
            }

            _snapshot = completed;
            if (_visibleAttemptId == attempt.AttemptId)
            {
                _visibleAttemptId = null;
            }
        }
    }

    internal void Abandon(MicrosoftAttemptContext attempt)
    {
        lock (_lock)
        {
            if (!_attempts.Remove(attempt.AttemptId) || _visibleAttemptId != attempt.AttemptId)
            {
                return;
            }

            _visibleAttemptId = null;
            _snapshot = _completedByFingerprint.Values
                .OrderByDescending(item => item.CompletionSequence)
                .FirstOrDefault() ?? CreateEmptySnapshot();
        }
    }

    private void UpdateAttempt(
        MicrosoftAttemptContext attempt,
        Func<ExchangeDeliverySnapshot, ExchangeDeliverySnapshot> update)
    {
        if (!_attempts.TryGetValue(attempt.AttemptId, out var snapshot))
        {
            return;
        }

        var updated = update(snapshot);
        _attempts[attempt.AttemptId] = updated;
        if (_visibleAttemptId == attempt.AttemptId)
        {
            _snapshot = updated;
        }
    }

    private static ExchangeDeliverySnapshot CreateEmptySnapshot()
    {
        return new ExchangeDeliverySnapshot(
            ExchangeDeliveryStatus.NotTested,
            null,
            null,
            null,
            null,
            false,
            false,
            false,
            false,
            false,
            false,
            false);
    }
}

public sealed record ExchangeDeliveryDiagnosticResult(
    Core.Queue.DeliveryOutcome Outcome,
    string? ErrorCategory,
    string? SafeMessage,
    ExchangeDeliverySnapshot Checkpoints,
    Guid CorrelationId);

internal sealed record ExchangeSmtpEndpoint(
    string Host,
    int Port,
    string TlsTargetHost,
    System.Net.Security.RemoteCertificateValidationCallback? TestCertificateValidation = null)
{
    public static ExchangeSmtpEndpoint Production { get; } = new(
        ExchangeSmtpOptions.ProductionHost,
        ExchangeSmtpOptions.ProductionPort,
        ExchangeSmtpOptions.ProductionHost);
}

internal static class ExchangeSmtpErrorCategories
{
    public const string Network = "Network";
    public const string Dns = "DNS";
    public const string Tls = "TLS";
    public const string Authentication = "Authentication";
    public const string Authorization = "Authorization";
    public const string SenderRejected = "SenderRejected";
    public const string RecipientRejected = "RecipientRejected";
    public const string MessageTooLarge = "MessageTooLarge";
    public const string TemporaryServerFailure = "TemporaryServerFailure";
    public const string PermanentServerFailure = "PermanentServerFailure";
    public const string Protocol = "Protocol";
    public const string Timeout = "Timeout";
    public const string Cancelled = "Cancelled";
    public const string AmbiguousAcceptance = "AmbiguousAcceptance";
}
