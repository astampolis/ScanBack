using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ScanBack.Infrastructure;

public sealed class MailOptions
{
    public const string SectionName = "Mail";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string FromEmail { get; set; } = "";
    public string FromName { get; set; } = "ScanBack";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool UseStartTls { get; set; } = true;
    public bool Enabled { get; set; }
}

// Retained for source compatibility; application code depends on the application boundary.
public interface IEmailSender : ScanBack.Application.IEmailSender;

public sealed class SmtpEmailSender(IOptionsMonitor<MailOptions> options) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        var configuration = options.CurrentValue;
        if (!configuration.Enabled || string.IsNullOrWhiteSpace(configuration.Host) || string.IsNullOrWhiteSpace(configuration.FromEmail))
            throw new InvalidOperationException("SMTP configuration is unavailable.");

        // Production transport requires verified TLS. Plain SMTP is supported only
        // for an explicitly configured loopback development mail catcher.
        var transport = configuration.UseStartTls ? SecureSocketOptions.StartTls
            : configuration.Port == 465 ? SecureSocketOptions.SslOnConnect
            : IsLoopback(configuration.Host) ? SecureSocketOptions.None
            : throw new InvalidOperationException("SMTP TLS is required for remote hosts.");
        if (transport == SecureSocketOptions.None && !string.IsNullOrEmpty(configuration.Username))
            throw new InvalidOperationException("SMTP authentication requires TLS.");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(configuration.FromName, configuration.FromEmail));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var smtp = new SmtpClient { Timeout = 30000 };
        await smtp.ConnectAsync(configuration.Host, configuration.Port, transport, cancellationToken);
        if (!string.IsNullOrWhiteSpace(configuration.Username))
            await smtp.AuthenticateAsync(configuration.Username, configuration.Password, cancellationToken);
        await smtp.SendAsync(message, cancellationToken);
        // A failed QUIT after successful DATA acceptance must not cause the
        // worker to retry an already accepted message.
        try { await smtp.DisconnectAsync(true, cancellationToken); }
        catch (Exception) { }
    }

    private static bool IsLoopback(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host is "127.0.0.1" or "::1" or "[::1]";
}
