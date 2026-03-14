using Microsoft.Extensions.Logging;

namespace FlowForge.WorkflowEngine.Handlers;

public class HttpRequestHandler(IHttpClientFactory httpClientFactory, ILogger<HttpRequestHandler> logger) : IWorkflowHandler
{
    public string TaskId => "http-request";

    public async Task<WorkflowResult> ExecuteAsync(WorkflowContext context, CancellationToken ct)
    {
        var url = context.GetParameter<string>("url");
        var method = context.GetParameter<string>("method") ?? "GET";

        logger.LogInformation("Making HTTP {Method} request to {Url}", method, url);

        var client = httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        
        var response = await client.SendAsync(request, ct);

        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(ct);
            context.Outputs["responseBody"] = content;
            return WorkflowResult.Success();
        }

        return WorkflowResult.Failure($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
    }
}
