using Microsoft.EntityFrameworkCore;
using Npgsql;
using Rag.Api.RateLimiting;
using Rag.Application.Security;
using Rag.Contracts.KnowledgeBases;
using Rag.Domain.Entities;
using Rag.Infrastructure.Persistence;

namespace Rag.Api.Endpoints;

internal static class KnowledgeBaseEndpoints
{
    public static IEndpointRouteBuilder MapKnowledgeBaseEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints
            .MapGroup("/v1/knowledge-bases")
            .WithTags("Knowledge bases")
            .WithMetadata(TenantRateLimitedMetadata.Instance)
            .RequireAuthorization(RagScopes.Admin);

        group
            .MapPost("/", CreateAsync)
            .WithName("CreateKnowledgeBase")
            .Produces<KnowledgeBaseResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        group
            .MapGet("/{knowledgeBaseId:guid}", GetAsync)
            .WithName("GetKnowledgeBase")
            .Produces<KnowledgeBaseResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        CreateKnowledgeBaseRequest request,
        ICurrentTenant currentTenant,
        RagDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    [nameof(request.Name)] =
                    [
                        "Name is required and cannot exceed 200 characters.",
                    ],
                });
        }

        string name = request.Name.Trim();
        bool duplicate = await dbContext.KnowledgeBases
            .AsNoTracking()
            .AnyAsync(
                knowledgeBase => knowledgeBase.TenantId == currentTenant.TenantId &&
                    knowledgeBase.Name == name,
                cancellationToken)
            .ConfigureAwait(false);
        if (duplicate)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Knowledge base already exists.");
        }

        KnowledgeBase knowledgeBase = KnowledgeBase.Create(currentTenant.TenantId, name);
        dbContext.KnowledgeBases.Add(knowledgeBase);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
            })
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Knowledge base already exists.");
        }

        KnowledgeBaseResponse response = MapResponse(knowledgeBase);
        return Results.Created(
            $"/v1/knowledge-bases/{knowledgeBase.Id:D}",
            response);
    }

    private static async Task<IResult> GetAsync(
        Guid knowledgeBaseId,
        ICurrentTenant currentTenant,
        RagDbContext dbContext,
        CancellationToken cancellationToken)
    {
        KnowledgeBase? knowledgeBase = await dbContext.KnowledgeBases
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == knowledgeBaseId &&
                    candidate.TenantId == currentTenant.TenantId,
                cancellationToken)
            .ConfigureAwait(false);

        return knowledgeBase is null
            ? Results.NotFound()
            : Results.Ok(MapResponse(knowledgeBase));
    }

    private static KnowledgeBaseResponse MapResponse(KnowledgeBase knowledgeBase) =>
        new(
            knowledgeBase.Id,
            knowledgeBase.Name,
            knowledgeBase.CreatedAt,
            knowledgeBase.UpdatedAt);
}
