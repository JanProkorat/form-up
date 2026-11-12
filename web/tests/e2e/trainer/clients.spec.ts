/**
 * #1066 — Clients list page regression smoke (tab switch, a filter chip, the
 * invite flow). Path is deliberately under trainer/ — every project in
 * playwright.config.ts matches a role subfolder via testMatch, so a spec at
 * the e2e/ root is collected by no project and silently never runs (see
 * PLAN-1066-clients-page.md §1).
 *
 * Imports `trainerTest`, not the bare `test`: the trainer project
 * deliberately carries no `storageState` (#897), so a spec using the bare
 * `test` export lands on the login page and asserts against the marketing
 * page instead of the authenticated portal.
 */
import { request as apiRequest } from '@playwright/test';
import type { Page, Route } from '@playwright/test';
import { trainerTest as test, coachTest, expect } from '../fixtures/auth';

const STUB_PACKAGE_ID = 'bbbbbbbb-0000-0000-0000-000000001907';
const STUB_FORM_ID = 'bbbbbbbb-0000-0000-0000-000000001930';

/**
 * The e2e coach has payments but no packages, so the drawer's offer pieces are stubbed to hold one package and no
 * discounts. Register before opening the drawer.
 */
async function stubOneCoachPackage(page: Page): Promise<void> {
  const json = (route: Route, body: unknown) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  await page.route(
    (url) => url.pathname === '/payments/packages',
    (route) =>
      json(route, {
        items: [
          {
            packageId: STUB_PACKAGE_ID,
            name: 'Monthly coaching',
            billing: 'Monthly',
            price: 1800,
            currency: 'CZK',
            showOnProfile: true,
            clientCount: 0,
          },
        ],
      }),
  );
  await page.route((url) => url.pathname === '/payments/discounts', (route) => json(route, { items: [] }));
}

