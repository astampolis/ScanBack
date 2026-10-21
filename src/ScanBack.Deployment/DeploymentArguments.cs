using System.Net.Mail;

namespace ScanBack.Deployment;

/// <summary>Strict command parsing avoids passing credentials through process arguments.</summary>
public sealed record DeploymentArguments(string Command, string? ConfigurationFile, string? Recipient, bool ConfirmSend, string? EnvironmentName = null)
{
    private static readonly HashSet<string> Commands = ["check-config", "migrate", "initialize", "create-admin", "backfill-identity-secrets", "smtp-test"];

    public static DeploymentArguments Parse(string[] args)
    {
        if (args.Length == 0 || !Commands.Contains(args[0]))
            throw new DeploymentCommandException("Choose check-config, migrate, initialize, create-admin, backfill-identity-secrets or smtp-test. Use --help for usage.");

        string? configFile = null;
        string? recipient = null;
        var confirmSend = false;
        string? environmentName = null;
        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--config" when configFile is null:
                    configFile = ReadValue(args, ref index, "--config requires an existing JSON configuration file.");
                    break;
                case "--environment" when environmentName is null:
                    environmentName = ReadValue(args, ref index, "--environment requires a host environment name.");
                    if (environmentName.Length > 64 || environmentName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
                        throw new DeploymentCommandException("--environment must contain only letters, numbers, hyphens or underscores.");
                    break;
                case "--to" when args[0] == "smtp-test" && recipient is null:
                    recipient = ReadValue(args, ref index, "smtp-test requires --to with one recipient address.");
                    break;
                case "--confirm-send" when args[0] == "smtp-test" && !confirmSend:
                    confirmSend = true;
                    break;
                default:
                    throw new DeploymentCommandException("An unknown, repeated or unsupported command argument was supplied. Credentials must be supplied through protected configuration or environment variables.");
            }
        }

        if (args[0] == "smtp-test" && !IsSingleMailbox(recipient))
            throw new DeploymentCommandException("smtp-test requires --to with one valid recipient address.");
        return new DeploymentArguments(args[0], configFile, recipient, confirmSend, environmentName);
    }

    public static bool IsSingleMailbox(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 254 && !value.Contains('\r') && !value.Contains('\n')
        && MailAddress.TryCreate(value, out var parsed)
        && string.Equals(parsed.Address, value, StringComparison.OrdinalIgnoreCase);

    private static string ReadValue(string[] args, ref int index, string message)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal))
            throw new DeploymentCommandException(message);
        return args[index];
    }
}

/// <summary>Only fixed, non-sensitive operational messages may be displayed by the CLI.</summary>
public sealed class DeploymentCommandException(string message) : Exception(message);
