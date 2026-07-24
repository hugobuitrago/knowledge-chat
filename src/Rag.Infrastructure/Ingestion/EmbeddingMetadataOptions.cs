using System.ComponentModel.DataAnnotations;
using Rag.Infrastructure.Persistence;

namespace Rag.Infrastructure.Ingestion;

public sealed class EmbeddingMetadataOptions
{
    public const string SectionName = "Embedding";

    [Required]
    [MaxLength(100)]
    public string Provider { get; init; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Model { get; init; } = string.Empty;

    [Range(
        RagDatabaseConstants.EmbeddingDimensions,
        RagDatabaseConstants.EmbeddingDimensions)]
    public int Dimensions { get; init; } = RagDatabaseConstants.EmbeddingDimensions;

    [Range(1, 256)]
    public int BatchSize { get; init; } = 32;

    [Range(1, 32)]
    public int MaxConcurrency { get; init; } = 2;

    [Range(1, 300)]
    public int RequestTimeoutSeconds { get; init; } = 30;
}
