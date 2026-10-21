namespace ScanBack.Application;

/// <summary>Transport boundary. Completion means provider acceptance, never human receipt.</summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default);
}

/// <summary>Processes one bounded batch of durable notification work.</summary>
public interface IOutboxProcessor
{
    Task ProcessPendingAsync(CancellationToken cancellationToken = default);
}

/// <summary>Deletes expired operational data according to the configured retention policy.</summary>
public interface IRetentionProcessor
{
    Task RemoveExpiredDataAsync(CancellationToken cancellationToken = default);
}

public static class NotificationPolicy
{
    public const int MaxAttempts = 5;
    public const int BatchSize = 20;
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    public static DateTimeOffset RetryAt(DateTimeOffset now, int attempts) =>
        now.AddMinutes(Math.Min(60, Math.Pow(2, Math.Clamp(attempts, 0, MaxAttempts))));
}

public static class RetentionPolicy
{
    public static DateTimeOffset Cutoff(DateTimeOffset now, int? retentionDays) =>
        now.AddDays(-Math.Clamp(retentionDays ?? 30, 1, 365));
}
