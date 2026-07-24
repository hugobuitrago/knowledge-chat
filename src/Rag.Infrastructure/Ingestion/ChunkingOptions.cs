using System.ComponentModel.DataAnnotations;

namespace Rag.Infrastructure.Ingestion;

public sealed class ChunkingOptions
{
    public const string SectionName = "Chunking";

    [Range(32, 4096)]
    public int MaxTokens { get; init; } = 500;

    [Range(0, 1024)]
    public int OverlapTokens { get; init; } = 80;
}
