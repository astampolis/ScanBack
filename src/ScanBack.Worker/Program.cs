using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScanBack.Infrastructure;
using ScanBack.Infrastructure.Hosting;

try
{
    // Windows services start in System32; configuration must resolve beside the executable.
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
        EnvironmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environments.Production
    });
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);
    var configFile = Environment.GetEnvironmentVariable("SCANBACK_CONFIG_FILE");
    if (!string.IsNullOrWhiteSpace(configFile))
    {
        if (!Path.IsPathFullyQualified(configFile))
            throw new InvalidOperationException("SCANBACK_CONFIG_FILE must be an absolute path.");
        builder.Configuration.AddJsonFile(configFile, optional: false, reloadOnChange: false);
    }
    builder.Configuration.AddEnvironmentVariables();
    var runtime = RuntimeConfiguration.Load(builder.Configuration, builder.Environment);
    builder.Services.AddWindowsService(options => options.ServiceName = "ScanBack.Worker");
    builder.Services.AddScanBackDataProtection(runtime);
    builder.Services.AddScanBackPersistence(builder.Configuration, runtime);
    builder.Services.Configure<MailOptions>(builder.Configuration.GetSection(MailOptions.SectionName));
    builder.Services.AddProcessingServices();
    builder.Services.AddHostedService<NotificationWorker>();
    builder.Services.AddHostedService<RetentionWorker>();
    // Provider exceptions can include database values even when parameter logging is off.
    // Processors emit fixed failure codes without attaching the original exception.
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None);
    builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(45));
    using var host = builder.Build();
    await host.RunAsync();
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception)
{
    // Configuration/SQL/transport exception text can contain secrets or personal data.
    Console.Error.WriteLine("ScanBack.Worker could not start. Check its protected configuration, runtime identity and deployment prerequisites.");
    return 1;
}
