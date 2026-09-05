using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure;
using FoundryGate.Domain.Config;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Gateway.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Tests.Predeployment.Api.Endpoints;

/// <summary>
/// <c>/api/v1/gateway/*</c> through the real pipeline against the factory's in-memory APIM and ARM
/// fakes (#225): the auth matrix, the tier list, reading a tier's model allowlist out of the
/// <c>fg-model-map-{tier}</c> named value, replacing it, the refusals that stop a map the gateway
/// would answer with a 404 instead of an honest 403, and the audit row a change leaves.
/// </summary>
/// <remarks>
/// One factory per class, so tests share a database and an APIM fake — every test therefore works on
/// its own tier's named value where it can, and asserts on rows it seeded itself rather than on
/// absolute counts.
/// </remarks>
public class GatewayEndpointTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string TiersPath = "/api/v1/gateway/tiers";

    [Theory]
    [InlineData("GET", TiersPath)]
    [InlineData("GET", TiersPath + "/standard/models")]
    [InlineData("PUT", TiersPath + "/standard/models")]
    public async Task Anonymous_request_returns_401(string method, string path)
    {
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
        if (method == "PUT")
        {
            request.Content = JsonContent.Create(new ReplaceTierModelsRequest(), options: JsonOptions);
        }

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", TiersPath)]
    [InlineData("GET", TiersPath + "/standard/models")]
    [InlineData("PUT", TiersPath + "/standard/models")]
    public async Task Non_admin_returns_403(string method, string path)
    {
        // The allowlist decides what every developer on a tier can reach, so reading it is admin-only
        // too — a developer's own view is the filtered alias list on GET /users/me.
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: false);

        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
        if (method == "PUT")
        {
            request.Content = JsonContent.Create(new ReplaceTierModelsRequest(), options: JsonOptions);
        }

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Tiers_are_the_configured_quota_tiers_with_their_allowlist_sizes()
    {
        factory.Apim.SeedNamedValue(
            GatewayModelMap.NamedValueName(GatewayTiers.Power),
            """{"sonnet":{"deployment":"claude-sonnet-4-5","backend":"foundry-anthropic-pool","provider":"anthropic"}}""");

        using var client = await AdminClientAsync();

        var tiers = await client.GetFromJsonAsync<IReadOnlyList<GatewayTierResponse>>(new Uri(TiersPath, UriKind.Relative), JsonOptions);

        Assert.NotNull(tiers);
        var power = Assert.Single(tiers, tier => tier.Tier == GatewayTiers.Power);
        Assert.Equal("Power", power.DisplayName);
        Assert.Equal(1, power.AllowedModelCount);

        var unlimited = Assert.Single(tiers, tier => tier.Tier == GatewayTiers.Unlimited);
        Assert.True(unlimited.IsUnlimited);
        Assert.Null(unlimited.MonthlyTokenQuota);
    }

    [Fact]
    public async Task A_tiers_models_are_read_from_the_named_value_and_flagged_against_the_real_deployments()
    {
        // Deployed in the primary account only, but routed at the pooled Anthropic backend: exactly the
        // shape that turns a 429 failover into a 404, so the read has to say which region is missing it.
        _ = factory.FoundryClient.Seed(ApiTestFactory.PrimaryFoundryAccount, "half-deployed-model");

        factory.Apim.SeedNamedValue(
            GatewayModelMap.NamedValueName(GatewayTiers.Standard),
            """
            {
              "half": {"deployment":"half-deployed-model","backend":"foundry-anthropic-pool","provider":"anthropic"},
              "ghost": {"deployment":"model-nobody-deployed","backend":"foundry-openai-fgtest-eus2","provider":"openai"}
            }
            """);

        using var client = await AdminClientAsync();

        var models = await client.GetFromJsonAsync<GatewayTierModelsResponse>(
            new Uri($"{TiersPath}/{GatewayTiers.Standard}/models", UriKind.Relative),
            JsonOptions);

        Assert.NotNull(models);

        var half = Assert.Single(models.Aliases, alias => alias.Alias == "half");
        Assert.True(half.DeploymentExists);
        Assert.Equal(GatewayModelMap.AnthropicPool, half.Pool);
        Assert.Equal(ModelProviderType.Anthropic, half.Provider);
        Assert.Equal([ApiTestFactory.SecondaryFoundryAccount], half.MissingFromAccounts);

        var ghost = Assert.Single(models.Aliases, alias => alias.Alias == "ghost");
        Assert.False(ghost.DeploymentExists);
        Assert.Equal(GatewayModelMap.OpenAiPool, ghost.Pool);
        Assert.Equal(ModelProviderType.OpenAi, ghost.Provider);
    }

    [Fact]
    public async Task An_unknown_tier_is_404()
    {
        using var client = await AdminClientAsync();

        var response = await client.GetAsync(new Uri($"{TiersPath}/platinum/models", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Replacing_the_allowlist_writes_the_named_value_and_audits_before_and_after()
    {
        const string Tier = GatewayTiers.Unlimited;
        var oid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(oid);

        _ = factory.FoundryClient.Seed(ApiTestFactory.PrimaryFoundryAccount, "audited-openai-model");
        factory.Apim.SeedNamedValue(GatewayModelMap.NamedValueName(Tier), "{}");

        using var client = factory.CreateClientAs(oid, isAdmin: true);

        var response = await client.PutAsJsonAsync(
            new Uri($"{TiersPath}/{Tier}/models", UriKind.Relative),
            new ReplaceTierModelsRequest
            {
                Aliases = [Alias("audited", "audited-openai-model", GatewayModelMap.OpenAiPool, ModelProviderType.OpenAi)],
            },
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The named value carries the resolved APIM backend id, not the logical pool: that is what the
        // policy's set-backend-service needs, and it is what the bicep writes.
        var stored = factory.Apim.NamedValueOf(GatewayModelMap.NamedValueName(Tier));
        Assert.NotNull(stored);
        Assert.Contains("\"deployment\":\"audited-openai-model\"", stored, StringComparison.Ordinal);
        Assert.Contains($"\"backend\":\"{GatewayModelMap.OpenAiBackendPrefix}{ApiTestFactory.PrimaryFoundryAccount}\"", stored, StringComparison.Ordinal);
        Assert.Contains("\"provider\":\"openai\"", stored, StringComparison.Ordinal);

        await using var dbContext = factory.CreateDbContext();
        var audit = await dbContext.AuditLogs
            .Where(log => log.Action == AuditActions.GatewayModelsUpdated && log.TargetId == Tier)
            .OrderByDescending(log => log.AuditLogId)
            .FirstAsync();

        Assert.Equal(AuditTargetTypes.GatewayTier, audit.TargetType);
        Assert.Contains("before", audit.Details, StringComparison.Ordinal);
        Assert.Contains("audited-openai-model", audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writing_the_map_it_already_holds_changes_nothing()
    {
        const string Tier = GatewayTiers.Power;
        var oid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(oid);

        _ = factory.FoundryClient.Seed(ApiTestFactory.PrimaryFoundryAccount, "noop-model");
        var name = GatewayModelMap.NamedValueName(Tier);
        var backend = GatewayModelMap.OpenAiBackendPrefix + ApiTestFactory.PrimaryFoundryAccount;
        factory.Apim.SeedNamedValue(
            name,
            "{\"noop\":{\"deployment\":\"noop-model\",\"backend\":\"" + backend + "\",\"provider\":\"openai\"}}");

        using var client = factory.CreateClientAs(oid, isAdmin: true);

        var before = factory.Apim.Calls.Count(call => call.StartsWith("SetNamedValue:", StringComparison.Ordinal));

        var response = await client.PutAsJsonAsync(
            new Uri($"{TiersPath}/{Tier}/models", UriKind.Relative),
            new ReplaceTierModelsRequest
            {
                Aliases = [Alias("noop", "noop-model", GatewayModelMap.OpenAiPool, ModelProviderType.OpenAi)],
            },
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // An audit row claiming a change that did not happen is worse than silence, and a write APIM
        // did not need is churn on the thing every request reads.
        Assert.Equal(before, factory.Apim.Calls.Count(call => call.StartsWith("SetNamedValue:", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("Sonnet", "Uppercase isn't a usable alias — the policy compares without normalizing.")]
    [InlineData("-leading", "An alias has to start with a letter or digit.")]
    public async Task A_malformed_alias_is_400(string alias, string because)
    {
        var oid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(oid);
        _ = factory.FoundryClient.Seed(ApiTestFactory.PrimaryFoundryAccount, "malformed-alias-model");

        using var client = factory.CreateClientAs(oid, isAdmin: true);

        var response = await client.PutAsJsonAsync(
            new Uri($"{TiersPath}/{GatewayTiers.Standard}/models", UriKind.Relative),
            new ReplaceTierModelsRequest
            {
                Aliases = [Alias(alias, "malformed-alias-model", GatewayModelMap.OpenAiPool, ModelProviderType.OpenAi)],
            },
            JsonOptions);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, because);
    }

    [Fact]
    public async Task An_alias_pointing_at_a_deployment_that_does_not_exist_is_400()
    {
        var oid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(oid);

        using var client = factory.CreateClientAs(oid, isAdmin: true);

        var response = await client.PutAsJsonAsync(
            new Uri($"{TiersPath}/{GatewayTiers.Standard}/models", UriKind.Relative),
            new ReplaceTierModelsRequest
            {
                Aliases = [Alias("nowhere", "no-such-deployment-anywhere", GatewayModelMap.OpenAiPool, ModelProviderType.OpenAi)],
            },
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("does not exist in any", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pooled_alias_whose_deployment_is_missing_from_a_region_is_400()
    {
        var oid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(oid);

        // Primary only, routed at the pool: the failover-into-404 shape infra/main.bicep warns about.
        _ = factory.FoundryClient.Seed(ApiTestFactory.PrimaryFoundryAccount, "single-region-model");

        using var client = factory.CreateClientAs(oid, isAdmin: true);

        var response = await client.PutAsJsonAsync(
            new Uri($"{TiersPath}/{GatewayTiers.Standard}/models", UriKind.Relative),
            new ReplaceTierModelsRequest
            {
                Aliases = [Alias("pooled", "single-region-model", GatewayModelMap.AnthropicPool, ModelProviderType.Anthropic)],
            },
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(ApiTestFactory.SecondaryFoundryAccount, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_alias_twice_is_400()
    {
        var oid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(oid);
        _ = factory.FoundryClient.Seed(ApiTestFactory.PrimaryFoundryAccount, "dupe-model");

        using var client = factory.CreateClientAs(oid, isAdmin: true);

        var response = await client.PutAsJsonAsync(
            new Uri($"{TiersPath}/{GatewayTiers.Standard}/models", UriKind.Relative),
            new ReplaceTierModelsRequest
            {
                Aliases =
                [
                    Alias("dupe", "dupe-model", GatewayModelMap.OpenAiPool, ModelProviderType.OpenAi),
                    Alias("dupe", "dupe-model", GatewayModelMap.OpenAiPool, ModelProviderType.OpenAi),
                ],
            },
            JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task APIM_refusing_the_write_is_409_and_leaves_the_allowlist_alone()
    {
        const string Tier = GatewayTiers.Standard;
        var oid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(oid);
        _ = factory.FoundryClient.Seed(ApiTestFactory.PrimaryFoundryAccount, "refused-model");

        factory.Apim.ThrowOnSetNamedValue = new RequestFailedException(412, "Precondition failed.");
        try
        {
            using var client = factory.CreateClientAs(oid, isAdmin: true);

            var response = await client.PutAsJsonAsync(
                new Uri($"{TiersPath}/{Tier}/models", UriKind.Relative),
                new ReplaceTierModelsRequest
                {
                    Aliases = [Alias("refused", "refused-model", GatewayModelMap.OpenAiPool, ModelProviderType.OpenAi)],
                },
                JsonOptions);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        finally
        {
            factory.Apim.ThrowOnSetNamedValue = null;
        }
    }

    /// <summary>An admin whose <c>User</c> row exists — every mutation needs one (403 otherwise).</summary>
    private async Task<HttpClient> AdminClientAsync()
    {
        var oid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(oid);
        return factory.CreateClientAs(oid, isAdmin: true);
    }

    private static TierModelAliasRequest Alias(string alias, string deploymentName, string pool, ModelProviderType provider) =>
        new()
        {
            Alias = alias,
            DeploymentName = deploymentName,
            Pool = pool,
            Provider = provider,
        };
}
