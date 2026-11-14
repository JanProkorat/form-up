/**
 * Playwright configuration for the trainer web portal E2E test suite.
 *
 * Architecture:
 *   - globalSetup calls POST /test/reset on the compose harness before any
 *     spec runs, so the DB starts from the deterministic seeded baseline
 *     (QaSeedRunner). Each CI run is idempotent; the previous run's data
 *     cannot bleed into the next.
 *   - A `setup` project runs auth.setup.ts and produces .auth/<role>.json
 *     storage-state files for each QA role.
 *   - The `trainer`, `client`, `nutritionist`, and `coach` projects depend on `setup`
 *     and pick up the corresponding storage-state file so no spec ever visits
 *     the login page at runtime.
 *
 * Compose harness:
 *   Boot with `scripts/test-env up` (or `npm run e2e:up`) before running specs.
 *   The harness API lives at E2E_API_URL (default https://localhost:5101 for
 *   host runs; the qa-playwright container overrides to https://api:8080).
 *   The Vite dev server (dev:e2e) proxies all API paths to that URL.
 *
 * baseURL:
 *   Host runs default to http://localhost:5173 (Vite dev server). The
 *   dockerised qa-playwright container overrides baseURL to http://web:5173
 *   via PLAYWRIGHT_BASE_URL — the `web` service is on the same qa-net
 *   network so DNS resolution works inside the browser context.
 *
 * TLS note: the compose harness uses a self-signed dev cert. Set
 * `ignoreHTTPSErrors: true` on the browser context so fetch/XHR calls inside
 * the SPA succeed. The global-setup Node https request already uses
 * rejectUnauthorized:false for the same reason.
 *
 * Env loading:
 *   .env.test (gitignored) in the repo root supplies QA_SEED_PASSWORD and
 *   JWT_SECRET to auth.setup.ts and global-setup.ts. dotenv.config does NOT
 *   override existing shell exports, so local devs and CI can still inject
 *   values via their own environment. Copy .env.test.example → .env.test and
 *   fill it in for local runs — do NOT source it manually first.
 *
 *   New spec authors: the globalSetup resets the database via POST /test/reset
 *   before any spec project runs. Add new specs to tests/e2e/<role>/ and rely
 *   on this reset rather than setting up data manually to avoid inter-run
 *   data contamination.
 */

import { defineConfig, devices } from '@playwright/test';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import dotenv from 'dotenv';

// ESM-safe __dirname substitute (package.json has "type":"module").
const __dirname = path.dirname(fileURLToPath(import.meta.url));

// Load QA_SEED_PASSWORD (and any other test-only vars) from .env.test in the
// repo root. dotenv silently no-ops when the file is absent (CI sets the vars
// via shell env before this runs). Does NOT override already-set env vars, so
// shell exports and GitHub Actions secrets always win.
dotenv.config({ path: path.resolve(__dirname, '..', '.env.test') });

const E2E_API_URL = process.env['E2E_API_URL'] ?? 'https://localhost:5101';

// Base URL for `page.goto('/...')`. Host runs use the Vite dev server on
// localhost:5173; the dockerised playwright container overrides this to the
// internal `http://web:5173` (qa-net DNS) via the PLAYWRIGHT_BASE_URL env.
const PLAYWRIGHT_BASE_URL =
  process.env['PLAYWRIGHT_BASE_URL'] ?? 'http://localhost:5173';

// Flip to skip the auto-spawned webServer when running inside the qa-playwright
// container — the dockerised `web` service IS the SPA host, so spawning a
// second `npm run dev:e2e` inside the playwright container would clash.
const IN_CONTAINER = process.env['PLAYWRIGHT_IN_CONTAINER'] === 'true';

