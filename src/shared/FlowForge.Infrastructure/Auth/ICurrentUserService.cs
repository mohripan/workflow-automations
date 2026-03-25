namespace FlowForge.Infrastructure.Auth;

public interface ICurrentUserService
{
    string? UserId          { get; }  // JWT "sub" claim
    string? Username        { get; }  // JWT "preferred_username" claim
    bool    IsAuthenticated { get; }
}
