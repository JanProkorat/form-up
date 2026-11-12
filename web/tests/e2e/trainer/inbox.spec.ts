/**
 * #1095 — Inbox + chat page. Path is under trainer/ so playwright.config.ts's
 * role-subfolder testMatch collects it (see clients.spec.ts's own header
 * comment). QA seed: a conversation between qa.trainer and "QA Client" with
 * an unread message from the client and a coach message containing a
 * YouTube link; a third, conversation-less client keeps the "No messages"
 * filter non-zero on both the clients list and the inbox.
 */
import type { Locator, Page } from '@playwright/test';
import { trainerTest as test, expect, openClientContext } from '../fixtures/auth';

const QA_CLIENT_NAME = /QA Client/;

/**
 * The inbox list row button for the "QA Client" conversation, scoped to an
 * exact name match. The seeded, conversation-less "QA Client3" roster entry
 * (see the file header comment) also renders a row once the "All" filter
 * lists every live-roster client, so the unanchored `QA_CLIENT_NAME` regex
 * alone resolves to two buttons — a strict-mode violation. Scoping to the
 * row's `[data-testid="inbox-row-name"]` name text via `exact: true` (same element
 * `getInboxRowNames` below reads) excludes "QA Client3" without excluding
 * the avatar's initials-fallback text ahead of it in DOM order.
 */
function qaClientRow(page: Page): Locator {
  return page.getByRole('button').filter({ has: page.getByText('QA Client', { exact: true }) });
}

/** The inbox row for the seeded ended-link "QA Former" conversation — never the first row, so it is not auto-opened. */
function qaFormerRow(page: Page): Locator {
  return page.getByRole('button').filter({ has: page.getByText('QA Former', { exact: true }) });
}

/** Every visible clients-list row's name link on the CURRENT tab, for filter-parity comparisons. */
async function getClientsListNamesOnCurrentTab(page: Page): Promise<string[]> {
  const links = page.locator("table tbody a[href^='/clients/']");
  return (await links.allInnerTexts()).map((t) => t.trim());
}

/**
 * The clients-list roster for a given filter chip, unioned across the
 * Active and Outside plan tabs — matching the population contract's "live links,
 * archived links excluded" (docs: GetConversationsEndpoint's roster-filter
 * path; a client outside the plan is still a *live* link). The All tab is not
 * used because it also lists archived clients. The inbox's own filter has no
 * tab concept — it always spans this same union, never just the Active tab alone.
 *
 * The clients page no longer has chips for most of these filters (#2084), but the API still does, so the
 * roster request is rewritten to carry the filter and the page renders the filtered list.
 */
async function getClientsListNamesForFilter(page: Page, filterParam: string): Promise<string[]> {
  const rosterRequest = /\/trainer\/clients\?/;
  await page.route(rosterRequest, (route) => {
    const url = new URL(route.request().url());
    url.searchParams.set('filter', filterParam);
    return route.continue({ url: url.toString() });
  });
  const names: string[] = [];
  try {
    for (const tab of ['Active', 'OutsidePlan'] as const) {
      await page.goto(`/clients?tab=${tab}`);
      await page.waitForLoadState('networkidle');
      names.push(...(await getClientsListNamesOnCurrentTab(page)));
    }
  } finally {
    await page.unroute(rosterRequest);
  }
  return names.sort();
}

/** Every visible inbox row's participant-name text, for filter-parity comparisons. */
async function getInboxRowNames(page: Page): Promise<string[]> {
  // Scoped to the name span specifically — a row's button also contains the
  // avatar's initials-fallback text ("QC") ahead of the name in DOM order,
  // so reading the button's whole innerText and taking the first line picks
  // up the initials, not the name.
  const names = page.locator('[data-testid="inbox-row-name"]');
  return (await names.allInnerTexts()).map((t) => t.trim()).sort();
}

/**
 * Drives the real SignalR hub as a second, independently-authenticated
 * party: negotiates a connection as qa.client and invokes `SendTyping`
 * directly, over the SAME origin (so the Vite dev server's `/hubs` proxy —
 * `ws: true` in vite.config.e2e.ts — carries it to the harness, with no
 * separate TLS trust dance in the browser).
 */
