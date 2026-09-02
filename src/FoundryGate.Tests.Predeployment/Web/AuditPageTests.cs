using Bunit;
using FoundryGate.Domain.Audit.Contracts;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Users.Contracts;
using FoundryGate.Web.Components;
using FoundryGate.Web.Pages;
using FoundryGate.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;

namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// <c>/audit</c> (#55): the server-paged grid, the filters it sends to <c>GET /audit</c>, and the
/// details blob's JSON rendering — including the malformed row that must still show.
/// </summary>
public class AuditPageTests : WebTestContext
{
    public AuditPageTests() => SignInAsAdmin();

    [Fact]
    public void The_action_filter_offers_the_domain_constants_not_a_retyped_list()
    {
        Assert.Contains(AuditActions.QuotaIncreaseApproved, Audit.Actions);
        Assert.Contains(AuditActions.KeyRotated, Audit.Actions);
        Assert.Contains(AuditActions.ConfigUpdated, Audit.Actions);
        Assert.Contains(AuditTargetTypes.User, Audit.TargetTypes);
        Assert.Contains(AuditTargetTypes.QuotaIncreaseRequest, Audit.TargetTypes);
    }

    [Fact]
    public void Renders_a_row_per_entry()
    {
        Api.AuditResult = Ok(WebTestData.Page(
            WebTestData.AuditEntry(action: AuditActions.KeyRotated, actorDisplayName: "Ada Admin")));

        var page = RenderPage<Audit>();

        var grid = page.Find("[data-testid='audit-grid']").TextContent;
        Assert.Contains(AuditActions.KeyRotated, grid, StringComparison.Ordinal);
        Assert.Contains("Ada Admin", grid, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_with_no_actor_is_attributed_to_the_system()
    {
        Api.AuditResult = Ok(WebTestData.Page(
            WebTestData.AuditEntry(action: AuditActions.QuotaMonthlyReset, actorDisplayName: null, targetType: null)));

        var page = RenderPage<Audit>();

        Assert.Contains("system", page.Find("[data-testid='audit-grid']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_result_says_so()
    {
        Api.AuditResult = Ok(WebTestData.Page<AuditLogEntryResponse>());

        var page = RenderPage<Audit>();

        Assert.Contains("No audit entries", page.Find("[data-testid='audit-empty']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_load_reports_in_the_grid_and_in_a_snackbar()
    {
        Api.AuditResult = ApiCallResult<PagedResult<AuditLogEntryResponse>>.Fail(
            ApiCallStatus.Unavailable,
            "Foundry Gate's API isn't reachable right now.");

        var page = RenderPage<Audit>();

        Assert.Contains("isn't reachable", page.Find("[data-testid='audit-empty']").TextContent, StringComparison.Ordinal);
        Assert.Contains(Snackbars, s => s.Severity == Severity.Error);
    }

    [Fact]
    public void Pages_on_the_server_rather_than_in_the_browser()
    {
        _ = RenderPage<Audit>();

        var paging = Assert.Single(Api.AuditQueries).Paging;
        Assert.Equal(1, paging.Page);
        Assert.Equal(25, paging.PageSize);
    }

    [Fact]
    public void Choosing_an_action_reloads_with_that_filter()
    {
        var page = RenderPage<Audit>();

        SetSelect(page, index: 0, value: AuditActions.KeyRotated);

        Assert.Equal(2, Api.AuditQueries.Count);
        Assert.Equal(AuditActions.KeyRotated, Api.AuditQueries[^1].Query.Action);
    }

    [Fact]
    public void Choosing_a_target_type_reloads_with_that_filter()
    {
        var page = RenderPage<Audit>();

        SetSelect(page, index: 1, value: AuditTargetTypes.ApiKey);

        Assert.Equal(AuditTargetTypes.ApiKey, Api.AuditQueries[^1].Query.TargetType);
    }

    [Fact]
    public async Task A_date_range_covers_the_whole_of_its_last_day_including_across_a_month_boundary()
    {
        // Pinned to a non-UTC zone: the picker hands back wall-clock dates and the grid renders
        // OccurredDate.ToLocalTime(), so reading those dates as UTC would slide the filter window
        // away from the timestamps on screen by the reader's own offset.
        Time = new FixedZoneTimeProvider(TimeSpan.FromHours(-5));

        var page = RenderPage<Audit>();
        var picker = page.FindComponent<MudDateRangePicker>();

        await page.InvokeAsync(() => picker.Instance.DateRangeChanged.InvokeAsync(
            new DateRange(new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Unspecified), new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Unspecified))));

        var query = Api.AuditQueries[^1].Query;

        // Midnight on 30 Aug where the admin is sitting — 05:00 UTC, not 00:00 UTC.
        Assert.Equal(new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.FromHours(-5)), query.FromDate);
        Assert.Equal(new DateTimeOffset(2026, 8, 30, 5, 0, 0, TimeSpan.Zero), query.FromDate!.Value.ToUniversalTime());

        // An admin who picks "2 Sep" means everything that happened on the 2nd, local time.
        Assert.NotNull(query.ToDate);
        Assert.Equal(TimeSpan.FromHours(-5), query.ToDate!.Value.Offset);
        Assert.Equal(new DateTime(2026, 9, 2), query.ToDate.Value.Date);
        Assert.True(query.ToDate.Value.TimeOfDay > TimeSpan.FromHours(23));
    }

    [Fact]
    public async Task Changing_a_filter_goes_back_to_the_first_page()
    {
        // Narrowing a filter while deep in the log used to ask for the same page number of the
        // narrowed set — and render "no entries match" for a filter with plenty of matches.
        Api.AuditResult = Ok(new PagedResult<AuditLogEntryResponse>(
            [WebTestData.AuditEntry()],
            TotalCount: 500,
            Page: 1,
            PageSize: 25));

        var page = RenderPage<Audit>();
        var grid = page.FindComponent<MudDataGrid<AuditLogEntryResponse>>();
        await page.InvokeAsync(() => grid.Instance.NavigateTo(Page.Last));
        Assert.True(Api.AuditQueries[^1].Paging.Page > 1, "the grid should have asked for a later page");

        SetSelect(page, index: 0, value: AuditActions.KeyRotated);

        Assert.Equal(1, Api.AuditQueries[^1].Paging.Page);
        Assert.Equal(AuditActions.KeyRotated, Api.AuditQueries[^1].Query.Action);
    }

    [Fact]
    public void An_action_query_parameter_filters_on_first_load()
    {
        // The dashboard's hard-stopped card links here as ?action=user.deactivated (#190): a page
        // that knows which action explains its number hands the admin the trail, not the whole log.
        _ = RenderPage<Audit>(("ActionFilter", AuditActions.UserDeactivated));

        Assert.Equal(AuditActions.UserDeactivated, Assert.Single(Api.AuditQueries).Query.Action);
    }

    [Fact]
    public void An_action_query_parameter_the_log_does_not_know_is_ignored()
    {
        // A mistyped deep link shows the whole log rather than an empty grid filtered by a value the
        // select cannot even render.
        _ = RenderPage<Audit>(("ActionFilter", "not.an.action"));

        Assert.Null(Assert.Single(Api.AuditQueries).Query.Action);
    }

    [Fact]
    public void Clearing_the_filters_reloads_with_none_of_them()
    {
        var page = RenderPage<Audit>();
        SetSelect(page, index: 0, value: AuditActions.KeyRotated);

        page.Find("[data-testid='audit-filter-clear']").Click();

        var query = Api.AuditQueries[^1].Query;
        Assert.Null(query.Action);
        Assert.Null(query.TargetType);
        Assert.Null(query.ActorUserId);
        Assert.Null(query.FromDate);
    }

    [Fact]
    public async Task The_actor_filter_searches_users_by_name()
    {
        // #191: the filter used to be a raw numeric field, so "who did this?" needed a detour
        // through /users to look an id up — the opposite of what a filter is for.
        Api.ArrangeUsers(WebTestData.User(userId: 41, displayName: "Ada Lovelace"));

        var page = RenderPage<Audit>();
        var matches = await SearchActorsAsync(page, "ada");

        Assert.Equal(41, Assert.Single(matches).UserId);
        var (query, paging) = Assert.Single(Api.UserListCalls);
        Assert.Equal("ada", query.Search);
        // Deactivated accounts still appear in the log, so the search must not filter them out.
        Assert.Null(query.IsActive);
        Assert.Equal(1, paging.Page);
    }

    [Fact]
    public async Task Choosing_an_actor_filters_the_log_by_their_user_id()
    {
        var page = RenderPage<Audit>();

        await SelectActorAsync(page, WebTestData.User(userId: 41, displayName: "Ada Lovelace"));

        Assert.Equal(41, Api.AuditQueries[^1].Query.ActorUserId);
    }

    [Fact]
    public async Task Clearing_the_actor_removes_the_filter()
    {
        var page = RenderPage<Audit>();
        await SelectActorAsync(page, WebTestData.User(userId: 41));

        await SelectActorAsync(page, null);

        Assert.Null(Api.AuditQueries[^1].Query.ActorUserId);
    }

    [Fact]
    public async Task Typing_a_bare_id_and_tabbing_out_filters_by_that_user()
    {
        // The manual id path #191 asked to keep: an admin holding an id from an export or a
        // colleague's message types it straight in.
        Api.UsersResult = ApiCallResult<PagedResult<UserResponse>>.Ok(WebTestData.Page<UserResponse>());
        Api.UserResult = ApiCallResult<UserDetailResponse>.Ok(
            WebTestData.UserDetail(WebTestData.User(userId: 41, displayName: "Ada Lovelace")));

        var page = RenderPage<Audit>();
        await TypeActorAsync(page, "41");
        await BlurActorAsync(page);

        Assert.Equal(41, Api.AuditQueries[^1].Query.ActorUserId);
        Assert.Contains("Ada Lovelace", page.FindComponent<MudAutocomplete<UserResponse>>().Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_id_lookup_waits_for_the_field_to_lose_focus_rather_than_firing_as_you_type()
    {
        // GET /users/{id} is the detail endpoint — memberships, allocation, masked key material — so
        // firing it at every debounce tick while someone types "412" spends three round trips a
        // keystroke to render one label.
        Api.UsersResult = ApiCallResult<PagedResult<UserResponse>>.Ok(WebTestData.Page<UserResponse>());
        Api.UserResult = ApiCallResult<UserDetailResponse>.Ok(
            WebTestData.UserDetail(WebTestData.User(userId: 412, displayName: "Ada Lovelace")));

        var page = RenderPage<Audit>();
        _ = await SearchActorsAsync(page, "4");
        _ = await SearchActorsAsync(page, "41");
        _ = await SearchActorsAsync(page, "412");

        Assert.Equal(0, Api.CallCount("GetUserAsync"));

        await TypeActorAsync(page, "412");
        await BlurActorAsync(page);

        Assert.Equal(1, Api.CallCount("GetUserAsync"));
    }

    [Fact]
    public async Task An_id_that_matches_no_user_leaves_the_filter_alone()
    {
        // A typo must not empty the grid: nothing was selected, so nothing is filtered.
        Api.UsersResult = ApiCallResult<PagedResult<UserResponse>>.Ok(WebTestData.Page<UserResponse>());
        Api.UserResult = ApiCallResult<UserDetailResponse>.Fail(ApiCallStatus.NotFound, "That wasn't found.");

        var page = RenderPage<Audit>();
        await TypeActorAsync(page, "999");
        await BlurActorAsync(page);

        Assert.Null(Api.AuditQueries[^1].Query.ActorUserId);
    }

    [Fact]
    public async Task Blurring_a_name_search_never_reaches_the_detail_endpoint()
    {
        var page = RenderPage<Audit>();
        await TypeActorAsync(page, "Ada");
        await BlurActorAsync(page);

        Assert.Equal(0, Api.CallCount("GetUserAsync"));
    }

    [Fact]
    public async Task A_search_term_that_is_an_id_still_only_searches_names()
    {
        // The search itself is names and emails; the id is the blur's job.
        Api.ArrangeUsers(WebTestData.User(userId: 7, displayName: "Seven Sisters"));

        var page = RenderPage<Audit>();
        var matches = await SearchActorsAsync(page, "41");

        Assert.Equal("41", Assert.Single(Api.UserListCalls).Query.Search);
        Assert.Equal(0, Api.CallCount("GetUserAsync"));
        Assert.Single(matches);
    }

    [Fact]
    public void An_actor_query_parameter_filters_on_first_load_and_names_the_person()
    {
        // A deep link into "everything this person did" has to survive being pasted into a chat.
        Api.UserResult = ApiCallResult<UserDetailResponse>.Ok(
            WebTestData.UserDetail(WebTestData.User(userId: 41, displayName: "Ada Lovelace")));

        var page = RenderPage<Audit>(("ActorFilter", 41));

        Assert.Equal(41, Assert.Single(Api.AuditQueries).Query.ActorUserId);
        Assert.Contains("Ada Lovelace", page.FindComponent<MudAutocomplete<UserResponse>>().Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void An_actor_query_parameter_still_filters_when_the_name_cannot_be_resolved()
    {
        Api.UserResult = ApiCallResult<UserDetailResponse>.Fail(ApiCallStatus.NotFound, "That wasn't found.");

        _ = RenderPage<Audit>(("ActorFilter", 41));

        Assert.Equal(41, Assert.Single(Api.AuditQueries).Query.ActorUserId);
    }

    [Fact]
    public void Details_json_is_pretty_printed()
    {
        var formatted = AuditDetails.Prettify("""{"before":null,"after":20000000}""");

        Assert.Contains("\n", formatted, StringComparison.Ordinal);
        Assert.Contains("\"after\": 20000000", formatted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"truncated\": ")]
    [InlineData("")]
    [InlineData(null)]
    public void Details_that_are_not_json_still_render_instead_of_throwing(string? details)
    {
        // A log viewer that throws on a malformed row hides the row you most need to see.
        var formatted = AuditDetails.Prettify(details);

        Assert.Equal(string.IsNullOrWhiteSpace(details) ? string.Empty : details, formatted);
    }

    /// <summary>
    /// Runs the actor autocomplete's own <c>SearchFunc</c>. MudBlazor opens its list through a
    /// popover the headless renderer never shows, so the search is driven directly — the same way
    /// the select filters are.
    /// </summary>
    private static async Task<IReadOnlyList<UserResponse>> SearchActorsAsync(IRenderedComponent<IComponent> page, string term)
    {
        // Declared non-nullable rather than `var`: the compiler resets a nullable local's flow state
        // inside a lambda, so a null check outside the closure would not carry into it.
        Func<string, CancellationToken, Task<IEnumerable<UserResponse>?>> search =
            page.FindComponent<MudAutocomplete<UserResponse>>().Instance.SearchFunc!;

        return await page.InvokeAsync(async () =>
            (await search(term, CancellationToken.None))?.ToList() ?? []);
    }

    /// <summary>Types into the actor box without picking anything — what the manual id path starts from.</summary>
    private static async Task TypeActorAsync(IRenderedComponent<IComponent> page, string text)
    {
        var autocomplete = page.FindComponent<MudAutocomplete<UserResponse>>();
        await page.InvokeAsync(() => autocomplete.Instance.TextChanged.InvokeAsync(text));
    }

    /// <summary>Moves focus off the actor box, which is when a typed id is resolved.</summary>
    private static async Task BlurActorAsync(IRenderedComponent<IComponent> page)
    {
        var autocomplete = page.FindComponent<MudAutocomplete<UserResponse>>();
        await page.InvokeAsync(() => autocomplete.Instance.OnBlur.InvokeAsync(new FocusEventArgs()));
    }

    /// <summary>Picks (or clears) the actor the way the popover's click would.</summary>
    private static async Task SelectActorAsync(IRenderedComponent<IComponent> page, UserResponse? actor)
    {
        var autocomplete = page.FindComponent<MudAutocomplete<UserResponse>>();
        await page.InvokeAsync(() => autocomplete.Instance.ValueChanged.InvokeAsync(actor));
    }

    private static void SetSelect(IRenderedComponent<IComponent> page, int index, string value)
    {
        // MudSelect's dropdown needs a popover the headless renderer never opens; drive the bound
        // value the way the component would, on the renderer's dispatcher.
        var select = page.FindComponents<MudSelect<string>>()[index];
        page.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(value)).GetAwaiter().GetResult();
    }

    private static ApiCallResult<T> Ok<T>(T value) => ApiCallResult<T>.Ok(value);
}
