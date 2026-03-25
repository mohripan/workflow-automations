using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace FlowForge.Infrastructure.Auth;

public class HttpContextCurrentUserService(IHttpContextAccessor accessor) : ICurrentUserService
{
    public string? UserId =>
        accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);

    public string? Username =>
        accessor.HttpContext?.User.FindFirstValue("preferred_username");

    public bool IsAuthenticated =>
        accessor.HttpContext?.User.Identity?.IsAuthenticated ?? false;
}
