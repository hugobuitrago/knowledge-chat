using System.ComponentModel.DataAnnotations;

namespace Rag.Infrastructure.Observability;

public sealed class OpenTelemetryOptions
{
    public const string SectionName = "OpenTelemetry";

    [Required]
    public string ServiceName { get; init; } = string.Empty;

    public string? OtlpEndpoint { get; init; }
}

