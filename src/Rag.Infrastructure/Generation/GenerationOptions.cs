using System.ComponentModel.DataAnnotations;

namespace Rag.Infrastructure.Generation;

public sealed class GenerationOptions
{
    public const string SectionName = "Generation";

    [Required]
    [MaxLength(100)]
    public string Provider { get; init; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Model { get; init; } = string.Empty;

    [Range(128, 32_000)]
    public int MaxContextTokens { get; init; } = 4_000;

    [Range(0, 50)]
    public int MaxHistoryMessages { get; init; } = 6;

    [Range(0, 100_000)]
    public int MaxHistoryCharacters { get; init; } = 8_000;

    [Range(1, 16_000)]
    public int MaxOutputTokens { get; init; } = 800;

    [Range(1, 300)]
    public int RequestTimeoutSeconds { get; init; } = 30;

    [Range(1, 100)]
    public int MinimumEvidenceCount { get; init; } = 1;

    [Range(0D, 1D)]
    public double MinimumEvidenceScore { get; init; }

    public GenerationFallbackMode FallbackMode { get; init; } =
        GenerationFallbackMode.EvidenceOnly;
}

public enum GenerationFallbackMode
{
    EvidenceOnly,
    SecondaryProvider,
}
