# Documentation — Astro/Starlight docs site on GitHub Pages

> GitHub: #19  
> Milestone: v0.1 — Foundation  
> Labels: epic, docs

## Overview
Documentation lives in a `docs-site/` Astro project using the Starlight theme, deployed to GitHub Pages. The site uses FoundryGate's own brand identity throughout — the portal-gate logo, Inter + Monaspace Argon typefaces, and the full `--fg-*` design token palette defined in `content/tokens.md`. Starlight's CSS variables are overridden to match those tokens so the docs feel like a first-party product surface, not a generic template. Brand source files live in `content/` at the repo root and are referenced into `docs-site/` during the scaffold step.

## Brand assets (source: `content/`)

| File | Description | Destination in docs-site |
|---|---|---|
| `content/logo-transparent.png` | Wordmark with gate-portal icon, transparent bg | `docs-site/src/assets/logo.png` (Starlight logo) |
| `content/logo.png` | Wordmark on dark bg | `docs-site/public/og-logo.png` (OpenGraph card) |
| `content/icons.png` | 7-glyph custom icon sheet (key, hex, link, chart, brackets, calendar, warning) | `docs-site/public/icons.png` (reference / sprite) |
| `content/tokens.md` | Design token spec (color palette, all `--fg-*` values) | Expanded into `docs-site/src/styles/tokens.css` |
| `content/typography.css` | Font declarations + type scale (Inter + Monaspace Argon) | Adapted into `docs-site/src/styles/typography.css` |

---

## Approach

### Scaffold Astro Starlight site, apply brand theme, and deploy to GitHub Pages (#59)

Create `docs-site/` using `npm create astro@latest -- --template starlight`. Then apply the full brand identity:

**1. Starlight config (`docs-site/astro.config.mjs`):**
```js
starlight({
  title: 'FoundryGate',
  logo: {
    src: './src/assets/logo.png',
    alt: 'FoundryGate',
    replacesTitle: true,
  },
  favicon: '/favicon.svg',           // derived from the gate-portal glyph
  customCss: [
    './src/styles/tokens.css',
    './src/styles/typography.css',
    './src/styles/starlight-theme.css',
  ],
  sidebar: [
    { label: 'Getting Started', items: ['index', 'getting-started/fork-guide', 'getting-started/cli-setup'] },
    { label: 'Architecture', items: ['architecture/overview'] },
    { label: 'Reference', items: ['reference/configuration', 'reference/api'] },
    { label: 'Contributing', items: ['contributing'] },
  ],
  social: { github: 'https://github.com/kolatts/foundry-gate' },
})
```

**2. Design tokens (`docs-site/src/styles/tokens.css`):**  
Expand the color values from `content/tokens.md` into a CSS custom properties block:
```css
:root {
  /* Background scale */
  --fg-bg-base: #080C12;
  --fg-bg-surface: #0D1520;
  --fg-bg-raised: #111927;
  --fg-bg-overlay: #162032;
  --fg-border-subtle: #1E2D42;
  --fg-border-strong: #2D4460;
  /* Azure primary */
  --fg-azure-dim: #0558A0;
  --fg-azure: #0078D4;
  --fg-azure-hover: #3BA3E8;
  --fg-azure-neon: #4DCFFF;
  /* Ember (quota / spend) */
  --fg-ember-soft: #FFB347;
  --fg-ember: #F58025;
  --fg-ember-hot: #FF5400;
  /* Text */
  --fg-text-primary: #E8EEF5;
  --fg-text-secondary: #A0B0C0;
  --fg-text-muted: #6B7E94;
  --fg-text-disabled: #3A4F63;
  /* Semantic */
  --fg-success: #22C55E;
  --fg-info: #38BDF8;
}
```