test.describe('clients list page', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/clients');
    await page.waitForLoadState('networkidle');
    await expect(page.getByRole('heading', { name: 'Clients' })).toBeVisible();
  });

  test('switching tabs updates the URL and the active tab', async ({ page }) => {
    await expect(page.getByRole('tab', { name: /^All/ })).toHaveAttribute('data-state', 'active');
    const pausedTab = page.getByRole('tab', { name: /No plan/ });
    await pausedTab.click();
    await page.waitForLoadState('networkidle');

    await expect(page).toHaveURL(/tab=Paused/);
    await expect(pausedTab).toHaveAttribute('data-state', 'active');

    // Table settles into either rows or the empty state — never stuck loading.
    await expect(page.getByText('Loading', { exact: false })).toHaveCount(0);
  });

  test('selecting a filter chip updates the URL and marks the chip active', async ({ page }) => {
    // The four chips of the old filter row are gone.
    for (const removed of [/Unread messages/, /No messages/, /New check-ins/, /Missing check-ins/]) {
      await expect(page.getByRole('button', { name: removed })).toHaveCount(0);
    }

    // "Ending soon" is one of the two chips left (All, Ending soon). A zero count
    // disables the chip, which the fixture may produce.
    const endingSoonChip = page.getByRole('button', { name: /Ending soon/ });
    await expect(endingSoonChip).toBeVisible();
    test.skip(await endingSoonChip.isDisabled(), 'No client is ending soon on this fixture, so the chip is disabled');
    await endingSoonChip.click();
    await page.waitForLoadState('networkidle');

    await expect(page).toHaveURL(/chip=EndingSoon/);
    await expect(endingSoonChip).toHaveAttribute('aria-pressed', 'true');
  });

  test('inviting a new client sends the invite and closes the drawer', async ({ page }) => {
    await stubOneCoachPackage(page);
    await page.getByRole('button', { name: 'Invite client', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Invite client', exact: true })).toBeVisible();

    const uniqueSuffix = Date.now();
    await page.getByRole('textbox', { name: 'Email', exact: true }).fill(`qa.invite.${uniqueSuffix}@formup.test`);
    await page.getByRole('textbox', { name: 'Email', exact: true }).press('Tab');

    // Onboarding form row: "None" is always offered, and picking it is an explicit skip.
    const formRow = page.getByTestId('offer-form-row');
    await formRow.getByRole('button', { name: 'Change' }).click();
    await page.getByRole('option', { name: /^None/ }).click();
    await expect(formRow.getByText('No form', { exact: true })).toBeVisible();

    // The package starts as "No package / Decide later", so no payable shows in the footer.
    await expect(page.getByRole('radio', { name: /No package/ })).toBeChecked();
    await expect(page.getByTestId('offer-payable')).toHaveCount(0);

    // Message to the client: live counter, and over 500 characters is blocked.
    const message = page.getByLabel('Message to the client');
    await expect(page.getByText('0 / 500', { exact: true })).toBeVisible();
    await message.fill('Hello there');
    await expect(page.getByText('11 / 500', { exact: true })).toBeVisible();
    await message.fill('x'.repeat(501));
    await expect(page.getByText('501 / 500', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Send invitation' })).toBeDisabled();
    await message.fill('Hello there');

    const submitButton = page.getByRole('button', { name: 'Send invitation' });
    await expect(submitButton).toBeEnabled();
    const requestSent = page.waitForRequest(
      (request) => request.method() === 'POST' && request.url().endsWith('/trainer/pending-invites'),
    );
    await submitButton.click();

    // "None" skips the form explicitly, so the server must not fall back to the default one.
    const body = (await requestSent).postDataJSON() as Record<string, unknown>;
    expect(body.skipOnboardingForm).toBe(true);
    expect(body.questionnairePublicId ?? null).toBeNull();
    expect(body.packageId).toBeUndefined();

    // Radix's Toast primitive renders a visible title plus a duplicate
    // aria-live announcer span — scope to the visible title so this doesn't
    // hit a strict-mode "resolved to 2 elements" violation.
    await expect(page.getByText('Invitation sent.', { exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Invite client', exact: true })).not.toBeVisible();
  });

  test('choosing a package tile sends its id and shows what the client pays', async ({ page }) => {
    await stubOneCoachPackage(page);
    await page.getByRole('button', { name: 'Invite client', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Invite client', exact: true })).toBeVisible();

    const packageTile = page.getByTestId('offer-package-tiles').getByRole('radio', { name: /Monthly coaching/ });
    await page.getByRole('textbox', { name: 'Email', exact: true }).fill(`qa.invite.pkg.${Date.now()}@formup.test`);
    await page.getByRole('textbox', { name: 'Email', exact: true }).press('Tab');

    await packageTile.click();
    await expect(packageTile).toBeChecked();
    await expect(page.getByTestId('offer-payable')).toBeVisible();

    // Stubbed so the spec does not create a second real invite (pending invites are rate-limited).
    let sentBody: Record<string, unknown> | undefined;
    await page.route('**/trainer/pending-invites', async (route) => {
      if (route.request().method() !== 'POST') {
        await route.fallback();
        return;
      }
      sentBody = route.request().postDataJSON() as Record<string, unknown>;
      await route.fulfill({ status: 201, contentType: 'application/json', body: '{}' });
    });
    await page.getByRole('button', { name: 'Send invitation' }).click();

    await expect(page.getByRole('heading', { name: 'Invite client', exact: true })).not.toBeVisible();
    expect(sentBody?.packageId).toBe(STUB_PACKAGE_ID);
  });

  test('an untouched onboarding form row sends the default form and no skip flag', async ({ page }) => {
    await stubOneCoachPackage(page);
    await page.route(
      (url) => url.pathname === '/trainer/questionnaires',
      (route) =>
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            questionnaires: [
              {
                publicId: STUB_FORM_ID,
                title: 'Default onboarding',
                type: 'Onboarding',
                isActive: true,
                isDefault: true,
                questionCount: 4,
                usedForClientCount: 0,
              },
            ],
          }),
        }),
    );
    await page.getByRole('button', { name: 'Invite client', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Invite client', exact: true })).toBeVisible();
    await expect(page.getByTestId('offer-form-row')).toContainText('Default onboarding');
    await page.getByRole('textbox', { name: 'Email', exact: true }).fill(`qa.invite.form.${Date.now()}@formup.test`);
    await page.getByRole('textbox', { name: 'Email', exact: true }).press('Tab');

    let sentBody: Record<string, unknown> | undefined;
    await page.route('**/trainer/pending-invites', async (route) => {
      if (route.request().method() !== 'POST') {
        await route.fallback();
        return;
      }
      sentBody = route.request().postDataJSON() as Record<string, unknown>;
      await route.fulfill({ status: 201, contentType: 'application/json', body: '{}' });
    });
    await page.getByRole('button', { name: 'Send invitation' }).click();

    await expect(page.getByRole('heading', { name: 'Invite client', exact: true })).not.toBeVisible();
    expect(sentBody?.questionnairePublicId).toBe(STUB_FORM_ID);
    expect(sentBody).not.toHaveProperty('skipOnboardingForm');
  });

  /**
   * Inviting a coach's email (Trainer or Nutritionist account) takes the
   * normal success path: the server no longer reveals whether an email
   * belongs to a coach, so the response is indistinguishable from a real
   * invite. The drawer closes and the usual success toast shows.
   *
   * qa.nutri@formup.test is the seeded Nutritionist
   * (docs/testing/e2e-fixtures.md:54). Kept to a single invite attempt:
   * pending-invite creation is rate-limited to 30 per 15 minutes per
   * trainer (Program.cs:178-188).
   */
  test('inviting a coach email takes the normal success path', async ({ page }) => {
    await page.getByRole('button', { name: 'Invite client', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Invite client', exact: true })).toBeVisible();

    await page.getByRole('textbox', { name: 'Email', exact: true }).fill('qa.nutri@formup.test');
    await page.getByRole('textbox', { name: 'Email', exact: true }).press('Tab');

    const submitButton = page.getByRole('button', { name: 'Send invitation' });
    await expect(submitButton).toBeEnabled();
    await submitButton.click();

    await expect(page.getByText('Invitation sent.', { exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Invite client', exact: true })).not.toBeVisible();
  });

  test.describe('live slot check in the invite drawer (#1148)', () => {
    const SLOT_CHECK = (url: URL) => url.pathname === '/trainer/pending-invites/slot-check';
    const TAKEN_EMAIL = 'taken.client@formup.test';

    async function openDrawerAndType(page: Page, email: string): Promise<void> {
      await page.getByRole('button', { name: 'Invite client', exact: true }).click();
      await expect(page.getByRole('heading', { name: 'Invite client', exact: true })).toBeVisible();
      await page.getByRole('textbox', { name: 'Email', exact: true }).fill(email);
    }

    test('a taken training slot shows the label with the typed email in place of the helper', async ({ page }) => {
      await page.route(SLOT_CHECK, (route) =>
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ nutritionTaken: false, trainingTaken: true }),
        }),
      );
      await openDrawerAndType(page, TAKEN_EMAIL);

      await expect(page.getByText(`User with email ${TAKEN_EMAIL} already has a coach for training.`)).toBeVisible();
      await expect(page.getByText('Already on Form Up?')).toHaveCount(0);
    });

    test('free slots keep the helper line and show no label', async ({ page }) => {
      const checked = page.waitForRequest((request) => SLOT_CHECK(new URL(request.url())));
      await page.route(SLOT_CHECK, (route) =>
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ nutritionTaken: false, trainingTaken: false }),
        }),
      );
      await openDrawerAndType(page, TAKEN_EMAIL);
      await checked;

      await expect(page.getByText('Already on Form Up?')).toBeVisible();
      await expect(page.getByTestId('invite-slot-taken')).toHaveCount(0);
    });

    test('an invalid email sends no slot check', async ({ page }) => {
      let calls = 0;
      await page.route(SLOT_CHECK, (route) => {
        calls += 1;
        return route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
      });
      await openDrawerAndType(page, 'not-an-email');
      await page.getByRole('textbox', { name: 'Email', exact: true }).press('Tab');

      await expect(page.getByTestId('invite-slot-taken')).toHaveCount(0);
      // The debounce is 400 ms; the poll window below outlasts it, so a premature request would be counted.
      await expect.poll(async () => calls, { timeout: 1500, intervals: [1500] }).toBe(0);
    });

    test('a rate-limited check shows no label and keeps Send enabled', async ({ page }) => {
      const checked = page.waitForResponse((response) => SLOT_CHECK(new URL(response.url())));
      await page.route(SLOT_CHECK, (route) =>
        route.fulfill({ status: 429, contentType: 'application/problem+json', body: '{}' }),
      );
      await openDrawerAndType(page, TAKEN_EMAIL);
      await page.getByRole('textbox', { name: 'Email', exact: true }).press('Tab');
      await checked;

      await expect(page.getByTestId('invite-slot-taken')).toHaveCount(0);
      await expect(page.getByText('Already on Form Up?')).toBeVisible();
      await expect(page.getByRole('button', { name: 'Send invitation' })).toBeEnabled();
    });
  });

  /**
   * #1079 — the type scale declared font sizes with no paired line-heights,
   * so every control wrapped around a line of text came out ~25% taller
   * than the wireframe. Measured, not eyeballed: a height assertion catches
   * the pairing silently regressing back to the browser's unpaired
   * `normal` line-height, which a visual/eyeball check would not.
   */
  test('control heights match the wireframe at 1920px (#1079)', async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1080 });
    await page.goto('/clients');
    await page.waitForLoadState('networkidle');

    const activeTab = page.getByRole('tab', { name: /Active/ });
    await expect(activeTab).toBeVisible();
    const tabHeight = (await activeTab.boundingBox())?.height ?? 0;
    expect(tabHeight).toBeGreaterThan(27);
    expect(tabHeight).toBeLessThan(31);

    const searchBox = page.getByPlaceholder('Search clients...');
    await expect(searchBox).toBeVisible();
    const searchHeight = (await searchBox.boundingBox())?.height ?? 0;
    // PageClientsC board: 36px search field.
    expect(searchHeight).toBeGreaterThan(35);
    expect(searchHeight).toBeLessThan(37);

    // The page eyebrow repeats this copy, so scope to the sidebar nav.
    const navSectionHeader = page.locator('nav').getByText('Client management', { exact: true });
    await expect(navSectionHeader).toBeVisible();
    const navHeaderHeight = (await navSectionHeader.boundingBox())?.height ?? 0;
    expect(navHeaderHeight).toBeGreaterThan(10);
    expect(navHeaderHeight).toBeLessThan(14);

    const pageTitle = page.getByRole('heading', { name: 'Clients' });
    const pageTitleHeight = (await pageTitle.boundingBox())?.height ?? 0;
    expect(pageTitleHeight).toBeGreaterThan(32);
    expect(pageTitleHeight).toBeLessThan(36);

    const allChip = page.getByRole('button', { name: /\bAll$/ });
    const countBadge = allChip.locator('span').first();
    await expect(countBadge).toBeVisible();
    const badgeBox = await countBadge.boundingBox();
    // PageClientsC board: min-width 20px, height 20px.
    expect(badgeBox?.height ?? 0).toBeGreaterThan(19);
    expect(badgeBox?.height ?? 0).toBeLessThan(21);
    expect(badgeBox?.width ?? 0).toBeGreaterThanOrEqual(badgeBox?.height ?? 0);
  });

  /**
   * #1086 — the tab count badge must stay a pill (wider than tall) even for a
   * one-character count. Face-independent: the badge's computed line-height is
   * pinned in px first, then the font is shrunk to 1px, so the text contributes
   * no width and only the min-width token (or padding) can keep width > height.
   * Without the pin, the unitless line-height shrinks the height too and the
   * assertion passes even without the fix.
   */
  test('tab count badge is a pill, not a circle, with a tiny glyph (#1086)', async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1080 });
    await page.goto('/clients');
    await page.waitForLoadState('networkidle');

    const activeTab = page.getByRole('tab', { name: /Active/ });
    await expect(activeTab).toBeVisible();

    const tabHeight = (await activeTab.boundingBox())?.height ?? 0;
    expect(tabHeight).toBeGreaterThan(29);
    expect(tabHeight).toBeLessThan(31);

    const tablistHeight = (await page.getByRole('tablist').boundingBox())?.height ?? 0;
    expect(tablistHeight).toBeGreaterThan(30);
    expect(tablistHeight).toBeLessThan(32);

    const tabBadge = activeTab.locator('span').first();
    await expect(tabBadge).toBeVisible();
    await tabBadge.evaluate((element) => {
      const badge = element as HTMLElement;
      badge.style.lineHeight = getComputedStyle(badge).lineHeight;
      badge.style.fontSize = '1px';
    });
    const badgeBox = await tabBadge.boundingBox();
    expect(badgeBox?.width ?? 0).toBeGreaterThan(badgeBox?.height ?? 0);
  });

  /**
   * #1081 — the "add client" drawer covers the default `side="right"`
   * case (the off-canvas nav drawer in shell.responsive.spec.ts covers
   * `side="left"`). See that spec's matching test for the full root-cause
   * comment on why a CSS transition can't drive Radix's Presence.
   */
  test('the add-client drawer content and overlay animate with distinct enter/exit keyframes and suspend removal on close (#1081)', async ({
    page,
  }) => {
    await page.getByRole('button', { name: 'Invite client', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Invite client', exact: true })).toBeVisible();

    const dialog = page.getByRole('dialog');
    const overlay = page.locator("[data-slot='sheet-overlay']");

    // A. CSS contract.
    await expect(dialog).toHaveCSS('animation-name', 'sheet-in-right');
    await expect(overlay).toHaveCSS('animation-name', 'sheet-overlay-in');
    const enterContentName = await dialog.evaluate((el) => getComputedStyle(el).animationName);
    const enterOverlayName = await overlay.evaluate((el) => getComputedStyle(el).animationName);

    await dialog.getByRole('button', { name: 'Close' }).click();

    // B. Exit suspension — the actual regression test.
    await expect(dialog).toHaveAttribute('data-state', 'closed');
    await expect(dialog).toHaveCSS('animation-name', 'sheet-out-right');
    await expect(overlay).toHaveCSS('animation-name', 'sheet-overlay-out');
    const exitContentName = await dialog.evaluate((el) => getComputedStyle(el).animationName);
    const exitOverlayName = await overlay.evaluate((el) => getComputedStyle(el).animationName);
    expect(exitContentName).not.toBe(enterContentName);
    expect(exitOverlayName).not.toBe(enterOverlayName);

    await expect(dialog).toBeHidden();
  });

  /**
   * #1091 — the plans-column popup had its content inverted relative to the
   * wireframe: the plan name rendered bold on line 1 with the type conveyed
   * only by an icon, and never as text. Restructured to a header (avatar +
   * full name + active-plan count) followed by one block per plan: the bold
   * type label with the start date right-aligned on line 1, the muted plan
   * name demoted to line 2. The QA seed's trainer link grants
   * CanViewTrainingPlans only (CanViewNutritionPlans is false), so the
   * fixture client "QA Client" surfaces exactly one training plan.
   */
  test('the plans popup states the plan type as text and demotes the name to line 2 (#1091)', async ({ page }) => {
    const trigger = page.getByRole('button', { name: '1 active plan' });
    await trigger.hover();

    const card = page.locator("[data-slot='hover-card-content']");
    await expect(card).toBeVisible();

    await expect(card.getByText('QA Client', { exact: false })).toBeVisible();
    await expect(card.getByText('Training', { exact: true })).toBeVisible();
    await expect(card.getByText('1 active plan', { exact: true })).toBeVisible();
    await expect(card.getByText('QA Test Plan — ForTime fixture', { exact: false })).toBeVisible();
    await expect(card.getByText('Since', { exact: false })).toHaveCount(0);
  });
});

