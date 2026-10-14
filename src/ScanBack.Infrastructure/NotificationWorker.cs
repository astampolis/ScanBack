using System.Security.Cryptography;
using MailKit.Net.Smtp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScanBack.Domain;

namespace ScanBack.Infrastructure;

/// <summary>
/// Sends the persistent outbox with retry backoff and atomic five-minute leases.
/// Delivery is at least once: a process failure after SMTP acceptance can cause a retry.
/// AcceptedAt means acceptance by the SMTP provider, never confirmed human receipt.
/// </summary>
public sealed class NotificationWorker(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<MailOptions> mailOptions,
    ILogger<NotificationWorker> logger) : BackgroundService
{
    public const int MaxAttempts = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try
            {
                if (mailOptions.CurrentValue.Enabled)
                    await ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                // Exception messages can contain email recipients or connection data.
                logger.LogWarning("Outbox polling failed; the next poll will retry.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var settings = await db.PlatformSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var notificationsEnabled = settings?.NotificationsEnabled != false;

        var now = DateTimeOffset.UtcNow;
        // Project IDs first: decrypting one damaged body should not block the batch.
        var pendingIds = await db.OutboxMessages.AsNoTracking()
            .Where(x => x.AcceptedAt == null && x.Attempts < MaxAttempts && x.NextAttemptAt <= now
                && (notificationsEnabled || x.Kind != "Incident"))
            .OrderBy(x => x.NextAttemptAt).ThenBy(x => x.CreatedAt)
            .Select(x => x.Id).Take(20).ToListAsync(cancellationToken);

        foreach (var id in pendingIds)
        {
            var claimedAt = DateTimeOffset.UtcNow;
            var leaseUntil = claimedAt.AddMinutes(5);
            var claimed = await db.OutboxMessages
                .Where(x => x.Id == id && x.AcceptedAt == null && x.Attempts < MaxAttempts && x.NextAttemptAt <= claimedAt)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.NextAttemptAt, leaseUntil)
                    .SetProperty(x => x.Attempts, x => x.Attempts + 1), cancellationToken);
            if (claimed == 0) continue;

            OutboxMessage? message = null;
            try
            {
                message = await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
                if (message is null) continue;
                if (message.Kind == "Incident" && !await MayNotifyIncidentAsync(db, message, cancellationToken))
                {
                    await MarkFailedAsync(db, id, "Notification no longer authorized", true, cancellationToken);
                    continue;
                }
                if (message.Kind == "ContactVerification" && !await MayVerifyContactAsync(db, message, cancellationToken))
                {
                    await MarkFailedAsync(db, id, "Contact verification expired or superseded", true, cancellationToken);
                    continue;
                }
                await sender.SendAsync(message.ToEmail, message.Subject, message.Body, cancellationToken);
                await db.OutboxMessages.Where(x => x.Id == id).ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.AcceptedAt, DateTimeOffset.UtcNow)
                    .SetProperty(x => x.LastError, (string?)null), cancellationToken);
                logger.LogInformation("Outbox message {MessageId} accepted by SMTP provider.", id);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                var errorCode = exception switch
                {
                    CryptographicException => "Protected message cannot be read",
                    SmtpCommandException => "SMTP command rejected",
                    SmtpProtocolException => "SMTP protocol failure",
                    MailKit.Security.AuthenticationException => "SMTP authentication failed",
                    System.Security.Authentication.AuthenticationException => "SMTP TLS verification failed",
                    InvalidOperationException => "SMTP configuration unavailable",
                    FormatException => "Invalid email address",
                    _ => "Email transport unavailable"
                };
                await MarkFailedAsync(db, id, errorCode, exception is CryptographicException or FormatException, cancellationToken);
                logger.LogWarning("Outbox message {MessageId} was not accepted: {FailureCode}.", id, errorCode);
            }
        }
    }

    private static async Task<bool> MayNotifyIncidentAsync(AppDbContext db, OutboxMessage message, CancellationToken cancellationToken)
    {
        if (message.IncidentId is null || message.EmergencyContactId is null) return false;
        var now = DateTimeOffset.UtcNow;
        return await db.Incidents.AnyAsync(x => x.Id == message.IncidentId && x.RiderProfileId == message.RiderProfileId
                && !x.IsClosed && x.ExpiresAt > now && x.EmergencyIdentifier.IsActive
                && x.RiderProfile.UserId != ""
                && x.AccessGrants.Any(access => access.EmergencyContactId == message.EmergencyContactId && !access.IsRevoked && access.ExpiresAt > now)
                && db.Users.Any(user => user.Id == x.RiderProfile.UserId && user.IsActive), cancellationToken)
            && await db.EmergencyContacts.AnyAsync(x => x.Id == message.EmergencyContactId && x.IsVerified && x.NotifyEnabled
                && x.Email == message.ToEmail && x.RiderProfileId == message.RiderProfileId, cancellationToken);
    }

    private static Task<bool> MayVerifyContactAsync(AppDbContext db, OutboxMessage message, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return db.EmergencyContacts.AnyAsync(contact => contact.Id == message.EmergencyContactId
            && !contact.IsVerified && contact.VerificationExpiresAt > now && contact.VerificationTokenHash != ""
            && contact.Email == message.ToEmail && db.Users.Any(user => user.Id == contact.RiderProfile.UserId && user.IsActive), cancellationToken);
    }

    private static async Task MarkFailedAsync(AppDbContext db, Guid id, string errorCode, bool permanent, CancellationToken cancellationToken)
    {
        var attempts = await db.OutboxMessages.Where(x => x.Id == id).Select(x => (int?)x.Attempts).SingleOrDefaultAsync(cancellationToken);
        if (attempts is null) return;
        var retryAt = DateTimeOffset.UtcNow.AddMinutes(Math.Min(60, Math.Pow(2, attempts.Value)));
        if (permanent)
            await db.OutboxMessages.Where(x => x.Id == id).ExecuteUpdateAsync(update => update
                .SetProperty(x => x.LastError, errorCode).SetProperty(x => x.Attempts, MaxAttempts)
                .SetProperty(x => x.NextAttemptAt, retryAt), cancellationToken);
        else
            await db.OutboxMessages.Where(x => x.Id == id).ExecuteUpdateAsync(update => update
                .SetProperty(x => x.LastError, errorCode).SetProperty(x => x.NextAttemptAt, retryAt), cancellationToken);
    }
}
