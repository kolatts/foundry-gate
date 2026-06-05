# Blazor WASM scaffold with MSAL auth and role-aware routing

> GitHub: #15  
> Milestone: v0.4 — Frontend  
> Labels: epic, frontend

## Overview
This epic bootstraps `FoundryGate.Web` as a production-ready Blazor WebAssembly application with Microsoft Authentication Library (MSAL) integration, role-based route guards, and a typed HTTP client that talks to the API. MudBlazor is installed as the single component library — it provides the layout shell, nav drawer, app bar, and snackbar notification system so subsequent epics can focus on page content rather than wiring up primitives. This epic establishes the shell that all subsequent frontend epics drop pages into.

## Approach

### Scaffold FoundryGate.Web with MSAL auth, role guards, and typed API client (#48)
Configure `Microsoft.Authentication.WebAssembly.Msal` in `Program.cs` with the Entra tenant ID, client ID, and scopes read from `wwwroot/appsettings.json`. Add a `RedirectToLogin` component and an `AuthorizeRouteView` in `App.razor` that redirects unauthenticated users and shows an `AccessDenied` component for unauthorised roles. Define two role constants (`Admin`, `Developer`) in `FoundryGate.Domain` and apply `[Authorize(Roles = ...)]` on page components. Register a typed `Foundry GateApiClient` using `IHttpClientFactory` with `AuthorizationMessageHandler` to attach Bearer tokens automatically.

Install `MudBlazor` and register `services.AddMudServices()` and the required CSS/JS in `index.html`. Replace the default Blazor shell with a `MudLayout` containing a `MudAppBar` (showing the signed-in user's display name and a sign-out icon button), a `MudDrawer` for the nav sidebar, and `MudNavMenu` / `MudNavLink` items grouped into Developer and Admin sections — the Admin group is hidden via `AuthorizeView`. Use `MudSnackbar` (injected `ISnackbar`) as the notification service throughout the app.

**Brand theme wiring (source: `content/`):**

Configure a `MudTheme` in `Program.cs` that maps the `--fg-*` design tokens to MudBlazor's palette:
```csharp
var theme = new MudTheme {
    PaletteDark = new PaletteDark {
        Primary         = "#0078D4",   // --fg-azure
        PrimaryLighten  = "#4DCFFF",   // --fg-azure-neon
        PrimaryDarken   = "#0558A0",   // --fg-azure-dim
        Warning         = "#FFB347",   // --fg-ember-soft
        Error           = "#FF5400",   // --fg-ember-hot
        Success         = "#22C55E",   // --fg-success
        Info            = "#38BDF8",   // --fg-info
        Background      = "#080C12",   // --fg-bg-base
        Surface         = "#0D1520",   // --fg-bg-surface
        DrawerBackground = "#0D1520",
        AppbarBackground = "#080C12",
        TextPrimary     = "#E8EEF5",   // --fg-text-primary
        TextSecondary   = "#A0B0C0",   // --fg-text-secondary
    },
    Typography = new Typography {
        Default = new Default { FontFamily = ["Inter", "system-ui", "-apple-system", "sans-serif"] },
    },
};
```

Register `services.AddMudServices(config => { config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight; })`.

Add `content/typography.css` to `wwwroot/css/typography.css` (adjust `@font-face` src URLs to `/fonts/` for the Blazor static file base). Place Inter and Monaspace Argon `.woff2` files in `wwwroot/fonts/` (not committed — fetched during CI via Fontsource or download script). Reference from `index.html`. Use `class="fg-mono"` on all token counts, quota numbers, and API key display fields so they render in Monaspace Argon. Add `content/tokens.md`'s CSS variables as `wwwroot/css/tokens.css` and link in `index.html` — this gives Blazor components access to the full `--fg-*` palette for any custom CSS that MudBlazor doesn't cover.

Files expected to be created or modified:
- `src/FoundryGate.Web/FoundryGate.Web.csproj`
- `src/FoundryGate.Web/Program.cs`
- `src/FoundryGate.Web/wwwroot/appsettings.json`
- `src/FoundryGate.Web/wwwroot/index.html`
- `src/FoundryGate.Web/wwwroot/css/tokens.css` (from `content/tokens.md` color values)
- `src/FoundryGate.Web/wwwroot/css/typography.css` (from `content/typography.css`)
- `src/FoundryGate.Web/wwwroot/fonts/README.md` (download instructions)
- `src/FoundryGate.Web/App.razor`
- `src/FoundryGate.Web/Shared/MainLayout.razor`
- `src/FoundryGate.Web/Shared/NavMenu.razor`
- `src/FoundryGate.Web/Shared/RedirectToLogin.razor`
- `src/FoundryGate.Web/Shared/AccessDenied.razor`
- `src/FoundryGate.Web/Services/Foundry GateApiClient.cs`

## Verification
- [ ] `dotnet build` passes
- [ ] Unauthenticated browser visit redirects to Entra login
- [ ] After login, Developer role sees developer nav items only; Admin nav group is hidden
- [ ] Admin role sees all nav items
- [ ] API client attaches Bearer token on every request (verified in browser DevTools Network tab)
- [ ] MudSnackbar notification appears for a test success/error trigger
- [ ] MudDrawer collapses correctly on mobile viewport
