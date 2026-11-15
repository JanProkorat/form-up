/**
 * Registry of this project's custom Tailwind theme tokens (declared in
 * `index.css`'s `@theme inline` block) that tailwind-merge's default config
 * does not recognise by shape and must be told about explicitly via
 * `extendTailwindMerge({ extend: { theme: { ... } } })` in `utils.ts`.
 *
 * `scripts/check-theme-tokens.mjs` parses `index.css` at build time and
 * fails if a token in one of these namespaces is missing here (or if
 * an entry here no longer exists in `index.css`) — keep both in sync
 * whenever the theme block changes. Only the token *suffix* (the part
 * after `--text-`, `--shadow-`, etc.) goes in these arrays, matching how
 * tailwind-merge's own theme scale arrays are shaped.
 */

/** `--text-*` — 34 keys. Falls through to tailwind-merge's colour-scale
 * catch-all without this: every one of these class names would otherwise be
 * silently deleted as a "text colour" conflict (#1078). */
export const TEXT_SIZE_TOKENS = [
  'title',
  'body',
  'label',
  'tick',
  'caption',
  'meta',
  'copy',
  'subhead',
  'lede',
  'card-title',
  'stat',
  'display',
  'price',
  'panel-title',
  'auth-title',
  'home-hero',
  'home-heading',
  'home-banner',
  'home-lead',
  'home-sub',
  'home-feature-title',
  'home-step-title',
  'home-body',
  'home-copy',
  'home-meta',
  'home-subhead',
  'home-wordmark',
  'home-wordmark-sm',
  'mockup-xs',
  'mockup-badge',
  'mockup-notif',
  'mockup-figure',
  'mockup-title',
  'mockup-greeting',
  'mockup-weight',
] as const;

/** `--shadow-*` — same delete-the-class failure mode as text sizes, via
 * tailwind-merge's `shadow-color` catch-all. */
export const SHADOW_TOKENS = ['dialog', 'sheet', 'popover', 'card', 'selection-bar', 'panel', 'raised', 'raised-hover'] as const;

/** `--radius-*` — only names outside tailwind-merge's t-shirt scale need
 * listing; without it `rounded-field` is never conflict-resolved against a
 * caller's `rounded-*`. */
export const RADIUS_TOKENS = ['field', 'thumb', 'tile', 'card', 'glass', 'phone', 'phone-hero'] as const;

/** `--spacing-*` — unknown to tailwind-merge without this (not deleted,
 * just never conflict-resolved). */
export const SPACING_TOKENS = [
  'badge-min',
  'swatch',
  'search',
  'drawer',
  'drawer-wide',
  'panel',
] as const;

/** `--tracking-*` — same unknown-class shape as spacing. */
export const TRACKING_TOKENS = [
  'label',
  'eyebrow',
  'wordmark',
  'wordmark-sm',
  'heading',
  'hero',
  'badge',
  'caps',
] as const;

/** `--animate-*` — same unknown-class shape as spacing. */
export const ANIMATE_TOKENS = [
  'content-in',
  'message-in-left',
  'message-in-right',
  'message-in-up',
  'sheet-in-right',
  'sheet-out-right',
  'sheet-in-left',
  'sheet-out-left',
  'sheet-in-top',
  'sheet-out-top',
  'sheet-in-bottom',
  'sheet-out-bottom',
  'sheet-overlay-in',
  'sheet-overlay-out',
  'dialog-in',
  'dialog-out',
  'dialog-overlay-in',
  'dialog-overlay-out',
  'popper-in-top',
  'popper-out-top',
  'popper-in-bottom',
  'popper-out-bottom',
  'popper-in-left',
  'popper-out-left',
  'popper-in-right',
  'popper-out-right',
  'toast-in',
  'toast-out',
] as const;
