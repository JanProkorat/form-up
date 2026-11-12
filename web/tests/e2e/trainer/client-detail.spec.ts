/**
 * #1094 — Client detail page, Overview tab. Path is under trainer/ so
 * playwright.config.ts's role-subfolder testMatch collects it (see
 * clients.spec.ts's own header comment — a spec at the e2e/ root runs on
 * no project at all).
 *
 * Verifies the layout contract: the not-yet-built tab and the two
 * still-unbuilt header buttons render disabled with the shell's coming-soon
 * treatment, the rebuilt Overview shows its three stat tiles and four lower
 * cards (#1295), and — the AC this issue's backend slice exists for — the
 * detail page's status pill text equals the same client's status badge on
 * the clients list, asserted by comparison rather than a hardcoded word.
 *
 * Chat went live in #1095 — it is no longer in DISABLED_BUTTON_NAMES; its
 * own assertion below checks it navigates to `/inbox?client=<publicId>`.
 */
import { trainerTest as test, expect } from '../fixtures/auth';

// The QA trainer's link is training-only, so the Nutrition tab is not rendered.
const DISABLED_TAB_NAMES = ['Workouts'];
const DISABLED_BUTTON_NAMES = ['Tasks', 'Notes'];

test.describe('client detail page — overview tab', () => {
  test('renders the layout contract: coming-soon tabs/buttons, the rebuilt overview cards, and a status pill matching the clients list', async ({
    page,
  }) => {
    await page.goto('/clients');
    await page.waitForLoadState('networkidle');

    // Exact name: "QA Client3" also sits on the All tab and matches /QA Client/.
    const row = page.getByRole('row').filter({ has: page.getByRole('link', { name: 'QA Client', exact: true }) });
    await expect(row).toBeVisible();

    // Capture the list badge's text BEFORE navigating away — this is the
    // value the detail page's pill must equal.
    // The list pill reads "Active plans" where the detail pill reads "Active"; "No plan" and "Archived" match.
    const listStatusText = (await row.locator('[data-slot="badge"]').first().innerText())
      .trim()
      .replace(/^Active plans$/, 'Active');
    expect(listStatusText.length).toBeGreaterThan(0);

    await row.getByRole('link', { name: 'QA Client', exact: true }).click();
    await page.waitForLoadState('networkidle');

    await expect(page.getByRole('heading', { name: /QA Client/ })).toBeVisible();

    // The status label comparison — never a hardcoded status word (AC).
    await expect(page.getByTestId('client-status')).toHaveText(listStatusText);

    // Overview, Photos, Payment and Automations are enabled for every coach (Nutrition
    // only with the nutrition capability); Workouts stays disabled with the
    // coming-soon treatment. Development was dropped from the row.
    await expect(page.getByRole('tab', { name: 'Overview' })).toBeEnabled();
    await expect(page.getByRole('tab', { name: 'Photos' })).toBeVisible();
    await expect(page.getByRole('tab', { name: 'Photos' })).toBeEnabled();
    await expect(page.getByRole('tab', { name: 'Payment' })).toBeVisible();
    await expect(page.getByRole('tab', { name: 'Payment' })).toBeEnabled();
    await expect(page.getByRole('tab', { name: 'Automations' })).toBeEnabled();
    for (const tabName of DISABLED_TAB_NAMES) {
      const tab = page.getByRole('tab', { name: tabName });
      await expect(tab).toBeVisible();
      await expect(tab).toBeDisabled();
    }

    await expect(page.getByRole('tab', { name: 'Development' })).toHaveCount(0);

    // The two still-unbuilt header action buttons render disabled; Info is gone.
    for (const buttonName of DISABLED_BUTTON_NAMES) {
      await expect(page.getByRole('button', { name: new RegExp(buttonName) })).toBeDisabled();
    }
    await expect(page.getByRole('button', { name: 'Info' })).toHaveCount(0);

    // Facts strip for a training-only link: goal, weight and the training plan,
    // no meal plan fact.
    const facts = page.getByTestId('client-header-facts');
    await expect(facts.getByText('Goal', { exact: true })).toBeVisible();
    await expect(facts.getByText('Weight', { exact: true })).toBeVisible();
    await expect(facts.getByText('Training plan', { exact: true })).toBeVisible();
    await expect(facts.getByText('Meal plan', { exact: true })).toHaveCount(0);

    // Chat is live (#1095) — it's a link, not a disabled button, and it
    // carries this client's publicId as the inbox deep-link query param.
    const chatLink = page.getByRole('link', { name: 'Chat' });
    await expect(chatLink).toBeVisible();
    const chatHref = await chatLink.getAttribute('href');
    expect(chatHref).toMatch(/^\/inbox\?client=[0-9a-f-]{36}$/);

    await expect(page.getByRole('tab', { name: 'Nutrition' })).toHaveCount(0);

    // A training-only link that deep-links to the Nutrition tab lands on Overview.
    const detailUrl = page.url();
    await page.goto(`${detailUrl}/nutrition`);
    await page.waitForLoadState('networkidle');
    await expect(page).toHaveURL(detailUrl);
    await expect(page.getByRole('tab', { name: 'Overview' })).toHaveAttribute('aria-selected', 'true');

    // Training-only stat row: training compliance, PRs this month, client since.
    for (const label of ['Training compliance', 'PRs this month', 'Client since']) {
      await expect(page.getByText(label, { exact: true })).toBeVisible();
    }
    for (const label of ['Meal compliance', 'Weight change']) {
      await expect(page.getByText(label, { exact: true })).toHaveCount(0);
    }

    // Only the training plan card, no nutrition card or no-access placeholder.
    await expect(page.getByRole('region', { name: 'Latest workout' })).toBeVisible();
    await expect(page.getByRole('region', { name: 'Current meal plan' })).toHaveCount(0);
    await expect(page.getByText('No nutrition access')).toHaveCount(0);

    // Client profile: injuries highlight and training row, no allergies or diet rows.
    await expect(page.getByText('Injuries', { exact: true })).toBeVisible();
    await expect(page.getByText('Training days', { exact: true })).toBeVisible();
    await expect(page.getByText('Allergies', { exact: true })).toHaveCount(0);
    await expect(page.getByText('Diet style', { exact: true })).toHaveCount(0);
    await expect(page.getByText('Daily target', { exact: true })).toHaveCount(0);

    // Lower cards: check-in trend + weight, recent activity + client profile.
    for (const name of ['Check-in trend', 'Weight', 'Recent activity', 'Client profile']) {
      await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
    }

    // The retired placeholders are gone: no TBD stat cards, no messages
    // trend widget.
    await expect(page.getByText('Average rating', { exact: true })).toHaveCount(0);
    await expect(page.getByText('Payments', { exact: true })).toHaveCount(0);
    await expect(page.getByRole('heading', { name: 'Messages trend' })).toHaveCount(0);
  });
});
