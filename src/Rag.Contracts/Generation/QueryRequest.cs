namespace Rag.Contracts.Generation;

public sealed record QueryRequest(
    Guid KnowledgeBaseId,
    string Query,
    IReadOnlyList<QueryHistoryMessageRequest>? History = null);

public sealed record QueryHistoryMessageRequest(string Role, string Content);
