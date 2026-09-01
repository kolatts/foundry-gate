using MudBlazor;

namespace FoundryGate.Web.Theme;

/// <summary>
/// Maps the Foundry Gate brand's `--fg-*` design tokens (<c>content/tokens.md</c>,
/// mirrored in <c>wwwroot/css/tokens.css</c>) onto a <see cref="MudTheme"/>. The brand is
/// dark-first (see <c>docs-site/src/pages/index.astro</c>'s own token block) — this app
/// ships a single dark palette rather than a light/dark pair; see the
/// <see cref="Palette"/> remarks for why <see cref="MudTheme.PaletteLight"/> is left at
/// the MudBlazor default instead of a second brand palette.
/// </summary>
public static class FoundryGateTheme
{
    /// <summary>The one theme this app renders. <see cref="Program"/> forces <c>IsDarkMode</c> on unconditionally.</summary>
    public static MudTheme Theme { get; } = new()
    {
        PaletteDark = new PaletteDark
        {
            // Azure primary scale (content/tokens.md)
            Primary = "#0078D4", // --fg-azure
            PrimaryLighten = "#4DCFFF", // --fg-azure-neon (focus ring / accent)
            PrimaryDarken = "#0558A0", // --fg-azure-dim (disabled state)

            // MudBlazor's Secondary color has no brand token of its own — pin it to
            // --fg-azure-dim rather than leaving it at MudBlazor's default pink, which
            // would leak an off-brand color into any component that defaults to
            // Color.Secondary (e.g. MudText Color="Color.Secondary" — see PR #98 review;
            // muted body text uses the "mud-text-secondary" CSS class instead, which reads
            // TextSecondary, not this).
            Secondary = "#0558A0", // --fg-azure-dim

            // Semantic (quota / spend / alerts use the ember scale; see QuotaGauge in a later epic)
            Warning = "#FFB347", // --fg-ember-soft — approaching limit
            Error = "#FF5400", // --fg-ember-hot — over limit / critical
            Success = "#22C55E", // --fg-success — model online / approved
            Info = "#38BDF8", // --fg-info — neutral callout

            // Background scale
            Black = "#080C12", // --fg-bg-base
            Background = "#080C12", // --fg-bg-base — page canvas
            BackgroundGray = "#111927", // --fg-bg-raised — elevated card
            Surface = "#0D1520", // --fg-bg-surface — card / panel
            DrawerBackground = "#0D1520", // --fg-bg-surface
            AppbarBackground = "#080C12", // --fg-bg-base

            // Text scale
            TextPrimary = "#E8EEF5", // --fg-text-primary
            TextSecondary = "#A0B0C0", // --fg-text-secondary
            TextDisabled = "#3A4F63", // --fg-text-disabled
            DrawerText = "#E8EEF5", // --fg-text-primary
            DrawerIcon = "#A0B0C0", // --fg-text-secondary
            AppbarText = "#E8EEF5", // --fg-text-primary

            // Borders / lines
            Divider = "#1E2D42", // --fg-border-subtle
            DividerLight = "#1E2D42", // --fg-border-subtle
            LinesDefault = "#1E2D42", // --fg-border-subtle
            LinesInputs = "#2D4460", // --fg-border-strong
            TableLines = "#1E2D42", // --fg-border-subtle

            ActionDefault = "#A0B0C0", // --fg-text-secondary
            ActionDisabled = "#3A4F63", // --fg-text-disabled
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = ["Inter", "system-ui", "-apple-system", "sans-serif"] },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
        },
    };
}
