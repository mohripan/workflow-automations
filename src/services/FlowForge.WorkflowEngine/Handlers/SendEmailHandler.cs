using Microsoft.Extensions.Logging;

namespace FlowForge.WorkflowEngine.Handlers;

public class SendEmailHandler(ILogger<SendEmailHandler> logger) : IWorkflowHandler
{
    public string TaskId => "send-email";

    public async Task<WorkflowResult> ExecuteAsync(WorkflowContext context, CancellationToken ct)
    {
        var to = context.GetParameter<string>("to");
        var subject = context.GetParameter<string>("subject");
        var body = context.GetParameter<string>("body");

        logger.LogInformation("Sending email to {To} with subject {Subject}", to, subject);
        
        // Simulate email sending
        await Task.Delay(1000, ct);

        return WorkflowResult.Success();
    }
}
