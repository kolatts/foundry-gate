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
            // deleteFilter restricts orphan deletion to the eight known seeded keys
            // (SystemConfigurationKeys.All): without it, a fork operator who adds their own
            // SystemConfiguration row (or a future key this code doesn't know about yet) would
            // have it silently deleted on the next deploy's re-seed.
            [nameof(SystemConfiguration)] = await context.SyncReferenceDataAsync<SystemConfiguration, string>(
                deleteFilter: c => SystemConfigurationKeys.All.Contains(c.Key),
                cancellationToken: cancellationToken)
        };

        return results;
    }
}
