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
        // waiting on, and a loaded CI runner has spent that on thread-pool scheduling alone.
        // Three seconds is ten times the debounce and still leaves a red run readable: at ~200
        // WaitFor* call sites, the 10 s this briefly carried would have turned a broken shared
        // component into a half-hour wait for an answer you want immediately (#203 review).
        DefaultWaitTimeout = WaitTimeout;

        Authorization = AddAuthorization();
    }

    /// <summary>
    /// The ceiling every <c>WaitFor*</c> in the Web suite waits up to: ten times the longest debounce
    /// any of these pages uses, and no more. The number only decides how long a <em>failing</em>
    /// assertion takes to give up, so it is chosen to keep a red run fast to read (#203).
    /// </summary>
    public static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Pages rendered through <see cref="RenderPage{TPage}"/>, so teardown can dispose them (bUnit does not — see <see cref="DisposeRenderedPagesAsync"/>).</summary>
    private readonly List<IComponent> _renderedPages = [];

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

        var root = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<TPage>(2);
            builder.AddMultipleAttributes(3, attributes!);
            builder.CloseComponent();
        });

        _renderedPages.Add(root.FindComponent<TPage>().Instance);
        return root;
    }

    /// <summary>
    /// Disposes every page this test has rendered, on the renderer's dispatcher, and waits for it.
    /// What a test uses when the point is "the page is gone" rather than "the test is over".
    /// </summary>
    /// <remarks>
    /// <b>bUnit's own teardown does not reach these pages</b> — measured, not assumed (#203). A
    /// component rendered as the root type (<c>Render&lt;Dashboard&gt;()</c>) is disposed by
    /// <c>DisposeComponentsAsync()</c>; the same component rendered <em>inside</em> a
    /// <see cref="RenderFragment"/>, which is what <see cref="RenderPage{TPage}"/> has to do to put
    /// the dialog and popover providers beside it, is not. That is why
    /// <c>DashboardPageTests.Stops_refreshing_once_the_page_is_gone</c> was failing on a real signal:
    /// its 20 ms refresh timer was never asked to stop, so the loop it asserts about had never been
    /// told the page was gone. Every <see cref="IDisposable"/> page had the same hole — Dashboard is
    /// simply the only one with a clock loud enough to hear.
    /// </remarks>
    protected async Task DisposeRenderedPagesAsync()
    {
        foreach (var component in _renderedPages)
        {
            await DisposeComponentAsync(component);
        }

        _renderedPages.Clear();
    }

    /// <summary>
    /// Disposes one component the way its host would: <see cref="IAsyncDisposable"/> in preference to
    /// <see cref="IDisposable"/> (which is Blazor's own order), on the renderer's dispatcher, because
    /// a component's teardown may touch state the renderer owns.
    /// </summary>
    private async Task DisposeComponentAsync(IComponent component)
    {
        switch (component)
        {
            case IAsyncDisposable asyncDisposable:
                await Renderer.Dispatcher.InvokeAsync(async () => await asyncDisposable.DisposeAsync());
                break;
            case IDisposable disposable:
                await Renderer.Dispatcher.InvokeAsync(disposable.Dispose);
                break;
            default:
                break;
        }
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// MudBlazor registers services that only implement <see cref="IAsyncDisposable"/>
    /// (<c>KeyInterceptorService</c> among them), and a synchronous container teardown throws on
    /// those. xUnit disposes an <see cref="IAsyncLifetime"/> test class asynchronously first, so the
    /// real teardown happens here and <see cref="Dispose(bool)"/> is left with nothing to do.
    /// </summary>
    async Task IAsyncLifetime.DisposeAsync()
    {
        // Pages first, and by hand — see DisposeRenderedPagesAsync for why bUnit's teardown misses
        // them. A page left undisposed keeps whatever it started running: a 20 ms PeriodicTimer that
        // outlives its test class burns thread-pool time for the rest of the suite, which is the kind
        // of load that makes *other* tests flaky under CI.
        foreach (var component in _renderedPages)
        {
            await DisposeComponentAsync(component);
        }

        _renderedPages.Clear();

        await DisposeAsync();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        // Deliberately empty — see IAsyncLifetime.DisposeAsync above.
    }
}
