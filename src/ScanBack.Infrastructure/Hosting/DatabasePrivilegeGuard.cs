using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ScanBack.Infrastructure.Hosting;

/// <summary>Rejects administrative database identities in running production hosts.</summary>
public sealed class DatabasePrivilegeGuard(IServiceScopeFactory scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var privileged = await db.Database.SqlQueryRaw<int>("""
                SELECT CASE WHEN IS_SRVROLEMEMBER('sysadmin') = 1
                    OR IS_SRVROLEMEMBER('securityadmin') = 1
                    OR IS_MEMBER('db_owner') = 1 OR IS_MEMBER('db_ddladmin') = 1
                    OR IS_MEMBER('db_securityadmin') = 1 OR IS_MEMBER('db_accessadmin') = 1
                    OR HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER') = 1
                    OR HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE') = 1
                    OR HAS_PERMS_BY_NAME('dbo', 'SCHEMA', 'ALTER') = 1
                    THEN 1 ELSE 0 END AS [Value]
                """).SingleAsync(cancellationToken);
            if (privileged != 0) throw new InvalidOperationException("The production database identity has administrative or schema-changing permissions. Use the restricted runtime identity documented in the IIS guide.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException && exception is not InvalidOperationException)
        {
            throw new InvalidOperationException("Production database access could not be validated. Check SQL connectivity, TLS trust, database initialization and the restricted runtime identity.");
        }
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
