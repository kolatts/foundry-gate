using FoundryGate.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// PR #98 review (Major): <see cref="FoundryGateApiClient"/> must map a thrown
/// <see cref="AccessTokenNotAvailableException"/> — MSAL's <c>AuthorizationMessageHandler</c>
/// throws this when it can't silently acquire/refresh a token (expired session, blocked
/// third-party cookies, ...) — to <see cref="ApiCallStatus.Unauthorized"/>, and must NOT
/// call the exception's own <c>Redirect()</c> (a data client shouldn't navigate; the UI
/// decides what "sign in again" looks like from the <see cref="ApiCallStatus.Unauthorized"/>
/// result — see <c>Pages/Home.razor</c>). Exercised by substituting a handler that throws
/// the same exception <c>AuthorizationMessageHandler</c> would, rather than the real
/// MSAL/JS-interop plumbing (not instantiable outside a browser host).
/// </summary>
public class FoundryGateApiClientTests
{
    [Fact]
    public async Task GetMeAsync_maps_AccessTokenNotAvailableException_to_Unauthorized()
    {
        var client = CreateClientThatThrows(CreateAccessTokenNotAvailableException());

        var result = await client.GetMeAsync();

        Assert.Equal(ApiCallStatus.Unauthorized, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task ActivateUserAsync_action_style_call_also_maps_AccessTokenNotAvailableException_to_Unauthorized()
    {
        // SendActionAsync (used by activate/deactivate/approve/reject/delete/...) has its
        // own catch clause, separate from SendAsync<T> — cover both paths.
        var client = CreateClientThatThrows(CreateAccessTokenNotAvailableException());

        var result = await client.ActivateUserAsync(userId: 1);

        Assert.Equal(ApiCallStatus.Unauthorized, result.Status);
        Assert.False(result.IsSuccess);
    }

    private static FoundryGateApiClient CreateClientThatThrows(Exception exception)
    {
        var httpClient = new HttpClient(new ThrowingHandler(exception))
        {
            BaseAddress = new Uri("https://foundrygate.test/api/v1/"),
        };
        return new FoundryGateApiClient(httpClient);
    }

    private static AccessTokenNotAvailableException CreateAccessTokenNotAvailableException()
    {
        var navigation = new TestNavigationManager();
        var tokenResult = new AccessTokenResult(AccessTokenResultStatus.RequiresRedirect, new AccessToken(), "authentication/login", interactiveRequest: null!);
        return new AccessTokenNotAvailableException(navigation, tokenResult, scopes: []);
    }

    /// <summary>Minimal concrete <see cref="NavigationManager"/> — abstract, and only constructible via its protected <c>Initialize</c> method.</summary>
    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://foundrygate.test/", "https://foundrygate.test/");
    }

    /// <summary>Stands in for MSAL's <c>AuthorizationMessageHandler</c>: throws instead of attaching a token.</summary>
    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }
}
