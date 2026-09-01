namespace FoundryGate.Data.Seeding;

/// <summary>
/// Interface for reference/lookup entities that <see cref="ReferenceDataExtensions.SyncReferenceDataAsync{TEntity,TId}"/>
/// can synchronize from code-defined seed data.
/// </summary>
/// <remarks>
/// Local port of the imagile-app <c>IReferenceDataEntity&lt;TSelf, TId&gt;</c> pattern. The public
/// <c>Imagile.Framework.EntityFrameworkCore</c> 1.0.12 package ships a similarly-named
/// <c>IReferenceEntity&lt;T&gt;</c> + <c>AddOrUpdateReferenceEntities</c> pair, but its upsert does a
/// blind <c>CurrentValues.SetValues(...)</c> that overwrites every column — including ones an admin
/// edited after seeding — and does not honor <c>[DoNotUpdate]</c>. That is wrong for
/// <c>SystemConfiguration</c>, whose whole point is that fork operators edit the seeded defaults via
/// the admin UI and re-seeding must not clobber their edits. This local copy keeps the
/// <c>[DoNotUpdate]</c>-respecting update semantics imagile-app relies on, reusing only the public,
/// unmodified <c>Imagile.Framework.Core.Attributes.DoNotUpdateAttribute</c>.
/// </remarks>
/// <typeparam name="TSelf">The implementing entity type.</typeparam>
/// <typeparam name="TId">The type of the item identifier (e.g. <see cref="string"/>, <see cref="int"/>, an enum).</typeparam>
public interface IReferenceDataEntity<TSelf, TId>
    where TSelf : IReferenceDataEntity<TSelf, TId>
    where TId : notnull
{
    /// <summary>The identifier used to match seed rows against existing rows during sync.</summary>
    TId ItemId { get; }

    /// <summary>Gets the full set of seed data for this entity type.</summary>
    static abstract IEnumerable<TSelf> GetSeedData();
}
