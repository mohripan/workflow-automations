using FlowForge.WebApi.DTOs.Requests;
using FluentValidation;

namespace FlowForge.WebApi.Validators;

public class CreateAutomationRequestValidator : AbstractValidator<CreateAutomationRequest>
{
    public CreateAutomationRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.HostGroupId).NotEmpty();
        RuleFor(x => x.Triggers).NotEmpty().WithMessage("At least one trigger is required");
        RuleFor(x => x.TriggerCondition).NotNull();
    }
}
