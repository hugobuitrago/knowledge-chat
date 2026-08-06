namespace Rag.Application.Retrieval;

public interface IHybridRetrievalService
{
    ValueTask<RetrievalResult?> RetrieveAsync(
        RetrievalCommand command,
        CancellationToken cancellationToken);
}

public sealed record RetrievalCommand(
    Guid TenantId,
    Guid KnowledgeBaseId,
    Guid? ChatbotId,
    string Query);

public sealed record RetrievalResult(
    Guid KnowledgeBaseId,
    Guid VersionId,
    bool Degraded,
    IReadOnlyList<RetrievedChunk> Chunks);

public sealed record RetrievedChunk(
    Guid ChunkId,
    Guid DocumentId,
    string FileName,
    int ChunkIndex,
    int StartOffset,
    int EndOffset,
    string Content,
    double Score);

public sealed record RetrievalCandidate(
    Guid ChunkId,
    Guid DocumentId,
    string FileName,
    int ChunkIndex,
    int StartOffset,
    int EndOffset,
    string Content,
    double StrategyScore);
