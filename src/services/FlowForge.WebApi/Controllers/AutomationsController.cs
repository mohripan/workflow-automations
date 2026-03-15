using FlowForge.WebApi.DTOs.Requests;
using FlowForge.WebApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace FlowForge.WebApi.Controllers;

[ApiController]
[Route("api/automations")]
public class AutomationsController(IAutomationService automationService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] AutomationQueryParams query, CancellationToken ct)
    {
        var result = await automationService.GetAllAsync(query, ct);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var result = await automationService.GetByIdAsync(id, ct);
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAutomationRequest request, CancellationToken ct)
    {
        var created = await automationService.CreateAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAutomationRequest request, CancellationToken ct)
    {
        var updated = await automationService.UpdateAsync(id, request, ct);
        return Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await automationService.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpGet("snapshots")]
    public async Task<IActionResult> GetAllSnapshots(CancellationToken ct)
    {
        var result = await automationService.GetAllSnapshotsAsync(ct);
        return Ok(result);
    }

    [HttpPost("{id:guid}/webhook")]
    public async Task<IActionResult> FireWebhook(Guid id, [FromHeader(Name = "X-Webhook-Secret")] string? secret, CancellationToken ct)
    {
        await automationService.FireWebhookAsync(id, secret, ct);
        return Accepted();
    }
}
