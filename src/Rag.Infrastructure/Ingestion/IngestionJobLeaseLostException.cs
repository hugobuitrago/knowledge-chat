namespace Rag.Infrastructure.Ingestion;

public sealed class IngestionJobLeaseLostException(Guid jobId)
    : Exception($"The lease for ingestion job '{jobId:D}' is no longer owned.");
