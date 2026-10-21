using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScanBack.Infrastructure;
using ScanBack.Infrastructure.Hosting;

namespace ScanBack.Deployment;

public static class DeploymentProgram
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            PrintUsage();
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        string? operation = null;
        try
        {
            var command = DeploymentArguments.Parse(args);
            operation = command.Command;
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = [],
                ApplicationName = typeof(DeploymentProgram).Assembly.GetName().Name,
                ContentRootPath = AppContext.BaseDirectory,
                EnvironmentName = command.EnvironmentName ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                    ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environments.Production
            });
            // Hosting and provider diagnostics may contain SQL endpoints or addresses.
            // The CLI emits only its fixed operational messages.
            builder.Logging.ClearProviders();
            builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);
            var configurationFile = command.ConfigurationFile ?? Environment.GetEnvironmentVariable("SCANBACK_CONFIG_FILE");
            if (!string.IsNullOrWhiteSpace(configurationFile))
            {
                if (!Path.IsPathFullyQualified(configurationFile) || !File.Exists(configurationFile))
                    throw new DeploymentCommandException("The external configuration file must be an existing absolute JSON path.");
                builder.Configuration.AddJsonFile(configurationFile, optional: false, reloadOnChange: false);
            }
            builder.Configuration.AddEnvironmentVariables();
            if (!string.IsNullOrWhiteSpace(configurationFile))
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["SCANBACK_CONFIG_FILE"] = configurationFile });

            var runtime = RuntimeConfiguration.Load(builder.Configuration, builder.Environment, isDeployment: true);
            RuntimeServiceRegistration.ValidateKeyProtection(runtime);
            if (command.Command == "check-config")
            {
                Console.WriteLine("Configuration validation passed. Database connectivity and SMTP delivery were not tested.");
                return 0;
            }
            if (command.Command == "smtp-test")
            {
                // Testing is deliberately excluded: only an explicitly named Development
                // environment may omit confirmation of an external message.
                if (!builder.Environment.IsDevelopment() && !command.ConfirmSend)
                    throw new DeploymentCommandException("smtp-test requires --confirm-send outside Development.");
                if (!runtime.Mail.Enabled)
                    throw new DeploymentCommandException("SMTP is disabled. Enable Mail:Enabled before testing delivery.");
                builder.Services.Configure<MailOptions>(builder.Configuration.GetSection(MailOptions.SectionName));
                builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
                using var mailHost = builder.Build();
                await using var scope = mailHost.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IEmailSender>().SendAsync(command.Recipient!,
                    "ScanBack SMTP configuration test",
                    "This is an explicitly requested ScanBack SMTP configuration test. It contains no rider, incident or access-token data.",
                    cancellation.Token);
                Console.WriteLine("The test message was accepted by the SMTP provider. Recipient delivery is not confirmed.");
                return 0;
            }

            builder.Services.AddScanBackDataProtection(runtime);
            builder.Services.AddScanBackPersistence(builder.Configuration, runtime, isDeployment: true);
            builder.Services.AddIdentityCore<ApplicationUser>(IdentityPolicy.Configure)
                .AddRoles<IdentityRole>()
                .AddEntityFrameworkStores<AppDbContext>()
                .AddDefaultTokenProviders();
            using var host = builder.Build();
            switch (command.Command)
            {
                case "migrate":
                    await DeploymentOperations.MigrateAsync(host.Services, cancellation.Token);
                    Console.WriteLine("Database migrations completed.");
                    break;
                case "initialize":
                    await DeploymentOperations.InitializeAsync(host.Services, cancellation.Token);
                    Console.WriteLine("Database baseline initialization completed. Existing settings were preserved.");
                    break;
                case "backfill-identity-secrets":
                    await DeploymentOperations.BackfillIdentitySecretsAsync(host.Services, cancellation.Token);
                    Console.WriteLine("Identity secret backfill completed. Existing authentication tokens were protected in place.");
                    break;
                case "create-admin":
                    await DeploymentOperations.CreateAdministratorAsync(host.Services, builder.Configuration, cancellation.Token);
                    Console.WriteLine("A new administrator account was created. Enroll multi-factor authentication before administrative access.");
                    break;
            }
            return 0;
        }
        catch (DeploymentCommandException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Operation cancelled.");
            return 130;
        }
        catch (Exception)
        {
            // Neither exception messages nor stack traces are safe deployment output:
            // providers can include passwords, SQL endpoints, recipients or tokens.
            Console.Error.WriteLine(operation == "smtp-test"
                ? "SMTP test failed. Check the protected SMTP configuration, TLS trust and network access."
                : "Deployment operation failed. Check required configuration, certificate access, SQL connectivity and deployment permissions.");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("ScanBack.Deployment check-config|migrate|initialize|create-admin|backfill-identity-secrets [--config <absolute-json-path>] [--environment <name>]");
        Console.WriteLine("ScanBack.Deployment smtp-test [--config <absolute-json-path>] [--environment <name>] --to <recipient> [--confirm-send]");
        Console.WriteLine("The default environment is Production; --environment overrides DOTNET_ENVIRONMENT and ASPNETCORE_ENVIRONMENT.");
        Console.WriteLine("Use SCANBACK_CONFIG_FILE for a shared external JSON path. Environment variables override JSON.");
        Console.WriteLine("Set Admin__Email and Admin__Password through protected configuration for create-admin; never pass credentials as arguments.");
        Console.WriteLine("Operational database commands use ConnectionStrings:DeploymentConnection when supplied, otherwise DefaultConnection.");
        Console.WriteLine("smtp-test sends exactly one message and requires --confirm-send outside Development. Other commands never send mail.");
    }
}
