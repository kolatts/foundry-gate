namespace FoundryGate.Tests.Predeployment.Data.Conventions;

/// <summary>
/// Foundry-Gate-specific design convention checks. Standard rules (nullable-string ban, max
/// length, boolean prefixes, etc.) live in the framework base
/// (<see cref="FoundryGateConventionTests"/>); this covers what it doesn't.
/// </summary>
public class DesignTests : InMemoryDatabaseTest
{
    [Fact]
    public void NonNullableStrings_ShouldDefaultToEmpty()
    {
        var violations = Context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties()
                .Where(p => p.ClrType == typeof(string))
                .Where(p => !p.IsNullable)
                .Select(property =>
                {
                    try
                    {
                        var instance = Activator.CreateInstance(entity.ClrType);
                        var value = property.PropertyInfo?.GetValue(instance) as string;
                        if (value is null || !string.Equals(value, string.Empty, StringComparison.Ordinal))
                        {
                            return $"{entity.ClrType.Name}.{property.Name} should default to string.Empty";
                        }
                    }
                    catch (MissingMethodException)
                    {
                        // No parameterless constructor - not one of our entities.
                    }

                    return null;
                })
                .Where(v => v is not null))
            .ToList();

        Assert.True(violations.Count == 0, $"Found {violations.Count} violation(s):\n  - {string.Join("\n  - ", violations)}");
    }
}
