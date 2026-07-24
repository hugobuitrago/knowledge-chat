namespace Rag.Application.Providers;

public interface IEmbeddingProvider
{
    ValueTask<EmbeddingBatch> GenerateAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken);
}

public sealed record EmbeddingBatch(
    string Model,
    int Dimensions,
    IReadOnlyList<ReadOnlyMemory<float>> Vectors);

public sealed class EmbeddingProviderException(
    string message,
    bool isTransient,
    Exception? innerException = null) : Exception(message, innerException)
{
    public bool IsTransient { get; } = isTransient;
}

