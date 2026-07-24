using System.ComponentModel.DataAnnotations;

namespace Rag.Infrastructure.Ingestion;

public sealed class UploadsOptions
{
    public const string SectionName = "Uploads";

    [Range(1_024, 104_857_600)]
    public long MaxFileSizeBytes { get; init; } = 10_485_760;

    [Range(1, 720)]
    public int IdempotencyTtlHours { get; init; } = 24;
}
