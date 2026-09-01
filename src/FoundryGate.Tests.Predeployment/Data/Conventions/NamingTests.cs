using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Tests.Predeployment.Data.Conventions;

/// <summary>
/// Foundry-Gate-specific naming convention checks that go beyond what
/// <see cref="FoundryGateConventionTests"/> (the <c>Imagile.Framework.EntityFrameworkCore.Testing</c>
/// base) already covers — property/column parity, no "ID" casing, and the stricter
/// <c>{Entity}Unique</c> shape for Guid columns (the framework's own rule only requires the
/// property to end in "Unique", not be prefixed with the owning entity's name).
/// </summary>
public class NamingTests : InMemoryDatabaseTest
{
    [Fact]
    public void Properties_ShouldMatchSqlColumns()
    {
        var violations = Context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties()
                .Where(p => p.Name != p.GetColumnName())
                .Select(p => $"{entity.ClrType.Name}.{p.Name} maps to column [{p.GetColumnName()}]"))
            .ToList();

        Assert.True(violations.Count == 0, $"Found {violations.Count} violation(s):\n  - {string.Join("\n  - ", violations)}");
    }

    [Fact]
    public void PrimaryKeys_ShouldUseLowercaseD()
    {
        var violations = Context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties()
                .Where(p => p.IsPrimaryKey())
                .Where(p => p.Name.EndsWith("ID", StringComparison.Ordinal))
                .Select(p => $"{entity.ClrType.Name}.{p.Name} should use 'Id' not 'ID'"))
            .ToList();

        Assert.True(violations.Count == 0, $"Found {violations.Count} violation(s):\n  - {string.Join("\n  - ", violations)}");
    }

    [Fact]
    public void ForeignKeys_ShouldUseLowercaseD()
    {
        var violations = Context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties()
                .Where(p => p.IsForeignKey())
                .Where(p => p.Name.EndsWith("ID", StringComparison.Ordinal))
                .Select(p => $"{entity.ClrType.Name}.{p.Name} should use 'Id' not 'ID'"))
            .ToList();

        Assert.True(violations.Count == 0, $"Found {violations.Count} violation(s):\n  - {string.Join("\n  - ", violations)}");
    }

    [Fact]
    public void Entities_ShouldNotContainEntitySuffix()
    {
        var violations = Context.Model.GetEntityTypes()
            .Where(e => e.ClrType.Name.EndsWith("Entity", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.ClrType.Name)
            .ToList();

        Assert.True(violations.Count == 0, $"Found {violations.Count} violation(s):\n  - {string.Join("\n  - ", violations)}");
    }

    [Fact]
    public void Guids_ShouldUseEntityNameUnique()
    {
        var violations = Context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties()
                .Where(p => p.ClrType == typeof(Guid) || p.ClrType == typeof(Guid?))
                .Where(p => !p.IsPrimaryKey() && !p.IsForeignKey())
                .Where(p => !string.Equals(p.Name, $"{entity.ClrType.Name}Unique", StringComparison.Ordinal))
                .Select(p => $"{entity.ClrType.Name}.{p.Name} should be '{entity.ClrType.Name}Unique'"))
            .ToList();

        Assert.True(violations.Count == 0, $"Found {violations.Count} violation(s):\n  - {string.Join("\n  - ", violations)}");
    }

    [Fact]
    public void DbSets_ShouldBePlural()
    {
        var dbSetProperties = typeof(FoundryGate.Data.AppDbContext).GetProperties()
            .Where(p => p.PropertyType.IsGenericType
                && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>));

        var violations = dbSetProperties
            .Where(p => !p.Name.EndsWith('s'))
            .Select(p => $"AppDbContext: DbSet<> property '{p.Name}' should be plural")
            .ToList();

        Assert.True(violations.Count == 0, $"Found {violations.Count} violation(s):\n  - {string.Join("\n  - ", violations)}");
    }

    [Fact]
    public void ForeignKeys_ShouldMatchNavigationPropertyName()
    {
        var violations = Context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetForeignKeys()
                .Where(fk => fk.Properties.Count == 1)
                .Select(fk =>
                {
                    var navigationName = fk.DependentToPrincipal?.Name;
                    if (navigationName is null)
                    {
                        return null;
                    }

                    var fkProperty = fk.Properties[0];
                    var expectedName = $"{navigationName}Id";
                    return string.Equals(fkProperty.Name, expectedName, StringComparison.Ordinal)
                        ? null
                        : $"{entity.ClrType.Name}.{fkProperty.Name} should be named {expectedName} (navigation: {navigationName})";
                })
                .Where(v => v is not null))
            .ToList();

        Assert.True(violations.Count == 0, $"Found {violations.Count} violation(s):\n  - {string.Join("\n  - ", violations)}");
    }
}
