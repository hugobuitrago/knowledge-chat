using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rag.Application.Generation;
using Rag.Application.Providers;
using Rag.Application.Retrieval;

namespace Rag.Infrastructure.Generation;

internal sealed class GroundedQueryService(
    IHybridRetrievalService retrievalService,
    ILanguageModelProvider primaryProvider,
    IEnumerable<ISecondaryLanguageModelProvider> secondaryProviders,
    IOptions<GenerationOptions> options,
    ILogger<GroundedQueryService> logger) : IQueryService
{
    private const string InsufficientContextAnswer =
        "There is not enough evidence in the active knowledge base to answer safely.";
    private const string EvidenceOnlyAnswer =
        "Answer generation is unavailable. Review the retrieved evidence.";

    public async ValueTask<QueryResult?> QueryAsync(
        QueryCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Query);

        RetrievalResult? retrieval = await retrievalService.RetrieveAsync(
            new RetrievalCommand(
                command.TenantId,
                command.KnowledgeBaseId,
                command.ChatbotId,
                command.Query),
            cancellationToken).ConfigureAwait(false);
        if (retrieval is null)
        {
            return null;
        }

        GenerationOptions settings = options.Value;
        IReadOnlyList<RetrievedChunk> evidence = LimitEvidence(
            retrieval.Chunks,
            settings);
        if (evidence.Count < settings.MinimumEvidenceCount)
        {
            return new QueryResult(
                retrieval.KnowledgeBaseId,
                retrieval.VersionId,
                InsufficientContextAnswer,
                Model: null,
                retrieval.Degraded,
                InsufficientContext: true,
                Citations: [],
                evidence);
        }

        IReadOnlyList<QueryHistoryMessage> history = LimitHistory(
            command.History,
            settings);
        LanguageModelRequest request = GroundedPromptBuilder.Build(
            command.Query,
            history,
            evidence,
            settings.MaxOutputTokens);

        ValidatedGeneration? generation = await TryGenerateAsync(
            primaryProvider,
            request,
            evidence,
            cancellationToken).ConfigureAwait(false);
        bool usedFallback = false;
        if (generation is null &&
            settings.FallbackMode == GenerationFallbackMode.SecondaryProvider)
        {
            ISecondaryLanguageModelProvider? secondary = secondaryProviders.FirstOrDefault();
            if (secondary is not null)
            {
                usedFallback = true;
                generation = await TryGenerateAsync(
                    secondary,
                    request,
                    evidence,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (generation is null)
        {
            return new QueryResult(
                retrieval.KnowledgeBaseId,
                retrieval.VersionId,
                EvidenceOnlyAnswer,
                Model: null,
                Degraded: true,
                InsufficientContext: false,
                Citations: [],
                evidence);
        }

        return new QueryResult(
            retrieval.KnowledgeBaseId,
            retrieval.VersionId,
            generation.Answer,
            generation.Model,
            retrieval.Degraded || usedFallback,
            InsufficientContext: false,
            generation.Citations,
            evidence);
    }

    private async ValueTask<ValidatedGeneration?> TryGenerateAsync(
        ILanguageModelProvider provider,
        LanguageModelRequest request,
        IReadOnlyList<RetrievedChunk> evidence,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(
            options.Value.RequestTimeoutSeconds));
        LanguageModelResult result;
        try
        {
            result = await provider.GenerateAsync(
                request,
                timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Language model request timed out. ProviderType={ProviderType}",
                provider.GetType().Name);
            return null;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Language model request failed. ProviderType={ProviderType} ErrorType={ErrorType}",
                provider.GetType().Name,
                exception.GetType().Name);
            return null;
        }

        if (string.IsNullOrWhiteSpace(result.Content) ||
            result.CitedChunkIds is null ||
            result.CitedChunkIds.Count == 0)
        {
            logger.LogWarning(
                "Language model returned an answer without valid structured citations. ProviderType={ProviderType}",
                provider.GetType().Name);
            return null;
        }

        Dictionary<Guid, RetrievedChunk> sentChunks = evidence.ToDictionary(
            static chunk => chunk.ChunkId);
        var citations = new List<RetrievedChunk>(result.CitedChunkIds.Count);
        foreach (Guid chunkId in result.CitedChunkIds.Distinct())
        {
            if (!sentChunks.TryGetValue(chunkId, out RetrievedChunk? chunk))
            {
                logger.LogWarning(
                    "Language model cited evidence that was not sent. ProviderType={ProviderType}",
                    provider.GetType().Name);
                return null;
            }

            citations.Add(chunk);
        }

        return new ValidatedGeneration(result.Content.Trim(), result.Model, citations);
    }

    private static IReadOnlyList<RetrievedChunk> LimitEvidence(
        IReadOnlyList<RetrievedChunk> chunks,
        GenerationOptions settings)
    {
        int remainingTokens = settings.MaxContextTokens;
        var result = new List<RetrievedChunk>(chunks.Count);
        foreach (RetrievedChunk chunk in chunks)
        {
            if (chunk.Score < settings.MinimumEvidenceScore ||
                string.IsNullOrWhiteSpace(chunk.Content))
            {
                continue;
            }

            int metadataTokens = 32 + EstimateTokens(chunk.FileName.Length);
            int contentTokens = remainingTokens - metadataTokens;
            if (contentTokens <= 0)
            {
                break;
            }

            int maxCharacters = checked(contentTokens * 4);
            string content = chunk.Content.Length <= maxCharacters
                ? chunk.Content
                : chunk.Content[..maxCharacters];
            result.Add(chunk with { Content = content });
            remainingTokens -= metadataTokens + EstimateTokens(content.Length);
            if (remainingTokens <= 0)
            {
                break;
            }
        }

        return result;
    }

    private static IReadOnlyList<QueryHistoryMessage> LimitHistory(
        IReadOnlyList<QueryHistoryMessage> history,
        GenerationOptions settings)
    {
        if (settings.MaxHistoryMessages == 0 ||
            settings.MaxHistoryCharacters == 0)
        {
            return [];
        }

        int remainingCharacters = settings.MaxHistoryCharacters;
        var selected = new List<QueryHistoryMessage>(settings.MaxHistoryMessages);
        foreach (QueryHistoryMessage message in history
            .TakeLast(settings.MaxHistoryMessages)
            .Reverse())
        {
            if (remainingCharacters <= 0 ||
                !IsAllowedHistoryRole(message.Role) ||
                string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            string content = message.Content.Length <= remainingCharacters
                ? message.Content
                : message.Content[..remainingCharacters];
            selected.Add(message with { Content = content });
            remainingCharacters -= content.Length;
        }

        selected.Reverse();
        return selected;
    }

    private static bool IsAllowedHistoryRole(string role) =>
        string.Equals(role, "user", StringComparison.Ordinal) ||
        string.Equals(role, "assistant", StringComparison.Ordinal);

    private static int EstimateTokens(int characterCount) =>
        Math.Max(1, (characterCount + 3) / 4);

    private sealed record ValidatedGeneration(
        string Answer,
        string Model,
        IReadOnlyList<RetrievedChunk> Citations);
}
