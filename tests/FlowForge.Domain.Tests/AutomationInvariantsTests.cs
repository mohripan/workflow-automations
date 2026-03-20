using FlowForge.Domain.Entities;
using Xunit;
using FlowForge.Domain.Enums;
using FlowForge.Domain.Exceptions;
using FlowForge.Domain.ValueObjects;
using FluentAssertions;

namespace FlowForge.Domain.Tests;

public class AutomationInvariantsTests
{
    // ── Create invariants ────────────────────────────────────────────────────

    [Fact]
    public void Create_EmptyTriggerList_ThrowsInvalidAutomationException()
    {
        var act = () => Automation.Create(
            name: "Test",
            description: null,
            taskId: "task-1",
            hostGroupId: Guid.NewGuid(),
            triggers: [],
            conditionRoot: Leaf("any"));

        act.Should().Throw<InvalidAutomationException>()
            .WithMessage("*at least one trigger*");
    }

    [Fact]
    public void Create_NullConditionRoot_ThrowsInvalidAutomationException()
    {
        var trigger = Trigger.Create("t1", "schedule", "{}");
        var act = () => Automation.Create(
            name: "Test",
            description: null,
            taskId: "task-1",
            hostGroupId: Guid.NewGuid(),
            triggers: [trigger],
            conditionRoot: null!);

        act.Should().Throw<InvalidAutomationException>()
            .WithMessage("*ConditionRoot*");
    }

    [Fact]
    public void Create_ConditionReferencesUnknownTriggerName_ThrowsInvalidTriggerConditionException()
    {
        var trigger = Trigger.Create("known", "schedule", "{}");
        var act = () => Automation.Create(
            name: "Test",
            description: null,
            taskId: "task-1",
            hostGroupId: Guid.NewGuid(),
            triggers: [trigger],
            conditionRoot: Leaf("unknown-name"));

        act.Should().Throw<InvalidTriggerConditionException>()
            .WithMessage("*unknown-name*");
    }

    [Fact]
    public void Create_ValidInputs_Succeeds()
    {
        var trigger = Trigger.Create("daily", "schedule", "{}");
        var automation = Automation.Create(
            name: "My Automation",
            description: "desc",
            taskId: "task-1",
            hostGroupId: Guid.NewGuid(),
            triggers: [trigger],
            conditionRoot: Leaf("daily"));

        automation.Should().NotBeNull();
        automation.Name.Should().Be("My Automation");
        automation.IsEnabled.Should().BeTrue();
        automation.Triggers.Should().HaveCount(1);
        automation.ActiveJobId.Should().BeNull();
    }

    // ── Update invariants ────────────────────────────────────────────────────

    [Fact]
    public void Update_EmptyTriggerList_ThrowsInvalidAutomationException()
    {
        var automation = ValidAutomation();
        var act = () => automation.Update("New", null, "task", Guid.NewGuid(), [], Leaf("any"));

        act.Should().Throw<InvalidAutomationException>();
    }

    [Fact]
    public void Update_NullConditionRoot_ThrowsInvalidAutomationException()
    {
        var automation = ValidAutomation();
        var trigger = Trigger.Create("t", "schedule", "{}");
        var act = () => automation.Update("New", null, "task", Guid.NewGuid(), [trigger], null!);

        act.Should().Throw<InvalidAutomationException>();
    }

    [Fact]
    public void Update_ConditionReferencesUnknownTriggerName_ThrowsInvalidTriggerConditionException()
    {
        var automation = ValidAutomation();
        var trigger = Trigger.Create("known", "schedule", "{}");
        var act = () => automation.Update("New", null, "task", Guid.NewGuid(), [trigger], Leaf("ghost"));

        act.Should().Throw<InvalidTriggerConditionException>();
    }

    // ── ActiveJobId lifecycle ────────────────────────────────────────────────

    [Fact]
    public void SetActiveJob_SetsActiveJobId()
    {
        var automation = ValidAutomation();
        var jobId = Guid.NewGuid();

        automation.SetActiveJob(jobId);

        automation.ActiveJobId.Should().Be(jobId);
    }

    [Fact]
    public void ClearActiveJob_ResetsActiveJobIdToNull()
    {
        var automation = ValidAutomation();
        automation.SetActiveJob(Guid.NewGuid());

        automation.ClearActiveJob();

        automation.ActiveJobId.Should().BeNull();
    }

    [Fact]
    public void SetActiveJob_CalledTwice_OverwritesPreviousJobId()
    {
        var automation = ValidAutomation();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        automation.SetActiveJob(first);
        automation.SetActiveJob(second);

        automation.ActiveJobId.Should().Be(second);
    }

    // ── Enable / Disable ──────────────────────────────────────────────────────

    [Fact]
    public void Disable_SetsIsEnabledToFalse()
    {
        var automation = ValidAutomation();
        automation.Disable();

        automation.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Enable_AfterDisable_SetsIsEnabledToTrue()
    {
        var automation = ValidAutomation();
        automation.Disable();
        automation.Enable();

        automation.IsEnabled.Should().BeTrue();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Automation ValidAutomation()
    {
        var trigger = Trigger.Create("t1", "schedule", "{}");
        return Automation.Create("Auto", null, "task-1", Guid.NewGuid(), [trigger], Leaf("t1"));
    }

    private static TriggerConditionNode Leaf(string name) => new(null, name, null);
}
