namespace Rag.Contracts.Ingestions;

public sealed record UploadAcceptedResponse(
    Guid DocumentId,
    Guid VersionId,
    Guid JobId,
    string StatusUrl);
