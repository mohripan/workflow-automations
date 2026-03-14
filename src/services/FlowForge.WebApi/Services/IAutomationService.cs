using FlowForge.WebApi.DTOs.Requests;
using FlowForge.WebApi.DTOs.Responses;

namespace FlowForge.WebApi.Services;

public interface IAutomationService
{
    Task<IEnumerable<AutomationResponse>> GetAllAsync(CancellationToken ct);
    Task<AutomationResponse> GetByIdAsync(Guid id, CancellationToken ct);
    Task<AutomationResponse> CreateAsync(CreateAutomationRequest request, CancellationToken ct);
    Task<AutomationResponse> UpdateAsync(Guid id, UpdateAutomationRequest request, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
    Task FireWebhookAsync(Guid id, string? secret, CancellationToken ct);
}