**3. Starlight theme override (`docs-site/src/styles/starlight-theme.css`):**  
Map `--fg-*` → Starlight's `--sl-*` variables so the template renders with the correct palette without editing any Starlight internals:
```css
:root {
  /* Accent (Azure) */
  --sl-color-accent-low:  var(--fg-azure-dim);
  --sl-color-accent:      var(--fg-azure);
  --sl-color-accent-high: var(--fg-azure-neon);

  /* Surface / background */
  --sl-color-black:  var(--fg-bg-base);
  --sl-color-gray-6: var(--fg-bg-surface);
  --sl-color-gray-5: var(--fg-bg-raised);
  --sl-color-gray-4: var(--fg-bg-overlay);
  --sl-color-gray-3: var(--fg-border-subtle);
  --sl-color-gray-2: var(--fg-border-strong);

  /* Text */
  --sl-color-white:  var(--fg-text-primary);
  --sl-color-gray-1: var(--fg-text-secondary);

  /* Semantic */
  --sl-color-green:  var(--fg-success);
  --sl-color-blue:   var(--fg-info);
  --sl-color-orange: var(--fg-ember);
  --sl-color-red:    var(--fg-ember-hot);

  /* Typography */
  --sl-font:      'Inter', system-ui, -apple-system, sans-serif;
  --sl-font-mono: 'Monaspace Argon', 'Fira Code', monospace;
}
```

