Generate a complete design token file for a developer tool called Foundry Gate.

Background scale (darkest to lightest):
--fg-bg-base: #080C12       page canvas
--fg-bg-surface: #0D1520    card / panel
--fg-bg-raised: #111927     elevated card
--fg-bg-overlay: #162032    dropdown / modal
--fg-border-subtle: #1E2D42 default border
--fg-border-strong: #2D4460 hover / active border

Azure primary scale:
--fg-azure-dim: #0558A0     disabled state
--fg-azure: #0078D4         primary action
--fg-azure-hover: #3BA3E8   hover
--fg-azure-neon: #4DCFFF    focus ring / accent

PNC ember scale (semantic: quota / spend / alerts):
--fg-ember-soft: #FFB347    approaching limit (warn)
--fg-ember: #F58025         at limit (danger)
--fg-ember-hot: #FF5400     over limit (critical)

Text scale:
--fg-text-primary: #E8EEF5
--fg-text-secondary: #A0B0C0
--fg-text-muted: #6B7E94
--fg-text-disabled: #3A4F63

Semantic (non-quota):
--fg-success: #22C55E       model online / approved
--fg-info: #38BDF8          neutral callout

Output as: CSS custom properties block, TypeScript const object, and a Tailwind theme extension. All three formats.