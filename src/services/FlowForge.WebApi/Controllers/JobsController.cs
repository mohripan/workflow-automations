using FlowForge.Domain.Exceptions;
using FlowForge.Domain.Repositories;
using FlowForge.WebApi.DTOs.Requests;
using FlowForge.WebApi.DTOs.Responses;
using FlowForge.WebApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace FlowForge.WebApi.Controllers;

[ApiController]
[Route("api/{connectionId}/jobs")]
public class JobsController(IServiceProvider serviceProvider, IJobService jobService) : ControllerBase
{
    private IJobRepository GetRepo(string connectionId)
        => serviceProvider.GetRequiredKeyedService<IJobRepository>(connectionId);

    [HttpGet]
    public async Task<IActionResult> GetAll(string connectionId, [FromQuery] JobQueryParams query, CancellationToken ct)
    {
        var repo = GetRepo(connectionId);
        var result = await jobService.GetAllAsync(repo, query, ct);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(string connectionId, Guid id, CancellationToken ct)
    {
        var repo = GetRepo(connectionId);
        var result = await jobService.GetByIdAsync(repo, id, ct);
        return Ok(result);
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(string connectionId, Guid id, CancellationToken ct)
    {
        var repo = GetRepo(connectionId);
        await jobService.RequestCancelAsync(repo, id, ct);
        return Accepted();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(string connectionId, Guid id, CancellationToken ct)
    {
        var repo = GetRepo(connectionId);
        await jobService.RemoveAsync(repo, id, ct);
        return NoContent();
    }
}
