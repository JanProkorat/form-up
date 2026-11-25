/**
 * #1066 — Clients list page, the parts that need a real browser or the real backend: the invite
 * journey, control geometry, drawer animation and the selection bar at narrow widths. Tab and Filter
 * URL state, legacy URLs, the invite request body, the slot check, client tags and the plans popup are
 * Vitest component tests (src/pages/ClientsPage.test.tsx, src/components/clients/*.test.tsx).
 * Path is deliberately under trainer/ — every project in
 * playwright.config.ts matches a role subfolder via testMatch, so a spec at
 * the e2e/ root is collected by no project and silently never runs (see
 * PLAN-1066-clients-page.md §1).
 *
 * Imports `trainerTest`, not the bare `test`: the trainer project
 * deliberately carries no `storageState` (#897), so a spec using the bare
 * `test` export lands on the login page and asserts against the marketing
 * page instead of the authenticated portal.
 */
import type { Page, Route } from '@playwright/test';
import { trainerTest as test, expect } from '../fixtures/auth';
import { recordExit, readExit } from '../fixtures/sheet-exit';

const STUB_PACKAGE_ID = 'bbbbbbbb-0000-0000-0000-000000001907';

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

    // Scope to the sidebar nav; a trainer sees the Library header (Forms).
    const navSectionHeader = page.locator('nav').getByText('Library', { exact: true });
    await expect(navSectionHeader).toBeVisible();
    const navHeaderHeight = (await navSectionHeader.boundingBox())?.height ?? 0;
    expect(navHeaderHeight).toBeGreaterThan(10);
    expect(navHeaderHeight).toBeLessThan(14);

    const pageTitle = page.getByRole('heading', { name: 'Clients' });
    const pageTitleHeight = (await pageTitle.boundingBox())?.height ?? 0;
    expect(pageTitleHeight).toBeGreaterThan(32);
    expect(pageTitleHeight).toBeLessThan(36);

    const filterButton = page.getByRole('button', { name: /^Filter/ });
    await expect(filterButton).toBeVisible();
    const filterHeight = (await filterButton.boundingBox())?.height ?? 0;
    // PageClientsC board: 36px filter button.
    expect(filterHeight).toBeGreaterThan(35);
    expect(filterHeight).toBeLessThan(37);
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

    // The exit animations last 200ms, shorter than a few sequential Playwright
    // round trips under load: once Radix unmounts the overlay, a post-close
    // locator read waits on a detached element until the test timeout. Record
    // the closed state inside the page before closing instead.
    await recordExit(dialog, 'content');
    await recordExit(overlay, 'overlay');

    await dialog.getByRole('button', { name: 'Close' }).click();

    // B. Exit suspension — the actual regression test.
    await expect
      .poll(() => readExit(page, 'content'))
      .toMatchObject({ closedWhileConnected: true, closedAnimationName: 'sheet-out-right' });
    await expect
      .poll(() => readExit(page, 'overlay'))
      .toMatchObject({ closedWhileConnected: true, closedAnimationName: 'sheet-overlay-out' });
    const contentExit = await readExit(page, 'content');
    const overlayExit = await readExit(page, 'overlay');
    expect(contentExit?.closedAnimationName).not.toBe(enterContentName);
    expect(overlayExit?.closedAnimationName).not.toBe(enterOverlayName);

    await expect(dialog).toBeHidden();
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

        // Scope to the selection bar so its Cancel button is the one matched.
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
