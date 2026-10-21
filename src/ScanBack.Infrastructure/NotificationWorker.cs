using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScanBack.Application;

namespace ScanBack.Infrastructure;

/// <summary>Host scheduling only; processing is reusable by the dedicated service.</summary>
public sealed class NotificationWorker(IServiceScopeFactory scopeFactory, ILogger<NotificationWorker> logger) : BackgroundService
{
    public const int MaxAttempts = NotificationPolicy.MaxAttempts;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            do
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<IOutboxProcessor>().ProcessPendingAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception)
                {
                    // Transport and database exceptions can contain personal data or credentials.
                    logger.LogWarning("Outbox polling failed; the next poll will retry.");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
