using FlowForge.Infrastructure.Audit;
using FlowForge.Infrastructure.Messaging.Redis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

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
    IConnectionMultiplexer redis,
    ILogger<DlqController> logger,
    IAuditLogger auditLogger) : ControllerBase
{
    private readonly IDatabase _db = redis.GetDatabase();

    [HttpGet]
    public async Task<IActionResult> GetEntries([FromQuery] int limit = 50)
    {
        var entries = await _db.StreamRangeAsync(StreamNames.Dlq, "-", "+", count: limit);
        var result = entries.Select(e => new DlqEntryResponse(
            Id:           e.Id!,
            SourceStream: (string?)e.Values.FirstOrDefault(v => v.Name == "sourceStream").Value ?? "",
            MessageId:    (string?)e.Values.FirstOrDefault(v => v.Name == "messageId").Value ?? "",
            Payload:      (string?)e.Values.FirstOrDefault(v => v.Name == "payload").Value ?? "",
            Error:        (string?)e.Values.FirstOrDefault(v => v.Name == "error").Value ?? "",
            FailedAt:     (string?)e.Values.FirstOrDefault(v => v.Name == "failedAt").Value ?? ""
        )).ToList();
        return Ok(result);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var deleted = await _db.StreamDeleteAsync(StreamNames.Dlq, [new RedisValue(id)]);
        if (deleted == 0)
            return NotFound();

        logger.LogInformation("DLQ entry {EntryId} deleted", id);
        await auditLogger.LogAsync("dlq.deleted", id);
        return NoContent();
    }

    [HttpPost("{id}/replay")]
    public async Task<IActionResult> Replay(string id)
    {
        var entries = await _db.StreamRangeAsync(StreamNames.Dlq, id, id, count: 1);
        if (entries.Length == 0)
            return NotFound();

        var entry = entries[0];
        var sourceStream = (string?)entry.Values.FirstOrDefault(v => v.Name == "sourceStream").Value;
        var payload      = (string?)entry.Values.FirstOrDefault(v => v.Name == "payload").Value;

        if (string.IsNullOrEmpty(sourceStream) || string.IsNullOrEmpty(payload))
            return BadRequest("DLQ entry is missing sourceStream or payload.");

        await _db.StreamAddAsync(sourceStream,
        [
            new NameValueEntry("payload",     payload),
            new NameValueEntry("traceparent", string.Empty)
        ]);

        logger.LogInformation("DLQ entry {EntryId} replayed to stream {SourceStream}", id, sourceStream);
        await auditLogger.LogAsync("dlq.replayed", id, new { sourceStream });
        return Accepted();
    }
}
