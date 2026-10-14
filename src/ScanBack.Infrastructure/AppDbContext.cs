using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ScanBack.Domain;

namespace ScanBack.Infrastructure;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options, IDataProtectionProvider dataProtectionProvider)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<RiderProfile> RiderProfiles => Set<RiderProfile>();
    public DbSet<EmergencyContact> EmergencyContacts => Set<EmergencyContact>();
    public DbSet<EmergencyIdentifier> EmergencyIdentifiers => Set<EmergencyIdentifier>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<IncidentAccess> IncidentAccesses => Set<IncidentAccess>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ConsentRecord> ConsentRecords => Set<ConsentRecord>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<PlatformSettings> PlatformSettings => Set<PlatformSettings>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        var healthProtector = dataProtectionProvider.CreateProtector("ScanBack.HealthFields.v1");
        var healthConverter = new ValueConverter<string?, string?>(
            value => value == null ? null : healthProtector.Protect(value),
            value => value == null ? null : healthProtector.Unprotect(value));
        var messageProtector = dataProtectionProvider.CreateProtector("ScanBack.OutboxBodies.v1");
        var messageConverter = new ValueConverter<string, string>(
            value => messageProtector.Protect(value), value => messageProtector.Unprotect(value));

        builder.Entity<RiderProfile>(entity =>
        {
            entity.HasIndex(x => x.UserId).IsUnique();
            entity.Property(x => x.UserId).HasMaxLength(450);
            entity.Property(x => x.FullName).HasMaxLength(180);
            entity.Property(x => x.PhotoFileName).HasMaxLength(200);
            entity.HasOne<ApplicationUser>().WithOne().HasForeignKey<RiderProfile>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(x => x.BloodType).HasConversion(healthConverter).HasColumnType("nvarchar(max)");
            entity.Property(x => x.Allergies).HasConversion(healthConverter).HasColumnType("nvarchar(max)");
            entity.Property(x => x.Conditions).HasConversion(healthConverter).HasColumnType("nvarchar(max)");
            entity.Property(x => x.Medications).HasConversion(healthConverter).HasColumnType("nvarchar(max)");
            entity.Property(x => x.OtherInformation).HasConversion(healthConverter).HasColumnType("nvarchar(max)");
            entity.Property(x => x.Instructions).HasConversion(healthConverter).HasColumnType("nvarchar(max)");
        });

        builder.Entity<EmergencyContact>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(160);
            entity.Property(x => x.Relationship).HasMaxLength(80);
            entity.Property(x => x.Email).HasMaxLength(254);
            entity.Property(x => x.Phone).HasMaxLength(40);
            entity.Property(x => x.VerificationTokenHash).HasMaxLength(64).IsUnicode(false);
            entity.HasIndex(x => x.VerificationTokenHash).IsUnique().HasFilter("[VerificationTokenHash] <> ''");
            entity.HasOne(x => x.RiderProfile).WithMany(x => x.Contacts).HasForeignKey(x => x.RiderProfileId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<EmergencyIdentifier>(entity =>
        {
            entity.Property(x => x.Label).HasMaxLength(80);
            entity.Property(x => x.TokenHash).HasMaxLength(64).IsUnicode(false);
            entity.Property(x => x.ProtectedToken).HasMaxLength(1024);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasOne(x => x.RiderProfile).WithMany(x => x.Identifiers).HasForeignKey(x => x.RiderProfileId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Incident>(entity =>
        {
            entity.Property(x => x.LocationDescription).HasMaxLength(500);
            entity.Property(x => x.Consciousness).HasMaxLength(32);
            entity.Property(x => x.SubmissionKey).HasMaxLength(128).IsUnicode(false);
            entity.HasIndex(x => x.SubmissionKey).IsUnique();
            entity.HasIndex(x => new { x.RiderProfileId, x.ReportedAt });
            entity.HasIndex(x => x.ExpiresAt);
            entity.HasOne(x => x.RiderProfile).WithMany().HasForeignKey(x => x.RiderProfileId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne(x => x.EmergencyIdentifier).WithMany().HasForeignKey(x => x.EmergencyIdentifierId).OnDelete(DeleteBehavior.NoAction);
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_Incidents_Coordinates", "([Latitude] IS NULL AND [Longitude] IS NULL) OR ([Latitude] IS NOT NULL AND [Longitude] IS NOT NULL AND [Latitude] BETWEEN -90 AND 90 AND [Longitude] BETWEEN -180 AND 180)");
                table.HasCheckConstraint("CK_Incidents_PeopleInvolved", "[PeopleInvolved] BETWEEN 1 AND 100");
            });
        });

        builder.Entity<IncidentAccess>(entity =>
        {
            entity.Property(x => x.Role).HasMaxLength(32);
            entity.Property(x => x.TokenHash).HasMaxLength(64).IsUnicode(false);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => x.ExpiresAt);
            entity.HasOne(x => x.Incident).WithMany(x => x.AccessGrants).HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<EmergencyContact>().WithMany().HasForeignKey(x => x.EmergencyContactId).OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<OutboxMessage>(entity =>
        {
            entity.Property(x => x.ToEmail).HasMaxLength(254);
            entity.Property(x => x.Subject).HasMaxLength(300);
            entity.Property(x => x.Kind).HasMaxLength(32);
            entity.Property(x => x.LastError).HasMaxLength(80);
            entity.Property(x => x.Body).HasConversion(messageConverter).HasColumnType("nvarchar(max)");
            entity.HasIndex(x => new { x.AcceptedAt, x.NextAttemptAt });
            entity.HasIndex(x => x.CreatedAt);
            entity.HasOne<Incident>().WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<RiderProfile>().WithMany().HasForeignKey(x => x.RiderProfileId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<EmergencyContact>().WithMany().HasForeignKey(x => x.EmergencyContactId).OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<ConsentRecord>(entity =>
        {
            entity.Property(x => x.UserId).HasMaxLength(450);
            entity.Property(x => x.PolicyVersion).HasMaxLength(64);
            entity.Property(x => x.Action).HasMaxLength(64);
            entity.HasIndex(x => new { x.UserId, x.CreatedAt });
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AuditEvent>(entity =>
        {
            entity.Property(x => x.ActorId).HasMaxLength(450);
            entity.Property(x => x.Action).HasMaxLength(100);
            entity.Property(x => x.EntityId).HasMaxLength(128);
            entity.HasIndex(x => x.CreatedAt);
        });

        builder.Entity<PlatformSettings>(entity =>
        {
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_PlatformSettings_Singleton", "[Id] = 1");
                table.HasCheckConstraint("CK_PlatformSettings_Retention", "[IncidentRetentionDays] BETWEEN 1 AND 365");
                table.HasCheckConstraint("CK_PlatformSettings_Access", "[IncidentAccessHours] BETWEEN 1 AND 168");
                table.HasCheckConstraint("CK_PlatformSettings_Cooldown", "[NotificationCooldownMinutes] BETWEEN 1 AND 1440");
            });
        });
    }
}
