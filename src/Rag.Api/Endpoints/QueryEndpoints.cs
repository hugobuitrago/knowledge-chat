using Microsoft.Extensions.Options;
using Rag.Api.RateLimiting;
using Rag.Application.Generation;
using Rag.Application.Retrieval;
using Rag.Application.Security;
using Rag.Contracts.Generation;
using Rag.Contracts.Retrieval;
using Rag.Infrastructure.Generation;
using Rag.Infrastructure.Retrieval;

namespace Rag.Api.Endpoints;

internal static class QueryEndpoints
{
    public static IEndpointRouteBuilder MapQueryEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapPost("/v1/query", QueryAsync)
            .WithName("QueryKnowledge")
            .WithTags("Generation")
            .WithMetadata(TenantRateLimitedMetadata.Instance)
            .RequireAuthorization(RagScopes.Retrieve)
            .Produces<QueryResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        return endpoints;
    }

    private static async Task<IResult> QueryAsync(
        QueryRequest request,
        ICurrentTenant currentTenant,
        IQueryService queryService,
        IOptions<RetrievalOptions> retrievalOptions,
        IOptions<GenerationOptions> generationOptions,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string[]>? errors = Validate(
            request,
            retrievalOptions.Value,
            generationOptions.Value);
        if (errors is not null)
        {
            return Results.ValidationProblem(errors);
        }

        IReadOnlyList<QueryHistoryMessage> history = request.History?
            .Select(static message => new QueryHistoryMessage(
                message.Role,
                message.Content.Trim()))
            .ToArray() ?? [];
        QueryResult? result = await queryService.QueryAsync(
            new QueryCommand(
                currentTenant.TenantId,
                request.KnowledgeBaseId,
                currentTenant.ChatbotId,
                request.Query.Trim(),
                history),
            cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(new QueryResponse(
            result.KnowledgeBaseId,
            result.VersionId,
            result.Answer,
            result.Model,
            result.Degraded,
            result.InsufficientContext,
            result.Citations.Select(static chunk => new QueryCitationResponse(
                chunk.ChunkId,
                ToSource(chunk))).ToArray(),
            result.Evidence.Select(static chunk => new RetrievedChunkResponse(
                chunk.ChunkId,
                chunk.Content,
                chunk.Score,
                ToSource(chunk))).ToArray()));
    }

    private static Dictionary<string, string[]>? Validate(
        QueryRequest request,
        RetrievalOptions retrievalOptions,
        GenerationOptions generationOptions)
    {
        Dictionary<string, string[]>? errors = null;
        if (request.KnowledgeBaseId == Guid.Empty)
        {
            errors ??= [];
            errors[nameof(request.KnowledgeBaseId)] =
                ["KnowledgeBaseId must be a non-empty UUID."];
        }

        if (string.IsNullOrWhiteSpace(request.Query) ||
            request.Query.Length > retrievalOptions.MaxQueryLength)
        {
            errors ??= [];
            errors[nameof(request.Query)] =
            [
                $"Query is required and cannot exceed {retrievalOptions.MaxQueryLength} characters.",
            ];
        }

        IReadOnlyList<QueryHistoryMessageRequest> history = request.History ?? [];
        int historyCharacters = history.Sum(static message =>
            message?.Content?.Length ?? 0);
        if (history.Count > generationOptions.MaxHistoryMessages ||
            historyCharacters > generationOptions.MaxHistoryCharacters ||
            history.Any(static message =>
                message is null ||
                !IsAllowedRole(message.Role) ||
                string.IsNullOrWhiteSpace(message.Content)))
        {
            errors ??= [];
            errors[nameof(request.History)] =
            [
                $"History accepts at most {generationOptions.MaxHistoryMessages} non-empty user/assistant messages and {generationOptions.MaxHistoryCharacters} characters.",
            ];
        }

        return errors;
    }

    private static bool IsAllowedRole(string? role) =>
        string.Equals(role, "user", StringComparison.Ordinal) ||
        string.Equals(role, "assistant", StringComparison.Ordinal);

    private static RetrievalSourceResponse ToSource(RetrievedChunk chunk) =>
        new(
            chunk.DocumentId,
            chunk.FileName,
            chunk.ChunkIndex,
            chunk.StartOffset,
            chunk.EndOffset);
}
