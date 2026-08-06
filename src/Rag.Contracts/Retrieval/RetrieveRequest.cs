namespace Rag.Contracts.Retrieval;

public sealed record RetrieveRequest(Guid KnowledgeBaseId, string Query);
