namespace FlowForge.Domain.Triggers;

public static class TriggerTypes
{
    public const string Schedule     = "schedule";
    public const string Sql          = "sql";
    public const string JobCompleted = "job-completed";
    public const string Webhook      = "webhook";
    public const string CustomScript = "custom-script";
}
