using FlowForge.Infrastructure.Audit;
using FlowForge.Infrastructure.Messaging.DeadLetter;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlowForge.WebApi.Controllers;

public record DlqEntryResponse(
    string Id,
    string SourceStream,
    string MessageId,
    string Payload,
    string Error,
    string FailedAt
);

[ApiController]
[Route("api/dlq")]
[Authorize(Policy = "AdminOnly")]
public class DlqController(
    IDlqReader dlqReader,
    ILogger<DlqController> logger,
    IAuditLogger auditLogger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetEntries([FromQuery] int limit = 50)
    {
        var entries = await dlqReader.GetEntriesAsync(limit);
        var result = entries.Select(e => new DlqEntryResponse(
            e.Id, e.SourceStream, e.MessageId, e.Payload, e.Error, e.FailedAt
        )).ToList();
        return Ok(result);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var deleted = await dlqReader.DeleteAsync(id);
        if (!deleted)
            return NotFound();

        logger.LogInformation("DLQ entry {EntryId} deleted", id);
        await auditLogger.LogAsync("dlq.deleted", id);
        return NoContent();
    }

    [HttpPost("{id}/replay")]
    public async Task<IActionResult> Replay(string id)
    {
        var entry = await dlqReader.GetEntryAsync(id);
        if (entry is null)
            return NotFound();

        if (string.IsNullOrEmpty(entry.SourceStream) || string.IsNullOrEmpty(entry.Payload))
            return BadRequest("DLQ entry is missing sourceStream or payload.");

        var replayed = await dlqReader.ReplayAsync(id);
        if (!replayed)
            return StatusCode(500, "Failed to replay DLQ entry.");

        logger.LogInformation("DLQ entry {EntryId} replayed to {SourceStream}", id, entry.SourceStream);
        await auditLogger.LogAsync("dlq.replayed", id, new { entry.SourceStream });
        return Accepted();
    }
}
