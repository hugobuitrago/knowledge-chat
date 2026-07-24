using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Rag.Application.Abstractions;
using Rag.Domain.Common;

namespace Rag.Infrastructure.Persistence;

internal sealed class UtcAuditableEntityInterceptor(IClock clock) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ApplyTimestamps(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ApplyTimestamps(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void ApplyTimestamps(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        DateTimeOffset now = clock.UtcNow.ToUniversalTime();

        foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<IHasCreatedAt> entry in
                 context.ChangeTracker.Entries<IHasCreatedAt>()
                     .Where(static entry => entry.State == EntityState.Added))
        {
            entry.Property(nameof(IHasCreatedAt.CreatedAt)).CurrentValue = now;
        }

        foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<IHasUpdatedAt> entry in
                 context.ChangeTracker.Entries<IHasUpdatedAt>()
                     .Where(static entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Property(nameof(IHasUpdatedAt.UpdatedAt)).CurrentValue = now;
        }
    }
}