const TRAINER_EMAIL = 'qa.trainer@formup.test';

interface LoginResponseBody {
  accessToken: string;
}

interface ClientTagApiBody {
  tagId?: string;
}

interface GetClientTagsApiBody {
  tags?: ClientTagApiBody[];
}

/**
 * Logs in as qa.trainer via a bare API context (same one-off pattern as
 * `nutritionist/ingredients.spec.ts`'s `loginAsNutritionist`), so tag
 * cleanup below never touches the fixture's own per-attempt browser-session
 * token (see `../fixtures/auth.ts`'s header comment).
 */
async function loginAsTrainer(baseURL: string): Promise<string> {
  const password = process.env['QA_SEED_PASSWORD'];
  if (!password) {
    throw new Error('[clients] QA_SEED_PASSWORD is not set. Copy .env.test.example to .env.test and fill it in.');
  }
  const api = await apiRequest.newContext({ baseURL });
  try {
    const response = await api.post('/auth/login', { data: { email: TRAINER_EMAIL, password } });
    if (!response.ok()) {
      throw new Error(`[clients] login as qa.trainer returned ${response.status()} ${response.statusText()}.`);
    }
    return ((await response.json()) as LoginResponseBody).accessToken;
  } finally {
    await api.dispose();
  }
}

/**
 * Deletes every client tag the caller owns via GET + DELETE
 * /trainer/client-tags, so each test starts and ends from zero tags —
 * CreateClientTagEndpoint 409s on a duplicate name otherwise.
 */
