using FlowForge.Domain.Entities;
using FlowForge.Domain.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowForge.WebApi.Controllers;

[ApiController]
[Route("api/host-groups")]
public class HostGroupsController(
    IHostGroupRepository hostGroupRepo,
    IWorkflowHostRepository hostRepo) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "ViewerOrAbove")]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var groups = await hostGroupRepo.GetAllAsync(ct);
        var result = groups.Select(g => new
        {
            g.Id,
            g.Name,
            g.ConnectionId,
            HasRegistrationToken = g.RegistrationTokenHash != null,
            g.CreatedAt,
            g.UpdatedAt,
        });
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "ViewerOrAbove")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var group = await hostGroupRepo.GetByIdAsync(id, ct);
        if (group is null) return NotFound();
        return Ok(new
        {
            group.Id,
            group.Name,
            group.ConnectionId,
            HasRegistrationToken = group.RegistrationTokenHash != null,
            group.CreatedAt,
            group.UpdatedAt,
        });
    }

    [HttpPost]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Create([FromBody] CreateHostGroupRequest request, CancellationToken ct)
    {
        var group = HostGroup.Create(request.Name, request.ConnectionId);
        await hostGroupRepo.SaveAsync(group, ct);
        return CreatedAtAction(nameof(GetById), new { id = group.Id },
            new
            {
                group.Id,
                group.Name,
                group.ConnectionId,
                HasRegistrationToken = false,
                group.CreatedAt,
                group.UpdatedAt,
            });
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

    [HttpPost("{id:guid}/registration-token")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> GenerateRegistrationToken(Guid id, CancellationToken ct)
    {
        var group = await hostGroupRepo.GetByIdAsync(id, ct);
        if (group is null) return NotFound();

        var rawToken = group.GenerateRegistrationToken();
        await hostGroupRepo.SaveAsync(group, ct);

        return Ok(new { Token = rawToken });
    }

    [HttpDelete("{id:guid}/registration-token")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> RevokeRegistrationToken(Guid id, CancellationToken ct)
    {
        var group = await hostGroupRepo.GetByIdAsync(id, ct);
        if (group is null) return NotFound();

        group.RevokeRegistrationToken();
        await hostGroupRepo.SaveAsync(group, ct);
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
        return NoContent();
    }

    public record CreateHostGroupRequest(string Name, string ConnectionId);
    public record CreateHostRequest(string Name);
}
