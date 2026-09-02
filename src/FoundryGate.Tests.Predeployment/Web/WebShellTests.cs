using System.Reflection;
using Bunit;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Dashboard.Contracts;
using FoundryGate.Web.Layout;
using FoundryGate.Web.Pages;
using FoundryGate.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// The shell around the pages: who is allowed on each route, where the front door sends people, and
/// the nav's pending-request badge.
/// </summary>
public class WebShellTests : WebTestContext
{
    [Theory]
    [InlineData(typeof(Dashboard))]
    [InlineData(typeof(Config))]
    [InlineData(typeof(Audit))]
    public void Admin_pages_require_the_admin_role(Type page)
    {
        // The route-level attribute is what AuthorizeRouteView enforces (App.razor renders
        // AccessDenied for an authenticated caller who fails it), so the attribute IS the gate —
        // a page that loses it silently becomes readable by every signed-in developer.
        var attribute = page.GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(RoleNames.Admin, attribute.Roles);
    }

    [Theory]
    [InlineData(typeof(Me))]
    [InlineData(typeof(MeRequest))]
    public void Developer_pages_require_a_signed_in_caller_but_no_role(Type page)
    {
        var attribute = page.GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(attribute);
        Assert.Null(attribute.Roles);
    }

    [Fact]
    public void The_front_door_sends_an_admin_to_the_dashboard()
    {
        SignInAsAdmin();
        var navigation = Services.GetRequiredService<NavigationManager>();

        _ = RenderPage<Home>();

        Assert.EndsWith("/dashboard", navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void The_front_door_sends_a_developer_to_their_own_account()
    {
        SignInAsDeveloper();
        var navigation = Services.GetRequiredService<NavigationManager>();

        _ = RenderPage<Home>();

        Assert.EndsWith("/me", navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void The_front_door_leaves_an_anonymous_visitor_on_the_sign_in_pitch()
    {
        SignOut();
        var navigation = Services.GetRequiredService<NavigationManager>();
        var startingUri = navigation.Uri;

        var page = RenderPage<Home>();

        Assert.Equal(startingUri, navigation.Uri);
        Assert.Contains("Sign in", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_nav_hides_the_admin_section_from_a_developer()
    {
        SignInAsDeveloper();

        var nav = RenderPage<NavMenu>();

        Assert.Contains("My Account", nav.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Audit Log", nav.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void The_nav_shows_the_admin_section_to_an_admin()
    {
        SignInAsAdmin();

        var nav = RenderPage<NavMenu>();

        Assert.Contains("Audit Log", nav.Markup, StringComparison.Ordinal);
        Assert.Contains("Configuration", nav.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_pending_badge_appears_only_once_the_dashboard_has_published_a_count()
    {
        SignInAsAdmin();
        var state = Services.GetRequiredService<DashboardStateService>();
        var nav = RenderPage<NavMenu>();

        Assert.DoesNotContain("mud-badge-visible", nav.Markup, StringComparison.Ordinal);

        await nav.InvokeAsync(() => state.SetPendingRequestCount(4));

        Assert.Contains("4", nav.Find("[data-testid='nav-pending-badge']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_dashboard_and_the_nav_agree_on_the_pending_count()
    {
        SignInAsAdmin();
        Api.DashboardResult = ApiCallResult<DashboardSummaryResponse>.Ok(WebTestData.Dashboard(pendingRequestCount: 7));

        // Both components share the one scoped service, exactly as they do in the app.
        var page = Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<NavMenu>(1);
            builder.CloseComponent();
            builder.OpenComponent<Dashboard>(2);
            builder.CloseComponent();
        });

        Assert.Contains("7", page.Find("[data-testid='nav-pending-badge']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pending_count_never_goes_negative()
    {
        var state = new DashboardStateService();

        state.SetPendingRequestCount(-3);

        Assert.Equal(0, state.PendingRequestCount);
    }

    [Fact]
    public void Publishing_the_same_count_twice_does_not_re_render_subscribers()
    {
        var state = new DashboardStateService();
        var notifications = 0;
        state.Changed += () => notifications++;

        state.SetPendingRequestCount(2);
        state.SetPendingRequestCount(2);

        Assert.Equal(1, notifications);
    }

    [Fact]
    public void Reflected_constant_sets_are_not_empty_and_carry_the_real_values()
    {
        // If the trimmer ever strips these constants, the filter dropdowns go blank in a published
        // build and nothing else fails — so assert the reflection itself, not just the page.
        var actions = ConstantSet.StringConstants(typeof(AuditActions));

        Assert.NotEmpty(actions);
        Assert.Contains(AuditActions.UserProvisioned, actions);
        Assert.Equal(actions.Distinct(StringComparer.Ordinal).Count(), actions.Count);
    }
}
