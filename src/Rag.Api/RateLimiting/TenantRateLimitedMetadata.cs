namespace Rag.Api.RateLimiting;

internal sealed class TenantRateLimitedMetadata
{
    public static TenantRateLimitedMetadata Instance { get; } = new();

    private TenantRateLimitedMetadata()
    {
    }
}