async function deleteAllClientTags(baseURL: string | undefined): Promise<void> {
  const resolvedBaseUrl = baseURL ?? 'http://localhost:5173';
  const accessToken = await loginAsTrainer(resolvedBaseUrl);
  const api = await apiRequest.newContext({ baseURL: resolvedBaseUrl });
  try {
    const listResponse = await api.get('/trainer/client-tags', {
      headers: { Authorization: `Bearer ${accessToken}` },
    });
    if (!listResponse.ok()) {
      throw new Error(
        `[clients] GET /trainer/client-tags returned ${listResponse.status()} ${listResponse.statusText()}.`,
      );
    }
    const { tags } = (await listResponse.json()) as GetClientTagsApiBody;
    for (const tag of tags ?? []) {
      if (!tag.tagId) {
        continue;
      }
      const deleteResponse = await api.delete(`/trainer/client-tags/${tag.tagId}`, {
        headers: { Authorization: `Bearer ${accessToken}` },
      });
      if (!deleteResponse.ok()) {
        throw new Error(
          `[clients] DELETE /trainer/client-tags/${tag.tagId} returned ${deleteResponse.status()} ${deleteResponse.statusText()}.`,
        );
      }
    }
  } finally {
    await api.dispose();
  }
}

/**
 * Creating a client tag from the tag *filter* popover: the popover closes
 * itself before CreateTagDialog opens, then reopens on success with the new
 * tag listed but unticked (a fresh tag has no clients yet, so ticking it
 * would filter the table to empty).
 */
