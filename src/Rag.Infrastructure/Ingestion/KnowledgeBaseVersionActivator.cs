using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Rag.Application.Ingestion;
using Rag.Domain.Entities;
using Rag.Domain.Enums;
using Rag.Infrastructure.Persistence;

namespace Rag.Infrastructure.Ingestion;

internal sealed class KnowledgeBaseVersionActivator(RagDbContext dbContext) :
    IKnowledgeBaseVersionActivator
{
    public async ValueTask<VersionActivationResult> ActivateAsync(
        Guid tenantId,
        Guid knowledgeBaseId,
        Guid versionId,
        CancellationToken cancellationToken)
    {
        IDbContextTransaction? ownedTransaction = null;
        if (dbContext.Database.CurrentTransaction is null)
        {
            ownedTransaction = await dbContext.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            await LockKnowledgeBaseAsync(
                tenantId,
                knowledgeBaseId,
                cancellationToken).ConfigureAwait(false);
            KnowledgeBaseVersion target = await dbContext.KnowledgeBaseVersions
                .SingleOrDefaultAsync(
                    version => version.Id == versionId &&
                        version.TenantId == tenantId &&
                        version.KnowledgeBaseId == knowledgeBaseId,
                    cancellationToken)
                .ConfigureAwait(false) ??
                throw new VersionActivationException(
                    "The knowledge base version was not found.");

            if (target.Status == KnowledgeBaseVersionStatus.Active)
            {
                if (ownedTransaction is not null)
                {
                    await ownedTransaction
                        .CommitAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                return new VersionActivationResult(target.Id, null, AlreadyActive: true);
            }

            await ValidateAsync(
                target,
                cancellationToken).ConfigureAwait(false);
            KnowledgeBaseVersion? previous = await dbContext.KnowledgeBaseVersions
                .SingleOrDefaultAsync(
                    version => version.TenantId == tenantId &&
                        version.KnowledgeBaseId == knowledgeBaseId &&
                        version.Status == KnowledgeBaseVersionStatus.Active,
                    cancellationToken)
                .ConfigureAwait(false);
            if (previous is not null)
            {
                previous.Archive();
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            target.Activate();
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (ownedTransaction is not null)
            {
                await ownedTransaction
                    .CommitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            return new VersionActivationResult(
                target.Id,
                previous?.Id,
                AlreadyActive: false);
        }
        catch
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction
                    .RollbackAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                dbContext.ChangeTracker.Clear();
            }

            throw;
        }
        finally
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task LockKnowledgeBaseAsync(
        Guid tenantId,
        Guid knowledgeBaseId,
        CancellationToken cancellationToken)
    {
        List<KnowledgeBase> matches = await dbContext.KnowledgeBases
            .FromSqlInterpolated(
                $"""
                SELECT knowledge_base.*, knowledge_base.xmin
                FROM knowledge_bases AS knowledge_base
                WHERE knowledge_base.id = {knowledgeBaseId}
                  AND knowledge_base.tenant_id = {tenantId}
                FOR UPDATE
                """)
            .AsTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (matches.Count == 0)
        {
            throw new VersionActivationException("The knowledge base was not found.");
        }
    }

    private async Task ValidateAsync(
        KnowledgeBaseVersion target,
        CancellationToken cancellationToken)
    {
        if (target.Status != KnowledgeBaseVersionStatus.Ready)
        {
            throw new VersionActivationException(
                $"Only a Ready version can be activated; current state is {target.Status}.");
        }

        IQueryable<Document> documents = dbContext.Documents.AsNoTracking()
            .Where(document => document.TenantId == target.TenantId &&
                document.KnowledgeBaseId == target.KnowledgeBaseId &&
                document.VersionId == target.Id);
        if (!await documents.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new VersionActivationException(
                "A version without documents cannot be activated.");
        }

        if (await documents.AnyAsync(
                document => document.Status != DocumentStatus.Indexed,
                cancellationToken).ConfigureAwait(false))
        {
            throw new VersionActivationException(
                "Every document must be indexed before activation.");
        }

        bool documentWithoutChunks = await documents.AnyAsync(
            document => !dbContext.DocumentChunks.AsNoTracking().Any(
                chunk => chunk.TenantId == target.TenantId &&
                    chunk.KnowledgeBaseId == target.KnowledgeBaseId &&
                    chunk.VersionId == target.Id &&
                    chunk.DocumentId == document.Id),
            cancellationToken).ConfigureAwait(false);
        if (documentWithoutChunks)
        {
            throw new VersionActivationException(
                "Every indexed document must contain at least one chunk.");
        }

        IQueryable<IngestionJob> jobs = dbContext.IngestionJobs.AsNoTracking()
            .Where(job => job.TenantId == target.TenantId &&
                job.KnowledgeBaseId == target.KnowledgeBaseId &&
                job.VersionId == target.Id);
        if (!await jobs.AnyAsync(cancellationToken).ConfigureAwait(false) ||
            await jobs.AnyAsync(
                job => job.Status != IngestionJobStatus.Completed,
                cancellationToken).ConfigureAwait(false) ||
            await documents.AnyAsync(
                document => !jobs.Any(job =>
                    job.DocumentId == document.Id &&
                    job.Status == IngestionJobStatus.Completed),
                cancellationToken).ConfigureAwait(false))
        {
            throw new VersionActivationException(
                "Every ingestion job must be completed before activation.");
        }
    }
}
