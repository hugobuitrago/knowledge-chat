using System.ComponentModel.DataAnnotations;

namespace Rag.Infrastructure.Ingestion;

public sealed class VersionMaintenanceOptions
{
    public const string SectionName = "VersionMaintenance";

    public bool Enabled { get; init; } = true;

    [Range(1, 1_440)]
    public int IntervalMinutes { get; init; } = 60;

    [Range(1, 8_760)]
    public int SupersededReadyAgeHours { get; init; } = 24;

    [Range(1, 1_000)]
    public int BatchSize { get; init; } = 100;
}
