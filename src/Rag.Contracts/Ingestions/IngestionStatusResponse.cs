namespace Rag.Contracts.Ingestions;

public sealed record IngestionStatusResponse(
    Guid JobId,
    Guid DocumentId,
    Guid KnowledgeBaseId,
    Guid VersionId,
    string Status,
    int Attempts,
    int MaxAttempts,
    DateTimeOffset AvailableAt,
    DateTimeOffset? LockedUntil);
