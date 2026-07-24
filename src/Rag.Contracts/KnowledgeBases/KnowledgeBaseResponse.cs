namespace Rag.Contracts.KnowledgeBases;

public sealed record KnowledgeBaseResponse(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
