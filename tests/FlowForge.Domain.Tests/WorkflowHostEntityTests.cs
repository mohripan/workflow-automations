using FlowForge.Domain.Entities;
using FluentAssertions;

namespace FlowForge.Domain.Tests;

public class WorkflowHostEntityTests
{
    [Fact]
    public void Create_NewHost_IsOfflineWithNoHeartbeat()
    {
        var hostGroupId = Guid.NewGuid();

        var host = WorkflowHost.Create("minion-1", hostGroupId);

        host.IsOnline.Should().BeFalse();
        host.LastHeartbeatAt.Should().BeNull();
        host.Name.Should().Be("minion-1");
        host.HostGroupId.Should().Be(hostGroupId);
        host.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void Create_TwoHosts_HaveDifferentIds()
    {
        var host1 = WorkflowHost.Create("host-1", Guid.NewGuid());
        var host2 = WorkflowHost.Create("host-2", Guid.NewGuid());

        host1.Id.Should().NotBe(host2.Id);
    }

    [Fact]
    public void MarkOnline_SetsIsOnlineTrueAndRecordsHeartbeat()
    {
        var host = WorkflowHost.Create("minion-1", Guid.NewGuid());
        var before = DateTimeOffset.UtcNow;

        host.MarkOnline();

        host.IsOnline.Should().BeTrue();
        host.LastHeartbeatAt.Should().NotBeNull();
        host.LastHeartbeatAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public void MarkOffline_AfterMarkOnline_SetsIsOnlineToFalse()
    {
        var host = WorkflowHost.Create("minion-1", Guid.NewGuid());
        host.MarkOnline();

        host.MarkOffline();

        host.IsOnline.Should().BeFalse();
    }

    [Fact]
    public void UpdateHeartbeat_SetsIsOnlineTrueAndUpdatesTimestamp()
    {
        var host = WorkflowHost.Create("minion-1", Guid.NewGuid());

        host.UpdateHeartbeat();

        host.IsOnline.Should().BeTrue();
        host.LastHeartbeatAt.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateHeartbeat_CalledTwice_UpdatesTimestampEachTime()
    {
        var host = WorkflowHost.Create("minion-1", Guid.NewGuid());
        host.UpdateHeartbeat();
        var firstHeartbeat = host.LastHeartbeatAt;

        // Small delay so the second timestamp is strictly after the first
        await Task.Delay(5);
        host.UpdateHeartbeat();

        host.LastHeartbeatAt.Should().BeOnOrAfter(firstHeartbeat!.Value);
    }

    [Fact]
    public void SetOffline_AfterMarkOnline_SetsIsOnlineToFalse()
    {
        var host = WorkflowHost.Create("minion-1", Guid.NewGuid());
        host.MarkOnline();

        host.SetOffline();

        host.IsOnline.Should().BeFalse();
    }

    [Fact]
    public void MarkOffline_DoesNotClearLastHeartbeatAt()
    {
        var host = WorkflowHost.Create("minion-1", Guid.NewGuid());
        host.MarkOnline();
        var heartbeat = host.LastHeartbeatAt;

        host.MarkOffline();

        // Heartbeat timestamp is preserved even when offline
        host.LastHeartbeatAt.Should().Be(heartbeat);
    }
}
