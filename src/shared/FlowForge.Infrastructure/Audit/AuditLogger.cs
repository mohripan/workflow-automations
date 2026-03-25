using System.Text.Json;
using FlowForge.Domain.Entities;
using FlowForge.Domain.Repositories;
using FlowForge.Infrastructure.Auth;

namespace FlowForge.Infrastructure.Audit;

public class AuditLogger(
    ICurrentUserService  currentUser,
    IAuditLogRepository  repository) : IAuditLogger
{
    public async Task LogAsync(
        string            action,
        string?           entityId = null,
        object?           detail   = null,
        CancellationToken ct       = default)
    {
        var detailJson = detail is null
            ? null
            : JsonSerializer.Serialize(detail);

        var log = AuditLog.Create(
            action:   action,
            entityId: entityId,
            userId:   currentUser.UserId,
            username: currentUser.Username,
            detail:   detailJson);

        await repository.AddAsync(log, ct);
    }
}
