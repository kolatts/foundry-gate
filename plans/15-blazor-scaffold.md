# Blazor WASM scaffold with MSAL auth and role-aware routing

> GitHub: #15  
> Milestone: v0.4 — Frontend  
> Labels: epic, frontend

## Overview
This epic bootstraps `FoundryGate.Web` as a production-ready Blazor WebAssembly application with Microsoft Authentication Library (MSAL) integration, role-based route guards, and a typed HTTP client that talks to the API. It establishes the shell layout (nav sidebar, top bar, toast notifications) and the authentication lifecycle (redirect to Entra login, token acquisition, token refresh) so that all subsequent frontend epics drop pages into a working frame rather than building plumbing from scratch.

## Approach

### Scaffold FoundryGate.Web with MSAL auth, role guards, and typed API client (#48)
Configure `Microsoft.Authentication.WebAssembly.Msal` in `Program.cs` with the Entra tenant ID, client ID, and scopes read from `wwwroot/appsettings.json`. Add a `RedirectToLogin` component and an `AuthorizeRouteView` in `App.razor` that redirects unauthenticated users and shows an `AccessDenied` component for unauthorised roles. Define two role constants (`Admin`, `Developer`) and apply `[Authorize(Roles = ...)]` attributes on page components. Register a typed `FoundryGateApiClient` using `IHttpClientFactory` with the API base URL from configuration; the client adds the Bearer token to every request using `AuthorizationMessageHandler`. Create a minimal shell layout with a responsive sidebar (Admin section hidden for Developer role), a top bar showing the signed-in user's name, and a `ToastService` for in-app notifications.

Files expected to be created or modified:
- `src/FoundryGate.Web/Program.cs`
- `src/FoundryGate.Web/wwwroot/appsettings.json`
- `src/FoundryGate.Web/App.razor`
- `src/FoundryGate.Web/Shared/MainLayout.razor`
- `src/FoundryGate.Web/Shared/NavMenu.razor`
- `src/FoundryGate.Web/Shared/RedirectToLogin.razor`
- `src/FoundryGate.Web/Shared/AccessDenied.razor`
- `src/FoundryGate.Web/Services/FoundryGateApiClient.cs`
- `src/FoundryGate.Web/Services/ToastService.cs`
- `src/FoundryGate.Web/FoundryGate.Web.csproj`

## Verification
- [ ] `dotnet build` passes
- [ ] Unauthenticated browser visit redirects to Entra login
- [ ] After login, Developer role sees developer nav items only
- [ ] Admin role sees all nav items including the admin section
- [ ] API client attaches Bearer token on every request (verified in browser DevTools)
- [ ] Toast notification appears correctly for a test success/error trigger
