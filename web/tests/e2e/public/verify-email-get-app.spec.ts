/**
 * "/verify-email?token=…" after a successful verification: a client-only
 * account lands on the get-the-app state, a coach on the profile step. The
 * verify call is faked with page.route, so the token is never consumed.
 *
 * Runs under the `public` project — signed-out context, like account-pages.spec.ts.
 */
import { test, expect } from '@playwright/test';
import type { Page } from '@playwright/test';

test.beforeEach(async ({ page }) => {
  await page.addInitScript("window.localStorage.setItem('lang', 'en');");
});

async function fakeVerify(
  page: Page,
  body: { message: string; email: string; firstName: string; roles: string[] }
): Promise<string[]> {
  const calls: string[] = [];
  await page.route('**/auth/verify-email', async (route) => {
    calls.push(route.request().url());
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  });
  return calls;
}

test('a verified client sees the get-the-app state, with no link into the portal, and verify fires once', async ({
  page,
}) => {
  const calls = await fakeVerify(page, {
    message: 'Email verified.',
    email: 'jan.novak@email.cz',
    firstName: 'Jan',
    roles: ['Client'],
  });

  await page.goto('/verify-email?token=fake-client-token');

  await expect(page.getByRole('heading', { name: 'Email verified' })).toBeVisible();
  await expect(page.getByText('Welcome to Form Up, Jan.')).toBeVisible();
  await expect(page.getByText('jan.novak@email.cz')).toBeVisible();
  await expect(page.getByRole('list', { name: 'Registration progress' })).toBeVisible();
  // No store URLs are configured in the harness, so both buttons render disabled.
  await expect(page.getByRole('button', { name: /App Store/ })).toBeVisible();
  await expect(page.getByRole('button', { name: /Google Play/ })).toBeVisible();

  await expect(page.locator('a[href="/login"], a[href="/clients"]')).toHaveCount(0);
  expect(calls).toHaveLength(1);
});

test('a verified client without a first name gets the name-less welcome line', async ({ page }) => {
  await fakeVerify(page, {
    message: 'Email verified.',
    email: 'anon@example.com',
    firstName: '',
    roles: ['Client'],
  });

  await page.goto('/verify-email?token=fake-client-token');

  await expect(page.getByText('Welcome to Form Up.', { exact: false })).toBeVisible();
  await expect(page.getByText('Welcome to Form Up,')).toHaveCount(0);
});

function requireSeedPassword(): string {
  const password = process.env['QA_SEED_PASSWORD'];
  if (!password) {
    throw new Error(
      'QA_SEED_PASSWORD is not set. Copy .env.test.example to .env.test, fill it in, ' +
        'then source it or load it with dotenv before running Playwright.',
    );
  }
  return password;
}

test('a verified coach without a session is told to sign in: the button leads to /login, then the plan step', async ({ page }) => {
  const password = requireSeedPassword();
  await fakeVerify(page, {
    message: 'Email verified.',
    email: 'qa.trainer@formup.test',
    firstName: 'Jan',
    roles: ['Trainer'],
  });

  await page.goto('/verify-email?token=fake-coach-token');
  await page.getByRole('link', { name: 'Sign in to choose your plan' }).click();

  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  await dialog.getByLabel('Email').fill('qa.trainer@formup.test');
  await dialog.getByLabel('Password').fill(password);
  await dialog.getByRole('button', { name: 'Sign in' }).click();

  await expect(page).toHaveURL(/\/register\/plan$/);
});

test('a verified coach without a session sees the profile step with sign-in copy on both actions', async ({ page }) => {
  const calls = await fakeVerify(page, {
    message: 'Email verified.',
    email: 'coach@example.com',
    firstName: 'Jan',
    roles: ['Trainer'],
  });

  await page.goto('/verify-email?token=fake-coach-token');

  await expect(page.getByRole('heading', { name: 'Email verified' })).toBeVisible();
  await expect(page.getByText('Welcome to Form Up, Jan. Your account is ready.')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Sign in to choose your plan' })).toHaveAttribute(
    'href',
    '/login?returnTo=%2Fregister%2Fplan'
  );
  await expect(page.getByRole('link', { name: 'Sign in and start on Free for now' })).toHaveAttribute(
    'href',
    '/login'
  );
  await expect(page.getByRole('link', { name: 'Choose your plan', exact: true })).toHaveCount(0);
  await expect(page.getByText('Invite your first client')).toBeVisible();
  await expect(page.getByRole('list', { name: 'Registration progress' })).toBeVisible();

  await expect(page.getByRole('button', { name: /App Store|Google Play/ })).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'Sign in', exact: true })).toHaveCount(0);
  expect(calls).toHaveLength(1);
});
