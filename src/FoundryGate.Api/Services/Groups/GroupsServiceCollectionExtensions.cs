namespace FoundryGate.Api.Services.Groups;

/// <summary>DI registration for the <c>Services/Groups</c> area. Invoked from <see cref="ApiServiceCollectionExtensions.AddFoundryGateApiServices"/>.</summary>
public static class GroupsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IGroupService"/> (CRUD + membership, #30/#31). Scoped, because it shares
    /// the request's <c>AppDbContext</c> with quota resolution and the audit writer — that shared
    /// context is what makes a group mutation, the allocations it moves and its audit row one unit of
    /// work.
    /// </summary>
    /// <remarks>
    /// The directory reconciliation behind <c>POST /groups/sync-entra</c> (#41) is <em>not</em>
    /// registered here any more: it moved to Core with the rest of the Entra area so the nightly
    /// <c>EntraSyncFunction</c> can run the same code (#151), and comes in through
    /// <c>AddEntraServices()</c> → <c>AddEntraCore()</c>.
    /// </remarks>
    public static IServiceCollection AddGroupsServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IGroupService, GroupService>();

        return services;
    }
}