async function sendTypingViaHub(page: Page, accessToken: string, conversationId: string): Promise<void> {
  await page.evaluate(
    async ({ accessToken, conversationId }) => {
      const negotiateRes = await fetch('/hubs/notifications/negotiate?negotiateVersion=1', {
        method: 'POST',
        headers: { Authorization: `Bearer ${accessToken}` },
      });
      const negotiate = (await negotiateRes.json()) as { connectionToken: string };
      const wsProtocol = window.location.protocol === 'https:' ? 'wss' : 'ws';
      const wsUrl = `${wsProtocol}://${window.location.host}/hubs/notifications?id=${negotiate.connectionToken}&access_token=${encodeURIComponent(accessToken)}`;

      await new Promise<void>((resolve, reject) => {
        const socket = new WebSocket(wsUrl);
        const timeout = setTimeout(() => reject(new Error('SendTyping hub call timed out')), 8000);
        let handshakeDone = false;

        socket.onopen = () => {
          socket.send(`${JSON.stringify({ protocol: 'json', version: 1 })}\u001e`);
        };
        socket.onmessage = () => {
          if (!handshakeDone) {
            handshakeDone = true;
            socket.send(`${JSON.stringify({ type: 1, target: 'SendTyping', arguments: [conversationId] })}\u001e`);
            setTimeout(() => {
              clearTimeout(timeout);
              socket.close();
              resolve();
            }, 400);
          }
        };
        socket.onerror = () => {
          clearTimeout(timeout);
          reject(new Error('SendTyping WebSocket errored'));
        };
      });
    },
    { accessToken, conversationId },
  );
}

/**
 * Whether the thread's message list is scrolled to (or within a few px of)
 * its bottom — the direct signal for "the newest message is in view" that
 * doesn't depend on Playwright's viewport-intersection visibility check,
 * which doesn't account for an ancestor's own scroll offset.
 */
async function isThreadScrolledToBottom(page: Page): Promise<boolean> {
  const SCROLL_BOTTOM_SLACK_PX = 4;
  return page.evaluate((slack) => {
    const el = document.querySelector('[data-testid="thread-message-list"]');
    if (!(el instanceof HTMLElement)) {
      return false;
    }
    return el.scrollHeight - el.scrollTop - el.clientHeight <= slack;
  }, SCROLL_BOTTOM_SLACK_PX);
}

const FILTERS: Array<{ name: string; inboxLabel: RegExp; filterParam: string }> = [
  { name: 'No messages', inboxLabel: /^No messages/, filterParam: 'NoMessages' },
  { name: 'Unread messages', inboxLabel: /^Unread messages/, filterParam: 'UnreadMessages' },
  { name: 'New check-ins', inboxLabel: /^New check-ins/, filterParam: 'NewCheckIns' },
  { name: 'Missing check-ins', inboxLabel: /^Missing check-ins/, filterParam: 'MissingCheckIns' },
  { name: 'Ending soon', inboxLabel: /^Ending soon/, filterParam: 'EndingSoon' },
];

