using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;

namespace FoundryGate.Data.Seeding;

/// <summary>
/// Seeds all code-defined reference data, in FK-dependency order. Idempotent — safe to run on
/// every deploy.
/// </summary>
public static class ReferenceDataSeeder
{
    /// <summary>Seeds reference data against <paramref name="context"/>.</summary>
    /// <returns>One <see cref="ReferenceDataSyncResult"/> per entity type, keyed by entity name.</returns>
    public static async Task<Dictionary<string, ReferenceDataSyncResult>> SeedAsync(
        AppDbContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var results = new Dictionary<string, ReferenceDataSyncResult>
        {
            // deleteFilter restricts orphan deletion to keys this code knows about — the seeded set
            // (SystemConfigurationKeys.All) plus the retired ones (SystemConfigurationKeys.Retired,
            // #164/#123). Without it, a fork operator who adds their own SystemConfiguration row (or a
            // future key this code doesn't know about yet) would have it silently deleted on the next
            // deploy's re-seed. Retiring a key is therefore a two-part move: drop it from All and from
            // GetSeedData so it stops being seeded, and name it in Retired so the filter still covers
            // it and the row is deleted on the next `db seed-reference`. Dropping it from All alone
            // would strand the row in every deployed database forever.
            [nameof(SystemConfiguration)] = await context.SyncReferenceDataAsync<SystemConfiguration, string>(
                deleteFilter: c => SystemConfigurationKeys.All.Contains(c.Key) || SystemConfigurationKeys.Retired.Contains(c.Key),
                cancellationToken: cancellationToken)
        };

        return results;
    }
}
