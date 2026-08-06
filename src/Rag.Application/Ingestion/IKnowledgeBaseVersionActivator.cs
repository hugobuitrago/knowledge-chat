namespace Rag.Application.Ingestion;

public interface IKnowledgeBaseVersionActivator
{
    ValueTask<VersionActivationResult> ActivateAsync(
        Guid tenantId,
        Guid knowledgeBaseId,
        Guid versionId,
        CancellationToken cancellationToken);
}

public sealed record VersionActivationResult(
    Guid VersionId,
    Guid? ArchivedVersionId,
    bool AlreadyActive);

public sealed class VersionActivationException(
    string message,
    Exception? innerException = null) : Exception(message, innerException);
