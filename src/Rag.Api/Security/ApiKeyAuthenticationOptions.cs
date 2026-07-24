namespace Rag.Api.Security;

public sealed class ApiKeyAuthenticationOptions
{
    public const string SectionName = "Authentication:ApiKey";

    public string HeaderName { get; init; } = "X-API-Key";

    public string Pepper { get; init; } = string.Empty;

    public List<ApiKeyClientOptions> Clients { get; init; } = [];
}

public sealed class ApiKeyClientOptions
{
    public string KeyId { get; init; } = string.Empty;

    public Guid TenantId { get; init; }

    public Guid? ChatbotId { get; init; }

    public string SecretHash { get; init; } = string.Empty;

    public List<string> Scopes { get; init; } = [];
}