test.describe('client tag filter — create a tag (#1119)', () => {
  test.beforeEach(async ({ page, baseURL }) => {
    await deleteAllClientTags(baseURL);
    await page.goto('/clients');
    await page.waitForLoadState('networkidle');
  });

  test.afterEach(async ({ baseURL }) => {
    await deleteAllClientTags(baseURL);
  });

  test('creates a tag from the filter popup and reopens it listed, unticked', async ({ page }) => {
    const popoverContent = page.locator("[data-slot='popover-content']");

    await page.getByRole('button', { name: 'Select tags' }).click();
    await expect(popoverContent).toBeVisible();
    await expect(popoverContent.getByText("You haven't created any tags yet.")).toBeVisible();

    await popoverContent.getByRole('button', { name: 'Create tag' }).click();

    // Scoped to data-slot, not role: Radix's Popover.Content also carries
    // role="dialog" (the WAI-ARIA "non-modal dialog" pattern), so
    // page.getByRole('dialog') matches both this modal AND the filter
    // popover whenever they briefly coexist mid-transition — exactly the
    // ambiguity that made the popover's own reopening race this locator.
    const dialog = page.locator("[data-slot='dialog-content']");
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole('heading', { name: 'Create a new tag' })).toBeVisible();

    const tagName = `QA Filter Tag ${Date.now()}`;
    await dialog.getByLabel('Name').fill(tagName);

    const submitButton = dialog.getByRole('button', { name: 'Create tag', exact: true });
    await expect(submitButton).toBeEnabled();
    await submitButton.click();

    // Dialog closes and the filter popover reopens on its own (see
    // ClientTagFilterPopover's `handleCreated`) — the same trigger click is
    // not repeated.
    await expect(dialog).toBeHidden();
    await expect(popoverContent).toBeVisible();

    const newTagRow = popoverContent.locator('li', { hasText: tagName });
    await expect(newTagRow).toBeVisible();
    await expect(newTagRow.getByRole('checkbox')).not.toBeChecked();
  });

  /** Duplicate tag name shows the specific translated error, not the generic fallback, and leaves the dialog open. */
  test('duplicate tag name shows the specific error toast and keeps the dialog open', async ({ page }) => {
    const popoverContent = page.locator("[data-slot='popover-content']");
    const dialog = page.locator("[data-slot='dialog-content']");
    const tagName = `QA Duplicate Tag ${Date.now()}`;

    await page.getByRole('button', { name: 'Select tags' }).click();
    await popoverContent.getByRole('button', { name: 'Create tag' }).click();
    await expect(dialog).toBeVisible();
    await dialog.getByLabel('Name').fill(tagName);
    await dialog.getByRole('button', { name: 'Create tag', exact: true }).click();
    await expect(dialog).toBeHidden();
    await expect(popoverContent).toBeVisible();
    await expect(popoverContent.locator('li', { hasText: tagName })).toBeVisible();

    // Same name again — the backend 409s.
    await popoverContent.getByRole('button', { name: 'Create tag' }).click();
    await expect(dialog).toBeVisible();
    await dialog.getByLabel('Name').fill(tagName);
    const submitButton = dialog.getByRole('button', { name: 'Create tag', exact: true });
    await expect(submitButton).toBeEnabled();
    await submitButton.click();

    const specificToast = page
      .locator("[data-slot='toast']")
      .filter({ hasText: 'A tag with this name already exists.' });
    await expect(specificToast).toBeVisible();
    await expect(page.getByText("Couldn't create the tag. Please try again.")).toHaveCount(0);

    // Dialog stays open on error, per the create-mutation's onError contract.
    await expect(dialog).toBeVisible();
  });

  /** The per-row tag picker returns focus to its own trigger, not `<body>`, after Cancel and after a successful create. */
  test('the per-row tag picker returns focus to its own trigger after cancel and after create', async ({ page }) => {
    const rowTrigger = page.getByRole('button', { name: 'Assign tags' }).first();
    const popoverContent = page.locator("[data-slot='popover-content']");
    const dialog = page.locator("[data-slot='dialog-content']");

    // Cancel path.
    await rowTrigger.click();
    await expect(popoverContent).toBeVisible();
    await popoverContent.getByRole('button', { name: 'Create tag' }).click();
    await expect(dialog).toBeVisible();
    await dialog.getByRole('button', { name: 'Cancel' }).click();
    await expect(dialog).toBeHidden();
    await expect(rowTrigger).toBeFocused();

    // Create path.
    await rowTrigger.click();
    await expect(popoverContent).toBeVisible();
    await popoverContent.getByRole('button', { name: 'Create tag' }).click();
    await expect(dialog).toBeVisible();
    const tagName = `QA Row Tag ${Date.now()}`;
    await dialog.getByLabel('Name').fill(tagName);
    await dialog.getByRole('button', { name: 'Create tag', exact: true }).click();
    await expect(dialog).toBeHidden();
    await expect(rowTrigger).toBeFocused();
  });

  /**
   * An ended link has no detail page (the API 404s it), so neither the name nor the row menu may open one.
   * The menu offers the kept payments instead (#1711).
   */
  test('an archived row with an ended link is not a link and its menu offers Payments, not the detail', async ({ page }) => {
    // The seed has no ended link, so fake the Archived tab's list response.
    await page.route(
      (url) => url.pathname.endsWith('/trainer/clients') && url.searchParams.get('status') === 'Archived',
      async (route) => {
        await route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            clients: [
              {
                publicId: '99999999-9999-9999-9999-999999999999',
                userId: '88888888-8888-8888-8888-888888888888',
                email: 'ended.link@formup.test',
                firstName: 'Ended',
                lastName: 'Link',
                isActive: false,
                status: 'Archived',
                tags: [],
                unreadMessageCount: 0,
                activePlans: [],
              },
            ],
            totalCount: 1,
            page: 1,
            pageSize: 25,
            tabCounts: {},
            filterCounts: {},
          }),
        });
      },
    );

    await page.getByRole('tab', { name: /Archived/ }).click();

    const row = page.getByRole('row', { name: /Ended Link/ });
    await expect(row).toBeVisible();
    await expect(row.getByRole('link')).toHaveCount(0);

    await row.getByRole('button', { name: 'Open row menu' }).click();
    await expect(page.getByRole('menuitem', { name: 'Open client detail' })).toHaveCount(0);
    await expect(page.getByRole('menuitem', { name: 'Payments' })).toHaveAttribute(
      'href',
      '/clients/99999999-9999-9999-9999-999999999999/payment',
    );
  });
});

