using System.ComponentModel.DataAnnotations;

namespace Rag.Infrastructure.Retrieval;

public sealed class RetrievalOptions
{
    public const string SectionName = "Retrieval";

    [Range(1, 100)]
    public int VectorTopK { get; init; } = 20;

    [Range(1, 100)]
    public int LexicalTopK { get; init; } = 20;

    [Range(5, 8)]
    public int FinalTopK { get; init; } = 8;

    [Range(1, 8)]
    public int MaxResultsPerDocument { get; init; } = 2;

    [Range(1, 1_000)]
    public int ReciprocalRankConstant { get; init; } = 60;

    [Range(0D, 1D)]
    public double MinimumScore { get; init; }

    [Range(1, 8_000)]
    public int MaxQueryLength { get; init; } = 2_000;

    [Required]
    public string TextSearchConfiguration { get; init; } = "simple";
}
