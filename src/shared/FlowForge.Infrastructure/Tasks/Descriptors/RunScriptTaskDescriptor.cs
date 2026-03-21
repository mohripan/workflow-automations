using FlowForge.Domain.Tasks;

namespace FlowForge.Infrastructure.Tasks.Descriptors;

public class RunScriptTaskDescriptor : ITaskTypeDescriptor
{
    public string TaskId => "run-script";
    public string DisplayName => "Run Script";
    public string? Description =>
        "Executes an external script or command. The job succeeds when the process exits with code 0.";

    public IReadOnlyList<TaskParameterField> Parameters =>
    [
        new("interpreter", "Interpreter", "text", Required: true,
            HelpText: "Path to the interpreter (e.g. python, bash, powershell)."),
        new("scriptPath",  "Script Path", "text", Required: true,
            HelpText: "Absolute path to the script file."),
        new("arguments",   "Arguments",  "text", Required: false,
            HelpText: "Optional command-line arguments passed to the script.")
    ];
}
