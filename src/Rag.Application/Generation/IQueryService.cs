using Rag.Application.Retrieval;

namespace Rag.Application.Generation;

public interface IQueryService
{
    ValueTask<QueryResult?> QueryAsync(
        QueryCommand command,
        CancellationToken cancellationToken);
}

public sealed record QueryCommand(
    Guid TenantId,
    Guid KnowledgeBaseId,
    Guid? ChatbotId,
    string Query,
    IReadOnlyList<QueryHistoryMessage> History);

public sealed record QueryHistoryMessage(string Role, string Content);

public sealed record QueryResult(
    Guid KnowledgeBaseId,
    Guid VersionId,
    string Answer,
    string? Model,
    bool Degraded,
    bool InsufficientContext,
    IReadOnlyList<RetrievedChunk> Citations,
    IReadOnlyList<RetrievedChunk> Evidence);
