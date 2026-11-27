/**
 * Register form flows ("/register") — #1058 phase 4.
 *
 * Runs under the `public` project (playwright.config.ts) — no `setup`
 * dependency, no `storageState`, genuinely signed-out context, matching
 * entry.spec.ts. globalSetup's POST /test/reset still runs once before the
 * whole Playwright invocation, so the QA seed baseline (including
 * qa.trainer@formup.test, used below as a known-duplicate email)
 * is guaranteed present.
 */
import { test, expect, type Page } from '@playwright/test';

const DUPLICATE_EMAIL = 'qa.trainer@formup.test';
const VALID_PASSWORD = 'CorrectHorse9';

// The app defaults to 'cs' when no 'lang' key exists in localStorage (see
// src/i18n/index.ts). Force English so this spec's text assertions are
// stable regardless of that default — mirrors entry.spec.ts.
test.beforeEach(async ({ page }) => {
  await page.addInitScript("window.localStorage.setItem('lang', 'en');");
});

/**
 * A fresh, run-unique email per registration attempt. globalSetup resets the
 * DB once per Playwright *invocation*, not per spec — a hardcoded address
 * would collide with itself on a retry or a second local run against the
 * same still-up compose harness (design-review finding, #1058).
 */
function uniqueEmail(prefix: string): string {
  return `${prefix}-${Date.now()}-${Math.floor(Math.random() * 1_000_000)}@example.com`;
}

async function fillValidRegisterForm(
  page: import('@playwright/test').Page,
  email: string
): Promise<void> {
  await page.getByRole('button', { name: /^Personal trainer/ }).click();
  await page.getByLabel('First name').fill('Ada');
  await page.getByLabel('Last name').fill('Lovelace');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password', { exact: true }).fill(VALID_PASSWORD);
}

// Measure only once every animation has settled, so boundingBox() heights are
// not skewed by a transition still in flight.
async function settleAnimations(page: Page): Promise<void> {
  await page.evaluate('Promise.all(document.getAnimations().map((animation) => animation.finished))');
}

test('/register is a full page that deep-links, and Sign in opens the landing dialog', async ({
  page,
}) => {
  // Regression: /register used to be a dialog route under EntryPage. It is its
  // own page now — no dialog, no landing sections — and the header Sign in
  // link goes to /login, which opens the landing page's sign-in dialog.
  await page.goto('/register');
  await page.waitForLoadState('networkidle');

  await expect(page).toHaveURL(/\/register$/);
  await expect(page.getByRole('heading', { name: 'Join our community' })).toBeVisible();
  await expect(page.getByLabel('First name')).toBeVisible();
  await expect(page.getByRole('dialog')).toHaveCount(0);

  await page.getByRole('link', { name: 'Sign in' }).click();
  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByRole('dialog').getByText('Welcome back')).toBeVisible();

  await page.getByRole('dialog').getByRole('link', { name: 'Create account' }).click();
  await expect(page).toHaveURL(/\/register$/);
  await expect(page.getByRole('heading', { name: 'Join our community' })).toBeVisible();
});

test('the client card is exclusive with the coach cards and adds a required health-data consent', async ({
  page,
}) => {
  await page.goto('/register');

  const trainer = page.getByRole('button', { name: /^Personal trainer/ });
  const nutritionist = page.getByRole('button', { name: /^Nutritionist/ });
  const client = page.getByRole('button', { name: /^I train for myself/ });

  // Coach roles combine; a coach sees only the terms checkbox.
  await trainer.click();
  await nutritionist.click();
  await expect(trainer).toHaveAttribute('aria-pressed', 'true');
  await expect(nutritionist).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByRole('checkbox')).toHaveCount(1);

  // Client replaces them and reveals the health-data consent.
  await client.click();
  await expect(client).toHaveAttribute('aria-pressed', 'true');
  await expect(trainer).toHaveAttribute('aria-pressed', 'false');
  await expect(nutritionist).toHaveAttribute('aria-pressed', 'false');
  await expect(page.getByRole('checkbox')).toHaveCount(2);

  await page.getByLabel('First name').fill('Ada');
  await page.getByLabel('Last name').fill('Lovelace');
  await page.getByLabel('Email').fill(uniqueEmail('client-consent'));
  await page.getByLabel('Password', { exact: true }).fill(VALID_PASSWORD);

  const submit = page.getByRole('button', { name: 'Create account' });
  await page.getByRole('checkbox', { name: /Terms/ }).check();
  await expect(submit).toBeDisabled();
  await page.getByRole('checkbox', { name: /health data/ }).check();
  await expect(submit).toBeEnabled();

  // Back to a coach role: the hidden consent goes away and no longer counts.
  await trainer.click();
  await expect(trainer).toHaveAttribute('aria-pressed', 'true');
  await expect(client).toHaveAttribute('aria-pressed', 'false');
  await expect(page.getByRole('checkbox')).toHaveCount(1);
  await expect(submit).toBeEnabled();
});

