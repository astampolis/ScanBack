using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ScanBack.Domain;
using ScanBack.Infrastructure;

namespace ScanBack.Deployment;

public static class DeploymentOperations
{
    public static async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>().Database;
        await database.MigrateAsync(cancellationToken);
    }

    /// <summary>Creates only missing baseline records; it never changes live platform settings.</summary>
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await WithTransactionAsync(services, async (provider, db) =>
        {
            var roles = provider.GetRequiredService<RoleManager<IdentityRole>>();
            if (!await roles.RoleExistsAsync("Administrator"))
            {
                var result = await roles.CreateAsync(new IdentityRole("Administrator"));
                if (!result.Succeeded) throw new DeploymentCommandException("Administrator role initialization failed.");
            }
            if (!await db.PlatformSettings.AnyAsync(cancellationToken))
                db.PlatformSettings.Add(new PlatformSettings());
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    /// <summary>Provisions a new account only. An existing account can never be promoted or reset here.</summary>
    public static async Task CreateAdministratorAsync(IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var email = configuration["Admin:Email"]?.Trim();
        var password = configuration["Admin:Password"];
        if (!DeploymentArguments.IsSingleMailbox(email) || string.IsNullOrWhiteSpace(password))
            throw new DeploymentCommandException("Set Admin__Email and Admin__Password through protected configuration or environment variables before create-admin.");

        await WithTransactionAsync(services, async (provider, db) =>
        {
            var users = provider.GetRequiredService<UserManager<ApplicationUser>>();
            var roles = provider.GetRequiredService<RoleManager<IdentityRole>>();
            if (!await roles.RoleExistsAsync("Administrator"))
                throw new DeploymentCommandException("Initialize the database before creating an administrator.");
            if (await users.FindByEmailAsync(email!) is not null || await users.FindByNameAsync(email!) is not null)
                throw new DeploymentCommandException("An account with that identity already exists. Existing accounts are never changed by create-admin.");

            var administrator = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, IsActive = true };
            var created = await users.CreateAsync(administrator, password);
            if (!created.Succeeded)
                throw new DeploymentCommandException("Administrator creation failed. Verify that the credentials satisfy the configured account policy.");
            var assigned = await users.AddToRoleAsync(administrator, "Administrator");
            if (!assigned.Succeeded)
                throw new DeploymentCommandException("Administrator role assignment failed; the account transaction was rolled back.");
            db.AuditEvents.Add(new AuditEvent { ActorId = administrator.Id, Action = "AdministratorProvisioned", EntityId = administrator.Id });
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Rewrites legacy Identity token values through the protected EF converter.
    /// Batches can safely be rerun after interruption; neither raw nor decoded values leave this process.
    /// </summary>
    public static async Task BackfillIdentitySecretsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        const int batchSize = 200;
        string? lastUserId = null;
        string? lastProvider = null;
        string? lastName = null;
        while (true)
        {
            var query = db.UserTokens.AsQueryable();
            if (lastUserId is not null)
                query = query.Where(token => string.Compare(token.UserId, lastUserId) > 0
                    || (token.UserId == lastUserId && (string.Compare(token.LoginProvider, lastProvider) > 0
                        || (token.LoginProvider == lastProvider && string.Compare(token.Name, lastName) > 0))));
            var tokens = await query.OrderBy(token => token.UserId).ThenBy(token => token.LoginProvider)
                .ThenBy(token => token.Name).Take(batchSize).ToListAsync(cancellationToken);
            if (tokens.Count == 0) break;
            foreach (var token in tokens)
                db.Entry(token).Property(value => value.Value).IsModified = true;
            await db.SaveChangesAsync(cancellationToken);
            var lastToken = tokens[^1];
            lastUserId = lastToken.UserId;
            lastProvider = lastToken.LoginProvider;
            lastName = lastToken.Name;
            db.ChangeTracker.Clear();
        }
        db.AuditEvents.Add(new AuditEvent { Action = "IdentitySecretsBackfilled" });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task WithTransactionAsync(IServiceProvider services, Func<IServiceProvider, AppDbContext, Task> operation,
        CancellationToken cancellationToken)
    {
        await using var strategyScope = services.CreateAsyncScope();
        var strategy = strategyScope.ServiceProvider.GetRequiredService<AppDbContext>().Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            // A fresh context on each retry avoids retaining entities from a rolled-back attempt.
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await operation(scope.ServiceProvider, db);
            await transaction.CommitAsync(cancellationToken);
        });
    }
}
