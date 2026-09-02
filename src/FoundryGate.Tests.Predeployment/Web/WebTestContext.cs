using Bunit;
using Bunit.Rendering;
using Bunit.TestDoubles;
using FoundryGate.Domain.Constants;
using FoundryGate.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// The shared bUnit host for every FoundryGate.Web component test: MudBlazor's services, a
/// <see cref="FakeFoundryGateApiClient"/> behind <see cref="IFoundryGateApiClient"/>, the scoped
/// <see cref="DashboardStateService"/> the nav badge reads, and JSInterop in loose mode (MudBlazor
/// components call into JS on render — strict mode would fail every test for reasons that have
/// nothing to do with the component under test).
/// </summary>
/// <remarks>
/// Derive a test class from this and call <see cref="SignInAsDeveloper"/> or
/// <see cref="SignInAsAdmin"/> before rendering anything that reads authentication state.
/// </remarks>
public class WebTestContext : BunitContext, IAsyncLifetime
{
    protected WebTestContext()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.AddMudServices();
        Services.AddSingleton<IFoundryGateApiClient>(Api);
        Services.AddScoped<DashboardStateService>();
    }

    // Some MudBlazor services (KeyInterceptorService) implement IAsyncDisposable only, and a
    // container holding one refuses synchronous disposal. Implementing IAsyncLifetime makes xUnit
    // tear the context down through DisposeAsync instead of Dispose.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    /// <summary>The canned API this context's components talk to. Set its result properties before rendering.</summary>
    public FakeFoundryGateApiClient Api { get; } = new();

    /// <summary>Snackbar messages raised during the test, in the order they were added.</summary>
    protected IEnumerable<Snackbar> Snackbars => Services.GetRequiredService<ISnackbar>().ShownSnackbars;

    /// <summary>Signs a plain developer in — authenticated, no roles.</summary>
    protected BunitAuthorizationContext SignInAsDeveloper(string name = "Dev Eloper")
    {
        var authorization = AddAuthorization();
        _ = authorization.SetAuthorized(name);
        return authorization;
    }

    /// <summary>Signs an administrator in — authenticated, holding <see cref="RoleNames.Admin"/>.</summary>
    protected BunitAuthorizationContext SignInAsAdmin(string name = "Ada Admin")
    {
        var authorization = AddAuthorization();
        _ = authorization.SetAuthorized(name);
        _ = authorization.SetRoles(RoleNames.Admin);
        return authorization;
    }

    /// <summary>
    /// Renders <typeparamref name="TPage"/> under MudBlazor's popover and dialog providers, the way
    /// <c>Layout/MainLayout</c> does in the app, and returns the <em>whole</em> tree. Both matter:
    /// an inline <c>MudDialog</c> (the rotate-key confirm, the config diff) is hosted by
    /// <c>MudDialogProvider</c> and therefore renders as a sibling of the page, so a result scoped
    /// to the page component alone can neither see the dialog nor click its buttons.
    /// </summary>
    /// <param name="parameters">Parameters to pass to the page, as <c>(name, value)</c> pairs.</param>
    protected IRenderedComponent<ContainerFragment> RenderPage<TPage>(params (string Name, object? Value)[] parameters)
        where TPage : IComponent
    {
        ArgumentNullException.ThrowIfNull(parameters);

        return Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1);
            builder.CloseComponent();

            builder.OpenComponent<TPage>(2);

            // ASP0006 wants literal sequence numbers so the diffing algorithm can rely on source
            // order. This fragment is built from a runtime array rather than source, so there is no
            // source order to encode — and the tree is rendered once per test and never re-diffed.
#pragma warning disable ASP0006
            var sequence = 3;
            foreach (var (name, value) in parameters)
            {
                builder.AddComponentParameter(sequence++, name, value);
            }
#pragma warning restore ASP0006

            builder.CloseComponent();
        });
    }

    /// <summary>Leaves the caller signed out.</summary>
    protected BunitAuthorizationContext SignOut()
    {
        var authorization = AddAuthorization();
        _ = authorization.SetNotAuthorized();
        return authorization;
    }
}
