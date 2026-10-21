using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ScanBack.Application;

namespace ScanBack.Infrastructure;

/// <summary>Transactional retention processing independent of its scheduling host.</summary>
public sealed class RetentionProcessor(AppDbContext db, ILogger<RetentionProcessor> logger) : IRetentionProcessor
{
    public async Task RemoveExpiredDataAsync(CancellationToken cancellationToken = default)
    {
        var settings = await db.PlatformSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var cutoff = RetentionPolicy.Cutoff(now, settings?.IncidentRetentionDays);
        var expiredIncidents = db.Incidents.Where(x => x.ReportedAt < cutoff && x.ExpiresAt <= now);

        // SQL Server's execution strategy must own the whole transaction when
        // EnableRetryOnFailure is configured on the application DbContext.
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var outboxCount = await db.OutboxMessages.Where(x =>
                (x.IncidentId != null && expiredIncidents.Any(incident => incident.Id == x.IncidentId))
                || (x.CreatedAt < cutoff && (x.IncidentId == null
                    || db.Incidents.Any(incident => incident.Id == x.IncidentId && incident.ExpiresAt <= now))))
                .ExecuteDeleteAsync(cancellationToken);
            // Incident access grants cascade from the incident; no cross-profile
            // cascade is configured for contact or sticker foreign keys.
            var incidentCount = await expiredIncidents.ExecuteDeleteAsync(cancellationToken);
            var accessCount = await db.IncidentAccesses.Where(x => x.ExpiresAt < cutoff).ExecuteDeleteAsync(cancellationToken);
            var auditCount = await db.AuditEvents.Where(x => x.CreatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (outboxCount + incidentCount + accessCount + auditCount > 0)
                logger.LogInformation("Retention removed {OutboxCount} outbox entries, {IncidentCount} incidents, {AccessCount} access grants and {AuditCount} audit entries.",
                    outboxCount, incidentCount, accessCount, auditCount);
        });
    }
}
