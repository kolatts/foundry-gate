using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Foundry;
using FoundryGate.Domain.Foundry.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Tests.Predeployment.Api.Endpoints;

/// <summary>
/// <c>/api/v1/foundry/*</c> through the real pipeline against the factory's in-memory ARM fake:
/// the auth matrix per endpoint (401 anonymous, 403 non-admin on admin routes, 200 developer on
/// <c>/models</c>), 201 + <c>Location</c> on create, 409 for an existing name, 400 for Anthropic
/// and for validation failures, 204/404 on delete, and the audit rows both mutations leave.
/// </summary>
public class FoundryEndpointTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string DeploymentsPath = "/api/v1/foundry/deployments";
    private const string ModelsPath = "/api/v1/foundry/models";

    [Theory]
    [InlineData("GET", DeploymentsPath)]
    [InlineData("GET", DeploymentsPath + "/" + ApiTestFactory.PrimaryFoundryAccount + "/anything")]
    [InlineData("POST", DeploymentsPath)]
    [InlineData("DELETE", DeploymentsPath + "/" + ApiTestFactory.PrimaryFoundryAccount + "/anything")]
    [InlineData("GET", ModelsPath)]
    public async Task Anonymous_request_returns_401(string method, string path)
    {
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", DeploymentsPath)]
    [InlineData("GET", DeploymentsPath + "/" + ApiTestFactory.PrimaryFoundryAccount + "/anything")]
    [InlineData("POST", DeploymentsPath)]
    [InlineData("DELETE", DeploymentsPath + "/" + ApiTestFactory.PrimaryFoundryAccount + "/anything")]
    public async Task Non_admin_on_a_deployments_route_returns_403(string method, string path)
    {
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: false);

        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
        if (method == "POST")
        {
            request.Content = JsonContent.Create(ValidRequest(Marker()));
        }

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Admin_lists_deployments_across_both_accounts_with_the_full_admin_shape()
    {
        var name = Marker();
        factory.FoundryClient.Seed(ApiTestFactory.SecondaryFoundryAccount, name, capacity: 25);
        factory.FoundryClient.Seed(ApiTestFactory.PrimaryFoundryAccount, name, capacity: 10);
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);

        var response = await client.GetAsync(new Uri(DeploymentsPath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var deployments = await response.Content.ReadFromJsonAsync<List<FoundryDeploymentResponse>>(JsonOptions);
        Assert.NotNull(deployments);
        var mine = deployments.Where(d => d.DeploymentName == name).ToList();
        Assert.Equal(
            [(ApiTestFactory.PrimaryFoundryAccount, 10), (ApiTestFactory.SecondaryFoundryAccount, 25)],
            mine.Select(d => (d.AccountName, d.Capacity)));
        Assert.All(mine, d =>
        {
            Assert.Equal("GlobalStandard", d.SkuName);
            Assert.Equal("Succeeded", d.ProvisioningState);
            Assert.NotNull(d.CreatedDate);
        });
    }

    [Fact]
    public async Task Any_authenticated_user_lists_models_deduplicated_with_the_developer_shape()
    {
        var name = Marker();
        factory.FoundryClient.Seed(ApiTestFactory.PrimaryFoundryAccount, name, provisioningState: "Creating");
        factory.FoundryClient.Seed(ApiTestFactory.SecondaryFoundryAccount, name, provisioningState: "Succeeded");
        // A developer with no User row yet — /models needs no audit actor, so it must still work.
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: false);

        var response = await client.GetAsync(new Uri(ModelsPath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var models = await response.Content.ReadFromJsonAsync<List<FoundryModelResponse>>(JsonOptions);
        Assert.NotNull(models);
        var model = Assert.Single(models, m => m.DeploymentName == name);
        Assert.Equal("gpt-4.1-mini", model.ModelName);
        Assert.Equal("2025-04-14", model.ModelVersion);
        Assert.Equal("OpenAI", model.ModelFormat);
        Assert.Equal("Succeeded", model.ProvisioningState);
    }

    [Fact]
    public async Task Admin_gets_one_deployment_or_404()
    {
        var name = Marker();
        factory.FoundryClient.Seed(ApiTestFactory.PrimaryFoundryAccount, name);
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);

        var found = await client.GetAsync(new Uri($"{DeploymentsPath}/{ApiTestFactory.PrimaryFoundryAccount}/{name}", UriKind.Relative));
        var missing = await client.GetAsync(new Uri($"{DeploymentsPath}/{ApiTestFactory.SecondaryFoundryAccount}/{name}", UriKind.Relative));
        var wrongAccount = await client.GetAsync(new Uri($"{DeploymentsPath}/not-ours/{name}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        var deployment = await found.Content.ReadFromJsonAsync<FoundryDeploymentResponse>(JsonOptions);
        Assert.Equal(name, deployment?.DeploymentName);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongAccount.StatusCode);
    }

    [Fact]
    public async Task Admin_create_returns_201_with_Location_and_writes_the_audit_row()
    {
        var admin = await factory.SeedUserAsync(displayName: "Admin Ada");
        using var client = factory.CreateClientAs(admin.EntraObjectId, isAdmin: true);
        var name = Marker();

        var response = await client.PostAsJsonAsync(new Uri(DeploymentsPath, UriKind.Relative), ValidRequest(name));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        // Case-insensitive: URL generation keeps the [controller] token's class-name casing ("Foundry")
        // until RouteOptions.LowercaseUrls is set host-wide; routing itself is case-insensitive.
        Assert.EndsWith($"{DeploymentsPath}/{ApiTestFactory.PrimaryFoundryAccount}/{name}", response.Headers.Location?.ToString(), StringComparison.OrdinalIgnoreCase);
        var created = await response.Content.ReadFromJsonAsync<FoundryDeploymentResponse>(JsonOptions);
        Assert.NotNull(created);
        Assert.Equal(name, created.DeploymentName);
        Assert.Equal("Creating", created.ProvisioningState);

        // The Location resolves.
        var follow = await client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, follow.StatusCode);

        await using var dbContext = factory.CreateDbContext();
        var audit = await dbContext.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.FoundryDeploymentCreated && a.TargetId == $"{ApiTestFactory.PrimaryFoundryAccount}/{name}");
        Assert.Equal(admin.UserId, audit.ActorUserId);
        Assert.Equal(AuditTargetTypes.FoundryDeployment, audit.TargetType);
    }

    [Fact]
    public async Task Admin_without_a_User_row_gets_403_and_nothing_is_created()
    {
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);
        var name = Marker();

        var response = await client.PostAsJsonAsync(new Uri(DeploymentsPath, UriKind.Relative), ValidRequest(name));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions);
        Assert.Contains("GET /users/me", problem?.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.FoundryClient.CreateCalls, c => c.DeploymentName == name);
    }

    [Fact]
    public async Task Create_with_an_existing_name_returns_409_and_never_re_PUTs()
    {
        var admin = await factory.SeedUserAsync(displayName: "Admin Ada");
        using var client = factory.CreateClientAs(admin.EntraObjectId, isAdmin: true);
        var name = Marker();
        factory.FoundryClient.Seed(ApiTestFactory.PrimaryFoundryAccount, name);

        var response = await client.PostAsJsonAsync(new Uri(DeploymentsPath, UriKind.Relative), ValidRequest(name));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions);
        Assert.Equal((int)HttpStatusCode.Conflict, problem?.Status);
        Assert.Contains("already exists", problem?.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.FoundryClient.CreateCalls, c => c.DeploymentName == name);
    }

    [Fact]
    public async Task Create_with_Anthropic_format_returns_400_pointing_at_107()
    {
        var admin = await factory.SeedUserAsync(displayName: "Admin Ada");
        using var client = factory.CreateClientAs(admin.EntraObjectId, isAdmin: true);
        var name = Marker();
        var request = ValidRequest(name) with
        {
            ModelFormat = FoundryModelFormatType.Anthropic,
            ModelName = "claude-haiku-4-5",
            ModelVersion = "20251001",
        };

        var response = await client.PostAsJsonAsync(new Uri(DeploymentsPath, UriKind.Relative), request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions);
        Assert.Contains("#107", problem?.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.FoundryClient.CreateCalls, c => c.DeploymentName == name);
    }

    [Fact]
    public async Task Create_with_an_unconfigured_account_returns_400()
    {
        var admin = await factory.SeedUserAsync(displayName: "Admin Ada");
        using var client = factory.CreateClientAs(admin.EntraObjectId, isAdmin: true);
        var request = ValidRequest(Marker()) with { AccountName = "someone-elses-account" };

        var response = await client.PostAsJsonAsync(new Uri(DeploymentsPath, UriKind.Relative), request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions);
        Assert.Contains("someone-elses-account", problem?.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_with_an_invalid_body_returns_400_validation_problem_details()
    {
        var admin = await factory.SeedUserAsync(displayName: "Admin Ada");
        using var client = factory.CreateClientAs(admin.EntraObjectId, isAdmin: true);
        var request = ValidRequest("bad name!") with { Capacity = 0, ModelVersion = string.Empty };

        var response = await client.PostAsJsonAsync(new Uri(DeploymentsPath, UriKind.Relative), request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions);
        Assert.NotNull(problem?.Errors);
        Assert.Contains(nameof(CreateFoundryDeploymentRequest.DeploymentName), problem.Errors.Keys);
        Assert.Contains(nameof(CreateFoundryDeploymentRequest.Capacity), problem.Errors.Keys);
        Assert.Contains(nameof(CreateFoundryDeploymentRequest.ModelVersion), problem.Errors.Keys);
        Assert.DoesNotContain(factory.FoundryClient.CreateCalls, c => c.DeploymentName == "bad name!");
    }

    [Fact]
    public async Task Admin_delete_returns_204_and_writes_the_audit_row()
    {
        var admin = await factory.SeedUserAsync(displayName: "Admin Ada");
        using var client = factory.CreateClientAs(admin.EntraObjectId, isAdmin: true);
        var name = Marker();
        factory.FoundryClient.Seed(ApiTestFactory.SecondaryFoundryAccount, name);

        var response = await client.DeleteAsync(new Uri($"{DeploymentsPath}/{ApiTestFactory.SecondaryFoundryAccount}/{name}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains((ApiTestFactory.SecondaryFoundryAccount, name), factory.FoundryClient.DeleteCalls);
        Assert.DoesNotContain(factory.FoundryClient.CreateCalls, c => c.DeploymentName == name); // never recreate

        await using var dbContext = factory.CreateDbContext();
        var audit = await dbContext.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.FoundryDeploymentDeleted && a.TargetId == $"{ApiTestFactory.SecondaryFoundryAccount}/{name}");
        Assert.Equal(admin.UserId, audit.ActorUserId);
    }

    [Fact]
    public async Task Delete_of_a_missing_deployment_returns_404()
    {
        var admin = await factory.SeedUserAsync(displayName: "Admin Ada");
        using var client = factory.CreateClientAs(admin.EntraObjectId, isAdmin: true);
        var name = Marker();

        var response = await client.DeleteAsync(new Uri($"{DeploymentsPath}/{ApiTestFactory.PrimaryFoundryAccount}/{name}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(factory.FoundryClient.DeleteCalls, c => c.DeploymentName == name);
    }

    private static string Marker() => $"dep-{Guid.NewGuid():N}"[..20];

    private static CreateFoundryDeploymentRequest ValidRequest(string deploymentName) =>
        new()
        {
            AccountName = ApiTestFactory.PrimaryFoundryAccount,
            DeploymentName = deploymentName,
            ModelFormat = FoundryModelFormatType.OpenAI,
            ModelName = "gpt-4.1-mini",
            ModelVersion = "2025-04-14",
            SkuName = "GlobalStandard",
            Capacity = 10,
        };
}
