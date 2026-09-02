using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Quota;
using FoundryGate.Domain.Requests;
using FoundryGate.Domain.Requests.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Tests.Predeployment.Api.Endpoints;

/// <summary>
/// <c>/api/v1/requests</c> through the real pipeline (#34, #35): the per-route auth contract, body
/// validation, the <c>201</c> + lowercase <c>Location</c> on submit, role-scoped listing and the
/// deliberate <c>404</c> on someone else's request, and the two review transitions with their
/// database and audit side effects. One database per class — every assertion is anchored on rows the
/// test itself seeded, never on absolute counts.
/// </summary>
public class RequestsEndpointTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string RequestsPath = "/api/v1/requests";
    private const string Justification = "Running a batch evaluation this sprint that needs more headroom.";
    private const long StandardCap = 5_000_000;
    private const long PowerCap = 20_000_000;

    // -- Auth contract --

    [Theory]
    [InlineData("GET", RequestsPath)]
    [InlineData("GET", RequestsPath + "/1")]
    [InlineData("POST", RequestsPath)]
    [InlineData("POST", RequestsPath + "/for/1")]
    [InlineData("POST", RequestsPath + "/1/approve")]
    [InlineData("POST", RequestsPath + "/1/reject")]
    public async Task Anonymous_request_returns_401(string method, string path)
    {
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative))
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(RequestsPath + "/for/1")]
    [InlineData(RequestsPath + "/1/approve")]
    [InlineData(RequestsPath + "/1/reject")]
    public async Task Authenticated_non_admin_returns_403_on_admin_routes(string path)
    {
        var dev = await factory.SeedUserAsync();
        using var client = factory.CreateClientAs(dev.EntraObjectId, isAdmin: false);

        var response = await client.PostAsync(new Uri(path, UriKind.Relative), JsonBody("{}"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Submit_by_an_authenticated_caller_with_no_User_row_returns_403_pointing_at_users_me()
    {
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync(
            new Uri(RequestsPath, UriKind.Relative),
            new SubmitQuotaIncreaseRequest { RequestedQuota = PowerCap, Justification = Justification });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Contains("GET /users/me", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    // -- POST /requests --

    [Fact]
    public async Task Submit_returns_201_with_a_lowercase_location_header_and_the_created_request()
    {
        var oid = Guid.NewGuid().ToString();
        var me = await factory.SeedUserAsync(entraObjectId: oid, displayName: "Grace Hopper", configure: u => u.MonthlyTokenQuota = StandardCap);
        using var client = factory.CreateClientAs(oid);
        var period = BillingPeriod.Current(factory.TimeProvider);

        var response = await client.PostAsJsonAsync(
            new Uri(RequestsPath, UriKind.Relative),
            new SubmitQuotaIncreaseRequest { RequestedQuota = PowerCap, Justification = Justification });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<QuotaIncreaseRequestResponse>(JsonOptions);
        Assert.NotNull(created);
        // Lowercase route token (RouteOptions.LowercaseUrls, #129) — "Requests", not the class name's casing.
        Assert.Equal($"/api/v1/requests/{created.QuotaIncreaseRequestId}", response.Headers.Location?.AbsolutePath);
        Assert.Equal(me.UserId, created.UserId);
        Assert.Equal(me.UserId, created.RequestedByUserId);
        Assert.Equal("Grace Hopper", created.UserDisplayName);
        Assert.Equal((period.Year, period.Month), (created.PeriodYear, created.PeriodMonth));
        Assert.Equal(StandardCap, created.CurrentQuota);
        Assert.Equal(PowerCap, created.RequestedQuota);
        Assert.Equal(QuotaRequestStatusType.Pending, created.StatusType);
        Assert.Equal(factory.TimeProvider.GetUtcNow(), created.CreatedDate);

        // The Location actually resolves.
        var followed = await client.GetFromJsonAsync<QuotaIncreaseRequestResponse>(response.Headers.Location, JsonOptions);
        Assert.Equal(created.QuotaIncreaseRequestId, followed?.QuotaIncreaseRequestId);

        await using var dbContext = factory.CreateDbContext();
        Assert.True(await dbContext.AuditLogs.AnyAsync(a =>
            a.Action == AuditActions.QuotaIncreaseSubmitted
            && a.ActorUserId == me.UserId
            && a.TargetId == created.QuotaIncreaseRequestId.ToString()));
    }

    [Fact]
    public async Task Submit_for_unlimited_is_accepted_from_a_finite_budget()
    {
        var oid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(entraObjectId: oid, configure: u => u.MonthlyTokenQuota = PowerCap);
        using var client = factory.CreateClientAs(oid);

        var response = await client.PostAsJsonAsync(
            new Uri(RequestsPath, UriKind.Relative),
            new SubmitQuotaIncreaseRequest { RequestedQuota = null, Justification = Justification });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<QuotaIncreaseRequestResponse>(JsonOptions);
        Assert.Null(created?.RequestedQuota);
    }

    [Fact]
    public async Task Submit_with_a_value_that_is_not_a_tier_returns_400_listing_the_tiers()
    {
        var oid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(entraObjectId: oid, configure: u => u.MonthlyTokenQuota = StandardCap);
        using var client = factory.CreateClientAs(oid);

        var response = await client.PostAsJsonAsync(
            new Uri(RequestsPath, UriKind.Relative),
            new SubmitQuotaIncreaseRequest { RequestedQuota = 7_000_000, Justification = Justification });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var detail = problem.GetProperty("detail").GetString();
        Assert.Contains("not a configured budget tier", detail, StringComparison.Ordinal);
        Assert.Contains(GatewayTiers.Power, detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Submit_that_is_not_an_increase_returns_400()
    {
        var oid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(entraObjectId: oid, configure: u => u.MonthlyTokenQuota = PowerCap);
        using var client = factory.CreateClientAs(oid);

        var response = await client.PostAsJsonAsync(
            new Uri(RequestsPath, UriKind.Relative),
            new SubmitQuotaIncreaseRequest { RequestedQuota = StandardCap, Justification = Justification });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Contains("is not an increase", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Submit_a_second_pending_request_in_the_same_period_returns_409()
    {
        var oid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(entraObjectId: oid, configure: u => u.MonthlyTokenQuota = StandardCap);
        using var client = factory.CreateClientAs(oid);
        var body = new SubmitQuotaIncreaseRequest { RequestedQuota = PowerCap, Justification = Justification };

        var first = await client.PostAsJsonAsync(new Uri(RequestsPath, UriKind.Relative), body);
        var second = await client.PostAsJsonAsync(new Uri(RequestsPath, UriKind.Relative), body);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Submit_with_a_too_short_justification_returns_400_validation_problem_details()
    {
        var oid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(entraObjectId: oid, configure: u => u.MonthlyTokenQuota = StandardCap);
        using var client = factory.CreateClientAs(oid);

        var response = await client.PostAsync(
            new Uri(RequestsPath, UriKind.Relative),
            JsonBody("""{"requestedQuota":20000000,"justification":"short"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions);
        Assert.NotNull(problem?.Errors);
        Assert.Contains(nameof(SubmitQuotaIncreaseRequest.Justification), problem.Errors.Keys);
    }

    // -- POST /requests/for/{userId} --

    [Fact]
    public async Task Admin_can_submit_on_a_users_behalf_and_is_recorded_as_the_requester()
    {
        var adminOid = Guid.NewGuid().ToString();
        var admin = await factory.SeedUserAsync(entraObjectId: adminOid);
        var dev = await factory.SeedUserAsync(configure: u => u.MonthlyTokenQuota = StandardCap);
        using var client = factory.CreateClientAs(adminOid, isAdmin: true);

        var response = await client.PostAsJsonAsync(
            new Uri($"{RequestsPath}/for/{dev.UserId}", UriKind.Relative),
            new SubmitQuotaIncreaseRequest { RequestedQuota = PowerCap, Justification = Justification });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<QuotaIncreaseRequestResponse>(JsonOptions);
        Assert.NotNull(created);
        Assert.Equal(dev.UserId, created.UserId);
        Assert.Equal(admin.UserId, created.RequestedByUserId);
    }

    [Fact]
    public async Task Admin_submitting_for_an_unknown_user_returns_404()
    {
        var adminOid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(entraObjectId: adminOid);
        using var client = factory.CreateClientAs(adminOid, isAdmin: true);

        var response = await client.PostAsJsonAsync(
            new Uri($"{RequestsPath}/for/{int.MaxValue}", UriKind.Relative),
            new SubmitQuotaIncreaseRequest { RequestedQuota = PowerCap, Justification = Justification });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -- GET /requests --

    [Fact]
    public async Task List_for_a_non_admin_returns_only_their_own_requests_in_the_paged_envelope()
    {
        var oid = Guid.NewGuid().ToString();
        var me = await factory.SeedUserAsync(entraObjectId: oid, configure: u => u.MonthlyTokenQuota = StandardCap);
        var other = await factory.SeedUserAsync(configure: u => u.MonthlyTokenQuota = StandardCap);
        await SeedRequestAsync(me.UserId, PowerCap);
        await SeedRequestAsync(other.UserId, PowerCap);
        using var client = factory.CreateClientAs(oid);

        var page = await client.GetFromJsonAsync<PagedResult<QuotaIncreaseRequestResponse>>(
            new Uri(RequestsPath, UriKind.Relative), JsonOptions);

        Assert.NotNull(page);
        Assert.Equal(1, page.Page);
        Assert.Equal(PagedRequest.DefaultPageSize, page.PageSize);
        Assert.All(page.Items, i => Assert.Equal(me.UserId, i.UserId));
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task List_for_a_non_admin_naming_another_user_returns_403()
    {
        var oid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(entraObjectId: oid);
        var other = await factory.SeedUserAsync();
        using var client = factory.CreateClientAs(oid);

        var response = await client.GetAsync(new Uri($"{RequestsPath}?userId={other.UserId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Admin_list_sees_other_users_requests_and_honours_the_status_and_user_filters()
    {
        var adminOid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(entraObjectId: adminOid);
        var dev = await factory.SeedUserAsync(configure: u => u.MonthlyTokenQuota = StandardCap);
        await SeedRequestAsync(dev.UserId, PowerCap);
        await SeedRequestAsync(dev.UserId, PowerCap, QuotaRequestStatusType.Approved, periodMonth: 8);
        using var client = factory.CreateClientAs(adminOid, isAdmin: true);

        var mine = await client.GetFromJsonAsync<PagedResult<QuotaIncreaseRequestResponse>>(
            new Uri($"{RequestsPath}?userId={dev.UserId}", UriKind.Relative), JsonOptions);
        var pending = await client.GetFromJsonAsync<PagedResult<QuotaIncreaseRequestResponse>>(
            new Uri($"{RequestsPath}?userId={dev.UserId}&status={(int)QuotaRequestStatusType.Pending}", UriKind.Relative), JsonOptions);
        var firstPage = await client.GetFromJsonAsync<PagedResult<QuotaIncreaseRequestResponse>>(
            new Uri($"{RequestsPath}?userId={dev.UserId}&page=1&pageSize=1", UriKind.Relative), JsonOptions);

        Assert.Equal(2, mine?.TotalCount);
        Assert.Equal(1, pending?.TotalCount);
        Assert.Equal(QuotaRequestStatusType.Pending, Assert.Single(pending!.Items).StatusType);
        Assert.Equal(2, firstPage?.TotalCount);
        Assert.Equal(2, firstPage?.TotalPages);
        Assert.Single(firstPage!.Items);
    }

    // -- GET /requests/{id} --

    [Fact]
    public async Task Get_someone_elses_request_returns_404_for_a_non_admin_and_200_for_an_admin()
    {
        var adminOid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(entraObjectId: adminOid);
        var intruderOid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(entraObjectId: intruderOid);
        var owner = await factory.SeedUserAsync(configure: u => u.MonthlyTokenQuota = StandardCap);
        var request = await SeedRequestAsync(owner.UserId, PowerCap);

        using var intruderClient = factory.CreateClientAs(intruderOid);
        using var adminClient = factory.CreateClientAs(adminOid, isAdmin: true);
        var asIntruder = await intruderClient.GetAsync(new Uri($"{RequestsPath}/{request.QuotaIncreaseRequestId}", UriKind.Relative));
        var asAdmin = await adminClient.GetAsync(new Uri($"{RequestsPath}/{request.QuotaIncreaseRequestId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, asIntruder.StatusCode);
        Assert.Equal(HttpStatusCode.OK, asAdmin.StatusCode);
    }

    [Fact]
    public async Task Get_an_unknown_request_returns_404()
    {
        var oid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(entraObjectId: oid);
        using var client = factory.CreateClientAs(oid);

        var response = await client.GetAsync(new Uri($"{RequestsPath}/{int.MaxValue}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -- POST /requests/{id}/approve --

    [Fact]
    public async Task Approve_applies_the_tier_to_the_user_re_resolves_the_period_and_audits_before_and_after()
    {
        var adminOid = Guid.NewGuid().ToString();
        var admin = await factory.SeedUserAsync(entraObjectId: adminOid);
        var dev = await factory.SeedUserAsync(configure: u => u.MonthlyTokenQuota = StandardCap);
        var request = await SeedRequestAsync(dev.UserId, PowerCap);
        using var client = factory.CreateClientAs(adminOid, isAdmin: true);
        var period = BillingPeriod.Current(factory.TimeProvider);

        var response = await client.PostAsJsonAsync(
            new Uri($"{RequestsPath}/{request.QuotaIncreaseRequestId}/approve", UriKind.Relative),
            new ReviewQuotaIncreaseRequest { ReviewNotes = "Approved for the eval sprint." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reviewed = await response.Content.ReadFromJsonAsync<QuotaIncreaseRequestResponse>(JsonOptions);
        Assert.NotNull(reviewed);
        Assert.Equal(QuotaRequestStatusType.Approved, reviewed.StatusType);
        Assert.Equal(admin.UserId, reviewed.ReviewedByUserId);
        Assert.Equal(factory.TimeProvider.GetUtcNow(), reviewed.ReviewedDate);
        Assert.Equal("Approved for the eval sprint.", reviewed.ReviewNotes);

        await using var dbContext = factory.CreateDbContext();
        var user = await dbContext.Users.SingleAsync(u => u.UserId == dev.UserId);
        Assert.Equal(PowerCap, user.MonthlyTokenQuota);
        Assert.False(user.IsUnlimited);
        var allocation = await dbContext.QuotaAllocations.SingleAsync(a => a.UserId == dev.UserId && a.PeriodYear == period.Year && a.PeriodMonth == period.Month);
        Assert.Equal(PowerCap, allocation.AllocatedTokens);
        Assert.Equal(GatewayTiers.Power, allocation.TierProductId);
        Assert.True(await dbContext.AuditLogs.AnyAsync(a =>
            a.Action == AuditActions.QuotaIncreaseApproved
            && a.ActorUserId == admin.UserId
            && a.TargetId == request.QuotaIncreaseRequestId.ToString()));
    }

    [Fact]
    public async Task Approving_an_already_decided_request_returns_409()
    {
        var adminOid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(entraObjectId: adminOid);
        var dev = await factory.SeedUserAsync(configure: u => u.MonthlyTokenQuota = StandardCap);
        var request = await SeedRequestAsync(dev.UserId, PowerCap);
        using var client = factory.CreateClientAs(adminOid, isAdmin: true);
        var path = new Uri($"{RequestsPath}/{request.QuotaIncreaseRequestId}/approve", UriKind.Relative);

        var first = await client.PostAsJsonAsync(path, new ReviewQuotaIncreaseRequest());
        var second = await client.PostAsJsonAsync(path, new ReviewQuotaIncreaseRequest());

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Approving_a_request_the_user_has_already_outgrown_returns_409_and_does_not_downgrade_them()
    {
        var adminOid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(entraObjectId: adminOid);
        var dev = await factory.SeedUserAsync(configure: u => u.MonthlyTokenQuota = StandardCap);
        var request = await SeedRequestAsync(dev.UserId, PowerCap);
        using var client = factory.CreateClientAs(adminOid, isAdmin: true);

        // An admin makes them unlimited between filing and review (PUT /users/{id}/quota).
        await using (var dbContext = factory.CreateDbContext())
        {
            var user = await dbContext.Users.SingleAsync(u => u.UserId == dev.UserId);
            user.IsUnlimited = true;
            user.MonthlyTokenQuota = null;
            await dbContext.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync(
            new Uri($"{RequestsPath}/{request.QuotaIncreaseRequestId}/approve", UriKind.Relative),
            new ReviewQuotaIncreaseRequest());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Contains("already unlimited", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);

        await using var probe = factory.CreateDbContext();
        var unchanged = await probe.Users.SingleAsync(u => u.UserId == dev.UserId);
        Assert.True(unchanged.IsUnlimited);
        Assert.Null(unchanged.MonthlyTokenQuota);
        Assert.Equal(
            QuotaRequestStatusType.Pending,
            (await probe.QuotaIncreaseRequests.SingleAsync(r => r.QuotaIncreaseRequestId == request.QuotaIncreaseRequestId)).StatusType);
    }

    [Fact]
    public async Task Submit_measures_against_live_resolution_and_creates_no_allocation()
    {
        var oid = Guid.NewGuid().ToString();
        var me = await factory.SeedUserAsync(entraObjectId: oid, configure: u => u.MonthlyTokenQuota = StandardCap);
        using var client = factory.CreateClientAs(oid);

        var response = await client.PostAsJsonAsync(
            new Uri(RequestsPath, UriKind.Relative),
            new SubmitQuotaIncreaseRequest { RequestedQuota = PowerCap, Justification = Justification });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<QuotaIncreaseRequestResponse>(JsonOptions);
        Assert.Equal(StandardCap, created?.CurrentQuota);

        // Asking for a budget is not activity that mints one: the row still appears on the first
        // GET /quota/allocations/me of the month, not here.
        await using var dbContext = factory.CreateDbContext();
        Assert.False(await dbContext.QuotaAllocations.AnyAsync(a => a.UserId == me.UserId));
    }

    [Fact]
    public async Task Approving_an_unknown_request_returns_404()
    {
        var adminOid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(entraObjectId: adminOid);
        using var client = factory.CreateClientAs(adminOid, isAdmin: true);

        var response = await client.PostAsJsonAsync(
            new Uri($"{RequestsPath}/{int.MaxValue}/approve", UriKind.Relative), new ReviewQuotaIncreaseRequest());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -- POST /requests/{id}/reject --

    [Fact]
    public async Task Reject_records_the_notes_leaves_the_quota_alone_and_frees_the_slot()
    {
        var adminOid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(entraObjectId: adminOid);
        var devOid = Guid.NewGuid().ToString();
        var dev = await factory.SeedUserAsync(entraObjectId: devOid, configure: u => u.MonthlyTokenQuota = StandardCap);
        var request = await SeedRequestAsync(dev.UserId, PowerCap);
        using var adminClient = factory.CreateClientAs(adminOid, isAdmin: true);
        using var devClient = factory.CreateClientAs(devOid);

        var response = await adminClient.PostAsJsonAsync(
            new Uri($"{RequestsPath}/{request.QuotaIncreaseRequestId}/reject", UriKind.Relative),
            new ReviewQuotaIncreaseRequest { ReviewNotes = "Use the shared batch account instead." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reviewed = await response.Content.ReadFromJsonAsync<QuotaIncreaseRequestResponse>(JsonOptions);
        Assert.Equal(QuotaRequestStatusType.Rejected, reviewed?.StatusType);
        Assert.Equal("Use the shared batch account instead.", reviewed?.ReviewNotes);

        await using (var dbContext = factory.CreateDbContext())
        {
            var user = await dbContext.Users.SingleAsync(u => u.UserId == dev.UserId);
            Assert.Equal(StandardCap, user.MonthlyTokenQuota);
        }

        // The developer can ask again in the same period now that the first request is decided.
        var resubmit = await devClient.PostAsJsonAsync(
            new Uri(RequestsPath, UriKind.Relative),
            new SubmitQuotaIncreaseRequest { RequestedQuota = PowerCap, Justification = Justification });
        Assert.Equal(HttpStatusCode.Created, resubmit.StatusCode);
    }

    [Fact]
    public async Task Review_notes_longer_than_the_limit_return_400_validation_problem_details()
    {
        var adminOid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(entraObjectId: adminOid);
        using var client = factory.CreateClientAs(adminOid, isAdmin: true);
        var tooLong = new string('x', ValidationConstants.ReviewNotesMaxLength + 1);

        var response = await client.PostAsJsonAsync(
            new Uri($"{RequestsPath}/1/reject", UriKind.Relative),
            new ReviewQuotaIncreaseRequest { ReviewNotes = tooLong });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions);
        Assert.NotNull(problem?.Errors);
        Assert.Contains(nameof(ReviewQuotaIncreaseRequest.ReviewNotes), problem.Errors.Keys);
    }

    [Fact]
    public async Task ExpireStale_is_admin_only()
    {
        using var anonymous = factory.CreateClient();
        using var developer = factory.CreateClientAs(Guid.NewGuid().ToString());

        var path = new Uri($"{RequestsPath}/expire-stale", UriKind.Relative);
        var asAnonymous = await anonymous.PostAsync(path, content: null);
        var asDeveloper = await developer.PostAsync(path, content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, asAnonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asDeveloper.StatusCode);
    }

    [Fact]
    public async Task ExpireStale_closes_requests_from_a_closed_period_and_leaves_this_periods_alone()
    {
        // #159/#204: the sweep the monthly reset runs, reachable on its own for the window between a
        // period ending and the next reset — where those requests are already unapprovable but still
        // clutter the queue, and where POST /quota/reset would be a much bigger hammer.
        var adminOid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(entraObjectId: adminOid);
        var dev = await factory.SeedUserAsync(
            entraObjectId: Guid.NewGuid().ToString(),
            configure: u => u.MonthlyTokenQuota = StandardCap);
        var other = await factory.SeedUserAsync(
            entraObjectId: Guid.NewGuid().ToString(),
            configure: u => u.MonthlyTokenQuota = StandardCap);

        var period = BillingPeriod.Current(factory.TimeProvider);
        var stale = await SeedRequestAsync(dev.UserId, PowerCap, periodMonth: period.Month == 1 ? 12 : period.Month - 1);
        var live = await SeedRequestAsync(other.UserId, PowerCap);

        using var client = factory.CreateClientAs(adminOid, isAdmin: true);
        var response = await client.PostAsync(new Uri($"{RequestsPath}/expire-stale", UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ExpireStaleRequestsResult>(JsonOptions);
        Assert.True(result!.ExpiredCount >= 1);

        await using var dbContext = factory.CreateDbContext();
        var closed = await dbContext.QuotaIncreaseRequests.SingleAsync(r => r.QuotaIncreaseRequestId == stale.QuotaIncreaseRequestId);
        Assert.Equal(QuotaRequestStatusType.Rejected, closed.StatusType);
        Assert.Null(closed.ReviewedByUserId); // nobody reviewed it — that is what marks it lapsed
        Assert.NotEmpty(closed.ReviewNotes);

        var untouched = await dbContext.QuotaIncreaseRequests.SingleAsync(r => r.QuotaIncreaseRequestId == live.QuotaIncreaseRequestId);
        Assert.Equal(QuotaRequestStatusType.Pending, untouched.StatusType);
    }

    // -- Helpers --

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task<QuotaIncreaseRequest> SeedRequestAsync(
        int userId,
        long? requestedQuota,
        QuotaRequestStatusType status = QuotaRequestStatusType.Pending,
        int? periodMonth = null)
    {
        var period = BillingPeriod.Current(factory.TimeProvider);
        await using var dbContext = factory.CreateDbContext();

        var request = new QuotaIncreaseRequest
        {
            UserId = userId,
            RequestedByUserId = userId,
            PeriodYear = period.Year,
            PeriodMonth = periodMonth ?? period.Month,
            CurrentQuota = StandardCap,
            RequestedQuota = requestedQuota,
            Justification = Justification,
            StatusType = status,
        };
        dbContext.QuotaIncreaseRequests.Add(request);
        await dbContext.SaveChangesAsync();

        return request;
    }
}
