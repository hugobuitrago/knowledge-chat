using Rag.Application.Retrieval;

namespace Rag.Infrastructure.Retrieval;

internal sealed class NoOpChunkReranker : IChunkReranker
{
    public ValueTask<IReadOnlyList<RetrievedChunk>> RerankAsync(
        string query,
        IReadOnlyList<RetrievedChunk> chunks,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(chunks);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(chunks);
    }
}
