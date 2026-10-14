using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ScanBack.Infrastructure;

/// <summary>Removes expired incident data according to the platform's retention policy.</summary>
public sealed class RetentionWorker(IServiceScopeFactory scopeFactory, ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { await RemoveExpiredDataAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                logger.LogWarning("Retention cleanup failed; the next scheduled run will retry.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RemoveExpiredDataAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await db.PlatformSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var retentionDays = Math.Clamp(settings?.IncidentRetentionDays ?? 30, 1, 365);
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddDays(-retentionDays);
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
