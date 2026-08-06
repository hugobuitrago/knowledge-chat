namespace Rag.Application.Providers;

public interface ILanguageModelProvider
{
    ValueTask<LanguageModelResult> GenerateAsync(
        LanguageModelRequest request,
        CancellationToken cancellationToken);
}

public sealed record LanguageModelRequest(
    IReadOnlyList<LanguageModelMessage> Messages,
    int? MaxOutputTokens = null);

public sealed record LanguageModelMessage(string Role, string Content);

public sealed record LanguageModelResult(
    string Model,
    string Content,
    int InputTokens,
    int OutputTokens,
    IReadOnlyList<Guid>? CitedChunkIds = null);

public sealed class LanguageModelProviderException : Exception
{
    public LanguageModelProviderException(
        string message,
        bool isTransient,
        Exception? innerException = null)
        : base(message, innerException)
    {
        IsTransient = isTransient;
    }

    public bool IsTransient { get; }
}

public interface IStreamingLanguageModelProvider
{
    IAsyncEnumerable<LanguageModelStreamUpdate> GenerateStreamingAsync(
        LanguageModelRequest request,
        CancellationToken cancellationToken);
}

public sealed record LanguageModelStreamUpdate(
    string ContentDelta,
    IReadOnlyList<Guid>? CitedChunkIds = null,
    bool IsComplete = false);