/**
 * #1085 — the floating selection bar collapses its Broadcast and Cancel
 * controls to icon-only below Tailwind's `sm` (640px), keeping each label as
 * the accessible name. The bar is `position: fixed`, so a `scrollWidth`
 * check alone cannot see it spill past the viewport: its bounding box is
 * asserted too. `toHaveText` cannot prove the collapse (sr-only text is still
 * text content), so the collapse is proven by measured pill size.
 *
 * The fixture's Active tab holds one or two clients, so the Czech count form
 * reached is "1 vybraný klient" / "2 vybraní klienti" (count_one / count_few);
 * the widest form ("N vybraných klientů", 5+) is not reachable without
 * extending the seed.
 */
type SelectionBarLocale = { lang: 'cs' | 'en' | 'de'; broadcast: string; cancel: string };

const SELECTION_BAR_LOCALES: SelectionBarLocale[] = [
  { lang: 'cs', broadcast: 'Odeslat zprávu', cancel: 'Zrušit' },
  { lang: 'en', broadcast: 'Broadcast', cancel: 'Cancel' },
  { lang: 'de', broadcast: 'Nachricht senden', cancel: 'Abbrechen' },
];

const NARROW_WIDTHS = [390, 639];
const DESKTOP_WIDTH = 1280;
const BAR_SINGLE_ROW_HEIGHT = 59.6;
const PILL_SINGLE_ROW_HEIGHT = 33.6;
const HEIGHT_TOLERANCE = 2;
const ICON_PILL_MAX_WIDTH = 44;

