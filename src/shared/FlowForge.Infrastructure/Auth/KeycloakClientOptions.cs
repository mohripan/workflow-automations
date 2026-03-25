namespace FlowForge.Infrastructure.Auth;

public class KeycloakClientOptions
{
    public const string SectionName = "Keycloak";

    /// <summary>Realm base URL, e.g. http://keycloak:8080/realms/flowforge</summary>
    public string Authority    { get; init; } = "";
    public string ClientId     { get; init; } = "";
    public string ClientSecret { get; init; } = "";
}
