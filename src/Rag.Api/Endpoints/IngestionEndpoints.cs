using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Rag.Api.RateLimiting;
using Rag.Application.Ingestion;
using Rag.Application.Providers;
using Rag.Application.Security;
using Rag.Contracts.Ingestions;
using Rag.Domain.Entities;
using Rag.Domain.Enums;
using Rag.Infrastructure.Ingestion;
using Rag.Infrastructure.Persistence;

namespace Rag.Api.Endpoints;

internal static class IngestionEndpoints
{
    private const string IdempotencyHeaderName = "Idempotency-Key";
    private const int MaximumIdempotencyKeyLength = 200;
    private const int MaximumBoundaryLength = 128;
    private const long MaximumMultipartOverheadBytes = 1_048_576;

    public static IEndpointRouteBuilder MapIngestionEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapPost(
                "/v1/knowledge-bases/{knowledgeBaseId:guid}/documents",
                UploadAsync)
            .WithName("UploadDocument")
            .WithTags("Ingestions")
            .WithMetadata(TenantRateLimitedMetadata.Instance)
            .RequireAuthorization(RagScopes.Ingest)
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<UploadAcceptedResponse>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        RouteGroupBuilder ingestionGroup = endpoints
            .MapGroup("/v1/ingestions")
            .WithTags("Ingestions")
            .WithMetadata(TenantRateLimitedMetadata.Instance)
            .RequireAuthorization(RagScopes.Ingest);
        ingestionGroup
            .MapGet("/{jobId:guid}", GetStatusAsync)
            .WithName("GetIngestionStatus")
            .Produces<IngestionStatusResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        ingestionGroup
            .MapPost("/{jobId:guid}/retry", RetryAsync)
            .WithName("RetryIngestion")
            .Produces<IngestionStatusResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return endpoints;
    }

    private static async Task<IResult> UploadAsync(
        Guid knowledgeBaseId,
        HttpRequest request,
        HttpResponse response,
        ICurrentTenant currentTenant,
        IDocumentUploadService uploadService,
        RagDbContext dbContext,
        IOptions<UploadsOptions> options,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdempotencyKey(request, out string idempotencyKey))
        {
            return ValidationProblem(
                IdempotencyHeaderName,
                $"A visible ASCII {IdempotencyHeaderName} header with at most " +
                $"{MaximumIdempotencyKeyLength} characters is required.");
        }

        bool knowledgeBaseExists = await dbContext.KnowledgeBases
            .AsNoTracking()
            .AnyAsync(
                knowledgeBase => knowledgeBase.Id == knowledgeBaseId &&
                    knowledgeBase.TenantId == currentTenant.TenantId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!knowledgeBaseExists)
        {
            return Results.NotFound();
        }

        if (request.ContentLength >
            options.Value.MaxFileSizeBytes + MaximumMultipartOverheadBytes)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "The upload is too large.");
        }

        if (!TryGetBoundary(request.ContentType, out string boundary))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status415UnsupportedMediaType,
                title: "Expected a multipart/form-data request.");
        }

        var reader = new MultipartReader(boundary, request.Body)
        {
            BodyLengthLimit =
                options.Value.MaxFileSizeBytes + MaximumMultipartOverheadBytes,
            HeadersCountLimit = 16,
            HeadersLengthLimit = 16 * 1024,
        };
        StagedDocumentUpload? stagedUpload = null;
        bool retainStoredObject = false;
        try
        {
            MultipartSection? section = await reader
                .ReadNextSectionAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!TryReadFileMetadata(
                    section,
                    out string fileName,
                    out string contentType,
                    out string validationError))
            {
                return ValidationProblem("file", validationError);
            }

            stagedUpload = await uploadService.StageAsync(
                new StageDocumentUploadCommand(
                    currentTenant.TenantId,
                    knowledgeBaseId,
                    fileName,
                    contentType,
                    section!.Body),
                cancellationToken).ConfigureAwait(false);

            if (await reader.ReadNextSectionAsync(cancellationToken).ConfigureAwait(false)
                is not null)
            {
                return ValidationProblem(
                    "file",
                    "Exactly one file section is allowed.");
            }

            DocumentUploadResult result = await uploadService.CommitAsync(
                stagedUpload,
                idempotencyKey,
                cancellationToken).ConfigureAwait(false);
            retainStoredObject = result.Outcome == DocumentUploadOutcome.Accepted;
            if (result.Outcome == DocumentUploadOutcome.Conflict)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "The idempotency key was already used for another request.");
            }

            if (result.Outcome == DocumentUploadOutcome.KnowledgeBaseNotFound)
            {
                return Results.NotFound();
            }

            var accepted = new UploadAcceptedResponse(
                result.DocumentId!.Value,
                result.VersionId!.Value,
                result.JobId!.Value,
                $"/v1/ingestions/{result.JobId.Value:D}");
            if (result.Outcome == DocumentUploadOutcome.Replayed)
            {
                response.Headers["Idempotency-Replayed"] = "true";
            }

            return Results.Accepted(accepted.StatusUrl, accepted);
        }
        catch (UploadValidationException exception)
        {
            if (exception.IsTooLarge)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status413PayloadTooLarge,
                    title: "The upload is too large.");
            }

            return ValidationProblem("file", exception.Message);
        }
        catch (InvalidDataException)
        {
            return ValidationProblem("file", "The multipart upload is malformed or too large.");
        }
        finally
        {
            if (stagedUpload is not null && !retainStoredObject)
            {
                await uploadService
                    .DiscardAsync(stagedUpload, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task<IResult> GetStatusAsync(
        Guid jobId,
        ICurrentTenant currentTenant,
        RagDbContext dbContext,
        CancellationToken cancellationToken)
    {
        IngestionJob? job = await dbContext.IngestionJobs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == jobId &&
                    candidate.TenantId == currentTenant.TenantId,
                cancellationToken)
            .ConfigureAwait(false);

        return job is null
            ? Results.NotFound()
            : Results.Ok(MapStatus(job));
    }

    private static async Task<IResult> RetryAsync(
        Guid jobId,
        ICurrentTenant currentTenant,
        RagDbContext dbContext,
        IIngestionJobQueue queue,
        CancellationToken cancellationToken)
    {
        IngestionJob? job = await dbContext.IngestionJobs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == jobId &&
                    candidate.TenantId == currentTenant.TenantId,
                cancellationToken)
            .ConfigureAwait(false);
        if (job is null)
        {
            return Results.NotFound();
        }

        if (job.Status is not (IngestionJobStatus.Failed or IngestionJobStatus.DeadLetter))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Only failed or dead-letter jobs can be retried.");
        }

        bool retried = await queue.RetryAsync(
            currentTenant.TenantId,
            jobId,
            cancellationToken).ConfigureAwait(false);
        if (!retried)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The ingestion job state changed before it could be retried.");
        }

        job = await dbContext.IngestionJobs
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == jobId, cancellationToken)
            .ConfigureAwait(false);
        IngestionStatusResponse status = MapStatus(job);
        return Results.Accepted($"/v1/ingestions/{jobId:D}", status);
    }

    private static bool TryGetIdempotencyKey(
        HttpRequest request,
        out string idempotencyKey)
    {
        idempotencyKey = string.Empty;
        if (!request.Headers.TryGetValue(IdempotencyHeaderName, out StringValues values) ||
            values.Count != 1)
        {
            return false;
        }

        string? value = values[0];
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumIdempotencyKeyLength ||
            !value.All(static character => character is >= '!' and <= '~'))
        {
            return false;
        }

        idempotencyKey = value;
        return true;
    }

    private static bool TryGetBoundary(string? contentType, out string boundary)
    {
        boundary = string.Empty;
        if (!MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? mediaType) ||
            !string.Equals(
                mediaType.MediaType.Value,
                "multipart/form-data",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value ?? string.Empty;
        return boundary.Length is > 0 and <= MaximumBoundaryLength;
    }

    private static bool TryReadFileMetadata(
        MultipartSection? section,
        out string fileName,
        out string contentType,
        out string validationError)
    {
        fileName = string.Empty;
        contentType = string.Empty;
        validationError = string.Empty;
        if (section is null ||
            !ContentDispositionHeaderValue.TryParse(
                section.ContentDisposition,
                out ContentDispositionHeaderValue? disposition) ||
            !string.Equals(
                disposition.DispositionType.Value,
                "form-data",
                StringComparison.OrdinalIgnoreCase) ||
            (!disposition.FileName.HasValue && !disposition.FileNameStar.HasValue))
        {
            validationError = "A single form-data file section is required.";
            return false;
        }

        string untrustedName = HeaderUtilities.RemoveQuotes(
            disposition.FileNameStar.HasValue
                ? disposition.FileNameStar
                : disposition.FileName).Value ?? string.Empty;
        fileName = Path.GetFileName(untrustedName.Replace('\\', '/'));
        if (fileName.Length is 0 or > 512 ||
            !string.Equals(Path.GetExtension(fileName), ".txt", StringComparison.OrdinalIgnoreCase))
        {
            validationError = "Only .txt files with names up to 512 characters are allowed.";
            return false;
        }

        if (!MediaTypeHeaderValue.TryParse(
                section.ContentType,
                out MediaTypeHeaderValue? mediaType) ||
            !string.Equals(
                mediaType.MediaType.Value,
                "text/plain",
                StringComparison.OrdinalIgnoreCase) ||
            (mediaType.Charset.HasValue &&
                !string.Equals(
                    mediaType.Charset.Value,
                    "utf-8",
                    StringComparison.OrdinalIgnoreCase)))
        {
            validationError = "The file Content-Type must be text/plain with UTF-8 encoding.";
            return false;
        }

        contentType = "text/plain";
        return true;
    }

    private static IResult ValidationProblem(string key, string error) =>
        Results.ValidationProblem(
            new Dictionary<string, string[]>
            {
                [key] = [error],
            });

    private static IngestionStatusResponse MapStatus(IngestionJob job) =>
        new(
            job.Id,
            job.DocumentId,
            job.KnowledgeBaseId,
            job.VersionId,
            job.Status.ToString(),
            job.Attempts,
            job.MaxAttempts,
            job.AvailableAt,
            job.LockedUntil);
}
