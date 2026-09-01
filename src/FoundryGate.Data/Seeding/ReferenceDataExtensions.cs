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
        var existing = await context.Set<TEntity>().AsNoTracking().ToListAsync(cancellationToken);
        var existingById = existing.ToDictionary(e => e.ItemId);

        var added = 0;
        var updated = 0;

        foreach (var seeded in seed)
        {
            if (existingById.TryGetValue(seeded.ItemId, out var current))
            {
                // Only attach/mark Modified when something actually changed, so a no-op sync
                // (the common case once an admin has customized a row) doesn't touch the row or
                // trip TimestampInterceptor into stamping a fresh ModifiedDate for nothing.
                if (UpdateProperties(context, current, seeded))
                {
                    context.Set<TEntity>().Update(current);
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
    /// Copies every writable property from <paramref name="source"/> onto <paramref name="target"/>,
    /// skipping key properties and any property tagged <c>[DoNotUpdate]</c>.
    /// </summary>
    /// <returns><see langword="true"/> if any property value actually changed.</returns>
    private static bool UpdateProperties<TEntity>(DbContext context, TEntity target, TEntity source)
        where TEntity : class
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity))
            ?? throw new InvalidOperationException($"Entity type {typeof(TEntity).Name} is not part of the model.");

        var keyProperties = entityType.FindPrimaryKey()?.Properties.Select(p => p.Name).ToHashSet()
            ?? [];

        var properties = typeof(TEntity)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite
                && p.GetCustomAttribute<DoNotUpdateAttribute>() is null
                && !keyProperties.Contains(p.Name));

        var changed = false;
        foreach (var property in properties)
        {
            var newValue = property.GetValue(source);
            var currentValue = property.GetValue(target);
            if (Equals(newValue, currentValue))
            {
                continue;
            }

            property.SetValue(target, newValue);
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
