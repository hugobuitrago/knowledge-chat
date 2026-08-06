using System.Text.Json;
using Microsoft.Extensions.Options;
using Rag.Application.Providers;

namespace Rag.Infrastructure.Generation;

internal sealed class DeterministicLanguageModelProvider(
    IOptions<GenerationOptions> options) : ILanguageModelProvider
{
    public ValueTask<LanguageModelResult> GenerateAsync(
        LanguageModelRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        LanguageModelMessage userMessage = request.Messages.Last(message =>
            string.Equals(message.Role, "user", StringComparison.Ordinal));
        using JsonDocument payload = JsonDocument.Parse(userMessage.Content);
        JsonElement evidence = payload.RootElement.GetProperty("Evidence");
        if (evidence.GetArrayLength() == 0)
        {
            return ValueTask.FromResult(new LanguageModelResult(
                options.Value.Model,
                "The supplied context is insufficient to answer safely.",
                EstimateTokens(request.Messages.Sum(static message =>
                    message.Content.Length)),
                12,
                []));
        }

        JsonElement first = evidence[0];
        Guid chunkId = first.GetProperty("ChunkId").GetGuid();
        string content = first.GetProperty("Content").GetString() ?? string.Empty;
        string answer = $"According to the retrieved evidence: {content}";
        return ValueTask.FromResult(new LanguageModelResult(
            options.Value.Model,
            answer,
            EstimateTokens(request.Messages.Sum(static message =>
                message.Content.Length)),
            EstimateTokens(answer.Length),
            [chunkId]));
    }

    private static int EstimateTokens(int characterCount) =>
        Math.Max(1, (characterCount + 3) / 4);
}
