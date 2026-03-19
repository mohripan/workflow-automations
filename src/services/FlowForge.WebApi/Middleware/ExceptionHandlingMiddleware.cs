using FlowForge.Domain.Exceptions;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace FlowForge.WebApi.Middleware;

public class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception ex)
    {
        var (statusCode, title) = ex switch
        {
            JobNotFoundException or AutomationNotFoundException
                => (StatusCodes.Status404NotFound, "Resource not found"),

            UnauthorizedWebhookException
                => (StatusCodes.Status401Unauthorized, "Unauthorized"),

            InvalidJobTransitionException or DomainException
                => (StatusCodes.Status422UnprocessableEntity, "Business rule violation"),

            ValidationException
                => (StatusCodes.Status400BadRequest, "Validation failed"),

            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred")
        };

        if (statusCode == StatusCodes.Status500InternalServerError)
            logger.LogError(ex, "Unhandled exception");

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = title,
            Status = statusCode,
            Detail = ex.Message
        });
    }
}
