using FlowForge.Domain.Tasks;

namespace FlowForge.Infrastructure.Tasks.Descriptors;

public class SendEmailTaskDescriptor : ITaskTypeDescriptor
{
    public string TaskId => "send-email";
    public string DisplayName => "Send Email";
    public string? Description => "Sends an email via SMTP to a recipient.";

    public IReadOnlyList<TaskParameterField> Parameters =>
    [
        new("to",      "To",      "text",     Required: true,  HelpText: "Recipient email address."),
        new("subject", "Subject", "text",     Required: true,  HelpText: "Email subject line."),
        new("body",    "Body",    "textarea", Required: true,  HelpText: "Email body (plain text)."),
        new("from",    "From",    "text",     Required: false, HelpText: "Override the default sender address.")
    ];
}
