using FlowForge.Domain.Tasks;

namespace FlowForge.Infrastructure.Tasks.Descriptors;

public class HttpRequestTaskDescriptor : ITaskTypeDescriptor
{
    public string TaskId => "http-request";
    public string DisplayName => "HTTP Request";
    public string? Description => "Makes an outbound HTTP request and stores the response body in job outputs.";

    public IReadOnlyList<TaskParameterField> Parameters =>
    [
        new("url",    "URL",    "text", Required: true,  HelpText: "The full URL to send the request to."),
        new("method", "Method", "text", Required: false, DefaultValue: "GET",
            HelpText: "HTTP method: GET, POST, PUT, PATCH, DELETE.")
    ];
}
