using FoundryGate.Cli.Commands.Db.Compare;
using Microsoft.SqlServer.Dac;

namespace FoundryGate.Tests.Predeployment.Cli;

/// <summary>
/// <c>db compare</c> must only ever look at table shape — CONVENTIONS.md's schema pipeline has nothing
/// to say about logins, permissions, or any other DacFx object type, so those must never appear as a
/// difference or get touched by <c>--apply</c>. These tests are what makes that true.
/// </summary>
public class SchemaCompareOptionsTests
{
    [Fact]
    public void Tables_is_the_only_included_object_type()
    {
        Assert.Equal([ObjectType.Tables], SchemaCompareOptions.IncludedObjectTypes);
    }

    [Fact]
    public void Every_object_type_except_Tables_is_excluded()
    {
        var allTypes = Enum.GetValues<ObjectType>();

        Assert.DoesNotContain(ObjectType.Tables, SchemaCompareOptions.ExcludedObjectTypes);
        Assert.Equal(allTypes.Length - 1, SchemaCompareOptions.ExcludedObjectTypes.Count);
        Assert.All(allTypes.Where(type => type != ObjectType.Tables), type => Assert.Contains(type, SchemaCompareOptions.ExcludedObjectTypes));
    }

    [Fact]
    public void Create_scopes_the_options_to_tables_and_ignores_column_order()
    {
        var options = SchemaCompareOptions.Create();

        Assert.DoesNotContain(ObjectType.Tables, options.ExcludeObjectTypes);
        Assert.Contains(ObjectType.Users, options.ExcludeObjectTypes);
        Assert.True(options.IgnoreColumnOrder);
        Assert.True(options.IgnoreWhitespace);
        Assert.True(options.IgnoreComments);
    }
}