test.describe('inbox page', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/inbox');
    await page.waitForLoadState('networkidle');
    await expect(page.getByRole('heading', { name: 'Inbox' })).toBeVisible();
  });

  test('the first conversation is selected and its thread is open on entry', async ({ page }) => {
    // Which conversation sorts first depends on what earlier tests created in the shared DB, so
    // nothing here assumes a specific participant. The mark-read deferral is proven over the wire.
    const markReadRequests: string[] = [];
    page.on('request', (request) => {
      if (request.method() === 'POST' && /\/conversations\/[^/?]+\/read$/.test(new URL(request.url()).pathname)) {
        markReadRequests.push(request.url());
      }
    });

    // Fresh navigation (the beforeEach load predates the listener).
    const listResponsePromise = page.waitForResponse((r) => r.url().includes('/conversations?') && r.status() === 200);
    await page.goto('/inbox');
    const listBody = (await (await listResponsePromise).json()) as Array<{ id?: string; participant?: { name?: string } }>;
    await page.waitForLoadState('networkidle');

    const firstRowName = (await page.locator('[data-testid="inbox-row-name"]').first().innerText()).trim();
    const firstConversation = listBody.find((r) => r.participant?.name?.trim() === firstRowName);
    if (!firstConversation?.id) {
      throw new Error(`Conversation id not found for first inbox row "${firstRowName}"`);
    }

    await expect(page.getByText('Select a chat to begin messaging')).toHaveCount(0);
    await expect(page.getByPlaceholder('Type your message here...')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Hide client' })).toBeVisible();
    // The open thread's header shows the first row's participant (the row itself also carries this text).
    const headerName = page
      .getByText(firstRowName, { exact: true })
      .and(page.locator(':not([data-testid="inbox-row-name"])'))
      .first();
    await expect(headerName).toBeVisible();

    // Merely entering the inbox must not mark the auto-selected thread read.
    await page.waitForLoadState('networkidle');
    expect(markReadRequests).toHaveLength(0);

    // The first interaction inside the thread pane marks exactly that conversation read.
    await page.getByTestId('thread-message-list').click();
    await expect.poll(() => markReadRequests.length).toBe(1);
    expect(markReadRequests[0]).toContain(`/conversations/${firstConversation.id}/read`);
  });

  test('empty-state copy matches the wireframe when the selected chat is not in the current list', async ({ page }) => {
    // The first row is auto-selected on entry; the Archived view (nothing seeded there) no longer
    // contains it, so the thread pane falls back to the empty state.
    await expect(page.getByPlaceholder('Type your message here...')).toBeVisible();
    await page.getByRole('button', { name: /Active/ }).click();
    await page.getByRole('menuitem', { name: 'Archived' }).click();
    await page.waitForLoadState('networkidle');

    await expect(page.getByText('Select a chat to begin messaging')).toBeVisible();
    await expect(
      page.getByText('Your client inbox, workout questions, and onboarding metrics live here.'),
    ).toBeVisible();
  });

  test('list renders with unread count badge, time, and last-message preview; opening the thread marks it read', async ({
    page,
  }) => {
    // QA Client is auto-opened on entry but stays unread until the trainer interacts; QA Former keeps
    // its seeded unread message until the trainer opens that thread.
    const row = qaFormerRow(page);
    await expect(row).toBeVisible();
    // Unread badge: a red count (a plain message counts, a cooperation event row does not).
    await expect(row.locator('[role="status"]')).toBeVisible();
    await expect(row.getByTestId('inbox-row-unread-count')).toHaveText(/^\d+\+?$/);

    await row.click();
    await page.waitForLoadState('networkidle');
    await expect(page.getByText('QA Former').first()).toBeVisible();

    // Read mark round-trips through the backend + a list invalidation —
    // re-fetch the row and confirm the unread badge is gone.
    await expect(row.locator('[role="status"]')).toHaveCount(0);
  });

  test('search narrows the list by participant name', async ({ page }) => {
    await expect(qaClientRow(page)).toBeVisible();

    await page.getByPlaceholder('Search chats...').fill('zzz-no-such-participant');
    await expect(page.getByText('No conversations found')).toBeVisible();
    await expect(qaClientRow(page)).toHaveCount(0);

    await page.getByPlaceholder('Search chats...').fill('QA Client');
    await expect(qaClientRow(page)).toBeVisible();
  });

  test('the Active/Archived switch changes the visible rows', async ({ page }) => {
    await expect(qaClientRow(page)).toBeVisible();

    await page.getByRole('button', { name: /Active/ }).click();
    await page.getByRole('menuitem', { name: 'Archived' }).click();
    await page.waitForLoadState('networkidle');

    // No conversation is seeded as archived — the active-view row disappears.
    await expect(qaClientRow(page)).toHaveCount(0);

    await page.getByRole('button', { name: /Archived/ }).click();
    await page.getByRole('menuitem', { name: 'Active' }).click();
    await page.waitForLoadState('networkidle');
    await expect(qaClientRow(page)).toBeVisible();
  });

  test('the three domain-less filters render disabled with the coming-soon tooltip', async ({ page }) => {
    await page.getByRole('button', { name: /^All \(\d+\)$/ }).click();

    for (const label of ['Failed payments', 'Blocked automations', 'Tasks overdue']) {
      const item = page.getByRole('menuitem', { name: new RegExp(label) });
      await expect(item).toBeVisible();
      await expect(item).toHaveAttribute('aria-disabled', 'true');
      await expect(item).toHaveAttribute('title', 'This page is coming soon.');
    }
  });

  for (const { name, inboxLabel, filterParam } of FILTERS) {
    test(`a live filter returns the clients-list chip set restricted to clients with a conversation (#1095): ${name}`, async ({
      page,
    }) => {
      // The inbox lists only clients that have a conversation, so the unfiltered inbox is the
      // "has a conversation" population the filtered set is restricted to.
      const withConversation = await getInboxRowNames(page);

      const clientsNames = (await getClientsListNamesForFilter(page, filterParam)).filter((n) =>
        withConversation.includes(n),
      );

      await page.goto('/inbox');
      await page.waitForLoadState('networkidle');
      await page.getByRole('button', { name: /^All \(\d+\)$/ }).click();
      // `networkidle` alone is unreliable here — the inbox keeps a live
      // SignalR WebSocket open, so wait for the SPECIFIC filtered request
      // this click triggers instead.
      const filteredResponsePromise = page.waitForResponse(
        (r) => r.url().includes('/conversations?') && r.url().includes(`filter=${filterParam}`) && r.status() === 200,
      );
      await page.getByRole('menuitem', { name: inboxLabel }).click();
      await filteredResponsePromise;

      // A brief render lag can separate the response resolving from React
      // committing the row list — poll rather than a single snapshot read.
      await expect.poll(() => getInboxRowNames(page)).toEqual(clientsNames);
    });
  }

  test('a YouTube link in message text renders as an embedded preview card', async ({ page }) => {
    await qaClientRow(page).click();
    await page.waitForLoadState('networkidle');

    const thumbnail = page.locator('img[src*="img.youtube.com"]');
    await expect(thumbnail).toBeVisible();
  });

  test('sending a message appends it to the thread and updates the list preview', async ({ page }) => {
    await qaClientRow(page).click();
    await page.waitForLoadState('networkidle');

    const uniqueText = `QA reply ${Date.now()}`;
    await page.getByPlaceholder('Type your message here...').fill(uniqueText);
    await page.getByRole('button', { name: 'Send message' }).click();

    // Own-message bubble in the thread, and the row's updated preview line —
    // both legitimately carry the same text, so each assertion is scoped to
    // avoid a strict-mode "resolved to 2 elements" violation.
    await expect(page.getByText(uniqueText, { exact: true }).last()).toBeVisible();
    await expect(qaClientRow(page).getByText(uniqueText)).toBeVisible();
  });

  test('the Show-client panel status pill equals the clients-list status pill, and the external link opens the client', async ({
    page,
  }) => {
    await page.goto('/clients');
    await page.waitForLoadState('networkidle');
    const listRow = page.getByRole('row', { name: QA_CLIENT_NAME }).filter({
      has: page.getByRole('link', { name: 'QA Client', exact: true }),
    });
    // The list pill reads "Active plans" where the panel pill reads "Active"; "No plan" and "Archived" match.
    const listStatusText = (await listRow.locator('[data-slot="badge"]').first().innerText())
      .trim()
      .replace(/^Active plans$/, 'Active');
    const qaClientHref = await listRow.getByRole('link', { name: 'QA Client', exact: true }).getAttribute('href');
    const qaClientId = qaClientHref?.split('/').pop();
    if (!qaClientId) {
      throw new Error('QA Client id not found on the clients list');
    }

    // Which row is auto-selected depends on earlier tests, so open QA Client explicitly. Its
    // dashboard is fetched either on load (if it is first) or on the click — register the wait
    // before navigating so both cases are covered.
    const dashboardResponse = page.waitForResponse(
      (r) => new RegExp(`/trainer/clients/${qaClientId}(\\?|$)`).test(r.url()) && r.status() === 200,
    );
    await page.goto('/inbox');
    await qaClientRow(page).click();
    const dashboard = (await (await dashboardResponse).json()) as {
      sessionsCompletedThisWeek?: number | null;
      sessionsPlannedThisWeek?: number | null;
    };
    await page.waitForLoadState('networkidle');

    // At the default 1280px viewport the panel is docked and open; "Hide client" closes it and
    // "Show client" brings it back.
    const panel = page.getByRole('complementary', { name: 'Client details' });
    await expect(panel).toBeVisible();
    await page.getByRole('button', { name: 'Hide client' }).click();
    // The aside stays mounted at width 0 with aria-hidden + inert, so role queries no longer see it.
    await expect(panel).toBeHidden();
    await page.getByRole('button', { name: 'Show client' }).click();
    const panelStatusBadge = panel.locator('[data-slot="badge"]').first();
    await expect(panelStatusBadge).toHaveText(listStatusText);

    // The caption mirrors the dashboard numbers (the seeded week varies); hidden when nothing is planned.
    const done = dashboard.sessionsCompletedThisWeek;
    const planned = dashboard.sessionsPlannedThisWeek;
    if (done != null && planned != null && planned > 0) {
      const shown = Math.min(done, planned);
      const caption = `Done this week · ${shown} of ${planned} ${planned === 1 ? 'session' : 'sessions'}`;
      await expect(panel.getByText(caption, { exact: true })).toBeVisible();
    } else {
      await expect(panel.getByText(/Done this week/)).toHaveCount(0);
    }

    await page.getByRole('link', { name: 'Open client profile' }).click();
    await page.waitForLoadState('networkidle');
    await expect(page).toHaveURL(/\/clients\/[0-9a-f-]{36}$/);
    await expect(page.getByRole('heading', { name: QA_CLIENT_NAME })).toBeVisible();
  });

  test('the client-detail back arrow returns to the inbox with the same thread open', async ({ page }) => {
    // Whichever row is auto-selected on entry; its name is read rather than assumed.
    const firstRowName = (await page.locator('[data-testid="inbox-row-name"]').first().innerText()).trim();
    await page.getByRole('link', { name: 'Open client profile' }).click();
    await expect(page).toHaveURL(/\/clients\/[0-9a-f-]{36}$/);
    const clientPath = new URL(page.url()).pathname;
    await expect(page.getByRole('heading', { name: firstRowName })).toBeVisible();

    await page.getByRole('link', { name: 'Back to clients' }).click();
    await page.waitForLoadState('networkidle');

    // The `?client=` deep link is consumed, leaving a clean /inbox URL with that client's thread open.
    await expect(page).toHaveURL('/inbox');
    await expect(page.getByText('Select a chat to begin messaging')).toHaveCount(0);
    await expect(page.getByPlaceholder('Type your message here...')).toBeVisible();
    await expect(page.getByRole('link', { name: 'Open client profile' })).toHaveAttribute('href', clientPath);
  });

  test('the client-detail Chat button opens the existing thread', async ({ page }) => {
    await page.goto('/clients');
    await page.waitForLoadState('networkidle');
    // Exact name: "QA Client3" also sits on the All tab and matches QA_CLIENT_NAME.
    await page.getByRole('link', { name: 'QA Client', exact: true }).click();
    await page.waitForLoadState('networkidle');

    await page.getByRole('link', { name: 'Chat' }).click();
    await page.waitForLoadState('networkidle');

    await expect(page).toHaveURL('/inbox');
    await expect(page.getByText('Select a chat to begin messaging')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Hide client' })).toBeVisible();
  });

  test('a newmessage SignalR event refreshes an open thread without a page reload, and the typing indicator appears and clears (#1095)', async ({
    page,
    browser,
    baseURL,
  }) => {
    // Re-fetch the list (fresh navigation, not the "no reload" behaviour
    // under test below) so we can intercept the app's own authenticated
    // request and read the real conversation id off it — a raw same-origin
    // fetch() from page.evaluate would skip the app's axios Bearer-token
    // interceptor (the access token lives in memory, not a cookie).
    const listResponsePromise = page.waitForResponse((r) => r.url().includes('/conversations?') && r.status() === 200);
    await page.goto('/inbox');
    const listBody = (await (await listResponsePromise).json()) as Array<{
      id?: string;
      participant?: { name?: string };
    }>;
    const match = listBody.find((r) => r.participant?.name?.includes('QA Client'));
    if (!match?.id) {
      throw new Error('QA Client conversation id not found');
    }
    const conversationId = match.id;

    await qaClientRow(page).click();
    await page.waitForLoadState('networkidle');

    const { context: clientContext, accessToken } = await openClientContext(browser, baseURL ?? 'http://localhost:5173');

    try {
      // newmessage: send AS qa.client directly against the API, bypassing
      // the UI, and confirm the trainer's already-open thread picks it up
      // live — no page.reload() anywhere in this test.
      const clientApi = await clientContext.request;
      const uniqueText = `QA client push ${Date.now()}`;
      const sendResponse = await clientApi.post(`/conversations/${conversationId}/messages`, {
        data: { text: uniqueText },
        headers: { Authorization: `Bearer ${accessToken}` },
      });
      expect(sendResponse.ok()).toBe(true);
      // Appears in both the thread bubble and the list's updated preview line.
      await expect(page.getByText(uniqueText, { exact: true }).last()).toBeVisible({ timeout: 10_000 });

      // typing: invoke SendTyping through the real hub from the client
      // context and confirm the indicator appears within ~1s and clears by ~3.5s.
      const clientPage = await clientContext.newPage();
      await clientPage.goto('/download-app');
      await sendTypingViaHub(clientPage, accessToken, conversationId);

      await expect(page.getByText('Typing…')).toBeVisible({ timeout: 1000 });
      await expect(page.getByText('Typing…')).toHaveCount(0, { timeout: 4000 });
    } finally {
      await clientContext.close();
    }
  });

  test('a long thread opens scrolled to the newest message, and stays there after sending (#1099 rework)', async ({
    page,
    browser,
    baseURL,
  }) => {
    // Same id-recovery pattern as the newmessage test above.
    const listResponsePromise = page.waitForResponse((r) => r.url().includes('/conversations?') && r.status() === 200);
    await page.goto('/inbox');
    const listBody = (await (await listResponsePromise).json()) as Array<{
      id?: string;
      participant?: { name?: string };
    }>;
    const match = listBody.find((r) => r.participant?.name?.includes('QA Client'));
    if (!match?.id) {
      throw new Error('QA Client conversation id not found');
    }
    const conversationId = match.id;

    const { context: clientContext, accessToken } = await openClientContext(browser, baseURL ?? 'http://localhost:5173');

    try {
      // Seed enough messages to overflow the viewport — the seeded fixture
      // thread alone (3 messages) fit on screen and hid this bug entirely.
      const clientApi = await clientContext.request;
      const OVERFLOW_MESSAGE_COUNT = 30;
      let newestSeededText = '';
      for (let i = 0; i < OVERFLOW_MESSAGE_COUNT; i += 1) {
        newestSeededText = `QA scroll fixture ${Date.now()}-${i}`;
        const response = await clientApi.post(`/conversations/${conversationId}/messages`, {
          data: { text: newestSeededText },
          headers: { Authorization: `Bearer ${accessToken}` },
        });
        expect(response.ok()).toBe(true);
      }

      await qaClientRow(page).click();
      await page.waitForLoadState('networkidle');

      // On a cold cache the thread must open scrolled to the newest message,
      // not stranded at the oldest one loaded (the rework's root cause).
      await expect(page.getByText(newestSeededText, { exact: true }).last()).toBeVisible();
      await expect.poll(() => isThreadScrolledToBottom(page)).toBe(true);

      const ownReply = `QA trainer reply ${Date.now()}`;
      await page.getByPlaceholder('Type your message here...').fill(ownReply);
      await page.getByRole('button', { name: 'Send message' }).click();

      await expect(page.getByText(ownReply, { exact: true }).last()).toBeVisible();
      await expect.poll(() => isThreadScrolledToBottom(page)).toBe(true);
    } finally {
      await clientContext.close();
    }
  });
});
