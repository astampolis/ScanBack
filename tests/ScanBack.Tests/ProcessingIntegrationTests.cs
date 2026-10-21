using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScanBack.Application;
using ScanBack.Domain;
using ScanBack.Infrastructure;

namespace ScanBack.Tests;

[Collection(SqlWebCollection.Name)]
public sealed class ProcessingIntegrationTests(SqlWebFixture fixture)
{
    [Fact]
    public async Task Concurrent_workers_atomically_lease_a_durable_message_and_send_it_once()
    {
        var message = Message();
        await fixture.InDatabase(async db => { db.Add(message); await db.SaveChangesAsync(); });
        var sender = new ControlledSender(message.ToEmail);
        using var firstScope = fixture.Factory.Services.CreateScope();
        using var secondScope = fixture.Factory.Services.CreateScope();
        var first = Processor(firstScope, sender).ProcessPendingAsync();
        try
        {
            await sender.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var leased = await fixture.InDatabase(db => db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == message.Id));
            Assert.Equal(1, leased.Attempts);
            Assert.Null(leased.AcceptedAt);
            Assert.True(leased.NextAttemptAt > DateTimeOffset.UtcNow.AddMinutes(4));
            await Processor(secondScope, sender).ProcessPendingAsync();
            Assert.Equal(1, sender.SendCount);
        }
        finally { sender.Release.TrySetResult(); await first; }
        var accepted = await fixture.InDatabase(db => db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == message.Id));
        Assert.NotNull(accepted.AcceptedAt);
        Assert.Equal(1, accepted.Attempts);
        Assert.Null(accepted.LastError);
    }

    [Fact]
    public async Task Transport_failure_persists_redacted_error_and_retry_then_a_new_worker_accepts_it()
    {
        var message = Message();
        await fixture.InDatabase(async db => { db.Add(message); await db.SaveChangesAsync(); });
        using (var scope = fixture.Factory.Services.CreateScope())
            await Processor(scope, new FailingSender(message.ToEmail)).ProcessPendingAsync();
        var failed = await fixture.InDatabase(db => db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == message.Id));
        Assert.Null(failed.AcceptedAt);
        Assert.Equal(1, failed.Attempts);
        Assert.Equal("Email transport unavailable", failed.LastError);
        Assert.True(failed.NextAttemptAt > DateTimeOffset.UtcNow.AddMinutes(1));
        await fixture.InDatabase(async db =>
        {
            await db.OutboxMessages.Where(x => x.Id == message.Id).ExecuteUpdateAsync(update => update.SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow.AddYears(-1)));
        });
        var sender = new RecordingSender(message.ToEmail);
        using (var scope = fixture.Factory.Services.CreateScope())
            await Processor(scope, sender).ProcessPendingAsync();
        var accepted = await fixture.InDatabase(db => db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == message.Id));
        Assert.Equal(1, sender.SendCount);
        Assert.Equal(2, accepted.Attempts);
        Assert.NotNull(accepted.AcceptedAt);
        Assert.Null(accepted.LastError);
    }

    [Fact]
    public async Task Verification_messages_are_delivered_while_notification_permission_is_still_pending()
    {
        var rider = await fixture.SeedRider(verifiedContact: false);
        var message = Message();
        message.Kind = "ContactVerification";
        message.RiderProfileId = rider.Profile.Id;
        message.EmergencyContactId = rider.Contact.Id;
        message.ToEmail = rider.Contact.Email;
        await fixture.InDatabase(async db =>
        {
            var contact = (await db.EmergencyContacts.FindAsync(rider.Contact.Id))!;
            contact.NotifyEnabled = false;
            contact.NotificationsRequested = true;
            contact.VerificationTokenHash = TokenService.Hash(TokenService.Generate());
            contact.VerificationExpiresAt = DateTimeOffset.UtcNow.AddDays(1);
            db.Add(message);
            await db.SaveChangesAsync();
        });
        var sender = new RecordingSender(message.ToEmail);
        using (var scope = fixture.Factory.Services.CreateScope())
            await Processor(scope, sender).ProcessPendingAsync();
        Assert.Equal(1, sender.SendCount);
        Assert.NotNull(await fixture.InDatabase(db => db.OutboxMessages.Where(x => x.Id == message.Id).Select(x => x.AcceptedAt).SingleAsync()));
        Assert.False(await fixture.InDatabase(db => db.EmergencyContacts.Where(x => x.Id == rider.Contact.Id).Select(x => x.NotifyEnabled).SingleAsync()));
    }

    [Fact]
    public async Task An_expired_worker_lease_is_reclaimed_from_durable_storage()
    {
        var message = Message();
        message.Attempts = 1;
        message.NextAttemptAt = DateTimeOffset.UtcNow.AddYears(-1);
        await fixture.InDatabase(async db => { db.Add(message); await db.SaveChangesAsync(); });
        var sender = new RecordingSender(message.ToEmail);
        using (var scope = fixture.Factory.Services.CreateScope())
            await Processor(scope, sender).ProcessPendingAsync();
        var recovered = await fixture.InDatabase(db => db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == message.Id));
        Assert.Equal(1, sender.SendCount);
        Assert.Equal(2, recovered.Attempts);
        Assert.NotNull(recovered.AcceptedAt);
    }

    [Theory]
    [InlineData("admin-blocked-sticker")]
    [InlineData("revoked-grant")]
    [InlineData("disabled-contact")]
    [InlineData("expired-incident")]
    [InlineData("inactive-rider")]
    [InlineData("changed-email")]
    public async Task Notification_authorization_is_rechecked_after_queueing_and_before_transport(string reason)
    {
        var rider = await fixture.SeedRider();
        var incident = await fixture.SeedIncident(rider);
        var message = Message();
        message.Kind = "Incident";
        message.IncidentId = incident.IncidentId;
        message.RiderProfileId = rider.Profile.Id;
        message.EmergencyContactId = rider.Contact.Id;
        message.ToEmail = rider.Contact.Email;
        await fixture.InDatabase(async db =>
        {
            db.Add(message);
            switch (reason)
            {
                case "admin-blocked-sticker": (await db.EmergencyIdentifiers.FindAsync(rider.Identifier.Id))!.IsAdminBlocked = true; break;
                case "revoked-grant": (await db.IncidentAccesses.FindAsync(incident.AccessId))!.IsRevoked = true; break;
                case "disabled-contact": (await db.EmergencyContacts.FindAsync(rider.Contact.Id))!.NotifyEnabled = false; break;
                case "expired-incident": (await db.Incidents.FindAsync(incident.IncidentId))!.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); break;
                case "inactive-rider": (await db.Users.FindAsync(rider.UserId))!.IsActive = false; break;
                case "changed-email": (await db.EmergencyContacts.FindAsync(rider.Contact.Id))!.Email = "new-private-address@example.test"; break;
            }
            await db.SaveChangesAsync();
        });
        var sender = new RecordingSender(message.ToEmail);
        using (var scope = fixture.Factory.Services.CreateScope())
            await Processor(scope, sender).ProcessPendingAsync();
        var suppressed = await fixture.InDatabase(db => db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == message.Id));
        Assert.Equal(0, sender.SendCount);
        Assert.Null(suppressed.AcceptedAt);
        Assert.Equal(NotificationPolicy.MaxAttempts, suppressed.Attempts);
        Assert.Equal("Notification no longer authorized", suppressed.LastError);
    }

    [Fact]
    public async Task Retention_removes_expired_SQL_rows_and_related_grants_but_preserves_current_personal_data()
    {
        var rider = await fixture.SeedRider();
        var expired = await fixture.SeedIncident(rider);
        var current = await fixture.SeedIncident(rider);
        var oldAudit = new AuditEvent { Action = "RetentionExpired", CreatedAt = DateTimeOffset.UtcNow.AddDays(-400) };
        var currentAudit = new AuditEvent { Action = "RetentionCurrent" };
        var expiredOutbox = Message();
        expiredOutbox.IncidentId = expired.IncidentId;
        expiredOutbox.CreatedAt = DateTimeOffset.UtcNow.AddDays(-400);
        var currentOutbox = Message();
        currentOutbox.IncidentId = current.IncidentId;
        var orphanedOldOutbox = Message();
        orphanedOldOutbox.CreatedAt = DateTimeOffset.UtcNow.AddDays(-400);
        await fixture.InDatabase(async db =>
        {
            var incident = (await db.Incidents.FindAsync(expired.IncidentId))!;
            incident.ReportedAt = DateTimeOffset.UtcNow.AddDays(-400);
            incident.ExpiresAt = DateTimeOffset.UtcNow.AddDays(-399);
            db.AddRange(oldAudit, currentAudit, expiredOutbox, currentOutbox, orphanedOldOutbox);
            await db.SaveChangesAsync();
        });
        using (var scope = fixture.Factory.Services.CreateScope())
            await new RetentionProcessor(scope.ServiceProvider.GetRequiredService<AppDbContext>(), NullLogger<RetentionProcessor>.Instance).RemoveExpiredDataAsync();
        await fixture.InDatabase(async db =>
        {
            Assert.False(await db.Incidents.AnyAsync(x => x.Id == expired.IncidentId));
            Assert.False(await db.IncidentAccesses.AnyAsync(x => x.Id == expired.AccessId));
            Assert.False(await db.OutboxMessages.AnyAsync(x => x.Id == expiredOutbox.Id || x.Id == orphanedOldOutbox.Id));
            Assert.False(await db.AuditEvents.AnyAsync(x => x.Id == oldAudit.Id));
            Assert.True(await db.Incidents.AnyAsync(x => x.Id == current.IncidentId));
            Assert.True(await db.IncidentAccesses.AnyAsync(x => x.Id == current.AccessId));
            Assert.True(await db.OutboxMessages.AnyAsync(x => x.Id == currentOutbox.Id));
            Assert.True(await db.AuditEvents.AnyAsync(x => x.Id == currentAudit.Id));
            Assert.True(await db.Users.AnyAsync(x => x.Id == rider.UserId));
            Assert.True(await db.EmergencyContacts.AnyAsync(x => x.Id == rider.Contact.Id));
        });
    }

    private static OutboxMessage Message() => new()
    {
        ToEmail = "processor-" + Guid.NewGuid().ToString("N") + "@example.test",
        Subject = "Integration processor test", Body = "Encrypted durable test body", Kind = "Testing", NextAttemptAt = DateTimeOffset.UtcNow.AddYears(-1)
    };

    private static OutboxProcessor Processor(IServiceScope scope, ScanBack.Application.IEmailSender sender) =>
        new(scope.ServiceProvider.GetRequiredService<AppDbContext>(), sender, new MailMonitor(), NullLogger<OutboxProcessor>.Instance);

    private sealed class MailMonitor : IOptionsMonitor<MailOptions>
    {
        public MailOptions CurrentValue { get; } = new() { Enabled = true };
        public MailOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<MailOptions, string?> listener) => null;
    }

    private class RecordingSender(string target) : ScanBack.Application.IEmailSender
    {
        protected string Target { get; } = target;
        private int _sendCount;
        public int SendCount => Volatile.Read(ref _sendCount);
        public virtual Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
        {
            if (to == Target) Interlocked.Increment(ref _sendCount);
            return Task.CompletedTask;
        }
    }

    private sealed class ControlledSender(string target) : RecordingSender(target)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
        {
            await base.SendAsync(to, subject, body, cancellationToken);
            if (to != Target) return;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
    }

    private sealed class FailingSender(string target) : RecordingSender(target)
    {
        public override Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default) =>
            to == Target ? Task.FromException(new IOException("private SMTP password and email must never enter operational errors")) : base.SendAsync(to, subject, body, cancellationToken);
    }
}
