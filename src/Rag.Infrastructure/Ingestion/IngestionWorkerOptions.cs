using System.ComponentModel.DataAnnotations;

namespace Rag.Infrastructure.Ingestion;

public sealed class IngestionWorkerOptions
{
    public const string SectionName = "Worker";

    [Range(1, 32)]
    public int MaxConcurrentJobs { get; init; } = 2;

    [Range(50, 60_000)]
    public int PollIntervalMilliseconds { get; init; } = 500;
}
