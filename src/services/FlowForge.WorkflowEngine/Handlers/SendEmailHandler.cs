using System.Net;
using System.Net.Mail;
using FlowForge.WorkflowEngine.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowForge.WorkflowEngine.Handlers;

public class SendEmailHandler(
    IOptions<SmtpOptions> smtpOptions,
    ILogger<SendEmailHandler> logger) : IWorkflowHandler
{
    public string TaskId => "send-email";

    public async Task<WorkflowResult> ExecuteAsync(WorkflowContext context, CancellationToken ct)
    {
        var opts = smtpOptions.Value;

        var to = context.GetParameter<string>("to");
        var subject = context.GetParameter<string>("subject");
        var body = context.GetParameter<string>("body");

        // Optional per-automation from address; falls back to configured default
        var from = opts.DefaultFromAddress;
        if (context.Parameters.TryGetValue("from", out var fromEl))
        {
            var fromVal = fromEl.GetString();
            if (!string.IsNullOrWhiteSpace(fromVal))
                from = fromVal;
        }

        logger.LogInformation("Sending email to {To} | subject: {Subject} | smtp: {Host}:{Port}",
            to, subject, opts.Host, opts.Port);

        using var client = new SmtpClient(opts.Host, opts.Port)
        {
            Credentials = new NetworkCredential(opts.Username, opts.Password),
            EnableSsl = opts.EnableSsl
        };

        using var message = new MailMessage(from, to, subject, body);
        await client.SendMailAsync(message, ct);

        logger.LogInformation("Email sent to {To}", to);
        return WorkflowResult.Success($"Email sent to {to}");
    }
}