test('submit is disabled on an empty form and enables the instant consent is ticked, with no blur/Tab afterward', async ({
  page,
}) => {
  // Regression: the submit button used to stay locked until focus left the
  // consent checkbox — RHF's default 'onBlur' mode only revalidates on a
  // field's OWN blur, and the checkbox's change handler never fired one, so
  // ticking consent last (the natural order) never unlocked the button
  // without an extra, unnecessary Tab/click. Fixed by wiring field.onBlur on
  // every Controller field plus switching to 'onTouched' (#1058, three
  // rounds — see RegisterForm.tsx's `mode: 'onTouched'` comment).
  //
  // Interaction order below is the regression itself and must not change:
  // role -> names -> email -> password -> consent LAST, with NO blur, Tab,
  // or click after ticking consent. The button must already be enabled at
  // that exact point.
  await page.goto('/register');

  const submit = page.getByRole('button', { name: 'Create account' });
  await expect(submit).toBeDisabled();

  await fillValidRegisterForm(page, uniqueEmail('unlock-check'));
  await expect(submit).toBeDisabled();

  await page.getByRole('checkbox').check();

  await expect(submit).toBeEnabled();
});

test('arriving at /register via a client-side navigation does not swallow the first click on the consent checkbox', async ({
  page,
}) => {
  // Regression: focusing the register form's empty first-name field made the
  // next click blur it first — RegisterForm runs React Hook Form in
  // `mode: 'onTouched'`, so that rendered "Enter your first name.", grew the
  // form and moved the checkbox out from under the pointer. Nothing may take
  // focus on arrival. A cold `page.goto('/register')` never focuses anything,
  // so this arrives from the sign-in dialog's Create account link instead.
  await page.goto('/login');
  await page.getByRole('dialog').getByRole('link', { name: 'Create account' }).click();
  await expect(page).toHaveURL(/\/register$/);
  await expect(page.getByLabel('First name')).toBeVisible();
  await settleAnimations(page);

  const form = page.locator('form');
  const before = await form.boundingBox();

  const consent = page.getByRole('checkbox');
  await consent.click();

  await expect(consent).toBeChecked();
  await expect(page.getByText('Enter your first name.')).not.toBeVisible();

  const after = await form.boundingBox();
  expect(after?.height).toBeCloseTo(before?.height ?? 0, 0);
});

test("a forced click on the disabled submit button does not change the register form's height", async ({
  page,
}) => {
  // Regression: RegisterForm's onSubmit handler guards `!isValid` specifically
  // to stop a submission forced past the disabled attribute from running
  // handleSubmit's full-schema validation, which would otherwise dump every
  // field's error at once and grow the form — the "six errors at once" bulk
  // dump this whole rework exists to prevent.
  await page.goto('/register');
  await settleAnimations(page);

  const form = page.locator('form');
  const before = await form.boundingBox();

  await page.getByRole('button', { name: 'Create account' }).click({ force: true });

  const after = await form.boundingBox();
  // toBeCloseTo, not toBe — getBoundingClientRect() has returned
  // sub-pixel-different floats (e.g. 560.5 vs 560.5000152587891) for an
  // otherwise visually identical layout; a whole extra error line would
  // move this by several pixels, so 1px of tolerance is still a real guard.
  expect(after?.height).toBeCloseTo(before?.height ?? 0, 0);
});

test('a successful registration reaches the check-your-email state, shows the address, and stays on /register', async ({
  page,
}) => {
  const email = uniqueEmail('register-success');

  await page.goto('/register');
  await fillValidRegisterForm(page, email);
  await page.getByRole('checkbox').check();
  await page.getByRole('button', { name: 'Create account' }).click();

  await expect(page.getByText('Check your email')).toBeVisible();
  await expect(page.getByText(email)).toBeVisible();
  await expect(page).toHaveURL(/\/register$/);

  // The resend wait is client-side and starts on arrival.
  await expect(page.getByRole('button', { name: 'Resend email' })).toBeDisabled();
  await expect(page.getByText(/available in \d:\d\d/)).toBeVisible();

  // "Change email" edits the same unverified account in place (real backend:
  // login accepts it, PUT /auth/email moves it) and restarts the resend wait.
  // Error and branch cases live in ChangeEmailForm.test.tsx.
  const newEmail = uniqueEmail('register-changed');
  await page.getByRole('button', { name: 'Change email' }).click();
  await page.getByLabel('New email address').fill(newEmail);
  await page.getByRole('button', { name: 'Send new link' }).click();

  await expect(page.getByRole('heading', { name: 'Check your email' })).toBeVisible();
  await expect(page.getByText(newEmail)).toBeVisible();
  await expect(page.getByText(email)).toHaveCount(0);
  await expect(page.getByText(/available in \d:\d\d/)).toBeVisible();
});

test('a duplicate email surfaces a localized message and never the raw "already taken" string', async ({
  page,
}) => {
  // Regression: RegisterEndpoint has no machine-readable code for a
  // duplicate email — it pipes ASP.NET Identity's raw English reason
  // ("Email 'x' is already taken.") straight through. RegisterForm must
  // pattern-match that reason and render a translated field error instead
  // of leaking the untranslated string into the DOM (design-review finding,
  // #1058).
  await page.goto('/register');
  await fillValidRegisterForm(page, DUPLICATE_EMAIL);
  await page.getByRole('checkbox').check();
  await page.getByRole('button', { name: 'Create account' }).click();

  await expect(page.getByText('This email is already registered.')).toBeVisible();
  await expect(page.locator('body')).not.toContainText('already taken');
});
