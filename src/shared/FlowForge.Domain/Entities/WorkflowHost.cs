namespace FlowForge.Domain.Entities;

public class WorkflowHost : BaseEntity<Guid>
{
    public string Name { get; private set; } = default!;
    public Guid HostGroupId { get; private set; }
    public bool IsOnline { get; private set; }
    public DateTimeOffset? LastHeartbeatAt { get; private set; }

    private WorkflowHost() { }

    public static WorkflowHost Create(string name, Guid hostGroupId)
    {
        return new WorkflowHost
        {
            Id = Guid.NewGuid(),
            Name = name,
            HostGroupId = hostGroupId,
            IsOnline = false
        };
    }

    public void MarkOnline()
    {
        IsOnline = true;
        LastHeartbeatAt = DateTimeOffset.UtcNow;
        UpdateTimestamp();
    }

    public void MarkOffline()
    {
        IsOnline = false;
        UpdateTimestamp();
    }

    public void UpdateHeartbeat()
    {
        LastHeartbeatAt = DateTimeOffset.UtcNow;
        IsOnline = true;
        UpdateTimestamp();
    }

    public void SetOffline()
    {
        IsOnline = false;
        UpdateTimestamp();
    }
}
