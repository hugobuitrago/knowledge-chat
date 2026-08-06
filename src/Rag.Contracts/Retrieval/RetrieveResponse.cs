namespace Rag.Contracts.Retrieval;

public sealed record RetrieveResponse(
    Guid KnowledgeBaseId,
    Guid VersionId,
    bool Degraded,
    IReadOnlyList<RetrievedChunkResponse> Results);

public sealed record RetrievedChunkResponse(
    Guid ChunkId,
    string Content,
    double Score,
    RetrievalSourceResponse Source);

public sealed record RetrievalSourceResponse(
    Guid DocumentId,
    string FileName,
    int ChunkIndex,
    int StartOffset,
    int EndOffset);
