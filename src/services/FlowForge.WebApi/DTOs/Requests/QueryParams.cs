namespace FlowForge.WebApi.DTOs.Requests;

public record PaginationParams(int Page = 1, int PageSize = 20);

public record AutomationQueryParams(string? Name = null) : PaginationParams;

public record JobQueryParams(
    Guid? AutomationId = null, 
    FlowForge.Domain.Enums.JobStatus? Status = null
) : PaginationParams;
