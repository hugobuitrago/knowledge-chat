using Rag.Contracts.Retrieval;

namespace Rag.Contracts.Generation;

public sealed record QueryResponse(
    Guid KnowledgeBaseId,
    Guid VersionId,
    string Answer,
    string? Model,
    bool Degraded,
    bool InsufficientContext,
    IReadOnlyList<QueryCitationResponse> Citations,
    IReadOnlyList<RetrievedChunkResponse> Evidence);

public sealed record QueryCitationResponse(
    Guid ChunkId,
    RetrievalSourceResponse Source);
