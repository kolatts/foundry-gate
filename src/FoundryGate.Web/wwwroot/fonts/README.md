# Fonts

`wwwroot/css/typography.css` `@font-face`s expect these files here (not committed —
binary font files don't belong in git history):

```
Inter-Regular.woff2
Inter-Medium.woff2
Inter-SemiBold.woff2
MonaspaceArgon-Regular.woff2
MonaspaceArgon-Medium.woff2
```

## Fetching them

**Inter** — via [Fontsource](https://fontsource.org/fonts/inter) or directly from
[rsms.me/inter](https://rsms.me/inter/) (OFL). Only the Regular/Medium/SemiBold static
`woff2` weights are needed.

**Monaspace Argon** — from the
[GitHub Next Monaspace release](https://github.com/githubnext/monaspace/releases) (OFL).
Only the Regular/Medium static `woff2` weights are needed.

CI fetches these at build time (see the `ui.yml` deploy workflow, spec §10.3) so they
never need to be committed. For local development, download the five files above into
this directory; until then, `typography.css`'s `@font-face` rules simply fail to match
and the browser falls back to the `system-ui` / monospace stacks already declared in
`--fg-font-ui` / `--fg-font-mono` — the app renders correctly either way, just not in the
brand typeface.
