using Rag.Application.Providers;

namespace Rag.Application.Ingestion;

public interface IDocumentIngestionProcessor
{
    ValueTask ProcessAsync(
        IngestionJobLease lease,
        CancellationToken cancellationToken);
}
