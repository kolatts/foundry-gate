using System.Text.Json;
using FoundryGate.Domain.Common;

namespace FoundryGate.Tests.Predeployment.Domain;

/// <summary>
/// <see cref="ApiError"/> exists to deserialize whatever error payload actually comes
/// back over the wire, including a payload missing fields entirely — these tests prove
/// that doesn't throw and that the "missing" case is observable as null, not a silent
/// non-null default (PR #91 review nit).
/// </summary>
public class ApiErrorTests
{
    // Mirrors JsonSerializerDefaults.Web (camelCase, case-insensitive) — what
    // System.Net.Http.Json's HttpClient extensions use by default, and what
    // FoundryGate.Web's HttpClient will realistically be configured with.
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Deserializing_a_payload_missing_type_and_title_does_not_throw_and_yields_null()
    {
        const string Json = """{"status":404,"detail":"User 42 not found."}""";

        ApiError? error = JsonSerializer.Deserialize<ApiError>(Json, WebOptions);

        Assert.NotNull(error);
        Assert.Null(error.Type);
        Assert.Null(error.Title);
        Assert.Equal(404, error.Status);
        Assert.Equal("User 42 not found.", error.Detail);
    }

    [Fact]
    public void Deserializing_a_full_payload_round_trips()
    {
        var original = new ApiError(
            Type: "https://tools.ietf.org/html/rfc7807",
            Title: "Not Found",
            Status: 404,
            Detail: "User 42 not found.",
            Instance: "/api/v1/users/42");

        string json = JsonSerializer.Serialize(original, WebOptions);
        ApiError? roundTripped = JsonSerializer.Deserialize<ApiError>(json, WebOptions);

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void DefaultType_matches_RFC7807s_own_default()
    {
        Assert.Equal("about:blank", ApiError.DefaultType);
    }
}
