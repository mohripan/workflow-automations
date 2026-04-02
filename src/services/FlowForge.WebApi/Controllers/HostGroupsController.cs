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
        return Ok(new { group.Id, group.Name, group.ConnectionId, group.CreatedAt, group.UpdatedAt });
    }

    [HttpPost]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Create([FromBody] CreateHostGroupRequest request, CancellationToken ct)
    {
        var group = HostGroup.Create(request.Name, request.ConnectionId);
        await hostGroupRepo.SaveAsync(group, ct);
        return CreatedAtAction(nameof(GetById), new { id = group.Id },
            new { group.Id, group.Name, group.ConnectionId, group.CreatedAt, group.UpdatedAt });
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

    public record CreateHostGroupRequest(string Name, string ConnectionId);
}
