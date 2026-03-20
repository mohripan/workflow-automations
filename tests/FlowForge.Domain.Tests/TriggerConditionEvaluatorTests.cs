using FlowForge.Domain.Enums;
using Xunit;
using FlowForge.Domain.ValueObjects;
using FlowForge.JobAutomator.Evaluators;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowForge.Domain.Tests;

public class TriggerConditionEvaluatorTests
{
    private readonly TriggerConditionEvaluator _sut = new(NullLogger<TriggerConditionEvaluator>.Instance);

    // ── Leaf node ────────────────────────────────────────────────────────────

    [Fact]
    public void SingleLeaf_WhenTriggerIsTrue_ReturnsTrue()
    {
        var node = Leaf("a");
        var results = Results(("a", true));

        _sut.Evaluate(node, results).Should().BeTrue();
    }

    [Fact]
    public void SingleLeaf_WhenTriggerIsFalse_ReturnsFalse()
    {
        var node = Leaf("a");
        var results = Results(("a", false));

        _sut.Evaluate(node, results).Should().BeFalse();
    }

    [Fact]
    public void SingleLeaf_WhenTriggerNameMissing_ReturnsFalse()
    {
        var node = Leaf("missing");
        var results = Results(("a", true));

        _sut.Evaluate(node, results).Should().BeFalse();
    }

    // ── AND ──────────────────────────────────────────────────────────────────

    [Fact]
    public void AndNode_BothTrue_ReturnsTrue()
    {
        var node = And(Leaf("a"), Leaf("b"));
        var results = Results(("a", true), ("b", true));

        _sut.Evaluate(node, results).Should().BeTrue();
    }

    [Fact]
    public void AndNode_FirstFalse_ReturnsFalse()
    {
        var node = And(Leaf("a"), Leaf("b"));
        var results = Results(("a", false), ("b", true));

        _sut.Evaluate(node, results).Should().BeFalse();
    }

    [Fact]
    public void AndNode_SecondFalse_ReturnsFalse()
    {
        var node = And(Leaf("a"), Leaf("b"));
        var results = Results(("a", true), ("b", false));

        _sut.Evaluate(node, results).Should().BeFalse();
    }

    [Fact]
    public void AndNode_BothFalse_ReturnsFalse()
    {
        var node = And(Leaf("a"), Leaf("b"));
        var results = Results(("a", false), ("b", false));

        _sut.Evaluate(node, results).Should().BeFalse();
    }

    // ── OR ───────────────────────────────────────────────────────────────────

    [Fact]
    public void OrNode_BothTrue_ReturnsTrue()
    {
        var node = Or(Leaf("a"), Leaf("b"));
        var results = Results(("a", true), ("b", true));

        _sut.Evaluate(node, results).Should().BeTrue();
    }

    [Fact]
    public void OrNode_FirstTrue_ReturnsTrue()
    {
        var node = Or(Leaf("a"), Leaf("b"));
        var results = Results(("a", true), ("b", false));

        _sut.Evaluate(node, results).Should().BeTrue();
    }

    [Fact]
    public void OrNode_SecondTrue_ReturnsTrue()
    {
        var node = Or(Leaf("a"), Leaf("b"));
        var results = Results(("a", false), ("b", true));

        _sut.Evaluate(node, results).Should().BeTrue();
    }

    [Fact]
    public void OrNode_BothFalse_ReturnsFalse()
    {
        var node = Or(Leaf("a"), Leaf("b"));
        var results = Results(("a", false), ("b", false));

        _sut.Evaluate(node, results).Should().BeFalse();
    }

    // ── Nested ───────────────────────────────────────────────────────────────

    [Fact]
    public void Nested_AndContainingOr_BothChildrenSatisfied_ReturnsTrue()
    {
        // AND( OR(a, b), c )
        var node = And(Or(Leaf("a"), Leaf("b")), Leaf("c"));
        var results = Results(("a", false), ("b", true), ("c", true));

        _sut.Evaluate(node, results).Should().BeTrue();
    }

    [Fact]
    public void Nested_AndContainingOr_OrFails_ReturnsFalse()
    {
        // AND( OR(a, b), c )
        var node = And(Or(Leaf("a"), Leaf("b")), Leaf("c"));
        var results = Results(("a", false), ("b", false), ("c", true));

        _sut.Evaluate(node, results).Should().BeFalse();
    }

    [Fact]
    public void Nested_OrContainingAnd_OneChildSatisfied_ReturnsTrue()
    {
        // OR( AND(a, b), c )
        var node = Or(And(Leaf("a"), Leaf("b")), Leaf("c"));
        var results = Results(("a", false), ("b", false), ("c", true));

        _sut.Evaluate(node, results).Should().BeTrue();
    }

    [Fact]
    public void Nested_OrContainingAnd_NoneChildSatisfied_ReturnsFalse()
    {
        // OR( AND(a, b), c )
        var node = Or(And(Leaf("a"), Leaf("b")), Leaf("c"));
        var results = Results(("a", true), ("b", false), ("c", false));

        _sut.Evaluate(node, results).Should().BeFalse();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static TriggerConditionNode Leaf(string name) =>
        new(null, name, null);

    private static TriggerConditionNode And(params TriggerConditionNode[] children) =>
        new(ConditionOperator.And, null, children);

    private static TriggerConditionNode Or(params TriggerConditionNode[] children) =>
        new(ConditionOperator.Or, null, children);

    private static IReadOnlyDictionary<string, bool> Results(params (string name, bool value)[] entries) =>
        entries.ToDictionary(e => e.name, e => e.value);
}
