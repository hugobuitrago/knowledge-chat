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
    int OutputTokens);

