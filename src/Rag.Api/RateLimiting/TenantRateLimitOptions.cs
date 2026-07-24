using System.ComponentModel.DataAnnotations;

namespace Rag.Api.RateLimiting;

public sealed class TenantRateLimitOptions
{
    public const string SectionName = "RateLimiting";

    [Range(1, 100_000)]
    public int TenantPermitLimit { get; init; } = 100;

    [Range(1, 100_000)]
    public int ChatbotPermitLimit { get; init; } = 50;

    [Range(1, 3_600)]
    public int WindowSeconds { get; init; } = 60;
}
