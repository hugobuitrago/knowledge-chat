using System.ComponentModel.DataAnnotations;
using Rag.Infrastructure.Persistence;

namespace Rag.Infrastructure.Ingestion;

public sealed class EmbeddingMetadataOptions
{
    public const string SectionName = "Embedding";

    [Required]
    [MaxLength(200)]
    public string Model { get; init; } = string.Empty;

    [Range(
        RagDatabaseConstants.EmbeddingDimensions,
        RagDatabaseConstants.EmbeddingDimensions)]
    public int Dimensions { get; init; } = RagDatabaseConstants.EmbeddingDimensions;
}
