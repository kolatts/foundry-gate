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
/// <para>
/// <b>Nothing here is shared between test classes</b> (audited for #203, which suspected it):
/// <see cref="Api"/>, <see cref="Time"/>, <see cref="Authorization"/>, bUnit's JS interop and
/// the whole service collection are per-instance, and xUnit gives each test class its own
/// collection — so classes running in parallel cannot see each other's arrangements. The one
/// static is <c>WebTestData.Tiers</c>, an immutable list of records. What the flakes actually were:
/// a one-second wait budget (raised below) and a fake that counted calls from the renderer's
/// thread while assertions read them from the test's (fixed in <see cref="RecordedCalls{T}"/>).
/// </para>
/// </remarks>
public abstract class WebTestContext : BunitContext, IAsyncLifetime
{
    protected WebTestContext()
    {
        Services.AddMudServices();
        Services.AddSingleton<IFoundryGateApiClient>(Api);
        Services.AddScoped<DashboardStateService>();

        // The admin pages read the tier catalogue through this rather than calling GET /quota/tiers
        // each; it fetches through the same fake, so a test still arranges tiers on Api.
        Services.AddScoped<QuotaTierCatalog>();
        // Resolved lazily so a test can replace Time after the constructor has run but before it
        // renders — which is the only order a test can actually use.
        Services.AddSingleton(_ => Time);

        // MudBlazor calls into JS on almost every component's first render. Loose mode answers all
        // of it with defaults instead of failing the test for something the component isn't about.
        JSInterop.Mode = JSRuntimeMode.Loose;

        // bUnit waits one second by default, which is a budget, not a contract: several of these
        // pages debounce input for 300 ms before they even start the request the assertion is
        // waiting on, and a loaded CI runner has spent that on thread-pool scheduling alone. That
        // is what #203 kept seeing — WaitForAssertion timeouts on tests that pass 10/10 locally.
        // A generous ceiling costs nothing while the suite is green (every wait returns as soon as
        // its condition holds) and only lengthens a run that was going to fail anyway.
        DefaultWaitTimeout = WaitTimeout;

        Authorization = AddAuthorization();
    }

    /// <summary>
    /// The ceiling every <c>WaitFor*</c> in the Web suite waits up to — deliberately far longer
    /// than any of these pages needs, because the number only decides how long a <em>failing</em>
    /// assertion takes to give up (#203).
    /// </summary>
    public static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The in-memory API the components under test talk to. Arrange on it before rendering.</summary>
    public FakeFoundryGateApiClient Api { get; } = new();

    /// <summary>
    /// The clock and time zone components see. Defaults to the real one; a test that cares about
    /// zone conversion (<c>/audit</c>'s date range) replaces it before rendering.
    /// </summary>
    public TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>bUnit's authentication/authorization double, behind <c>AuthorizeView</c> and <c>[Authorize]</c>.</summary>
    protected BunitAuthorizationContext Authorization { get; }

    /// <summary>
    /// Waits for something that happens <em>off</em> the render loop — a gated API call returning,
    /// a background timer stopping. bUnit's <c>WaitForAssertion</c> re-checks on renders, so it is
    /// the wrong tool for a condition that produces none (a reply that lands after the page was
    /// disposed renders nothing at all); this polls instead, which is the difference between a
    /// deterministic wait and a <c>Task.Delay</c> that happens to be long enough (#203).
    /// </summary>
    /// <param name="condition">Checked until it is true or <see cref="WaitTimeout"/> elapses.</param>
    /// <param name="because">What was expected — the failure message when it never became true.</param>
    protected static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var deadline = DateTime.UtcNow + WaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Timed out after {WaitTimeout.TotalSeconds:0.#}s waiting for {because}.");
    }

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
