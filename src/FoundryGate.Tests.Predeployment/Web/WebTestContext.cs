using Bunit;
using Bunit.TestDoubles;
using FoundryGate.Domain.Constants;
using FoundryGate.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// Base for every Blazor component test: a bUnit renderer wired the way
/// <c>FoundryGate.Web</c>'s Program.cs wires the real app — MudBlazor's services, an
/// <see cref="IFoundryGateApiClient"/> that answers from memory instead of HTTP, bUnit's fake
/// authentication state, and loose JS interop so MudBlazor's popovers, scroll listeners and
/// viewport observers don't need a browser.
/// </summary>
/// <remarks>
/// Pages are rendered with <c>RenderPage</c> rather than
/// <c>RenderComponent</c>: MudBlazor's dialogs and snackbars go through providers that live
/// outside the page's own render tree, so a test that clicks "Delete" and expects a
/// confirmation needs those providers in the same renderer. Anything a page shows through
/// <see cref="IDialogService"/> is therefore findable on the returned root.
/// </remarks>
public abstract class WebTestContext : BunitContext
{
    protected WebTestContext()
    {
        // MudBlazor's own registration, minus the snackbar position the app sets (nothing here
        // renders a real snackbar host).
        Services.AddMudServices();
        Services.AddSingleton<IFoundryGateApiClient>(Api);

        // MudBlazor calls into JS on almost every component's first render (popover anchoring,
        // resize observers, focus traps). Loose mode answers all of it with defaults.
        JSInterop.Mode = JSRuntimeMode.Loose;

        Authorization = AddAuthorization();
    }

    /// <summary>The in-memory API the components under test talk to. Arrange on it before rendering.</summary>
    public FakeFoundryGateApiClient Api { get; } = new();

    /// <summary>bUnit's authentication/authorization double, behind <c>AuthorizeView</c> and <c>[Authorize]</c>.</summary>
    protected BunitAuthorizationContext Authorization { get; }

    /// <summary>Signs in a caller holding <see cref="RoleNames.Admin"/> — what every admin page requires.</summary>
    protected void SignInAsAdmin(string displayName = "Ada Admin")
    {
        Authorization.SetAuthorized(displayName);
        Authorization.SetRoles(RoleNames.Admin);
    }

    /// <summary>Signs in an authenticated caller with no roles — a developer.</summary>
    protected void SignInAsDeveloper(string displayName = "Dev Eloper") => Authorization.SetAuthorized(displayName);

    /// <summary>Leaves the caller unauthenticated.</summary>
    protected void SignOut() => Authorization.SetNotAuthorized();

    /// <summary>
    /// Renders a page alongside MudBlazor's dialog, popover and snackbar providers, so dialogs
    /// the page opens are part of the same render tree and can be found and clicked.
    /// </summary>
    /// <param name="parameters">Route/component parameters, e.g. the <c>Id</c> of a detail page.</param>
    protected IRenderedComponent<TPage> RenderPage<TPage>(params (string Name, object? Value)[] parameters)
        where TPage : Microsoft.AspNetCore.Components.IComponent
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var attributes = parameters.ToDictionary(p => p.Name, p => p.Value);

        return Render<TPage>(builder =>
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
}