test.describe('clients selection bar at narrow viewports (#1085)', () => {
  for (const locale of SELECTION_BAR_LOCALES) {
    for (const width of NARROW_WIDTHS) {
      test(`${locale.lang} at ${width}px: fits the viewport, stays one row, actions are icon-only with full accessible names`, async ({
        page,
      }) => {
        await page.addInitScript((lang) => window.localStorage.setItem('lang', lang), locale.lang);
        await page.setViewportSize({ width, height: 800 });
        await page.goto('/clients');
        await page.waitForLoadState('networkidle');

        // Locale-neutral: the select-all aria-label is localised.
        const selectAll = page.getByRole('columnheader').first().getByRole('checkbox');
        await expect(selectAll).toBeEnabled();
        await selectAll.click();

        // The All tab also lists pending rows with their own Cancel button, so scope to the selection bar.
        const bar = page
          .locator('div.fixed')
          .filter({ has: page.getByRole('button', { name: locale.broadcast, exact: true }) });
        const broadcast = bar.getByRole('button', { name: locale.broadcast, exact: true });
        const cancel = bar.getByRole('button', { name: locale.cancel, exact: true });
        await expect(broadcast).toBeVisible();
        await expect(cancel).toBeVisible();

        const barBox = await bar.boundingBox();
        const viewport = await page.evaluate(() => ({
          innerWidth: window.innerWidth,
          scrollWidth: document.documentElement.scrollWidth,
        }));

        expect(viewport.scrollWidth).toBeLessThanOrEqual(viewport.innerWidth);
        expect(barBox).not.toBeNull();
        expect(barBox?.x ?? -1).toBeGreaterThanOrEqual(0);
        expect((barBox?.x ?? 0) + (barBox?.width ?? 0)).toBeLessThanOrEqual(viewport.innerWidth);
        expect(Math.abs((barBox?.height ?? 0) - BAR_SINGLE_ROW_HEIGHT)).toBeLessThanOrEqual(HEIGHT_TOLERANCE);

        for (const action of [broadcast, cancel]) {
          const box = await action.boundingBox();
          expect(box?.width ?? Infinity).toBeLessThanOrEqual(ICON_PILL_MAX_WIDTH);
          expect(Math.abs((box?.height ?? 0) - PILL_SINGLE_ROW_HEIGHT)).toBeLessThanOrEqual(HEIGHT_TOLERANCE);
        }
      });
    }

    test(`${locale.lang} at ${DESKTOP_WIDTH}px: labels are visible and the pills keep their desktop height`, async ({
      page,
    }) => {
      await page.addInitScript((lang) => window.localStorage.setItem('lang', lang), locale.lang);
      await page.setViewportSize({ width: DESKTOP_WIDTH, height: 800 });
      await page.goto('/clients');
      await page.waitForLoadState('networkidle');

      const selectAll = page.getByRole('columnheader').first().getByRole('checkbox');
      await expect(selectAll).toBeEnabled();
      await selectAll.click();

      const bar = page
        .locator('div.fixed')
        .filter({ has: page.getByRole('button', { name: locale.broadcast, exact: true }) });
      const broadcast = bar.getByRole('button', { name: locale.broadcast, exact: true });
      const cancel = bar.getByRole('button', { name: locale.cancel, exact: true });
      await expect(broadcast.getByText(locale.broadcast, { exact: true })).toBeVisible();
      await expect(cancel.getByText(locale.cancel, { exact: true })).toBeVisible();

      for (const action of [broadcast, cancel]) {
        const box = await action.boundingBox();
        expect(Math.abs((box?.height ?? 0) - PILL_SINGLE_ROW_HEIGHT)).toBeLessThanOrEqual(HEIGHT_TOLERANCE);
        expect(box?.width ?? 0).toBeGreaterThan(ICON_PILL_MAX_WIDTH);
      }
    });
  }
});

