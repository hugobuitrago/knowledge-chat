using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Rag.Domain.Entities;

namespace Rag.Infrastructure.Persistence;

internal sealed class PersistenceInvariantInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Validate(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Validate(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void Validate(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<KnowledgeBaseVersion> entry in
                 context.ChangeTracker.Entries<KnowledgeBaseVersion>()
                     .Where(static entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            if (entry.Entity.EmbeddingDimensions != RagDatabaseConstants.EmbeddingDimensions)
            {
                throw new InvalidOperationException(
                    $"Embedding dimensions must be {RagDatabaseConstants.EmbeddingDimensions} for this schema.");
            }
        }

        foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<DocumentChunk> entry in
                 context.ChangeTracker.Entries<DocumentChunk>()
                     .Where(static entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            if (entry.Entity.Embedding.Length != RagDatabaseConstants.EmbeddingDimensions)
            {
                throw new InvalidOperationException(
                    $"Chunk embedding must contain {RagDatabaseConstants.EmbeddingDimensions} values.");
            }
        }
    }
}

