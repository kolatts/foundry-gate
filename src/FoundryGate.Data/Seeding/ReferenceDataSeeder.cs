using FoundryGate.Data.Entities;

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
            [nameof(SystemConfiguration)] = await context
                .SyncReferenceDataAsync<SystemConfiguration, string>(cancellationToken: cancellationToken)
        };

        return results;
    }
}
