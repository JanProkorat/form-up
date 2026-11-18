---
paths:
  - "web/**"
description: Which web test layer proves what — Vitest component tests by default, Playwright E2E only for real-browser behaviour and one real-backend journey per feature
---

# Testing Rules (web)

> **Descriptive.** Written after `PLAN-web-test-layer.md` (#2223, #2225,
> #2230, #2232). Every web test used to be a 5–8 s Playwright test, so TDD
> grew E2E by every epic. The backend already works this way:
> [`testing.md`](testing.md) — the cheapest layer that can prove it, and once.

## Which layer proves what

**Vitest component test — the default.** `web/src/**/*.test.tsx`, jsdom +
Testing Library + msw, `npm run test`. Use it for:

- loading, empty, error and retry states
- validation, dialogs, menus, toggles, focus return
- copy and numbers computed from a payload
- the request a UI action sends (body, how often), and requests it must not send
- URL and search-param state, including legacy URL fallbacks

**Playwright E2E — only for these.** `web/tests/e2e`, the Docker harness.

- Real-browser behaviour: layout, computed CSS and geometry, animations,
  scroll position, `@dnd-kit` drag, new tabs, storage across a reload,
  SignalR push.
- One real-backend journey per feature: the main path, writing for real.
- What only the real server can prove: anti-enumeration, two APIs agreeing
  on seeded data, server-side validation that the UI doesn't repeat.

## Write the component test first

TDD on a web change starts with a Vitest test. Add an E2E test only for a
case in the E2E list above, and name which case in the PR body.

## Moving a test

- Delete it from E2E in the same PR. Both versions in the tree is double
  coverage.
- Assert the same outcome. If jsdom forces a narrower assertion (no
  stylesheets, no images, no layout), name it in the PR body, or keep the
  test in E2E.
- A Playwright project left empty is removed from
  `web/tests/e2e/ci-shards.json`. `ci-shards-check.mjs` (run from `web/`)
  fails on a missing or doubled test.

## Component-test conventions

- Render with `renderWithProviders` (`web/src/test/render.tsx`): StrictMode
  like `main.tsx`, a fresh `QueryClient` without retries, a memory data
  router. A page test mounts the real page on its route; per-page fakes live
  next to it in `web/src/test/` (`clientDetail.tsx`, `clientsList.tsx`,
  `inbox.tsx`).
- Fake HTTP with msw (`web/src/test/msw/`). An unhandled request fails the
  test after it runs (`setup.ts` records them; msw alone only fails the
  request, which a page may swallow). Serve a page's background loads in its
  helper, not per test. Type fake payloads with `src/api/generated.ts` types,
  so an API shape change breaks the typecheck.
- Don't `vi.mock` `src/api/*` or axios: the real axios layer, interceptors
  included, must run.
- Query by role and accessible name. Copy is English (`lang` is pinned).
- Scope a `findBy*` to the element you mean when the same text shows twice
  (a toast and an inline message). It retries on "found multiple elements"
  until one disappears, then passes on the wrong one (#2235).
- Pin "now" when an assertion depends on the date.
- Don't lower the time limits in `vitest.config.ts` (15 s per test) or
  `setup.ts` (5 s async wait). Page tests failed under load at the defaults
  (#2232).
- Add a jsdom stub to `setup.ts` only when a test hits the gap.

## No double coverage

One behaviour, one test, at the cheapest layer that proves it. Two tests
proving the same thing, in one layer or across layers, are merged or one is
deleted. `pr-reviewer` checks this (review checklist item 15).

## Verify

`react-verify` runs `npm run test` with the build and lint. Web CI runs it as
"Unit and component tests". E2E runs sharded in `.github/workflows/e2e.yml`;
the groups are in `web/tests/e2e/ci-shards.json`.
