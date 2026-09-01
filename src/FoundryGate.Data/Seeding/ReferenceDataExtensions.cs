using System.Reflection;
using Imagile.Framework.Core.Attributes;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Data.Seeding;

/// <summary>
/// Synchronizes <see cref="IReferenceDataEntity{TSelf,TId}"/> rows: adds missing rows, updates
/// existing rows (skipping any property tagged <c>[DoNotUpdate]</c>), and deletes rows no longer
/// present in the seed data. Idempotent: running it twice with the same seed data is a no-op the
/// second time.
/// </summary>
public static class ReferenceDataExtensions
{
    /// <summary>
    /// Synchronizes reference data for <typeparamref name="TEntity"/> against <paramref name="context"/>.
    /// </summary>
    /// <typeparam name="TEntity">The reference entity type.</typeparam>
    /// <typeparam name="TId">The type of <see cref="IReferenceDataEntity{TSelf,TId}.ItemId"/>.</typeparam>
    /// <param name="context">The database context.</param>
    /// <param name="seedData">Seed data to sync to; defaults to <see cref="IReferenceDataEntity{TSelf,TId}.GetSeedData"/>.</param>
    /// <param name="deleteFilter">Optional filter restricting which orphaned rows are deleted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Counts of added, updated, and deleted rows.</returns>
    public static async Task<ReferenceDataSyncResult> SyncReferenceDataAsync<TEntity, TId>(
        this DbContext context,
        IEnumerable<TEntity>? seedData = null,
        Func<TEntity, bool>? deleteFilter = null,
        CancellationToken cancellationToken = default)
        where TEntity : class, IReferenceDataEntity<TEntity, TId>
        where TId : notnull
    {
        ArgumentNullException.ThrowIfNull(context);

        var seed = (seedData ?? TEntity.GetSeedData()).ToList();

        // Deliberately NOT AsNoTracking(): existing rows come back tracked (Unchanged), so
        // mutating them in place below marks them Modified automatically. Fetching untracked and
        // then calling Set<TEntity>().Update(current) on a detached copy is what the original
        // version of this method did, and it throws once anything else in this DbContext instance
        // has already loaded the same row (EF refuses to attach a second instance with a key it's
        // already tracking) — a latent bug that only stayed hidden because SystemConfiguration
        // marks every column [DoNotUpdate], so the Update() branch was never actually reached.
        var existing = await context.Set<TEntity>().ToListAsync(cancellationToken);
        var existingById = existing.ToDictionary(e => e.ItemId);

        var added = 0;
        var updated = 0;

        foreach (var seeded in seed)
        {
            if (existingById.TryGetValue(seeded.ItemId, out var tracked))
            {
                if (UpdateProperties(context, tracked, seeded))
                {
                    updated++;
                }

                existingById.Remove(seeded.ItemId);
            }
            else
            {
                context.Set<TEntity>().Add(seeded);
                added++;
            }
        }

        var orphans = deleteFilter is null
            ? existingById.Values
            : existingById.Values.Where(deleteFilter);

        var orphanList = orphans.ToList();
        context.RemoveRange(orphanList);

        await context.SaveChangesAsync(cancellationToken);
        return new ReferenceDataSyncResult(added, updated, orphanList.Count);
    }

    /// <summary>
    /// Copies every mapped scalar column from <paramref name="source"/> onto <paramref name="target"/>,
    /// skipping key columns and any column tagged <c>[DoNotUpdate]</c>.
    /// </summary>
    /// <remarks>
    /// Walks <c>IEntityType.GetProperties()</c> (EF's own scalar-property metadata) rather than
    /// raw CLR reflection over every public property: navigation properties (e.g.
    /// <c>SystemConfiguration.UpdatedByUser</c>) simply aren't in that set, so there's no risk of
    /// reflection-copying a navigation reference between two different instances of the related
    /// entity — a shared-instance accident that happened to be harmless only because nothing
    /// downstream relied on it.
    /// </remarks>
    /// <returns><see langword="true"/> if any property value actually changed.</returns>
    private static bool UpdateProperties<TEntity>(DbContext context, TEntity target, TEntity source)
        where TEntity : class
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity))
            ?? throw new InvalidOperationException($"Entity type {typeof(TEntity).Name} is not part of the model.");

        var keyProperties = entityType.FindPrimaryKey()?.Properties.Select(p => p.Name).ToHashSet()
            ?? [];

        var changed = false;
        foreach (var efProperty in entityType.GetProperties())
        {
            if (keyProperties.Contains(efProperty.Name))
            {
                continue;
            }

            var propertyInfo = efProperty.PropertyInfo;
            if (propertyInfo is null || propertyInfo.GetCustomAttribute<DoNotUpdateAttribute>() is not null)
            {
                continue;
            }

            var newValue = propertyInfo.GetValue(source);
            var currentValue = propertyInfo.GetValue(target);
            if (Equals(newValue, currentValue))
            {
                continue;
            }

            propertyInfo.SetValue(target, newValue);
            changed = true;
        }

        return changed;
    }
}

/// <summary>Result of a <see cref="ReferenceDataExtensions.SyncReferenceDataAsync{TEntity,TId}"/> call.</summary>
/// <param name="Added">Number of rows inserted.</param>
/// <param name="Updated">Number of rows with at least one changed, non-<c>[DoNotUpdate]</c> property.</param>
/// <param name="Deleted">Number of orphaned rows removed.</param>
public sealed record ReferenceDataSyncResult(int Added, int Updated, int Deleted)
{
    /// <summary>Total number of rows touched by the sync.</summary>
    public int TotalChanges => Added + Updated + Deleted;
}
