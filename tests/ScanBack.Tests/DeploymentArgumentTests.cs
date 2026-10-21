using ScanBack.Deployment;

namespace ScanBack.Tests;

public sealed class DeploymentArgumentTests
{
    [Theory]
    [InlineData("migrate", "--password", "secret-canary")]
    [InlineData("migrate", "--connection", "secret-canary")]
    [InlineData("initialize", "--confirm-send", "")]
    [InlineData("create-admin", "--to", "someone@example.test")]
    [InlineData("migrate", "--environment", "Production\nsecret-canary")]
    public void Unsupported_or_credential_arguments_are_rejected_without_echoing_their_values(string command, string flag, string value)
    {
        var failure = Assert.Throws<DeploymentCommandException>(() => DeploymentArguments.Parse([command, flag, value]));
        Assert.DoesNotContain(value.Length == 0 ? "secret-canary" : value, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Display Name <recipient@example.test>")]
    [InlineData("one@example.test,two@example.test")]
    [InlineData("recipient@example.test\r\nBcc:extra@example.test")]
    [InlineData("invalid")]
    public void SMTP_test_refuses_ambiguous_or_header_injected_recipients(string recipient)
    {
        Assert.Throws<DeploymentCommandException>(() => DeploymentArguments.Parse(["smtp-test", "--to", recipient, "--confirm-send"]));
    }

    [Fact]
    public void CLI_parsing_requires_explicit_confirmation_for_sending_and_rejects_repeated_options()
    {
        Assert.False(DeploymentArguments.Parse(["smtp-test", "--to", "recipient@example.test"]).ConfirmSend);
        Assert.True(DeploymentArguments.Parse(["smtp-test", "--to", "recipient@example.test", "--confirm-send"]).ConfirmSend);
        Assert.Throws<DeploymentCommandException>(() => DeploymentArguments.Parse(["migrate", "--environment", "Production", "--environment", "Testing"]));
        Assert.Throws<DeploymentCommandException>(() => DeploymentArguments.Parse(["smtp-test", "--to", "recipient@example.test", "--confirm-send", "--confirm-send"]));
    }
}
