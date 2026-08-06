using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rag.Application.Abstractions;
using Rag.Application.Ingestion;
using Rag.Domain.Entities;
using Rag.Domain.Enums;
using Rag.Infrastructure.Persistence;

namespace Rag.Infrastructure.Ingestion;

internal sealed class KnowledgeBaseVersionMaintenance(
    RagDbContext dbContext,
    IClock clock,
    IOptions<VersionMaintenanceOptions> options) :
    IKnowledgeBaseVersionMaintenance
{
    public async ValueTask<int> ArchiveSupersededReadyVersionsAsync(
        CancellationToken cancellationToken)
    {
        DateTimeOffset cutoff = clock.UtcNow.AddHours(
            -options.Value.SupersededReadyAgeHours);
        List<KnowledgeBaseVersion> candidates = await dbContext.KnowledgeBaseVersions
            .Where(candidate =>
                candidate.Status == KnowledgeBaseVersionStatus.Ready &&
                candidate.UpdatedAt <= cutoff &&
                dbContext.KnowledgeBaseVersions.Any(active =>
                    active.TenantId == candidate.TenantId &&
                    active.KnowledgeBaseId == candidate.KnowledgeBaseId &&
                    active.Status == KnowledgeBaseVersionStatus.Active &&
                    active.UpdatedAt > candidate.UpdatedAt))
            .OrderBy(static candidate => candidate.UpdatedAt)
            .Take(options.Value.BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (KnowledgeBaseVersion candidate in candidates)
        {
            candidate.Archive();
        }

        if (candidates.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return candidates.Count;
    }
}
