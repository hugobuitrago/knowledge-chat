using System.ComponentModel.DataAnnotations;

namespace Rag.Infrastructure.Ingestion;

public sealed class JobsOptions
{
    public const string SectionName = "Jobs";

    [Range(1, 3_600)]
    public int LeaseDurationSeconds { get; init; } = 60;

    [Range(1, 100)]
    public int MaxAttempts { get; init; } = 5;

    [Range(1, 3_600)]
    public int BaseRetryDelaySeconds { get; init; } = 5;

    [Range(1, 86_400)]
    public int MaxRetryDelaySeconds { get; init; } = 300;
}
