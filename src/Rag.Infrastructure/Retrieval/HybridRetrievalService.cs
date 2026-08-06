using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector;
using Rag.Application.Providers;
using Rag.Application.Retrieval;
using Rag.Domain.Enums;
using Rag.Infrastructure.Ingestion;
using Rag.Infrastructure.Persistence;

namespace Rag.Infrastructure.Retrieval;

internal sealed class HybridRetrievalService(
    RagDbContext dbContext,
    EmbeddingBatchExecutor embeddingExecutor,
    IChunkReranker reranker,
    IOptions<RetrievalOptions> options,
    ILogger<HybridRetrievalService> logger) : IHybridRetrievalService
{
    private const string VectorSearchSql =
        """
        SELECT chunk.id,
               chunk.document_id,
               document.file_name,
               chunk.chunk_index,
               chunk.content,
               chunk.start_offset,
               chunk.end_offset,
               (1.0 - (chunk.embedding <=> $4))::double precision AS strategy_score
        FROM document_chunks AS chunk
        INNER JOIN documents AS document
            ON document.tenant_id = chunk.tenant_id
           AND document.knowledge_base_id = chunk.knowledge_base_id
           AND document.version_id = chunk.version_id
           AND document.id = chunk.document_id
        INNER JOIN knowledge_base_versions AS version
            ON version.tenant_id = chunk.tenant_id
           AND version.knowledge_base_id = chunk.knowledge_base_id
           AND version.id = chunk.version_id
        WHERE chunk.tenant_id = $1
          AND chunk.knowledge_base_id = $2
          AND chunk.version_id = $3
          AND version.status = 'Active'
        ORDER BY chunk.embedding <=> $4, chunk.id
        LIMIT $5;
        """;

    private const string LexicalSearchSql =
        """
        WITH lexical_query AS (
            SELECT websearch_to_tsquery(CAST($4 AS regconfig), $5) AS value
        )
        SELECT chunk.id,
               chunk.document_id,
               document.file_name,
               chunk.chunk_index,
               chunk.content,
               chunk.start_offset,
               chunk.end_offset,
               ts_rank_cd(chunk.search_vector, lexical_query.value, 32)::double precision
                   AS strategy_score
        FROM document_chunks AS chunk
        INNER JOIN documents AS document
            ON document.tenant_id = chunk.tenant_id
           AND document.knowledge_base_id = chunk.knowledge_base_id
           AND document.version_id = chunk.version_id
           AND document.id = chunk.document_id
        INNER JOIN knowledge_base_versions AS version
            ON version.tenant_id = chunk.tenant_id
           AND version.knowledge_base_id = chunk.knowledge_base_id
           AND version.id = chunk.version_id
        CROSS JOIN lexical_query
        WHERE chunk.tenant_id = $1
          AND chunk.knowledge_base_id = $2
          AND chunk.version_id = $3
          AND version.status = 'Active'
          AND lexical_query.value @@ chunk.search_vector
        ORDER BY strategy_score DESC, chunk.id
        LIMIT $6;
        """;

    public async ValueTask<RetrievalResult?> RetrieveAsync(
        RetrievalCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Query);

        ActiveVersion? preflight = await FindActiveVersionAsync(
            command,
            cancellationToken).ConfigureAwait(false);
        if (preflight is null)
        {
            return null;
        }

        EmbeddingBatch? embedding = null;
        bool degraded = false;
        try
        {
            embedding = await embeddingExecutor
                .GenerateAsync([command.Query], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EmbeddingProviderException exception)
        {
            degraded = true;
            logger.LogWarning(
                "Query embedding failed; lexical retrieval will be used. ErrorType={ErrorType} Transient={Transient}",
                exception.GetType().Name,
                exception.IsTransient);
        }

        IReadOnlyList<RetrievalCandidate> vectorRanking = [];
        IReadOnlyList<RetrievalCandidate> lexicalRanking;
        Guid versionId;
        await using (IDbContextTransaction transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false))
        {
            ActiveVersion? activeVersion = await FindActiveVersionAsync(
                command,
                cancellationToken).ConfigureAwait(false);
            if (activeVersion is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            versionId = activeVersion.Id;
            ReadOnlyMemory<float>? queryVector = ValidateEmbedding(
                embedding,
                activeVersion);
            if (embedding is not null && queryVector is null)
            {
                degraded = true;
                logger.LogWarning(
                    "Query embedding metadata was incompatible with the active version; lexical retrieval will be used. VersionId={VersionId}",
                    activeVersion.Id);
            }

            NpgsqlConnection connection = (NpgsqlConnection)dbContext.Database
                .GetDbConnection();
            NpgsqlTransaction npgsqlTransaction = (NpgsqlTransaction)transaction
                .GetDbTransaction();
            if (queryVector is ReadOnlyMemory<float> vector)
            {
                vectorRanking = await SearchVectorAsync(
                    connection,
                    npgsqlTransaction,
                    command,
                    activeVersion.Id,
                    vector,
                    cancellationToken).ConfigureAwait(false);
            }

            lexicalRanking = await SearchLexicalAsync(
                connection,
                npgsqlTransaction,
                command,
                activeVersion.Id,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<RetrievedChunk> fused = ReciprocalRankFusion.Fuse(
            vectorRanking,
            lexicalRanking,
            options.Value.ReciprocalRankConstant);
        IReadOnlyList<RetrievedChunk> reranked = fused;
        try
        {
            reranked = await reranker
                .RerankAsync(command.Query, fused, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            degraded = true;
            logger.LogWarning(
                "Chunk reranking failed; fused ranking will be returned. ErrorType={ErrorType}",
                exception.GetType().Name);
        }

        IReadOnlyList<RetrievedChunk> selected = SelectFinalChunks(reranked);
        return new RetrievalResult(
            command.KnowledgeBaseId,
            versionId,
            degraded,
            selected);
    }

    private async Task<ActiveVersion?> FindActiveVersionAsync(
        RetrievalCommand command,
        CancellationToken cancellationToken) =>
        await dbContext.KnowledgeBaseVersions
            .AsNoTracking()
            .Where(version =>
                version.TenantId == command.TenantId &&
                version.KnowledgeBaseId == command.KnowledgeBaseId &&
                version.Status == KnowledgeBaseVersionStatus.Active &&
                (command.ChatbotId == null || dbContext.Chatbots.Any(chatbot =>
                    chatbot.Id == command.ChatbotId &&
                    chatbot.TenantId == command.TenantId &&
                    chatbot.KnowledgeBaseId == command.KnowledgeBaseId)))
            .Select(version => new ActiveVersion(
                version.Id,
                version.EmbeddingModel,
                version.EmbeddingDimensions))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    private static ReadOnlyMemory<float>? ValidateEmbedding(
        EmbeddingBatch? embedding,
        ActiveVersion activeVersion)
    {
        if (embedding is null ||
            embedding.Vectors.Count != 1 ||
            embedding.Dimensions != activeVersion.EmbeddingDimensions ||
            !string.Equals(
                embedding.Model,
                activeVersion.EmbeddingModel,
                StringComparison.Ordinal))
        {
            return null;
        }

        ReadOnlyMemory<float> vector = embedding.Vectors[0];
        if (vector.Length != activeVersion.EmbeddingDimensions ||
            vector.Span.ContainsAnyExceptFinite())
        {
            return null;
        }

        return vector;
    }

    private async Task<IReadOnlyList<RetrievalCandidate>> SearchVectorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RetrievalCommand command,
        Guid versionId,
        ReadOnlyMemory<float> queryVector,
        CancellationToken cancellationToken)
    {
        await using var sql = new NpgsqlCommand(VectorSearchSql, connection, transaction);
        sql.Parameters.AddWithValue(command.TenantId);
        sql.Parameters.AddWithValue(command.KnowledgeBaseId);
        sql.Parameters.AddWithValue(versionId);
        sql.Parameters.AddWithValue(new Vector(queryVector.ToArray()));
        sql.Parameters.AddWithValue(options.Value.VectorTopK);
        return await ReadCandidatesAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<RetrievalCandidate>> SearchLexicalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RetrievalCommand command,
        Guid versionId,
        CancellationToken cancellationToken)
    {
        await using var sql = new NpgsqlCommand(LexicalSearchSql, connection, transaction);
        sql.Parameters.AddWithValue(command.TenantId);
        sql.Parameters.AddWithValue(command.KnowledgeBaseId);
        sql.Parameters.AddWithValue(versionId);
        sql.Parameters.AddWithValue(options.Value.TextSearchConfiguration);
        sql.Parameters.AddWithValue(command.Query);
        sql.Parameters.AddWithValue(options.Value.LexicalTopK);
        return await ReadCandidatesAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<RetrievalCandidate>> ReadCandidatesAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var candidates = new List<RetrievalCandidate>();
        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(new RetrievalCandidate(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetString(4),
                reader.GetDouble(7)));
        }

        return candidates;
    }

    private IReadOnlyList<RetrievedChunk> SelectFinalChunks(
        IReadOnlyList<RetrievedChunk> chunks)
    {
        var selected = new List<RetrievedChunk>(options.Value.FinalTopK);
        var documentCounts = new Dictionary<Guid, int>();
        var seenChunks = new HashSet<Guid>();
        foreach (RetrievedChunk chunk in chunks)
        {
            if (chunk.Score < options.Value.MinimumScore ||
                !seenChunks.Add(chunk.ChunkId))
            {
                continue;
            }

            documentCounts.TryGetValue(chunk.DocumentId, out int documentCount);
            if (documentCount >= options.Value.MaxResultsPerDocument)
            {
                continue;
            }

            selected.Add(chunk);
            documentCounts[chunk.DocumentId] = documentCount + 1;
            if (selected.Count == options.Value.FinalTopK)
            {
                break;
            }
        }

        return selected;
    }

    private sealed record ActiveVersion(
        Guid Id,
        string EmbeddingModel,
        int EmbeddingDimensions);
}

internal static class FloatSpanExtensions
{
    public static bool ContainsAnyExceptFinite(this ReadOnlySpan<float> values)
    {
        foreach (float value in values)
        {
            if (!float.IsFinite(value))
            {
                return true;
            }
        }

        return false;
    }
}
