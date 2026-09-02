namespace FoundryGate.Api.Services.Groups;

/// <summary>DI registration for the <c>Services/Groups</c> area. Invoked from <see cref="ApiServiceCollectionExtensions.AddFoundryGateApiServices"/>.</summary>
public static class GroupsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the two scoped services behind <c>/api/v1/groups</c>: <see cref="IGroupService"/>
    /// (CRUD + membership, #30/#31) and <see cref="IEntraGroupSyncService"/> (directory reconciliation,
    /// #41). Both are scoped because they share the request's <c>AppDbContext</c> with quota resolution
    /// and the audit writer — that shared context is what makes a group mutation, the allocations it
    /// moves and its audit row one unit of work.
    /// </summary>
    public static IServiceCollection AddGroupsServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IGroupService, GroupService>();
        services.AddScoped<IEntraGroupSyncService, EntraGroupSyncService>();

        return services;
    }
}
