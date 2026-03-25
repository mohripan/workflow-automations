using FlowForge.WebApi.DTOs.Requests;
using FlowForge.WebApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowForge.WebApi.Controllers;

[ApiController]
[Route("api/automations")]
public class AutomationsController(
    IAutomationService automationService,
    ILogger<AutomationsController> logger) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "ViewerOrAbove")]
    public async Task<IActionResult> GetAll([FromQuery] AutomationQueryParams query, CancellationToken ct)
        => Ok(await automationService.GetAllAsync(query, ct));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "ViewerOrAbove")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
        => Ok(await automationService.GetByIdAsync(id, ct));

    [HttpPost]
    [Authorize(Policy = "OperatorOrAbove")]
    public async Task<IActionResult> Create([FromBody] CreateAutomationRequest request, CancellationToken ct)
    {
        var created = await automationService.CreateAsync(request, ct);
        logger.LogInformation("Automation {AutomationId} ({Name}) created", created.Id, created.Name);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "OperatorOrAbove")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAutomationRequest request, CancellationToken ct)
    {
        var updated = await automationService.UpdateAsync(id, request, ct);
        logger.LogInformation("Automation {AutomationId} updated", id);
        return Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await automationService.DeleteAsync(id, ct);
        logger.LogInformation("Automation {AutomationId} deleted", id);
        return NoContent();
    }

    [HttpPut("{id:guid}/enable")]
    [Authorize(Policy = "OperatorOrAbove")]
    public async Task<IActionResult> Enable(Guid id, CancellationToken ct)
    {
        await automationService.EnableAsync(id, ct);
        logger.LogInformation("Automation {AutomationId} enabled", id);
        return NoContent();
    }

    [HttpPut("{id:guid}/disable")]
    [Authorize(Policy = "OperatorOrAbove")]
    public async Task<IActionResult> Disable(Guid id, CancellationToken ct)
    {
        await automationService.DisableAsync(id, ct);
        logger.LogInformation("Automation {AutomationId} disabled", id);
        return NoContent();
    }

    [HttpPost("{id:guid}/webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> FireWebhook(
        Guid id,
        [FromHeader(Name = "X-Webhook-Secret")] string? secret,
        CancellationToken ct)
    {
        await automationService.FireWebhookAsync(id, secret, ct);
        return Accepted();
    }

    [HttpGet("snapshots")]
    [Authorize(Policy = "InternalService")]
    public async Task<IActionResult> GetAllSnapshots(CancellationToken ct)
    {
        var result = await automationService.GetAllSnapshotsAsync(ct);
        return Ok(result);
    }
}
