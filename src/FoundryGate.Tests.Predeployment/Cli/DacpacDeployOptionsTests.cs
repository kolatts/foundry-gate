using FoundryGate.Cli.Commands.Db.Deploy;
using Microsoft.SqlServer.Dac;

namespace FoundryGate.Tests.Predeployment.Cli;

/// <summary>
/// The dacpac and <c>db grant-identities</c> share one invariant: everything the CLI creates after the
/// deploy (the API/Functions contained users and their role memberships) is excluded from the comparison,
/// so a <c>--drop-objects</c> deploy cannot strip it. <c>_deploy-database.yml</c> states that in a comment;
/// these tests are what makes the statement true.
/// </summary>
public class DacpacDeployOptionsTests
{
    [Fact]
    public void Excludes_both_the_contained_users_and_their_role_memberships()
    {
        // Users alone is not enough: DacFx treats a role membership as its own object type, so a
        // --drop-objects run would keep the user and drop its db_datareader/db_datawriter grants.
        Assert.Contains(ObjectType.Users, DacpacDeployOptions.ExcludedObjectTypes);
        Assert.Contains(ObjectType.RoleMembership, DacpacDeployOptions.ExcludedObjectTypes);

        var options = DacpacDeployOptions.Create(dropObjects: true, allowDataLoss: false);

        Assert.Contains(ObjectType.Users, options.ExcludeObjectTypes);
        Assert.Contains(ObjectType.RoleMembership, options.ExcludeObjectTypes);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Data_loss_is_blocked_unless_explicitly_allowed(bool allowDataLoss, bool expectedBlock)
    {
        // CONVENTIONS.md §Schema pipeline: safety is the default, --allow-data-loss is the opt-out.
        var options = DacpacDeployOptions.Create(dropObjects: false, allowDataLoss);

        Assert.Equal(expectedBlock, options.BlockOnPossibleDataLoss);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Drop_objects_maps_straight_to_DropObjectsNotInSource(bool dropObjects)
    {
        var options = DacpacDeployOptions.Create(dropObjects, allowDataLoss: false);

        Assert.Equal(dropObjects, options.DropObjectsNotInSource);
        Assert.True(options.GenerateSmartDefaults);
        Assert.True(options.ScriptDatabaseCompatibility);
    }
}
