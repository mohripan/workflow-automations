using FlowForge.Contracts.Events;
using FlowForge.WebApi.DTOs.Requests;
using FlowForge.WebApi.DTOs.Responses;

namespace FlowForge.WebApi.Services;

public interface IAutomationService
{
    Task<PagedResult<AutomationResponse>> GetAllAsync(AutomationQueryParams query, CancellationToken ct);
    Task<AutomationResponse> GetByIdAsync(Guid id, CancellationToken ct);
    Task<AutomationResponse> CreateAsync(CreateAutomationRequest request, CancellationToken ct);
    Task<AutomationResponse> UpdateAsync(Guid id, UpdateAutomationRequest request, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
    Task EnableAsync(Guid id, CancellationToken ct);
    Task DisableAsync(Guid id, CancellationToken ct);
    Task FireWebhookAsync(Guid id, string? rawBody, string? signatureHeader, CancellationToken ct);
    Task<IReadOnlyList<AutomationSnapshot>> GetAllSnapshotsAsync(CancellationToken ct);
}
