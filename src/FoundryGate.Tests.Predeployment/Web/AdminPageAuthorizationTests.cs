using FoundryGate.Domain.Constants;
using FoundryGate.Web.Pages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// Route-level authorization for the admin pages. <c>App.razor</c> already turns a failed
/// <see cref="AuthorizeAttribute"/> into <c>AccessDenied</c> for an authenticated caller and a
/// sign-in redirect for an anonymous one, so what each page has to get right is the attribute
/// itself — and a missing role there is a silent hole, not a visible bug.
/// </summary>
public class AdminPageAuthorizationTests
{
    public static TheoryData<Type> AdminOnlyPages() =>
    [
        typeof(Users),
        typeof(UserDetail),
        typeof(UsersSync),
        typeof(Groups),
        typeof(GroupNew),
        typeof(GroupDetail),
        typeof(Foundry),
        typeof(QuotaAllocations),
    ];

    [Theory]
    [MemberData(nameof(AdminOnlyPages))]
    public void Admin_pages_require_the_admin_role(Type page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var authorize = Assert.Single(page.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>());

        Assert.Equal(RoleNames.Admin, authorize.Roles);
    }

    [Theory]
    [MemberData(nameof(AdminOnlyPages))]
    public void Admin_pages_are_routable(Type page)
    {
        ArgumentNullException.ThrowIfNull(page);

        Assert.NotEmpty(page.GetCustomAttributes(typeof(RouteAttribute), inherit: true));
    }

    [Theory]
    [InlineData(typeof(Requests))]
    [InlineData(typeof(RequestDetail))]
    public void The_request_pages_are_open_to_any_authenticated_caller(Type page)
    {
        ArgumentNullException.ThrowIfNull(page);

        // A developer sees their own requests here; the API — not this attribute — is what keeps
        // them from seeing anyone else's, and RequestReviewPanel is what keeps them from
        // reviewing.
        var authorize = Assert.Single(page.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>());

        Assert.Null(authorize.Roles);
    }
}
