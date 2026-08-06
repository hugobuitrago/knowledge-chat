using Microsoft.Extensions.Options;
using Rag.Api.RateLimiting;
using Rag.Application.Retrieval;
using Rag.Application.Security;
using Rag.Contracts.Retrieval;
using Rag.Infrastructure.Retrieval;

namespace Rag.Api.Endpoints;

internal static class RetrievalEndpoints
{
    public static IEndpointRouteBuilder MapRetrievalEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapPost("/v1/retrieve", RetrieveAsync)
            .WithName("RetrieveKnowledge")
            .WithTags("Retrieval")
            .WithMetadata(TenantRateLimitedMetadata.Instance)
            .RequireAuthorization(RagScopes.Retrieve)
            .Produces<RetrieveResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        return endpoints;
    }

    private static async Task<IResult> RetrieveAsync(
        RetrieveRequest request,
        ICurrentTenant currentTenant,
        IHybridRetrievalService retrievalService,
        IOptions<RetrievalOptions> options,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string[]>? errors = Validate(request, options.Value);
        if (errors is not null)
        {
            return Results.ValidationProblem(errors);
        }

        RetrievalResult? result = await retrievalService.RetrieveAsync(
            new RetrievalCommand(
                currentTenant.TenantId,
                request.KnowledgeBaseId,
                currentTenant.ChatbotId,
                request.Query.Trim()),
            cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(new RetrieveResponse(
            result.KnowledgeBaseId,
            result.VersionId,
            result.Degraded,
            result.Chunks.Select(static chunk => new RetrievedChunkResponse(
                chunk.ChunkId,
                chunk.Content,
                chunk.Score,
                new RetrievalSourceResponse(
                    chunk.DocumentId,
                    chunk.FileName,
                    chunk.ChunkIndex,
                    chunk.StartOffset,
                    chunk.EndOffset))).ToArray()));
    }

    private static Dictionary<string, string[]>? Validate(
        RetrieveRequest request,
        RetrievalOptions options)
    {
        Dictionary<string, string[]>? errors = null;
        if (request.KnowledgeBaseId == Guid.Empty)
        {
            errors ??= [];
            errors[nameof(request.KnowledgeBaseId)] =
                ["KnowledgeBaseId must be a non-empty UUID."];
        }

        if (string.IsNullOrWhiteSpace(request.Query) ||
            request.Query.Length > options.MaxQueryLength)
        {
            errors ??= [];
            errors[nameof(request.Query)] =
            [
                $"Query is required and cannot exceed {options.MaxQueryLength} characters.",
            ];
        }

        return errors;
    }
}
