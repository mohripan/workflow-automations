using FlowForge.Domain.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowForge.WebApi.Controllers;

[ApiController]
[Route("api/audit-logs")]
[Authorize(Policy = "AdminOnly")]
public class AuditLogsController(IAuditLogRepository repository) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string?         entityId = null,
        [FromQuery] DateTimeOffset? from     = null,
        [FromQuery] DateTimeOffset? to       = null,
        CancellationToken           ct       = default)
    {
        var logs = await repository.GetAllAsync(entityId, from, to, ct);
        return Ok(logs);
    }
}