**4. Typography (`docs-site/src/styles/typography.css`):**  
Copy `content/typography.css` verbatim, updating the `@font-face` src URLs to point to `docs-site/public/fonts/` (i.e., `/foundry-gate/fonts/` for the GitHub Pages base path):
```css
@font-face {
  font-family: 'Inter';
  src: url('/foundry-gate/fonts/Inter-Regular.woff2') format('woff2');
  ...
}
```
Provide download instructions in a `docs-site/public/fonts/README.md`: Inter from Google Fonts / Fontsource, Monaspace Argon from [github.com/githubnext/monaspace](https://github.com/githubnext/monaspace/releases). Add `public/fonts/*.woff2` to `.gitignore` — fonts are fetched at build time, not committed.

**5. Favicon:**  
Export the gate-portal glyph from the logo as `docs-site/public/favicon.svg` — a minimal single-color SVG of the square-frame-with-converging-lines mark at 32×32, stroke: `--fg-azure-neon`, fill: none.

**6. Deploy workflow (`docs.yml`):**  
Path triggers: `docs-site/**` and `content/**` (changes to brand source files should trigger a rebuild). Font files are fetched via an `npm run fetch-fonts` script (or Fontsource npm packages) before build.

Files expected to be created or modified:
- `docs-site/astro.config.mjs`
- `docs-site/package.json`
- `docs-site/src/assets/logo.png` (copy of `content/logo-transparent.png`)
- `docs-site/public/favicon.svg`
- `docs-site/public/og-logo.png` (copy of `content/logo.png`)
- `docs-site/public/icons.png` (copy of `content/icons.png`)
- `docs-site/public/fonts/README.md`
- `docs-site/src/styles/tokens.css`
- `docs-site/src/styles/typography.css`
- `docs-site/src/styles/starlight-theme.css`
- `docs-site/src/content/docs/index.mdx`
- `.github/workflows/docs.yml` (add `content/**` to path triggers)

### Add motion and subtle animation layer (`docs-site/src/styles/motion.css`) (#59-motion)
> Implemented as part of sub-issue #59 — add after the base theme is working.

The docs site should feel alive but never distracting. All motion respects `prefers-reduced-motion`. Target: elements feel like they arrive, not like they snap into place.

**Page load — staggered reveal:**
```css
@keyframes fg-rise {
  from { opacity: 0; transform: translateY(10px); }
  to   { opacity: 1; transform: translateY(0); }
}

/* Starlight renders content in .sl-markdown-content — stagger children */
.sl-markdown-content > * {
  animation: fg-rise 0.4s cubic-bezier(0.16, 1, 0.3, 1) both;
}
.sl-markdown-content > *:nth-child(1)  { animation-delay: 0.05s; }
.sl-markdown-content > *:nth-child(2)  { animation-delay: 0.10s; }
.sl-markdown-content > *:nth-child(3)  { animation-delay: 0.15s; }
/* ... up to nth-child(8) at 0.40s — beyond that no delay */
```

**Sidebar nav links — hover glow:**
```css
.sidebar-content a {
  transition: color 0.2s ease, border-left-color 0.2s ease;
  border-left: 2px solid transparent;
}
.sidebar-content a:hover {
  color: var(--fg-azure-neon);
  border-left-color: var(--fg-azure-neon);
}
.sidebar-content [aria-current="page"] {
  border-left-color: var(--fg-azure);
  color: var(--fg-text-primary);
}
```

**Code blocks — subtle accent bar:**
```css
.expressive-code pre {
  border-left: 3px solid var(--fg-azure-dim);
  transition: border-left-color 0.25s ease;
}
.expressive-code pre:hover {
  border-left-color: var(--fg-azure-neon);
}
```

**Callout/aside cards — soft entrance:**
```css
.starlight-aside {
  animation: fg-rise 0.35s cubic-bezier(0.16, 1, 0.3, 1) 0.2s both;
  transition: box-shadow 0.2s ease;
}
.starlight-aside:hover {
  box-shadow: 0 0 0 1px var(--fg-border-strong);
}
```

**Hero (index.mdx) — optional ember-glow on the CTA button:**
```css
.hero .action.primary {
  background: var(--fg-azure);
  transition: background 0.2s ease, box-shadow 0.2s ease;
}
.hero .action.primary:hover {
  background: var(--fg-azure-hover);
  box-shadow: 0 0 16px 2px color-mix(in srgb, var(--fg-azure-neon) 40%, transparent);
}
```

**Reduced-motion guard (wraps the entire file):**
```css
@media (prefers-reduced-motion: reduce) {
  *, *::before, *::after {
    animation-duration: 0.01ms !important;
    transition-duration: 0.01ms !important;
  }
}
```

Add `'./src/styles/motion.css'` to the `customCss` array in `astro.config.mjs` after the theme override file.

Files expected to be created or modified:
- `docs-site/src/styles/motion.css`
- `docs-site/astro.config.mjs` (add motion.css to customCss)

### Write fork guide as Astro content (#56)
`docs-site/src/content/docs/getting-started/fork-guide.md`. Checklist-driven; covers Entra App Registration, APIM product + policy setup (`llm-emit-token-metric`, `azure-openai-token-limit`), GitHub OIDC credentials, Bicep deploy, and first CI run. Each step has exact CLI commands, expected output, and a troubleshooting note. Completable in under two hours.

Files expected to be created or modified:
- `docs-site/src/content/docs/getting-started/fork-guide.md`

### Write developer CLI quickstart, architecture overview, configuration reference, and README (#57)
- `getting-started/cli-setup.md` — how a developer configures Claude Code or Codex CLI with their APIM key and gateway URL (copy-paste snippets, curl verification, available model names)
- `architecture/overview.md` — Mermaid system diagram, five-level quota resolution flowchart, APIM key lifecycle state machine, Azure Functions data flow
- `reference/configuration.md` — all eight `SystemConfiguration` keys and all `appsettings.json` / env vars
- `README.md` — one-paragraph description, features list, link to live GitHub Pages site, build/license badges

Files expected to be created or modified:
- `docs-site/src/content/docs/getting-started/cli-setup.md`
- `docs-site/src/content/docs/architecture/overview.md`
- `docs-site/src/content/docs/reference/configuration.md`
- `README.md`

## Verification
- [ ] `npm run build` in `docs-site/` produces `dist/` with no errors or warnings
- [ ] Site is accessible at `kolatts.github.io/foundry-gate` after `docs.yml` runs
- [ ] Logo renders in Starlight header (`replacesTitle: true` hides the text title)
- [ ] Sidebar uses the `--fg-azure` accent color, not Starlight's default purple
- [ ] Code blocks render in Monaspace Argon
- [ ] Body text renders in Inter
- [ ] `content/**` path change triggers a docs rebuild in CI
- [ ] Mermaid diagrams render without errors
