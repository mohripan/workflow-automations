using FlowForge.Domain.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace FlowForge.WebApi.Controllers;

[ApiController]
[Route("api/task-types")]
public class TaskTypesController(
    ITaskTypeRegistry registry,
    ILogger<TaskTypesController> logger) : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll()
    {
        var types = registry.GetAll()
            .OrderBy(d => d.DisplayName)
            .ToList();

        logger.LogDebug("Returning {Count} task type descriptors", types.Count);
        return Ok(types);
    }

    [HttpGet("{taskId}")]
    public IActionResult GetById(string taskId)
    {
        var descriptor = registry.Get(taskId);
        if (descriptor is null)
        {
            logger.LogWarning("Task type '{TaskId}' not found", taskId);
            return NotFound(new ProblemDetails
            {
                Title = "Task type not found",
                Detail = $"No task type with id '{taskId}' is registered.",
                Status = StatusCodes.Status404NotFound
            });
        }

        return Ok(descriptor);
    }
}
