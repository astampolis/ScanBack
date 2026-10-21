using Microsoft.AspNetCore.Diagnostics;

namespace ScanBack.Web.Hosting;

public sealed class SafeExceptionHandler(ILogger<SafeExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError("A request failed. Diagnostic reference: {Reference}.", context.TraceIdentifier);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync("<!doctype html><html lang=\"en\"><meta name=\"viewport\" content=\"width=device-width\"><title>ScanBack</title><body><h1>We could not complete this request</h1><p>Please try again. If someone needs emergency help, call immediately.</p><p><a href=\"tel:112\">Call 112</a> · <a href=\"tel:166\">Call 166 — EKAB</a></p></body></html>", cancellationToken);
        return true;
    }
}