export default defineConfig({
  testDir: './tests/e2e',

  // Exclusion for quarantined specs (#1051). testMatch below is an
  // UNANCHORED regexp matched against the full path, so a spec under
  // tests/e2e/_legacy/<role>/<name>.spec.ts would still match a project's
  // /<role>\/.+\.spec\.ts/ pattern — quarantined specs are therefore
  // flattened (no role subfolder) so no testMatch pattern can find them.
  // That flattening is what actually holds the quarantine, for every
  // project. This root-level testIgnore is NOT a second independent guard
  // for the projects that set their own `testIgnore` (`trainer`,
  // `nutritionist`): Playwright project config replaces (does not merge
  // with) the root value, so this root setting is inert for them. It only
  // adds real protection for projects that don't set their own testIgnore
  // (`setup`, `trainer-chat`, `nutritionist-chat`).
  testIgnore: /_legacy\//,

  globalSetup: './tests/e2e/global-setup.ts',

  /* Retries: 2 in CI, 0 locally (dev gets immediate feedback) */
  retries: process.env['CI'] ? 2 : 0,

  /* Workers: 4 in CI, undefined (cpu-count) locally. Specs that share mutable
     seeded state run in the single-worker `*-chat` projects below. */
  workers: process.env['CI'] ? 4 : undefined,

  reporter: process.env['CI']
    ? [['list'], ['html', { open: 'never' }]]
    : [['list']],

  use: {
    /* All page navigations use the configured base URL */
    baseURL: PLAYWRIGHT_BASE_URL,

    /* The app talks to the compose harness through the Vite proxy; the
       harness uses a self-signed dev cert, so ignore TLS errors inside
       the browser context. */
    ignoreHTTPSErrors: true,

    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
  },

  projects: [
    // ─── Auth setup — runs first, produces .auth/*.json ──────────────────────
    {
      name: 'setup',
      testMatch: /auth\.setup\.ts/,
      use: {
        ...devices['Desktop Chrome'],
      },
    },

    // ─── Public / unauthenticated specs ───────────────────────────────────────
    // Picks up only tests/e2e/public/**. No `dependencies` on `setup` and no
    // `storageState` — these specs exercise the "/" entry page as a signed-out
    // visitor, including the login form itself, so they must never inherit a
    // token (#1055). globalSetup's POST /test/reset still runs before this
    // project, same as every other project.
    {
      name: 'public',
      testMatch: /public\/.+\.spec\.ts/,
      use: {
        ...devices['Desktop Chrome'],
      },
    },

    // ─── Trainer-scoped specs ─────────────────────────────────────────────────
    // Picks up only tests/e2e/trainer/**. Add new trainer-role specs there so
    // they are never duplicated by the client or nutritionist projects.
    // Each spec should call page.waitForLoadState('networkidle') after
    // page.goto('/dashboard') to let the auth-store's restoreSession()
    // (POST /auth/refresh on mount) complete before asserting page content.
    // The globalSetup calls POST /test/reset before this project runs, so
    // the DB is at the deterministic QA seed baseline.
    //
    // No `storageState` option here (#897): every trainer spec imports the
    // `trainerTest` export from tests/e2e/fixtures/auth.ts, which overrides
    // the built-in `storageState` fixture to mint a fresh, single-use
    // refresh token per test ATTEMPT rather than sharing the one token
    // .auth/trainer.json used to hold across every spec. `dependencies:
    // ['setup']` stays — auth.setup.ts still writes .auth/trainer.json, and
    // the fixture reads it as a template for the non-auth parts of storage
    // state (dark-mode preference, Google cookie). Leaving `storageState`
    // out of this project's `use` is deliberate: a spec that forgot to
    // import `trainerTest` gets an unauthenticated context and fails loudly,
    // instead of silently falling back to the shared on-disk token and
    // reintroducing this bug.
    {
      name: 'trainer',
      dependencies: ['setup'],
      testMatch: /trainer\/.+\.spec\.ts/,
      testIgnore: [
        /trainer\/inbox-attachments\.spec\.ts/,
        /trainer\/price-ended-link\.spec\.ts/,
        /trainer\/(client-detail|clients|inbox-cooperation-events|inbox)\.spec\.ts/,
      ],
      use: {
        ...devices['Desktop Chrome'],
      },
    },

    // ─── Trainer chat specs ───────────────────────────────────────────────────
    // client-detail, clients, inbox-cooperation-events and inbox share the seeded
    // QA Client conversation's read state (inbox marks it read; the others expect
    // its unread state) and look it up by the "QA Client" substring, which
    // QA Client2 also matches. `workers: 1` keeps them serial, in alphabetical
    // file order, as they ran before the suite went parallel.
    {
      name: 'trainer-chat',
      dependencies: ['setup'],
      workers: 1,
      testMatch: /trainer\/(client-detail|clients|inbox-cooperation-events|inbox)\.spec\.ts/,
      use: {
        ...devices['Desktop Chrome'],
      },
    },

    // ─── Trainer-media specs ──────────────────────────────────────────────────
    // inbox-attachments.spec.ts only. Split out of `trainer` (#1096) because it
    // mutates the seeded QA Client conversation's read state, which
    // inbox.spec.ts and clients.spec.ts both depend on staying unread.
    // `dependencies` make it run after every trainer and trainer-chat test.
    {
      name: 'trainer-media',
      dependencies: ['trainer', 'trainer-chat'],
      testMatch: /trainer\/inbox-attachments\.spec\.ts/,
      use: {
        ...devices['Desktop Chrome'],
      },
    },

    // ─── Trainer-ending spec ──────────────────────────────────────────────────
    // price-ended-link.spec.ts only. It ends the seeded QA Ending client's link
    // through the client API, which changes qa.trainer's roster and fires a
    // notification, so it runs after every trainer and trainer-chat test.
    {
      name: 'trainer-ending',
      dependencies: ['trainer', 'trainer-chat'],
      testMatch: /trainer\/price-ended-link\.spec\.ts/,
      use: {
        ...devices['Desktop Chrome'],
      },
    },

    // ─── Client-scoped specs ──────────────────────────────────────────────────
    // Picks up only tests/e2e/client/**. Add new client-role specs there.
    {
      name: 'client',
      dependencies: ['setup'],
      testMatch: /client\/.+\.spec\.ts/,
      use: {
        ...devices['Desktop Chrome'],
        storageState: '.auth/client.json',
      },
    },

    // ─── Nutritionist-scoped specs ────────────────────────────────────────────
    // Picks up only tests/e2e/nutritionist/**. Add new nutritionist-role specs there.
    // food-admin-upload is a container-driven canonical
    // AC flow — its selectors are exercised by `scripts/test-env run <flow>`
    // inside qa-playwright (where the seeded fixtures + dockerised web service
    // are the source of truth). Excluded from host runs to keep regression
    // smoke (trainer/clients.spec.ts) fast and deterministic.
    //
    // No `storageState` option here (#897), for the same reason as the
    // `trainer` project above: every nutritionist spec imports the
    // `nutritionistTest` export from tests/e2e/fixtures/auth.ts, which mints
    // a fresh per-attempt refresh token instead of sharing the one token
    // .auth/nutritionist.json used to hold across all three nutritionist
    // specs. `dependencies: ['setup']` stays for the same reason too.
    {
      name: 'nutritionist',
      dependencies: ['setup'],
      testMatch: /nutritionist\/.+\.spec\.ts/,
      testIgnore: IN_CONTAINER
        ? [
            /nutritionist\/recipe-tags\.spec\.ts/,
            /nutritionist\/(client-detail-meal-plan|client-plan-editor|inbox-cooperation-events)\.spec\.ts/,
          ]
        : [
            /nutritionist\/food-admin-upload\.spec\.ts/,
            /nutritionist\/recipe-tags\.spec\.ts/,
            /nutritionist\/(client-detail-meal-plan|client-plan-editor|inbox-cooperation-events)\.spec\.ts/,
          ],
      use: {
        ...devices['Desktop Chrome'],
      },
    },

    // ─── Coach (both-roles) specs ─────────────────────────────────────────────
    // Picks up only tests/e2e/coach/**: specs for qa.coach (Trainer +
    // Nutritionist, #1311), whose only client is the seeded "QA Duo". Like
    // `trainer` and `nutritionist`, specs import `coachTest` from
    // tests/e2e/fixtures/auth.ts for a per-attempt refresh token, so there is
    // no `storageState` here.
    {
      name: 'coach',
      dependencies: ['setup'],
      testMatch: /coach\/.+\.spec\.ts/,
      use: {
        ...devices['Desktop Chrome'],
      },
    },

    // ─── Admin specs ──────────────────────────────────────────────────────────
    // Picks up only tests/e2e/admin/**. The e2e test data has no admin account
    // yet, so these specs sign in as the QA trainer (`trainerTest`, which needs
    // the `setup` project's `.auth/trainer.json` template), fake the Admin role
    // through a mocked /users/me/roles and mock the admin API they read.
    {
      name: 'admin',
      dependencies: ['setup'],
      testMatch: /admin\/.+\.spec\.ts/,
      use: {
        ...devices['Desktop Chrome'],
      },
    },

    // ─── Nutritionist chat specs ──────────────────────────────────────────────
    // client-detail-meal-plan and inbox-cooperation-events both use the
    // qa.client2 roster: the meal-plan spec matches a loose /QA Client/ row,
    // which the cooperation-events spec's client request changes. `workers: 1`
    // keeps them serial, in alphabetical file order. client-plan-editor is here
    // too: publishing makes its plan the client's newest Active plan, which
    // client-detail-meal-plan reads, so it must run after that spec.
    {
      name: 'nutritionist-chat',
      dependencies: ['setup'],
      workers: 1,
      testMatch: /nutritionist\/(client-detail-meal-plan|client-plan-editor|inbox-cooperation-events)\.spec\.ts/,
      use: {
        ...devices['Desktop Chrome'],
      },
    },

    // ─── Nutritionist recipe-tags spec ────────────────────────────────────────
    // recipe-tags.spec.ts only. Split out of `nutritionist` because
    // ingredients.spec.ts deletes ALL of qa.nutri's food tags and asserts
    // "No tags yet." — a recipe-tag test on the same shared list would race it.
    // `dependencies: ['nutritionist']` makes it run after every nutritionist test.
    {
      name: 'nutritionist-tags',
      dependencies: ['nutritionist'],
      testMatch: /nutritionist\/recipe-tags\.spec\.ts/,
      use: {
        ...devices['Desktop Chrome'],
      },
    },
  ],

  /* Web server: spawn `npm run dev:e2e` automatically — UNLESS we're running
     inside the qa-playwright container, where the `web` service already
     serves the SPA on http://web:5173 over the internal docker network.
     - reuseExistingServer lets local devs keep the server running (faster iteration).
     - In CI (host runner) the server is always spawned fresh for isolation.
     The server must be healthy on :5173 before specs start.
  */
  webServer: IN_CONTAINER
    ? undefined
    : {
        command: 'npm run dev:e2e',
        url: 'http://localhost:5173',
        reuseExistingServer: !process.env['CI'],
        timeout: 60_000,
        // Forward the E2E_API_URL so the Vite proxy knows where to point
        env: {
          E2E_API_URL,
        },
      },
});
