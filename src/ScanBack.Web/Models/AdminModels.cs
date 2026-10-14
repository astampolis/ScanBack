using System.ComponentModel.DataAnnotations;

namespace ScanBack.Web.Models;

public sealed class AdminDashboardModel
{
    public int Accounts { get; init; }
    public int ActiveAccounts { get; init; }
    public int ActiveIdentifiers { get; init; }
    public int OpenIncidents { get; init; }
    public int QueuedNotifications { get; init; }
    public int AcceptedNotifications { get; init; }
    public int ExhaustedNotifications { get; init; }
    public bool NotificationsEnabled { get; init; }
}

public sealed class AdminAccountsModel
{
    public string? Search { get; init; }
    public IReadOnlyList<AdminAccountRow> Accounts { get; init; } = [];
}

public sealed class AdminAccountRow
{
    public string Id { get; init; } = "";
    public string Email { get; init; } = "";
    public bool IsActive { get; init; }
    public bool EmailConfirmed { get; init; }
    public bool IsAdministrator { get; set; }
    public bool IsCurrentAccount { get; set; }
}

public sealed class AdminIdentifiersModel
{
    public string? Search { get; init; }
    public IReadOnlyList<AdminIdentifierRow> Identifiers { get; init; } = [];
}

public sealed class AdminIdentifierRow
{
    public Guid Id { get; init; }
    public string Label { get; init; } = "";
    public string AccountId { get; init; } = "";
    public bool IsActive { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
}

public sealed class AdminIncidentsModel
{
    public IReadOnlyList<AdminIncidentRow> Incidents { get; init; } = [];
}

public sealed class AdminIncidentRow
{
    public Guid Id { get; init; }
    public Guid RiderProfileId { get; init; }
    public DateTimeOffset ReportedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public bool IsClosed { get; init; }
    public bool HasLocation { get; init; }
    public int QueuedNotifications { get; init; }
    public int AcceptedNotifications { get; init; }
    public int ExhaustedNotifications { get; init; }
}

public sealed class AdminSettingsModel
{
    [Range(7, 365)]
    public int IncidentRetentionDays { get; set; } = 30;

    [Range(1, 72)]
    public int IncidentAccessHours { get; set; } = 24;

    [Range(1, 60)]
    public int NotificationCooldownMinutes { get; set; } = 10;

    public bool NotificationsEnabled { get; set; } = true;
}

public sealed class AdminAuditModel
{
    public IReadOnlyList<AdminAuditRow> Events { get; init; } = [];
}

public sealed class AdminAuditRow
{
    public DateTimeOffset CreatedAt { get; init; }
    public string? ActorId { get; init; }
    public string Action { get; init; } = "";
    public string? EntityId { get; init; }
}


public static class AdminDisplay
{
    private static readonly TimeZoneInfo Athens = TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens");
    public static string LocalTime(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, Athens).ToString("dd/MM/yyyy HH:mm");
}
