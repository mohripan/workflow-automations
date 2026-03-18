using FlowForge.WebApi.DTOs.Requests;
using FluentValidation;

namespace FlowForge.WebApi.Validators;

public class CreateAutomationRequestValidator : AbstractValidator<CreateAutomationRequest>
{
    public CreateAutomationRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.HostGroupId).NotEmpty();
        RuleFor(x => x.TaskId).NotEmpty();

        RuleFor(x => x.Triggers)
            .NotEmpty()
            .WithMessage("At least one trigger is required.");

        RuleForEach(x => x.Triggers).ChildRules(trigger =>
        {
            trigger.RuleFor(t => t.Name)
                .NotEmpty().MaximumLength(100)
                .WithMessage("Each trigger must have a non-empty name (max 100 chars).");
            trigger.RuleFor(t => t.TypeId)
                .NotEmpty()
                .WithMessage("Each trigger must have a non-empty TypeId. Call GET /api/triggers/types.");
        });

        RuleFor(x => x.Triggers)
            .Must(triggers =>
                triggers.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() == triggers.Count)
            .WithMessage("Trigger names must be unique within an automation.");

        RuleFor(x => x.TriggerCondition)
            .NotNull()
            .WithMessage("TriggerCondition is required. " +
                         "For a single trigger, use: { \"triggerName\": \"your-trigger-name\" }.");

        RuleFor(x => x)
            .Must(req => req.TriggerCondition == null ||
                         AllConditionNamesExist(
                             req.TriggerCondition,
                             req.Triggers.Select(t => t.Name).ToHashSet(StringComparer.Ordinal)))
            .WithMessage("TriggerCondition references a TriggerName not present in the Triggers list.")
            .When(x => x.Triggers.Count > 0 && x.TriggerCondition is not null);
    }

    private static bool AllConditionNamesExist(TriggerConditionRequest node, HashSet<string> knownNames)
    {
        if (node.TriggerName is not null) return knownNames.Contains(node.TriggerName);
        return node.Nodes?.All(n => AllConditionNamesExist(n, knownNames)) ?? true;
    }
}
