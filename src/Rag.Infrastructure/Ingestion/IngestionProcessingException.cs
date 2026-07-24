namespace Rag.Infrastructure.Ingestion;

public sealed class IngestionProcessingException(
    string message,
    bool isTransient,
    Exception? innerException = null) : Exception(message, innerException)
{
    public bool IsTransient { get; } = isTransient;
}
