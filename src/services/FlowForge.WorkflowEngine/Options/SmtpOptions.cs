namespace FlowForge.WorkflowEngine.Options;

public class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string Host { get; init; } = "sandbox.smtp.mailtrap.io";
    public int Port { get; init; } = 2525;
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public bool EnableSsl { get; init; } = true;
    public string DefaultFromAddress { get; init; } = "noreply@flowforge.io";
}
