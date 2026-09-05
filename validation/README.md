# Validation evidence

Captured evidence from **real deployed FoundryGate gateways** — one file per spin-up /
test / spin-down cycle, named `<date>-gateway-cycle.md`. The docs site links to this
folder (not to individual files) from the landing page and from
[Architecture at a glance](../docs-site/src/content/docs/architecture/diagram.mdx), so a
new cycle's write-up is reachable the moment it is committed.

A cycle write-up records, verbatim and unedited:

- the responses a developer's tooling actually got — the `200` with its
  `x-fg-remaining-quota` / `x-fg-remaining-tpm` / `x-fg-tokens-consumed` headers, the
  `429` with its `Retry-After`, the `403` when the monthly quota is spent or the model is
  not on the tier;
- what was measured (timings, token counts) and how;
- what was deployed at the time — environment, regions, tiers, model deployments — so a
  result can be tied to the shape of the gateway that produced it.

Nothing in here is illustrative or reconstructed. Demonstrative examples belong on the
docs site, where they are labelled as such (CLAUDE.md docs invariant 1); this folder is
only for what a real gateway did.

The automation that produces these cycles is tracked in
[#219](https://github.com/kolatts/foundry-gate/issues/219).
