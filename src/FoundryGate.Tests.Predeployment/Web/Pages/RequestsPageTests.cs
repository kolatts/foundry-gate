using Bunit;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Requests;
using FoundryGate.Domain.Requests.Contracts;
using FoundryGate.Web.Pages;
using FoundryGate.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace FoundryGate.Tests.Predeployment.Web.Pages;

/// <summary>
/// <c>/requests</c> and <c>/requests/{id}</c> (#53): the review drawer is where a verdict is
/// entered, a developer sees the same facts read-only, and <c>?status=Pending</c> — the link the
/// admin dashboard's badge uses — arrives as a filter.
/// </summary>
public class RequestsPageTests : WebTestContext
{
    public RequestsPageTests() => Api.ArrangeTiers(WebTestData.Tiers);

    [Fact]
    public void An_admin_sees_the_requester_column_and_the_requested_tier()
    {
        SignInAsAdmin();
        Api.ArrangeRequests(WebTestData.Request(requestId: 5, requestedQuota: 20_000_000));

        var page = RenderPage<Requests>();

        page.WaitForAssertion(() =>
        {
            Assert.Contains("Dev Eloper", page.Markup, StringComparison.Ordinal);
            Assert.Contains("Power", page.Markup, StringComparison.Ordinal);
            Assert.Contains("Pending", page.Find("[data-testid=request-status-5]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_list_is_not_narrowed_to_one_user_from_here()
    {
        // The API already scopes a developer to their own requests and gives an admin
        // everyone's, so this page never sends ?userId= — per-user filtering starts at
        // /users/{id}.
        SignInAsAdmin();

        var page = RenderPage<Requests>();

        page.WaitForAssertion(() =>
        {
            Assert.NotEmpty(Api.RequestListCalls);
            Assert.All(Api.RequestListCalls, c => Assert.Null(c.Query.UserId));
        });
    }

    [Fact]
    public void The_dashboards_status_Pending_deep_link_arrives_as_a_filter()
    {
        SignInAsAdmin();

        var page = RenderPage<Requests>(("StatusQuery", "Pending"));

        page.WaitForAssertion(() =>
            Assert.All(Api.RequestListCalls, c => Assert.Equal(QuotaRequestStatusType.Pending, c.Query.Status)));
    }

    [Fact]
    public void An_unrecognised_status_in_the_url_is_ignored_rather_than_breaking_the_page()
    {
        SignInAsAdmin();

        var page = RenderPage<Requests>(("StatusQuery", "Sideways"));

        page.WaitForAssertion(() => Assert.All(Api.RequestListCalls, c => Assert.Null(c.Query.Status)));
    }

    [Fact]
    public void Opening_a_pending_row_shows_the_justification_and_the_verdict_buttons()
    {
        SignInAsAdmin();
        Api.ArrangeRequests(WebTestData.Request(requestId: 5));

        var page = RenderPage<Requests>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("tbody tr")));

        page.Find("tbody tr").Click();

        page.WaitForAssertion(() =>
        {
            Assert.Contains("Running a large migration", page.Find("[data-testid=request-justification]").TextContent, StringComparison.Ordinal);
            Assert.NotNull(page.Find("[data-testid=request-approve]"));
            Assert.NotNull(page.Find("[data-testid=request-reject]"));
        });
    }

    [Fact]
    public void Approving_from_the_drawer_sends_the_review_notes_and_flips_the_row()
    {
        SignInAsAdmin();
        Api.ArrangeRequests(WebTestData.Request(requestId: 5));

        var page = RenderPage<Requests>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("tbody tr")));
        page.Find("tbody tr").Click();

        page.WaitForElement("textarea[data-testid=request-notes-input]").Change("Fine for this month.");
        page.Find("[data-testid=request-approve]").Click();

        page.WaitForAssertion(() =>
        {
            var (requestId, review) = Assert.Single(Api.ApprovedRequests);
            Assert.Equal(5, requestId);
            Assert.Equal("Fine for this month.", review.ReviewNotes);

            // Optimistic: the row shows the verdict without the grid re-fetching the page.
            Assert.Contains("Approved", page.Find("[data-testid=request-status-5]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Rejecting_from_the_drawer_calls_reject_not_approve()
    {
        SignInAsAdmin();
        Api.ArrangeRequests(WebTestData.Request(requestId: 5));

        var page = RenderPage<Requests>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("tbody tr")));
        page.Find("tbody tr").Click();
        page.WaitForElement("[data-testid=request-reject]").Click();

        page.WaitForAssertion(() =>
        {
            Assert.Equal(5, Assert.Single(Api.RejectedRequests).RequestId);
            Assert.Empty(Api.ApprovedRequests);
        });
    }

    [Fact]
    public async Task Draft_review_notes_never_travel_from_one_request_to_the_next()
    {
        // The reviewer's reproduction, verbatim: open request 5, type notes, dismiss the drawer by
        // its scrim (which used to leave the panel mounted rather than unmounting it), open request
        // 6, approve. Request 6 was approved with request 5's notes, and its requester read them.
        SignInAsAdmin();
        Api.ArrangeRequests(WebTestData.Request(requestId: 5), WebTestData.Request(requestId: 6));

        var page = RenderPage<Requests>();
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("tbody tr").Count));

        page.FindAll("tbody tr")[0].Click();
        page.WaitForElement("textarea[data-testid=request-notes-input]").Change("Only for request 5.");

        // Dismissing without the close button — the path that left _selected set.
        var drawer = page.FindComponent<MudDrawer>().Instance;
        await page.InvokeAsync(() => drawer.OpenChanged.InvokeAsync(false));

        page.FindAll("tbody tr")[1].Click();
        page.WaitForElement("[data-testid=request-approve]").Click();

        page.WaitForAssertion(() =>
        {
            var (requestId, review) = Assert.Single(Api.ApprovedRequests);
            Assert.Equal(6, requestId);
            Assert.Null(review.ReviewNotes);
        });
    }

    [Fact]
    public void Losing_a_race_to_another_reviewer_shows_their_verdict_instead_of_live_buttons()
    {
        // api.md: two reviewers deciding at once means one gets a 409. Leaving the drawer saying
        // Pending with both buttons live invites pressing again.
        SignInAsAdmin();
        Api.ArrangeRequests(WebTestData.Request(requestId: 5));
        Api.MutationResult = ApiCallResult<bool>.Fail(
            ApiCallStatus.Error,
            "Already reviewed.",
            new ApiError(ApiError.DefaultType, "Conflict", 409, "Already reviewed."));
        Api.RequestResult = ApiCallResult<QuotaIncreaseRequestResponse>.Ok(
            WebTestData.Request(requestId: 5, status: QuotaRequestStatusType.Rejected, reviewNotes: "Not this month."));

        var page = RenderPage<Requests>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("tbody tr")));
        page.Find("tbody tr").Click();
        page.WaitForElement("[data-testid=request-approve]").Click();

        page.WaitForAssertion(() =>
        {
            Assert.Contains("Not this month.", page.Find("[data-testid=request-review-notes]").TextContent, StringComparison.Ordinal);
            Assert.Empty(page.FindAll("[data-testid=request-approve]"));
        });
    }

