namespace Rag.Infrastructure.Ingestion;

public sealed class UploadValidationException(
    string message,
    bool isTooLarge = false) : Exception(message)
{
    public bool IsTooLarge { get; } = isTooLarge;
}
