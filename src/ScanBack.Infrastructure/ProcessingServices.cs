using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ScanBack.Application;

namespace ScanBack.Infrastructure;

public static class ProcessingServices
{
    public static IServiceCollection AddProcessingServices(this IServiceCollection services)
    {
        services.TryAddScoped<IEmailSender, SmtpEmailSender>();
        services.TryAddScoped<ScanBack.Application.IEmailSender>(provider => provider.GetRequiredService<IEmailSender>());
        services.TryAddScoped<IOutboxProcessor, OutboxProcessor>();
        services.TryAddScoped<IRetentionProcessor, RetentionProcessor>();
        return services;
    }
}
