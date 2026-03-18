using FlowForge.Domain.Triggers;
using FlowForge.WebApi.DTOs.Responses;
using Microsoft.AspNetCore.Mvc;

namespace FlowForge.WebApi.Controllers;

[ApiController]
[Route("api/triggers")]
public class TriggersController(
    ITriggerTypeRegistry registry,
    ILogger<TriggersController> logger) : ControllerBase
{
    [HttpGet("types")]
    public IActionResult GetAllTypes()
    {
        var schemas = registry.GetAll()
            .Select(d => d.GetSchema())
            .OrderBy(s => s.DisplayName)
            .ToList();

        logger.LogDebug("Returning {Count} trigger type schemas", schemas.Count);
        return Ok(schemas);
    }

    [HttpGet("types/{typeId}")]
    public IActionResult GetType(string typeId)
    {
        var descriptor = registry.Get(typeId);
        if (descriptor is null)
        {
            logger.LogWarning("Trigger type '{TypeId}' not found", typeId);
            return NotFound(new ProblemDetails
            {
                Title = "Trigger type not found",
                Detail = $"No trigger type with id '{typeId}' is registered.",
                Status = StatusCodes.Status404NotFound
            });
        }

        return Ok(descriptor.GetSchema());
    }

    [HttpPost("types/{typeId}/validate-config")]
    public IActionResult ValidateConfig(string typeId, [FromBody] ValidateConfigRequest request)
    {
        var descriptor = registry.Get(typeId);
        if (descriptor is null)
            return NotFound(new ProblemDetails
            {
                Title = "Trigger type not found",
                Detail = $"No trigger type with id '{typeId}' is registered.",
                Status = StatusCodes.Status404NotFound
            });

        var errors = descriptor.ValidateConfig(request.ConfigJson);

        if (errors.Count > 0)
        {
            logger.LogDebug(
                "Config validation for trigger type '{TypeId}' failed: {Errors}",
                typeId, string.Join("; ", errors));

            return UnprocessableEntity(new TriggerConfigValidationResult(
                TypeId: typeId, IsValid: false, Errors: errors));
        }

        return Ok(new TriggerConfigValidationResult(
            TypeId: typeId, IsValid: true, Errors: []));
    }
}

public record ValidateConfigRequest(string ConfigJson);

public record TriggerConfigValidationResult(
    string TypeId,
    bool IsValid,
    IReadOnlyList<string> Errors
);
