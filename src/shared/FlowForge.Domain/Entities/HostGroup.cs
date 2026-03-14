namespace FlowForge.Domain.Entities;

public class HostGroup : BaseEntity<Guid>
{
    public string Name { get; private set; } = default!;
    public string ConnectionId { get; private set; } = default!;

    private HostGroup() { }

    public static HostGroup Create(string name, string connectionId)
    {
        return new HostGroup
        {
            Id = Guid.NewGuid(),
            Name = name,
            ConnectionId = connectionId
        };
    }
}
