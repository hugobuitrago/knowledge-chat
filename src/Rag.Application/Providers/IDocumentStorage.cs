namespace Rag.Application.Providers;

public interface IDocumentStorage
{
    ValueTask<StoredDocument> StoreAsync(
        DocumentStorageWriteRequest request,
        CancellationToken cancellationToken);

    ValueTask<Stream> OpenReadAsync(
        string objectKey,
        CancellationToken cancellationToken);

    ValueTask DeleteAsync(
        string objectKey,
        CancellationToken cancellationToken);
}

public sealed record DocumentStorageWriteRequest(
    string ObjectKey,
    Stream Content,
    string ContentType);

public sealed record StoredDocument(
    string ObjectKey,
    long Length,
    string ContentHash);

