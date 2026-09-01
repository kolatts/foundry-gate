using FoundryGate.Data;
using FoundryGate.Data.Entities;
using Imagile.Framework.EntityFrameworkCore.Testing;
using Imagile.Framework.EntityFrameworkCore.Testing.Configuration;
using Imagile.Framework.EntityFrameworkCore.Testing.Rules;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Tests.Predeployment.Data.Conventions;

/// <summary>
/// Standard EF Core naming/design conventions from
/// <c>Imagile.Framework.EntityFrameworkCore.Testing</c> (int/enum PKs, no nullable bools, no
/// nullable strings, string max-length, plural/PascalCase table names, FK/PK/DateTime/bool/Guid/
/// enum naming). App-specific rules NamingTests/DesignTests cover what this base doesn't.
/// </summary>
public class FoundryGateConventionTests : DbContextConventionTests
{
    protected override IEnumerable<DbContext> CreateContexts()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared")
            .Options;

        var context = new AppDbContext(options);
        context.Database.EnsureCreated();

        return [context];
    }

    protected override void Configure(ConventionTestOptionsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // SystemConfiguration is a natural string-keyed lookup table (Key = "DefaultMonthlyTokenQuota"
        // etc.) referenced by name from docs, code, and the admin UI — an int surrogate key would
        // add nothing but indirection. Spec §3.1 defines it this way explicitly.
        _ = builder.ForRule<PrimaryKeysMustBeIntsRule>(rule =>
            rule.ExcludeEntity<SystemConfiguration>());

        // User.EmployeeId is nullable by design: null means "Entra has not populated an employee
        // id for this user" (e.g. contractor, not-yet-synced), which is a real, distinct state
        // from "known to be blank" — collapsing it to string.Empty would lose that signal.
        _ = builder.ForRule<ProhibitNullableStringsRule>(rule =>
            rule.ExcludeProperty<User, string?>(u => u.EmployeeId));
    }
}
