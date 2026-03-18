using FlowForge.Contracts.Events;
using FlowForge.Domain.Entities;
using FlowForge.Domain.Exceptions;
using FlowForge.Domain.Repositories;
using FlowForge.Domain.Triggers;
using FlowForge.Domain.ValueObjects;
using FlowForge.Infrastructure.Caching;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.WebApi.DTOs.Requests;
using FlowForge.WebApi.DTOs.Responses;

namespace FlowForge.WebApi.Services;

public class AutomationService(
    IAutomationRepository automationRepo,
    IHostGroupRepository hostGroupRepo,
    ITriggerTypeRegistry registry,
    IMessagePublisher publisher,
    IRedisService redis) : IAutomationService
{
    public async Task<PagedResult<AutomationResponse>> GetAllAsync(AutomationQueryParams query, CancellationToken ct)
    {
        var automations = await automationRepo.GetAllAsync(ct);

        var filtered = automations.AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.Name))
            filtered = filtered.Where(a => a.Name.Contains(query.Name, StringComparison.OrdinalIgnoreCase));

        var total = filtered.Count();
        var items = filtered
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(MapToResponse)
            .ToList();

        return new PagedResult<AutomationResponse>(items, total, query.Page, query.PageSize);
    }

    public async Task<AutomationResponse> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var automation = await automationRepo.GetByIdAsync(id, ct)
            ?? throw new AutomationNotFoundException(id);
        return MapToResponse(automation);
    }

    public async Task<AutomationResponse> CreateAsync(CreateAutomationRequest request, CancellationToken ct)
    {
        ValidateTriggerConfigs(request.Triggers);

        var triggers = request.Triggers
            .Select(t => Trigger.Create(t.Name, t.TypeId, t.ConfigJson))
            .ToList();

        var conditionRoot = MapConditionNode(request.TriggerCondition);

        var automation = Automation.Create(
            name: request.Name,
            description: request.Description,
            taskId: request.TaskId,
            hostGroupId: request.HostGroupId,
            triggers: triggers,
            conditionRoot: conditionRoot);

        await automationRepo.SaveAsync(automation, ct);

        var snapshot = await MapToSnapshotAsync(automation, ct);
        await publisher.PublishAsync(new AutomationChangedEvent(automation.Id, ChangeType.Created, snapshot), ct: ct);

        return MapToResponse(automation);
    }

    public async Task<AutomationResponse> UpdateAsync(Guid id, UpdateAutomationRequest request, CancellationToken ct)
    {
        var automation = await automationRepo.GetByIdAsync(id, ct)
            ?? throw new AutomationNotFoundException(id);

        ValidateTriggerConfigs(request.Triggers);

        var triggers = request.Triggers
            .Select(t => Trigger.Create(t.Name, t.TypeId, t.ConfigJson))
            .ToList();

        var conditionRoot = MapConditionNode(request.TriggerCondition);

        automation.Update(request.Name, request.Description, request.TaskId, request.HostGroupId, triggers, conditionRoot);

        await automationRepo.SaveAsync(automation, ct);

        var snapshot = await MapToSnapshotAsync(automation, ct);
        await publisher.PublishAsync(new AutomationChangedEvent(automation.Id, ChangeType.Updated, snapshot), ct: ct);

        return MapToResponse(automation);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var automation = await automationRepo.GetByIdAsync(id, ct)
            ?? throw new AutomationNotFoundException(id);
        await automationRepo.DeleteAsync(automation, ct);

        await publisher.PublishAsync(new AutomationChangedEvent(id, ChangeType.Deleted, null), ct: ct);
    }

    public async Task EnableAsync(Guid id, CancellationToken ct)
    {
        var automation = await automationRepo.GetByIdAsync(id, ct)
            ?? throw new AutomationNotFoundException(id);
        automation.Enable();
        await automationRepo.SaveAsync(automation, ct);

        var snapshot = await MapToSnapshotAsync(automation, ct);
        await publisher.PublishAsync(new AutomationChangedEvent(automation.Id, ChangeType.Updated, snapshot), ct: ct);
    }

    public async Task DisableAsync(Guid id, CancellationToken ct)
    {
        var automation = await automationRepo.GetByIdAsync(id, ct)
            ?? throw new AutomationNotFoundException(id);
        automation.Disable();
        await automationRepo.SaveAsync(automation, ct);

        var snapshot = await MapToSnapshotAsync(automation, ct);
        await publisher.PublishAsync(new AutomationChangedEvent(automation.Id, ChangeType.Updated, snapshot), ct: ct);
    }

    public async Task FireWebhookAsync(Guid id, string? secret, CancellationToken ct)
    {
        var automation = await automationRepo.GetByIdAsync(id, ct)
            ?? throw new AutomationNotFoundException(id);

        if (!automation.IsEnabled)
            throw new InvalidAutomationException($"Automation {id} is disabled.");

        var webhookTrigger = automation.Triggers.FirstOrDefault(t => t.TypeId == TriggerTypes.Webhook)
            ?? throw new InvalidAutomationException("Automation does not have a webhook trigger.");

        await redis.SetAsync(
            key: $"trigger:webhook:{webhookTrigger.Id}:fired",
            value: "1",
            expiry: TimeSpan.FromMinutes(10));
    }

    public async Task<IReadOnlyList<AutomationSnapshot>> GetAllSnapshotsAsync(CancellationToken ct)
    {
        var automations = await automationRepo.GetAllAsync(ct);
        var snapshots = new List<AutomationSnapshot>();
        foreach (var a in automations)
            snapshots.Add(await MapToSnapshotAsync(a, ct));
        return snapshots;
    }

    private void ValidateTriggerConfigs(IEnumerable<CreateTriggerRequest> triggers)
    {
        foreach (var t in triggers)
        {
            if (!registry.IsKnown(t.TypeId))
                throw new InvalidAutomationException(
                    $"Unknown trigger type '{t.TypeId}'. Call GET /api/triggers/types to see available types.");

            var errors = registry.Get(t.TypeId)!.ValidateConfig(t.ConfigJson);
            if (errors.Count > 0)
                throw new InvalidAutomationException(
                    $"Trigger '{t.Name}' (type '{t.TypeId}') has invalid config: {string.Join("; ", errors)}");
        }
    }

    private async Task<AutomationSnapshot> MapToSnapshotAsync(Automation a, CancellationToken ct)
    {
        var hostGroup = await hostGroupRepo.GetByIdAsync(a.HostGroupId, ct)
            ?? throw new InvalidAutomationException($"Host group {a.HostGroupId} not found.");

        return new AutomationSnapshot(
            Id: a.Id,
            Name: a.Name,
            IsEnabled: a.IsEnabled,
            HostGroupId: a.HostGroupId,
            ConnectionId: hostGroup.ConnectionId,
            TaskId: a.TaskId,
            Triggers: a.Triggers.Select(t => new TriggerSnapshot(t.Id, t.Name, t.TypeId, t.ConfigJson)).ToList(),
            ConditionRoot: MapConditionNodeToSnapshot(a.ConditionRoot)
        );
    }

    private static TriggerConditionNode MapConditionNodeToSnapshot(TriggerConditionNode node) => node;

    private static AutomationResponse MapToResponse(Automation a) => new(
        Id: a.Id,
        Name: a.Name,
        Description: a.Description,
        HostGroupId: a.HostGroupId,
        TaskId: a.TaskId,
        IsEnabled: a.IsEnabled,
        Triggers: a.Triggers.Select(t => new TriggerResponse(t.Id, t.Name, t.TypeId, t.ConfigJson)).ToList(),
        TriggerCondition: MapConditionResponse(a.ConditionRoot),
        CreatedAt: a.CreatedAt,
        UpdatedAt: a.UpdatedAt
    );

    private static TriggerConditionResponse MapConditionResponse(TriggerConditionNode node) => new(
        Operator: node.Operator,
        TriggerName: node.TriggerName,
        Nodes: node.Nodes?.Select(MapConditionResponse).ToList()
    );

    private static TriggerConditionNode MapConditionNode(TriggerConditionRequest r) => new(
        Operator: r.Operator,
        TriggerName: r.TriggerName,
        Nodes: r.Nodes?.Select(MapConditionNode).ToList()
    );
}
