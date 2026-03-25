using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlowForge.Contracts.Events;
using FlowForge.Domain.Entities;
using FlowForge.Domain.Exceptions;
using FlowForge.Domain.Repositories;
using FlowForge.Domain.Triggers;
using FlowForge.Domain.ValueObjects;
using FlowForge.Infrastructure.Audit;
using FlowForge.Infrastructure.Caching;
using FlowForge.Infrastructure.Encryption;
using FlowForge.Infrastructure.Messaging.Outbox;
using FlowForge.WebApi.DTOs.Requests;
using FlowForge.WebApi.DTOs.Responses;

namespace FlowForge.WebApi.Services;

public class AutomationService(
    IAutomationRepository automationRepo,
    IHostGroupRepository hostGroupRepo,
    ITriggerTypeRegistry registry,
    IEncryptionService encryption,
    IOutboxWriter outboxWriter,
    IRedisService redis,
    IAuditLogger auditLogger) : IAutomationService
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

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
            .Select(t => Trigger.Create(t.Name, t.TypeId, EncryptSensitiveFields(t.TypeId, t.ConfigJson)))
            .ToList();

        var conditionRoot = MapConditionNode(request.TriggerCondition);

        var automation = Automation.Create(
            name: request.Name,
            description: request.Description,
            taskId: request.TaskId,
            hostGroupId: request.HostGroupId,
            triggers: triggers,
            conditionRoot: conditionRoot,
            timeoutSeconds: request.TimeoutSeconds,
            maxRetries: request.MaxRetries,
            taskConfig: request.TaskConfig);

        var snapshot = await MapToSnapshotAsync(automation, ct);
        await outboxWriter.WriteAsync(new AutomationChangedEvent(automation.Id, ChangeType.Created, snapshot), ct);
        await automationRepo.SaveAsync(automation, ct);
        await auditLogger.LogAsync("automation.created", automation.Id.ToString(), new { automation.Name }, ct);

        return MapToResponse(automation);
    }

    public async Task<AutomationResponse> UpdateAsync(Guid id, UpdateAutomationRequest request, CancellationToken ct)
    {
        var automation = await automationRepo.GetByIdAsync(id, ct)
            ?? throw new AutomationNotFoundException(id);

        ValidateTriggerConfigs(request.Triggers);

        var triggers = request.Triggers
            .Select(t => Trigger.Create(t.Name, t.TypeId, EncryptSensitiveFields(t.TypeId, t.ConfigJson)))
            .ToList();

        var conditionRoot = MapConditionNode(request.TriggerCondition);

        automation.Update(request.Name, request.Description, request.TaskId, request.HostGroupId, triggers, conditionRoot, request.TimeoutSeconds, request.MaxRetries, request.TaskConfig);

        var snapshot = await MapToSnapshotAsync(automation, ct);
        await outboxWriter.WriteAsync(new AutomationChangedEvent(automation.Id, ChangeType.Updated, snapshot), ct);
        await automationRepo.SaveAsync(automation, ct);
        await auditLogger.LogAsync("automation.updated", automation.Id.ToString(), new { automation.Name }, ct);

        return MapToResponse(automation);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var automation = await automationRepo.GetByIdAsync(id, ct)
            ?? throw new AutomationNotFoundException(id);
        await outboxWriter.WriteAsync(new AutomationChangedEvent(id, ChangeType.Deleted, null), ct);
        await automationRepo.DeleteAsync(automation, ct);
        await auditLogger.LogAsync("automation.deleted", id.ToString(), new { automation.Name }, ct);
    }

    public async Task EnableAsync(Guid id, CancellationToken ct)
    {
        var automation = await automationRepo.GetByIdAsync(id, ct)
            ?? throw new AutomationNotFoundException(id);
        automation.Enable();

        var snapshot = await MapToSnapshotAsync(automation, ct);
        await outboxWriter.WriteAsync(new AutomationChangedEvent(automation.Id, ChangeType.Updated, snapshot), ct);
        await automationRepo.SaveAsync(automation, ct);
        await auditLogger.LogAsync("automation.enabled", id.ToString(), ct: ct);
    }

    public async Task DisableAsync(Guid id, CancellationToken ct)
    {
        var automation = await automationRepo.GetByIdAsync(id, ct)
            ?? throw new AutomationNotFoundException(id);
        automation.Disable();

        var snapshot = await MapToSnapshotAsync(automation, ct);
        await outboxWriter.WriteAsync(new AutomationChangedEvent(automation.Id, ChangeType.Updated, snapshot), ct);
        await automationRepo.SaveAsync(automation, ct);
        await auditLogger.LogAsync("automation.disabled", id.ToString(), ct: ct);
    }

    public async Task FireWebhookAsync(Guid id, string? rawBody, string? signatureHeader, CancellationToken ct)
    {
        var automation = await automationRepo.GetByIdAsync(id, ct)
            ?? throw new AutomationNotFoundException(id);

        if (!automation.IsEnabled)
            throw new InvalidAutomationException($"Automation {id} is disabled.");

        var webhookTrigger = automation.Triggers.FirstOrDefault(t => t.TypeId == TriggerTypes.Webhook)
            ?? throw new InvalidAutomationException("Automation does not have a webhook trigger.");

        var config = JsonSerializer.Deserialize<WebhookTriggerConfig>(webhookTrigger.ConfigJson);
        if (!string.IsNullOrEmpty(config?.Secret))
        {
            const string prefix = "sha256=";

            if (string.IsNullOrEmpty(signatureHeader) || !signatureHeader.StartsWith(prefix))
                throw new UnauthorizedWebhookException(id);

            byte[] expected;
            try { expected = Convert.FromHexString(signatureHeader[prefix.Length..]); }
            catch (FormatException) { throw new UnauthorizedWebhookException(id); }

            var decryptedSecret = encryption.Decrypt(config.Secret);
            var secretBytes     = Encoding.UTF8.GetBytes(decryptedSecret);
            var bodyBytes       = Encoding.UTF8.GetBytes(rawBody ?? string.Empty);
            var computed        = HMACSHA256.HashData(secretBytes, bodyBytes);

            if (!CryptographicOperations.FixedTimeEquals(computed, expected))
                throw new UnauthorizedWebhookException(id);
        }

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

    // ── Encryption helpers ───────────────────────────────────────────────────

    /// <summary>Encrypts sensitive fields in a trigger's configJson before storage.</summary>
    private string EncryptSensitiveFields(string typeId, string configJson)
    {
        var sensitiveFields = registry.Get(typeId)?.GetSensitiveFieldNames() ?? [];
        if (sensitiveFields.Count == 0) return configJson;

        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(configJson, _jsonOptions);
        if (doc is null) return configJson;

        var result = new Dictionary<string, object?>(doc.Count);
        foreach (var (key, value) in doc)
        {
            // Encrypt string values for sensitive fields (skip if already encrypted)
            if (sensitiveFields.Contains(key, StringComparer.OrdinalIgnoreCase)
                && value.ValueKind == JsonValueKind.String)
            {
                var plain = value.GetString() ?? string.Empty;
                result[key] = encryption.IsEncrypted(plain) ? plain : encryption.Encrypt(plain);
            }
            else
            {
                result[key] = value;
            }
        }

        return JsonSerializer.Serialize(result);
    }

    /// <summary>
    /// Replaces sensitive field values with "***" in the configJson returned to API callers.
    /// The stored (encrypted) value is never sent to the client.
    /// </summary>
    private string RedactSensitiveFields(string typeId, string configJson)
    {
        var sensitiveFields = registry.Get(typeId)?.GetSensitiveFieldNames() ?? [];
        if (sensitiveFields.Count == 0) return configJson;

        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(configJson, _jsonOptions);
        if (doc is null) return configJson;

        var result = new Dictionary<string, object?>(doc.Count);
        foreach (var (key, value) in doc)
        {
            if (sensitiveFields.Contains(key, StringComparer.OrdinalIgnoreCase)
                && value.ValueKind == JsonValueKind.String)
                result[key] = "***";
            else
                result[key] = value;
        }

        return JsonSerializer.Serialize(result);
    }

    // ── Private mapping ──────────────────────────────────────────────────────

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

        // Snapshot carries encrypted configJson — evaluators (in JobAutomator) decrypt at eval time.
        return new AutomationSnapshot(
            Id: a.Id,
            Name: a.Name,
            IsEnabled: a.IsEnabled,
            HostGroupId: a.HostGroupId,
            ConnectionId: hostGroup.ConnectionId,
            TaskId: a.TaskId,
            Triggers: a.Triggers.Select(t => new TriggerSnapshot(t.Id, t.Name, t.TypeId, t.ConfigJson)).ToList(),
            ConditionRoot: MapConditionNodeToSnapshot(a.ConditionRoot),
            TimeoutSeconds: a.TimeoutSeconds,
            MaxRetries: a.MaxRetries,
            TaskConfig: a.TaskConfig
        );
    }

    private static TriggerConditionNode MapConditionNodeToSnapshot(TriggerConditionNode node) => node;

    private AutomationResponse MapToResponse(Automation a) => new(
        Id: a.Id,
        Name: a.Name,
        Description: a.Description,
        HostGroupId: a.HostGroupId,
        TaskId: a.TaskId,
        IsEnabled: a.IsEnabled,
        TimeoutSeconds: a.TimeoutSeconds,
        MaxRetries: a.MaxRetries,
        TaskConfig: a.TaskConfig,
        // Sensitive fields are redacted — passwords never leave the server in API responses
        Triggers: a.Triggers.Select(t => new TriggerResponse(
            t.Id, t.Name, t.TypeId, RedactSensitiveFields(t.TypeId, t.ConfigJson))).ToList(),
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

    private record WebhookTriggerConfig(string? Secret);
}
