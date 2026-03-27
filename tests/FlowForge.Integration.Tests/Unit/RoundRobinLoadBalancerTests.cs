using FlowForge.Domain.Entities;
using FlowForge.JobOrchestrator.LoadBalancing;
using FluentAssertions;

namespace FlowForge.Integration.Tests.Unit;

/// <summary>
/// Pure unit tests for <see cref="RoundRobinLoadBalancer"/> — no containers required.
/// </summary>
public class RoundRobinLoadBalancerTests
{
    // ── Single host ───────────────────────────────────────────────────────────

    [Fact]
    public void Select_SingleHost_AlwaysReturnsThatHost()
    {
        var sut = new RoundRobinLoadBalancer();
        var host = MakeHost("host-1");
        var groupId = Guid.NewGuid();

        var result1 = sut.Select([host], groupId);
        var result2 = sut.Select([host], groupId);
        var result3 = sut.Select([host], groupId);

        result1.Should().Be(host);
        result2.Should().Be(host);
        result3.Should().Be(host);
    }

    // ── Round-robin distribution ──────────────────────────────────────────────

    [Fact]
    public void Select_TwoHosts_AlternatesBetweenThem()
    {
        var sut = new RoundRobinLoadBalancer();
        var h1 = MakeHost("host-1");
        var h2 = MakeHost("host-2");
        var hosts = new List<WorkflowHost> { h1, h2 };
        var groupId = Guid.NewGuid();

        var results = Enumerable.Range(0, 4)
            .Select(_ => sut.Select(hosts, groupId))
            .ToList();

        // Pattern: h1, h2, h1, h2 (or h2, h1, h2, h1 depending on start value)
        results[0].Should().NotBe(results[1], "consecutive calls must pick different hosts");
        results[0].Should().Be(results[2], "every other call should pick the same host");
        results[1].Should().Be(results[3]);
    }

    [Fact]
    public void Select_ThreeHosts_CyclesThroughAll()
    {
        var sut = new RoundRobinLoadBalancer();
        var h1 = MakeHost("host-1");
        var h2 = MakeHost("host-2");
        var h3 = MakeHost("host-3");
        var hosts = new List<WorkflowHost> { h1, h2, h3 };
        var groupId = Guid.NewGuid();

        // Collect one full cycle
        var selected = Enumerable.Range(0, 3)
            .Select(_ => sut.Select(hosts, groupId))
            .ToHashSet(ReferenceEqualityComparer.Instance);

        selected.Should().HaveCount(3, "all three hosts must be selected within one full cycle");
    }

    // ── Independent counters per host group ───────────────────────────────────

    [Fact]
    public void Select_DifferentHostGroups_HaveIndependentCounters()
    {
        var sut = new RoundRobinLoadBalancer();
        var ha = MakeHost("a-1");
        var hb = MakeHost("b-1");
        var hc = MakeHost("b-2");
        var groupA = Guid.NewGuid();
        var groupB = Guid.NewGuid();

        // Advance group B counter twice so it would pick index 2 if shared with group A
        sut.Select([hb, hc], groupB);
        sut.Select([hb, hc], groupB);

        // Group A starts fresh at index 0
        var fromGroupA = sut.Select([ha], groupA);

        fromGroupA.Should().Be(ha, "group A counter is independent of group B");
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public void Select_EmptyHostList_ThrowsInvalidOperationException()
    {
        var sut = new RoundRobinLoadBalancer();

        var act = () => sut.Select([], Guid.NewGuid());

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Select_HostListShrinks_StaysWithinBounds()
    {
        var sut = new RoundRobinLoadBalancer();
        var h1 = MakeHost("host-1");
        var h2 = MakeHost("host-2");
        var groupId = Guid.NewGuid();

        // Advance counter to 1 with two hosts
        sut.Select([h1, h2], groupId);

        // Now only one host remains — counter must wrap to 0
        var result = sut.Select([h1], groupId);

        result.Should().Be(h1, "index must wrap when host list shrinks");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static WorkflowHost MakeHost(string name)
    {
        var host = WorkflowHost.Create(name, Guid.NewGuid());
        host.MarkOnline();
        return host;
    }
}