    [Fact]
    public async Task Changing_the_filter_writes_it_back_into_the_url()
    {
        // The deep link already worked inbound; without this the page you are looking at is not the
        // page you can send someone.
        SignInAsAdmin();
        var page = RenderPage<Requests>();
        page.WaitForAssertion(() => Assert.NotEmpty(Api.RequestListCalls));

        var navigation = Services.GetRequiredService<NavigationManager>();
        var filter = page.FindComponent<MudSelect<QuotaRequestStatusType?>>().Instance;
        await page.InvokeAsync(() => filter.ValueChanged.InvokeAsync(QuotaRequestStatusType.Approved));

        page.WaitForAssertion(() =>
        {
            Assert.Contains("status=Approved", navigation.Uri, StringComparison.Ordinal);
            Assert.Contains(Api.RequestListCalls, c => c.Query.Status == QuotaRequestStatusType.Approved);
        });
    }

    [Fact]
    public void An_already_decided_request_opens_read_only()
    {
        SignInAsAdmin();
        Api.ArrangeRequests(WebTestData.Request(requestId: 5, status: QuotaRequestStatusType.Approved, reviewNotes: "Approved for the migration."));

        var page = RenderPage<Requests>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("tbody tr")));
        page.Find("tbody tr").Click();

        page.WaitForAssertion(() =>
        {
            Assert.Contains("Approved for the migration.", page.Find("[data-testid=request-review-notes]").TextContent, StringComparison.Ordinal);
            Assert.Empty(page.FindAll("[data-testid=request-approve]"));
            Assert.Empty(page.FindAll("[data-testid=request-reject]"));
        });
    }

    [Fact]
    public void A_developer_sees_their_own_requests_with_no_verdict_buttons()
    {
        SignInAsDeveloper();
        Api.ArrangeRequests(WebTestData.Request(requestId: 5));

        var page = RenderPage<Requests>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("tbody tr")));
        page.Find("tbody tr").Click();

        page.WaitForAssertion(() =>
        {
            Assert.Contains("My quota requests", page.Markup, StringComparison.Ordinal);
            Assert.NotNull(page.Find("[data-testid=request-justification]"));
            Assert.Empty(page.FindAll("[data-testid=request-approve]"));
        });
    }

    [Fact]
    public void The_detail_route_renders_the_same_panel_as_the_drawer()
    {
        SignInAsAdmin();
        Api.RequestResult = ApiCallResult<QuotaIncreaseRequestResponse>.Ok(WebTestData.Request(requestId: 5));

        var page = RenderPage<RequestDetail>(("Id", 5));

        page.WaitForAssertion(() =>
        {
            Assert.NotNull(page.Find("[data-testid=request-panel]"));
            Assert.Contains("Running a large migration", page.Find("[data-testid=request-justification]").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_404_on_the_detail_route_explains_that_someone_elses_request_looks_the_same()
    {
        SignInAsDeveloper();
        Api.RequestResult = ApiCallResult<QuotaIncreaseRequestResponse>.Fail(ApiCallStatus.NotFound, "That wasn't found.");

        var page = RenderPage<RequestDetail>(("Id", 404));

        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=request-not-found]")));
    }

    [Fact]
    public void A_403_from_the_list_renders_access_denied()
    {
        SignInAsAdmin();
        Api.RequestsResult = ApiCallResult<PagedResult<QuotaIncreaseRequestResponse>>
            .Fail(ApiCallStatus.Forbidden, "You don't have permission to do that.");

        var page = RenderPage<Requests>();

        page.WaitForAssertion(() => Assert.Contains("Access denied", page.Markup, StringComparison.Ordinal));
    }
}
