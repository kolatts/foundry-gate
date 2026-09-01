using FoundryGate.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Data;

/// <summary>
/// The single EF Core context for Foundry Gate's control-plane database. Registered plainly via
/// <c>AddDbContext&lt;AppDbContext&gt;</c> (see <see cref="ServiceCollectionExtensions"/>) — no
/// sharding, no per-tenant context resolution; a fork IS the tenant (CONVENTIONS.md).
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Group> Groups => Set<Group>();

    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();

    public DbSet<QuotaAllocation> QuotaAllocations => Set<QuotaAllocation>();

    public DbSet<QuotaIncreaseRequest> QuotaIncreaseRequests => Set<QuotaIncreaseRequest>();

    public DbSet<SystemConfiguration> SystemConfigurations => Set<SystemConfiguration>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