// A coach holding both roles picks the invite scope, and the slot-check label follows it (#1148).
coachTest.describe('invite drawer slot check for a both-roles coach (#1148)', () => {
  coachTest('the label lists only the professions inside the selected scope', async ({ page }) => {
    const email = 'taken.both@formup.test';
    await page.route(
      (url) => url.pathname === '/trainer/pending-invites/slot-check',
      (route) =>
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ nutritionTaken: true, trainingTaken: true }),
        }),
    );
    await page.goto('/clients');
    await page.getByRole('button', { name: 'Invite client', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Invite client', exact: true })).toBeVisible();
    await page.getByRole('textbox', { name: 'Email', exact: true }).fill(email);

    const training = page.getByText(`User with email ${email} already has a coach for training.`);
    const nutrition = page.getByText(`User with email ${email} already has a coach for nutrition.`);

    // Default scope is Both.
    await expect(training).toBeVisible();
    await expect(nutrition).toBeVisible();

    const scopeGroup = page.getByRole('group', { name: "What you'll coach" });
    await scopeGroup.getByRole('button', { name: 'Training', exact: true }).click();
    await expect(training).toBeVisible();
    await expect(nutrition).toHaveCount(0);

    await scopeGroup.getByRole('button', { name: 'Nutrition', exact: true }).click();
    await expect(nutrition).toBeVisible();
    await expect(training).toHaveCount(0);
  });
});
