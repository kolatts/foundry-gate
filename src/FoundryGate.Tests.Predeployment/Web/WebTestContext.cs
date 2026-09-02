using Bunit;
using Bunit.TestDoubles;
using FoundryGate.Domain.Constants;
using FoundryGate.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// The shared bUnit host for every FoundryGate.Web component test, wired the way
/// <c>Program.cs</c> wires the real app: MudBlazor's services, an
/// <see cref="IFoundryGateApiClient"/> that answers from memory instead of over HTTP, the scoped
/// <see cref="DashboardStateService"/> the nav badge reads, a <see cref="TimeProvider"/>, bUnit's
/// fake authentication state, and loose JS interop so MudBlazor's popovers, focus traps and resize
/// observers don't need a browser.
/// </summary>
/// <remarks>
/// Derive a test class from this and call <see cref="SignInAsDeveloper"/> or
/// <see cref="SignInAsAdmin"/> before rendering anything that reads authentication state.
/// <para>
/// Pages are rendered with <see cref="RenderPage{TPage}"/> rather than <c>Render&lt;TPage&gt;</c>,
/// and it returns the <em>root</em>: MudBlazor hosts dialogs inside <see cref="MudDialogProvider"/>,
/// which sits beside the page rather than inside it, so a result scoped to the page alone can
/// neither see a confirmation dialog nor click its buttons.
/// <c>root.FindComponent&lt;TPage&gt;()</c> reaches the page itself when a test needs it.
/// </para>
/// </remarks>
public abstract class WebTestContext : BunitContext, IAsyncLifetime
{
    protected WebTestContext()
    {
        Services.AddMudServices();
        Services.AddSingleton<IFoundryGateApiClient>(Api);
        Services.AddScoped<DashboardStateService>();
        // Resolved lazily so a test can replace Time after the constructor has run but before it
        // renders — which is the only order a test can actually use.
        Services.AddSingleton(_ => Time);

        // MudBlazor calls into JS on almost every component's first render. Loose mode answers all
        // of it with defaults instead of failing the test for something the component isn't about.
        JSInterop.Mode = JSRuntimeMode.Loose;

        Authorization = AddAuthorization();
    }

    /// <summary>The in-memory API the components under test talk to. Arrange on it before rendering.</summary>
    public FakeFoundryGateApiClient Api { get; } = new();

    /// <summary>
    /// The clock and time zone components see. Defaults to the real one; a test that cares about
    /// zone conversion (<c>/audit</c>'s date range) replaces it before rendering.
    /// </summary>
    public TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>bUnit's authentication/authorization double, behind <c>AuthorizeView</c> and <c>[Authorize]</c>.</summary>
    protected BunitAuthorizationContext Authorization { get; }

    /// <summary>Snackbar messages raised during the test, in the order they were added.</summary>
    protected IEnumerable<Snackbar> Snackbars => Services.GetRequiredService<ISnackbar>().ShownSnackbars;

    /// <summary>Signs in an authenticated caller with no roles — a developer.</summary>
    protected void SignInAsDeveloper(string displayName = "Dev Eloper") => Authorization.SetAuthorized(displayName);

    /// <summary>Signs in a caller holding <see cref="RoleNames.Admin"/> — what every admin page requires.</summary>
    protected void SignInAsAdmin(string displayName = "Ada Admin")
    {
        Authorization.SetAuthorized(displayName);
        Authorization.SetRoles(RoleNames.Admin);
    }

    /// <summary>Leaves the caller unauthenticated.</summary>
    protected void SignOut() => Authorization.SetNotAuthorized();

    /// <summary>
    /// Renders a page alongside MudBlazor's popover and dialog providers and returns the root, so
    /// dialogs the page opens are part of the same tree and can be found and clicked.
    /// </summary>
    /// <param name="parameters">Route/component parameters, e.g. the <c>Id</c> of a detail page.</param>
    protected IRenderedComponent<IComponent> RenderPage<TPage>(params (string Name, object? Value)[] parameters)
        where TPage : IComponent
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var attributes = parameters.ToDictionary(p => p.Name, p => p.Value);

        return Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<TPage>(2);
            builder.AddMultipleAttributes(3, attributes!);
            builder.CloseComponent();
        });
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// MudBlazor registers services that only implement <see cref="IAsyncDisposable"/>
    /// (<c>KeyInterceptorService</c> among them), and a synchronous container teardown throws on
    /// those. xUnit disposes an <see cref="IAsyncLifetime"/> test class asynchronously first, so the
    /// real teardown happens here and <see cref="Dispose(bool)"/> is left with nothing to do.
    /// </summary>
    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        // Deliberately empty — see IAsyncLifetime.DisposeAsync above.
    }
}
