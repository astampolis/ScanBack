using ScanBack.Infrastructure.Hosting;
using ScanBack.Web.Hosting;

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.AddScanBackConfiguration();
    var runtime = RuntimeConfiguration.Load(builder.Configuration, builder.Environment);
    builder.Services.AddScanBackWeb(builder.Configuration, runtime);
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None);
    builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);
    builder.Logging.AddFilter("Microsoft.AspNetCore.Diagnostics", LogLevel.None);
    builder.WebHost.ConfigureKestrel(o => { o.Limits.MaxRequestBodySize = 3 * 1024 * 1024; o.AddServerHeader = false; });
    var app = builder.Build();
    app.UseScanBackWeb(runtime);
    await app.RunAsync();
    return 0;
}
catch (Exception exception) when (exception.GetType().Name != "HostAbortedException")
{
    Console.Error.WriteLine("ScanBack could not start. Run ScanBack.Deployment check-config with the protected production configuration and inspect the deployment guide.");
    return 1;
}

public partial class Program;
