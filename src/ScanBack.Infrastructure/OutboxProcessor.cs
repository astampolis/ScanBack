using System.Security.Cryptography;
using MailKit.Net.Smtp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScanBack.Application;
using ScanBack.Domain;

namespace ScanBack.Infrastructure;

/// <summary>
/// Durable, bounded outbox processing with atomic leases and rechecked authorization.
/// Delivery is at least once: failure after SMTP acceptance can cause a retry.
/// </summary>
public sealed class OutboxProcessor(
    AppDbContext db,
    ScanBack.Application.IEmailSender sender,
    IOptionsMonitor<MailOptions> mailOptions,
    ILogger<OutboxProcessor> logger) : IOutboxProcessor
{
    public async Task ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        if (!mailOptions.CurrentValue.Enabled) return;
        var settings = await db.PlatformSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var notificationsEnabled = settings?.NotificationsEnabled != false;

        var now = DateTimeOffset.UtcNow;
        // Project IDs first: decrypting one damaged body should not block the batch.
        var pendingIds = await db.OutboxMessages.AsNoTracking()
            .Where(x => x.AcceptedAt == null && x.Attempts < NotificationPolicy.MaxAttempts && x.NextAttemptAt <= now
                && (notificationsEnabled || x.Kind != "Incident"))
            .OrderBy(x => x.NextAttemptAt).ThenBy(x => x.CreatedAt)
            .Select(x => x.Id).Take(NotificationPolicy.BatchSize).ToListAsync(cancellationToken);

        foreach (var id in pendingIds)
        {
            var claimedAt = DateTimeOffset.UtcNow;
            var leaseUntil = claimedAt.Add(NotificationPolicy.LeaseDuration);
            var claimed = await db.OutboxMessages
                .Where(x => x.Id == id && x.AcceptedAt == null && x.Attempts < NotificationPolicy.MaxAttempts && x.NextAttemptAt <= claimedAt)
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
                && !x.EmergencyIdentifier.IsAdminBlocked && x.EmergencyIdentifier.RevokedAt == null
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
            && (contact.NotificationsRequested || contact.PublicPhoneRequested)
            && contact.VerificationExpiresAt > now && contact.VerificationTokenHash != ""
            && contact.Email == message.ToEmail && db.Users.Any(user => user.Id == contact.RiderProfile.UserId && user.IsActive), cancellationToken);
    }

    private static async Task MarkFailedAsync(AppDbContext db, Guid id, string errorCode, bool permanent, CancellationToken cancellationToken)
    {
        var attempts = await db.OutboxMessages.Where(x => x.Id == id).Select(x => (int?)x.Attempts).SingleOrDefaultAsync(cancellationToken);
        if (attempts is null) return;
        var retryAt = NotificationPolicy.RetryAt(DateTimeOffset.UtcNow, attempts.Value);
        if (permanent)
            await db.OutboxMessages.Where(x => x.Id == id).ExecuteUpdateAsync(update => update
                .SetProperty(x => x.LastError, errorCode).SetProperty(x => x.Attempts, NotificationPolicy.MaxAttempts)
                .SetProperty(x => x.NextAttemptAt, retryAt), cancellationToken);
        else
            await db.OutboxMessages.Where(x => x.Id == id).ExecuteUpdateAsync(update => update
                .SetProperty(x => x.LastError, errorCode).SetProperty(x => x.NextAttemptAt, retryAt), cancellationToken);
    }
}
