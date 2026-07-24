using System.ComponentModel.DataAnnotations;

namespace Rag.Infrastructure.Storage;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    [Required]
    public string Provider { get; init; } = string.Empty;

    [Required]
    public string LocalPath { get; init; } = string.Empty;
}
