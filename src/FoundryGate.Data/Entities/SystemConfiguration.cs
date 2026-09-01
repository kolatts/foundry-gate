using System.ComponentModel.DataAnnotations;
using FoundryGate.Data.Interfaces;
using FoundryGate.Data.Seeding;
using Imagile.Framework.Core.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FoundryGate.Data.Entities;

/// <summary>
/// A single fork-wide configuration key/value pair (e.g. the default monthly token quota, the
/// APIM resource id). Seeded with placeholder defaults on deploy; fork operators overwrite the
/// <see cref="Value"/> via the admin <c>/config</c> page, and re-seeding must never clobber that
/// edit — see <see cref="ReferenceDataExtensions.SyncReferenceDataAsync{TEntity,TId}"/>.
/// </summary>
public class SystemConfiguration : IModifiedDate, IReferenceDataEntity<SystemConfiguration, string>
{
    /// <summary>
    /// The configuration key. A natural string key (not the usual <c>{Entity}Id</c> identity
    /// column) because forks and docs reference these keys by name; excluded from the
    /// int-primary-key convention rule in <c>FoundryGateConventionTests</c>.
    /// </summary>
    [Key]
    [Required]
    [StringLength(100)]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// The configured value. <c>[DoNotUpdate]</c>: seeding only inserts missing keys — it must
    /// never overwrite a value an operator already edited via the admin config page.
    /// </summary>
    [Required]
    [StringLength(500)]
    [DoNotUpdate]
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Set by <see cref="Interceptors.TimestampInterceptor"/> whenever this row is genuinely
    /// inserted or updated by application code. <c>[DoNotUpdate]</c> so re-running the reference
    /// data sync on a row that already exists does not touch it (and does not trip the
    /// interceptor into stamping a new timestamp for a no-op sync).
    /// </summary>
    [DoNotUpdate]
    public DateTimeOffset ModifiedDate { get; set; }

    /// <summary>
    /// Admin who last changed this key via the config UI; <see langword="null"/> for
    /// seeded/system writes. <c>[DoNotUpdate]</c> alongside <see cref="Value"/>.
    /// </summary>
    [DoNotUpdate]
    public int? UpdatedByUserId { get; set; }

    /// <summary>
    /// Explicit interface implementation so this identity accessor is invisible to both EF Core's
    /// model discovery and <see cref="ReferenceDataExtensions"/>'s public-property reflection —
    /// it is derived from <see cref="Key"/>, never an independent column.
    /// </summary>
    string IReferenceDataEntity<SystemConfiguration, string>.ItemId => Key;

    // Navigation
    public User? UpdatedByUser { get; set; }

    /// <summary>The eight placeholder defaults forks must override via the admin config page (spec §3.1).</summary>
    public static IEnumerable<SystemConfiguration> GetSeedData() =>
    [
        new() { Key = "DefaultMonthlyTokenQuota", Value = "1000000" },
        new() { Key = "ApimResourceId", Value = string.Empty },
        new() { Key = "ApimGatewayUrl", Value = string.Empty },
        new() { Key = "ApimProductId", Value = "foundrygate" },
        new() { Key = "FoundryResourceId", Value = string.Empty },
        new() { Key = "EntraTenantId", Value = string.Empty },
        new() { Key = "EntraGroupSyncEnabled", Value = "false" },
        new() { Key = "ResetDayOfMonth", Value = "1" }
    ];
}

/// <summary>
/// <see cref="SystemConfiguration.UpdatedByUserId"/> is one of several FKs into <see cref="User"/>
/// across the model; kept <see cref="DeleteBehavior.NoAction"/> (not the entity's cascade path) so
/// deleting a user never deletes/blocks unrelated configuration history.
/// </summary>
internal sealed class SystemConfigurationConfiguration : IEntityTypeConfiguration<SystemConfiguration>
{
    public void Configure(EntityTypeBuilder<SystemConfiguration> builder)
    {
        builder.HasOne(c => c.UpdatedByUser)
            .WithMany()
            .HasForeignKey(c => c.UpdatedByUserId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
