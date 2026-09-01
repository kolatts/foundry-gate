using System.Security.Claims;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Tests.Predeployment.Data;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;

namespace FoundryGate.Tests.Predeployment.Api.Services.Identity;

/// <summary>
/// Claim parsing and <c>User</c> resolution for <see cref="CurrentUserAccessor"/>. The oid claim
/// arrives under two different claim types depending on the JWT handler's inbound claim mapping —
/// both must resolve, and a missing oid on an authenticated principal must be a 403-class failure
/// rather than a null.
/// </summary>
public class CurrentUserAccessorTests : InMemoryDatabaseTest
{
    private const string Oid = "11111111-2222-3333-4444-555555555555";

    [Theory]
    [InlineData(ClaimConstants.Oid)]
    [InlineData(ClaimConstants.ObjectId)]
    public void EntraObjectId_reads_the_oid_from_either_claim_type(string claimType)
    {
        var accessor = CreateAccessor(new Claim(claimType, Oid));

        Assert.Equal(Oid, accessor.EntraObjectId);
    }

    [Fact]
    public void EntraObjectId_throws_UnauthorizedAccessException_when_the_authenticated_principal_has_no_oid()
    {
        var accessor = CreateAccessor(new Claim(ClaimConstants.Name, "No Oid"));

        var exception = Assert.Throws<UnauthorizedAccessException>(() => accessor.EntraObjectId);

        Assert.Contains("oid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EntraObjectId_throws_UnauthorizedAccessException_when_the_principal_is_not_authenticated()
    {
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        var accessor = new CurrentUserAccessor(new FixedHttpContextAccessor(httpContext), Context);

        Assert.Throws<UnauthorizedAccessException>(() => accessor.EntraObjectId);
    }

    [Fact]
    public void EntraObjectId_throws_UnauthorizedAccessException_when_there_is_no_HttpContext_at_all()
    {
        var accessor = new CurrentUserAccessor(new FixedHttpContextAccessor(null), Context);

        Assert.Throws<UnauthorizedAccessException>(() => accessor.EntraObjectId);
    }

    [Fact]
    public void IsAdmin_is_true_only_with_the_admin_role_claim()
    {
        var admin = CreateAccessor(new Claim(ClaimConstants.Oid, Oid), new Claim(ClaimConstants.Roles, RoleNames.Admin));
        var developer = CreateAccessor(new Claim(ClaimConstants.Oid, Oid));
        var otherRole = CreateAccessor(new Claim(ClaimConstants.Oid, Oid), new Claim(ClaimConstants.Roles, "FoundryGate.Something"));

        Assert.True(admin.IsAdmin);
        Assert.False(developer.IsAdmin);
        Assert.False(otherRole.IsAdmin);
    }

    [Fact]
    public void DisplayName_and_Email_read_the_name_and_preferred_username_claims_and_are_null_when_absent()
    {
        var full = CreateAccessor(
            new Claim(ClaimConstants.Oid, Oid),
            new Claim(ClaimConstants.Name, "Ada Lovelace"),
            new Claim(ClaimConstants.PreferredUserName, "ada@contoso.com"));
        var bare = CreateAccessor(new Claim(ClaimConstants.Oid, Oid));

        Assert.Equal("Ada Lovelace", full.DisplayName);
        Assert.Equal("ada@contoso.com", full.Email);
        Assert.Null(bare.DisplayName);
        Assert.Null(bare.Email);
    }

    [Fact]
    public async Task TryGetUserAsync_returns_null_for_an_unknown_oid_as_a_first_class_outcome()
    {
        var accessor = CreateAccessor(new Claim(ClaimConstants.Oid, Oid));

        var user = await accessor.TryGetUserAsync(CancellationToken.None);

        Assert.Null(user);
    }

    [Fact]
    public async Task GetRequiredUserAsync_throws_UnauthorizedAccessException_for_an_unknown_oid_telling_the_caller_to_provision_first()
    {
        // 403, not 404: an authenticated principal with no row is an authorization-state problem
        // (not provisioned yet), and it must read the same as IAuditService.LogAsync's failure.
        var accessor = CreateAccessor(new Claim(ClaimConstants.Oid, Oid));

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => accessor.GetRequiredUserAsync(CancellationToken.None));

        Assert.Contains(Oid, exception.Message, StringComparison.Ordinal);
        Assert.Contains("GET /users/me", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryGetUserAsync_returns_the_tracked_row_and_the_same_instance_for_the_rest_of_the_request()
    {
        var seeded = new User { EntraObjectId = Oid, DisplayName = "Ada Lovelace", Email = "ada@contoso.com" };
        Context.Users.Add(seeded);
        await Context.SaveChangesAsync();
        Context.ChangeTracker.Clear();
        var accessor = CreateAccessor(new Claim(ClaimConstants.Oid, Oid));

        var first = await accessor.TryGetUserAsync(CancellationToken.None);
        var second = await accessor.GetRequiredUserAsync(CancellationToken.None);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(EntityState.Unchanged, Context.Entry(first).State);

        // "Tracked so callers can mutate": a plain property set must flow through SaveChanges.
        first.DisplayName = "Ada King";
        await Context.SaveChangesAsync();
        var reloaded = await Context.Users.AsNoTracking().SingleAsync(u => u.EntraObjectId == Oid);
        Assert.Equal("Ada King", reloaded.DisplayName);
    }

    [Fact]
    public async Task TryGetUserAsync_finds_a_User_that_was_added_to_the_context_but_not_yet_saved()
    {
        // The auto-provision unit of work (#28): Add the user, then resolve "the current user" for
        // the audit row, then save once. Before the fix this went straight to the database and missed.
        var accessor = CreateAccessor(new Claim(ClaimConstants.Oid, Oid));
        var added = new User { EntraObjectId = Oid, DisplayName = "New Joiner", Email = "new@contoso.com" };
        Context.Users.Add(added);

        var resolved = await accessor.TryGetUserAsync(CancellationToken.None);

        Assert.Same(added, resolved);
        Assert.Equal(EntityState.Added, Context.Entry(added).State);
    }

    [Fact]
    public async Task TryGetUserAsync_does_not_cache_a_miss_so_a_caller_that_provisions_the_user_sees_it_on_the_next_call()
    {
        var accessor = CreateAccessor(new Claim(ClaimConstants.Oid, Oid));

        Assert.Null(await accessor.TryGetUserAsync(CancellationToken.None));

        Context.Users.Add(new User { EntraObjectId = Oid, DisplayName = "New Joiner", Email = "new@contoso.com" });
        await Context.SaveChangesAsync();

        var provisioned = await accessor.TryGetUserAsync(CancellationToken.None);
        Assert.NotNull(provisioned);
        Assert.Equal("New Joiner", provisioned.DisplayName);
    }

    private CurrentUserAccessor CreateAccessor(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, "TestAuth", nameType: ClaimConstants.Name, roleType: ClaimConstants.Roles);
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        return new CurrentUserAccessor(new FixedHttpContextAccessor(httpContext), Context);
    }
}
