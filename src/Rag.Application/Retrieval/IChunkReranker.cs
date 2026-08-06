namespace Rag.Application.Retrieval;

public interface IChunkReranker
{
    ValueTask<IReadOnlyList<RetrievedChunk>> RerankAsync(
        string query,
        IReadOnlyList<RetrievedChunk> chunks,
        CancellationToken cancellationToken);
}
