using FlowForge.Domain.Entities;
using FlowForge.Domain.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowForge.WebApi.Controllers;

[ApiController]
[Route("api/host-groups")]
public class HostGroupsController(
    IHostGroupRepository hostGroupRepo,
    IWorkflowHostRepository hostRepo,
    IAuditLogRepository auditRepo) : ControllerBase
{
    private string? CurrentUsername => User.FindFirst("preferred_username")?.Value;

    [HttpGet]
    [Authorize(Policy = "ViewerOrAbove")]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var groups = await hostGroupRepo.GetAllAsync(ct);
        var result = groups.Select(MapGroupDto);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "ViewerOrAbove")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var group = await hostGroupRepo.GetByIdWithTokensAsync(id, ct);
        if (group is null) return NotFound();
        return Ok(MapGroupDto(group));
    }

    [HttpPost]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Create([FromBody] CreateHostGroupRequest request, CancellationToken ct)
    {
        var group = HostGroup.Create(request.Name, request.ConnectionId);
        await hostGroupRepo.SaveAsync(group, ct);
        await Audit("HostGroup.Created", group.Id.ToString(),
            $"Created host group '{request.Name}' (connection: {request.ConnectionId})", ct);
        return CreatedAtAction(nameof(GetById), new { id = group.Id }, MapGroupDto(group));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Delete(Guid id, [FromBody] DeleteHostGroupRequest request, CancellationToken ct)
    {
        var group = await hostGroupRepo.GetByIdAsync(id, ct);
        if (group is null) return NotFound();

        if (!string.Equals(group.Name, request.ConfirmName, StringComparison.Ordinal))
            return BadRequest(new { Message = "Confirmation name does not match the host group name." });

        var hosts = await hostRepo.GetByGroupAsync(id, ct);
        foreach (var host in hosts)
            await hostRepo.DeleteAsync(host, ct);

        await hostGroupRepo.DeleteAsync(group, ct);
        await Audit("HostGroup.Deleted", id.ToString(),
            $"Deleted host group '{group.Name}' and {hosts.Count} host(s)", ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/hosts")]
    [Authorize(Policy = "ViewerOrAbove")]
    public async Task<IActionResult> GetHosts(Guid id, CancellationToken ct)
    {
        var hosts = await hostRepo.GetByGroupAsync(id, ct);
        var result = hosts.Select(h => new
        {
            h.Id,
            h.Name,
            h.IsOnline,
            LastHeartbeat = h.LastHeartbeatAt,
        });
        return Ok(result);
    }

    [HttpGet("{id:guid}/tokens")]
    [Authorize(Policy = "ViewerOrAbove")]
    public async Task<IActionResult> GetTokens(Guid id, CancellationToken ct)
    {
        var group = await hostGroupRepo.GetByIdWithTokensAsync(id, ct);
        if (group is null) return NotFound();

        var result = group.RegistrationTokens.Select(t => new
        {
            t.Id,
            t.Label,
            t.ExpiresAt,
            t.IsExpired,
            t.CreatedAt,
        });
        return Ok(result);
    }

    [HttpPost("{id:guid}/registration-token")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> GenerateRegistrationToken(
        Guid id,
        [FromBody] GenerateTokenRequest? request,
        CancellationToken ct)
    {
        var group = await hostGroupRepo.GetByIdWithTokensAsync(id, ct);
        if (group is null) return NotFound();

        var ttl = TimeSpan.FromHours(request?.ExpiresInHours ?? 24);
        var (token, rawToken) = group.AddRegistrationToken(ttl, request?.Label);
        await hostGroupRepo.SaveAsync(group, ct);
        await Audit("RegistrationToken.Generated", group.Id.ToString(),
            $"Generated token '{request?.Label ?? "(unnamed)"}' for group '{group.Name}', expires in {ttl.TotalHours}h", ct);

        return Ok(new { Token = rawToken, token.Id, token.ExpiresAt, token.Label });
    }

    [HttpDelete("{id:guid}/registration-token/{tokenId:guid}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> RevokeRegistrationToken(Guid id, Guid tokenId, CancellationToken ct)
    {
        var group = await hostGroupRepo.GetByIdWithTokensAsync(id, ct);
        if (group is null) return NotFound();

        if (!group.RevokeRegistrationToken(tokenId))
            return NotFound(new { Message = "Token not found." });

        await hostGroupRepo.SaveAsync(group, ct);
        await Audit("RegistrationToken.Revoked", group.Id.ToString(),
            $"Revoked token {tokenId} from group '{group.Name}'", ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/hosts")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> CreateHost(Guid id, [FromBody] CreateHostRequest request, CancellationToken ct)
    {
        var group = await hostGroupRepo.GetByIdAsync(id, ct);
        if (group is null) return NotFound();

        var existing = await hostRepo.GetByNameAsync(request.Name, ct);
        if (existing is not null)
            return Conflict(new { Message = $"A host named '{request.Name}' already exists." });

        var host = WorkflowHost.Create(request.Name, id);
        await hostRepo.SaveAsync(host, ct);
        await Audit("Host.Added", group.Id.ToString(),
            $"Added host '{request.Name}' to group '{group.Name}'", ct);

        return Created($"/api/host-groups/{id}/hosts", new
        {
            host.Id,
            host.Name,
            host.IsOnline,
            LastHeartbeat = host.LastHeartbeatAt,
        });
    }

    [HttpDelete("{id:guid}/hosts/{hostId:guid}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> RemoveHost(Guid id, Guid hostId, CancellationToken ct)
    {
        var host = await hostRepo.GetByIdAsync(hostId, ct);
        if (host is null || host.HostGroupId != id) return NotFound();

        await hostRepo.DeleteAsync(host, ct);
        await Audit("Host.Removed", id.ToString(),
            $"Removed host '{host.Name}' from group {id}", ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/activity")]
    [Authorize(Policy = "ViewerOrAbove")]
    public async Task<IActionResult> GetActivity(Guid id, CancellationToken ct)
    {
        var logs = await auditRepo.GetAllAsync(entityId: id.ToString(), ct: ct);
        return Ok(logs.Take(50));
    }

    private static object MapGroupDto(HostGroup g) => new
    {
        g.Id,
        g.Name,
        g.ConnectionId,
        ActiveTokenCount = g.ActiveTokenCount,
        HasActiveTokens = g.HasActiveTokens,
        g.CreatedAt,
        g.UpdatedAt,
    };

    private async Task Audit(string action, string entityId, string detail, CancellationToken ct)
    {
        var log = AuditLog.Create(action, entityId,
            User.FindFirst("sub")?.Value, CurrentUsername, detail);
        await auditRepo.AddAsync(log, ct);
    }

    public record CreateHostGroupRequest(string Name, string ConnectionId);
    public record DeleteHostGroupRequest(string ConfirmName);
    public record CreateHostRequest(string Name);
    public record GenerateTokenRequest(string? Label, double? ExpiresInHours);
}
