namespace Rag.Application.Ingestion;

public interface IKnowledgeBaseVersionMaintenance
{
    ValueTask<int> ArchiveSupersededReadyVersionsAsync(
        CancellationToken cancellationToken);
}
