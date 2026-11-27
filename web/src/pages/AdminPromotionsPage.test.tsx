/**
 * '/admin/promotion-offers' — the billing period of a new promotion: which cadences it discounts, the hint that
 * explains how a duration works on yearly invoices, and the server refusal for an annual-only promotion on a
 * plan that has no yearly price.
 */
import { describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import { PromotionDuration, PromotionKind, PromotionMode, PromotionStatus } from '@/api/generated';
import type { PromotionDto } from '@/api/generated';
import {
  newAdminPromotionsState,
  renderAdminPromotions,
  serveAdminPromotions,
  type AdminPromotionsState,
} from '@/test/adminPromotions';
import { apiUrl, http, problem, rfc7807 } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

type Rendered = ReturnType<typeof renderAdminPromotions>;

async function openDrawer(state: AdminPromotionsState): Promise<Rendered> {
  serveAdminPromotions(state);
  const rendered = renderAdminPromotions();
  await rendered.user.click(await screen.findByRole('button', { name: 'New promotion' }));
  await screen.findByRole('textbox', { name: 'Name · Czech' });
  return rendered;
}

async function fillRequired({ user }: Rendered) {
  await user.type(screen.getByRole('textbox', { name: 'Name · Czech' }), 'Launch');
  await user.type(screen.getByRole('textbox', { name: 'Value' }), '20');
}

const periodGroup = () => screen.getByRole('group', { name: 'Billing period' });

const HINT = /takes off the first yearly invoice only.*Forever takes off every yearly invoice/;

describe('AdminPromotionsPage billing period', () => {
  it('covers both cadences by default and sends Both', async () => {
    const state = newAdminPromotionsState();
    const rendered = await openDrawer(state);

    await fillRequired(rendered);
    await rendered.user.click(screen.getByRole('button', { name: 'Save promotion' }));

    expect(await screen.findByText('Promotion saved.')).toBeInTheDocument();
    expect(state.creates).toHaveLength(1);
    expect(state.creates[0]).toMatchObject({ billingPeriod: 'Both' });
  });

  it('sends the trimmed English and German names, and leaves an empty one out', async () => {
    const state = newAdminPromotionsState();
    const rendered = await openDrawer(state);

    await fillRequired(rendered);
    await rendered.user.type(screen.getByRole('textbox', { name: 'Name · English' }), '  Launch offer ');
    await rendered.user.type(screen.getByRole('textbox', { name: 'Name · German' }), '   ');
    await rendered.user.click(screen.getByRole('button', { name: 'Save promotion' }));
    await screen.findByText('Promotion saved.');

    expect(state.creates[0]).toMatchObject({ name: 'Launch', nameEn: 'Launch offer' });
    expect(state.creates[0]).not.toHaveProperty('nameDe');
  });

  it('sends Annual and Monthly as chosen', async () => {
    const state = newAdminPromotionsState();
    const rendered = await openDrawer(state);

    await fillRequired(rendered);
    await rendered.user.click(within(periodGroup()).getByRole('button', { name: 'Annual' }));
    await rendered.user.click(screen.getByRole('button', { name: 'Save promotion' }));
    await screen.findByText('Promotion saved.');

    expect(state.creates[0]).toMatchObject({ billingPeriod: 'Annual' });
  });

  it('explains how durations work on yearly invoices for Annual and Both, and hides that for Monthly', async () => {
    const rendered = await openDrawer(newAdminPromotionsState());

    expect(screen.getByText(HINT)).toBeInTheDocument();

    await rendered.user.click(within(periodGroup()).getByRole('button', { name: 'Monthly' }));
    expect(screen.queryByText(HINT)).not.toBeInTheDocument();

    await rendered.user.click(within(periodGroup()).getByRole('button', { name: 'Annual' }));
    expect(screen.getByText(HINT)).toBeInTheDocument();
  });

  it('shows the refusal under Plans and keeps the drawer when a plan has no yearly price', async () => {
    const state = newAdminPromotionsState();
    const rendered = await openDrawer(state);
    server.use(http.post(apiUrl('/admin/promotions'), () => problem(400, 'PROMOTION_PLANS_INVALID')));

    await fillRequired(rendered);
    await rendered.user.click(within(periodGroup()).getByRole('button', { name: 'Annual' }));
    await rendered.user.click(screen.getByRole('button', { name: 'Save promotion' }));

    expect(
      await screen.findByText(
        'Pick at least one paid plan. An annual-only promotion needs plans that have a yearly price.',
      ),
    ).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Name · Czech' })).toHaveValue('Launch');
  });
});

describe('AdminPromotionsPage ?new=1 deep link', () => {
  it('opens the drawer once and removes the param with a replace', async () => {
    serveAdminPromotions(newAdminPromotionsState());
    const { router } = renderAdminPromotions('?new=1');

    expect(await screen.findByRole('textbox', { name: 'Name · Czech' })).toBeInTheDocument();
    await waitFor(() => expect(router.state.location.search).toBe(''));
    expect(router.state.historyAction).toBe('REPLACE');
    // Removing the param does not close the drawer.
    expect(screen.getByRole('textbox', { name: 'Name · Czech' })).toBeInTheDocument();
  });

  it('keeps the drawer closed without the param', async () => {
    serveAdminPromotions(newAdminPromotionsState());
    renderAdminPromotions();

    expect(await screen.findByRole('button', { name: 'New promotion' })).toBeInTheDocument();
    expect(screen.queryByRole('textbox', { name: 'Name · Czech' })).not.toBeInTheDocument();
  });
});

const promotion = (overrides: Partial<PromotionDto>): PromotionDto => ({
  promotionId: '00000000-0000-4000-8000-000000000001',
  name: 'Launch offer',
  kind: PromotionKind.Percent,
  value: 30,
  mode: PromotionMode.Automatic,
  duration: PromotionDuration.Months,
  durationMonths: 3,
  claimFrom: '2026-11-01T00:00:00Z',
  claimUntil: '2026-12-31T22:59:59Z',
  planCodes: ['start', 'pro', 'studio'],
  usedCount: 12,
  status: PromotionStatus.Active,
  ...overrides,
});

const LAUNCH = promotion({});
const PARTNER = promotion({
  promotionId: '00000000-0000-4000-8000-000000000002',
  name: 'Partner code',
  value: 20,
  mode: PromotionMode.Code,
  code: 'COACH20',
  duration: PromotionDuration.Forever,
  durationMonths: undefined,
  claimUntil: undefined,
  maxUses: 50,
  usedCount: 18,
});
const BLACK_FRIDAY = promotion({
  promotionId: '00000000-0000-4000-8000-000000000003',
  name: 'Black Friday',
  value: 40,
  durationMonths: 6,
  usedCount: 0,
  status: PromotionStatus.Scheduled,
});
const SUMMER = promotion({
  promotionId: '00000000-0000-4000-8000-000000000004',
  name: 'Summer 2026',
  mode: PromotionMode.Code,
  code: 'SUMMER',
  durationMonths: 2,
  planCodes: ['start', 'pro'],
  usedCount: 31,
  status: PromotionStatus.Ended,
});

const listState = () => newAdminPromotionsState([LAUNCH, PARTNER, BLACK_FRIDAY, SUMMER].map((item) => ({ ...item })));
const row = (name: RegExp) => screen.findByRole('row', { name });

describe('AdminPromotionsPage list', () => {
  it('shows each promotion with how, value, length, window, uses and status', async () => {
    serveAdminPromotions(listState());
    renderAdminPromotions();

    expect(await screen.findByRole('heading', { name: 'Promotions', level: 1 })).toBeInTheDocument();
    for (const header of ['Promotion', 'How', 'Value', 'Lasts', 'Offer window', 'Plans', 'Used', 'Status']) {
      expect(screen.getByRole('columnheader', { name: header })).toBeInTheDocument();
    }

    const launch = await row(/Launch offer/);
    expect(within(launch).getByText('For everyone at checkout')).toBeInTheDocument();
    expect(within(launch).getByText('Automatic', { exact: true })).toBeInTheDocument();
    expect(within(launch).getByText('−30 %')).toBeInTheDocument();
    expect(within(launch).getByText('3 months')).toBeInTheDocument();
    expect(within(launch).getByText('Active', { exact: true })).toBeInTheDocument();

    const partner = screen.getByRole('row', { name: /Partner code/ });
    expect(within(partner).getByText('COACH20')).toBeInTheDocument();
    expect(within(partner).getByText('Forever')).toBeInTheDocument();
    expect(within(partner).getByText('No end date')).toBeInTheDocument();
    expect(within(partner).getByText('18 of 50')).toBeInTheDocument();

    const summer = screen.getByRole('row', { name: /Summer 2026/ });
    expect(within(summer).getByText('Ended', { exact: true })).toBeInTheDocument();

    // No row menu; a row that has not ended carries a direct End button, an ended row none. The plans column is
    // covered by AdminPromotionsPlans.test.tsx.
    expect(screen.queryByRole('button', { name: /Open menu/ })).not.toBeInTheDocument();
    expect(within(launch).getByRole('button', { name: 'End Launch offer' })).toHaveAttribute('title', 'End promotion');
    expect(within(screen.getByRole('row', { name: /Black Friday/ })).getByRole('button', { name: 'End Black Friday' })).toBeInTheDocument();
    expect(within(summer).queryByRole('button', { name: /^End / })).not.toBeInTheDocument();
  });

  it('the tabs ask the server for that status and show only its promotions', async () => {
    const state = listState();
    serveAdminPromotions(state);
    const { user } = renderAdminPromotions();
    await row(/Launch offer/);

    await user.click(screen.getByRole('tab', { name: 'Scheduled' }));
    expect(await row(/Black Friday/)).toBeInTheDocument();
    expect(screen.queryByRole('row', { name: /Launch offer/ })).not.toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: 'Ended' }));
    expect(await row(/Summer 2026/)).toBeInTheDocument();
    expect(screen.queryByRole('row', { name: /Black Friday/ })).not.toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: 'All' }));
    expect(await row(/Launch offer/)).toBeInTheDocument();

    expect(state.listStatuses).toEqual(expect.arrayContaining(['All', 'Scheduled', 'Ended']));
  });
});

describe('AdminPromotionsPage ending a promotion', () => {
  it('confirms, posts an empty JSON body and refetches the list', async () => {
    const state = listState();
    serveAdminPromotions(state);
    const { user } = renderAdminPromotions();
    await row(/Launch offer/);
    const readsBefore = state.listStatuses.length;

    await user.click(screen.getByRole('button', { name: 'End Launch offer' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByRole('heading', { name: 'End “Launch offer”?' })).toBeInTheDocument();
    expect(within(dialog).getByText('The 12 coaches who already have it keep their discount until it runs out.')).toBeInTheDocument();
    expect(state.ends).toHaveLength(0);

    await user.click(within(dialog).getByRole('button', { name: 'End promotion' }));

    expect(await screen.findByText('Promotion ended.')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(state.ends).toHaveLength(1);
    expect(state.ends[0].path).toBe('/admin/promotions/00000000-0000-4000-8000-000000000001/end');
    expect(state.ends[0].contentType).toContain('application/json');
    expect(state.ends[0].body).toBe('{}');

    // The refetch shows the row as Ended, and an Ended row has no End button.
    await waitFor(() =>
      expect(within(screen.getByRole('row', { name: /Launch offer/ })).getByText('Ended', { exact: true })).toBeInTheDocument(),
    );
    expect(within(screen.getByRole('row', { name: /Launch offer/ })).queryByRole('button', { name: /^End / })).not.toBeInTheDocument();
    expect(state.listStatuses.length).toBeGreaterThan(readsBefore);
  });

  it('hides the note when nobody has the promotion yet, and Cancel ends nothing', async () => {
    const state = listState();
    serveAdminPromotions(state);
    const { user } = renderAdminPromotions();
    await row(/Black Friday/);

    await user.click(screen.getByRole('button', { name: 'End Black Friday' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByRole('heading', { name: 'End “Black Friday”?' })).toBeInTheDocument();
    expect(within(dialog).queryByText(/already have it|already has it/)).not.toBeInTheDocument();

    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(state.ends).toHaveLength(0);
  });

  it('a promotion that already ended shows a toast, closes the dialog and refetches', async () => {
    const state = listState();
    state.endResponse = () => rfc7807(409, 'PROMOTION_ALREADY_ENDED');
    serveAdminPromotions(state);
    const { user } = renderAdminPromotions();
    await row(/Partner code/);
    const readsBefore = state.listStatuses.length;

    await user.click(screen.getByRole('button', { name: 'End Partner code' }));
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'End promotion' }));

    expect(await screen.findByText('This promotion has already ended.')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(() => expect(state.listStatuses.length).toBeGreaterThan(readsBefore));
  });

  it('a payment-provider failure keeps the dialog open and the row unchanged', async () => {
    const state = listState();
    state.endResponse = () => rfc7807(502, 'BILLING_PROVIDER_ERROR');
    serveAdminPromotions(state);
    const { user } = renderAdminPromotions();
    await row(/Partner code/);
    const readsBefore = state.listStatuses.length;

    await user.click(screen.getByRole('button', { name: 'End Partner code' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'End promotion' }));

    expect(await screen.findByText("The payment provider didn't answer. Please try again in a moment.")).toBeInTheDocument();
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(state.listStatuses).toHaveLength(readsBefore);

    // The open modal hides the table from role queries, so close it before checking the row.
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    const partner = screen.getByRole('row', { name: /Partner code/ });
    expect(within(partner).getByText('Active', { exact: true })).toBeInTheDocument();
    expect(within(partner).getByRole('button', { name: 'End Partner code' })).toBeInTheDocument();
  });
});

describe('AdminPromotionsPage drawer', () => {
  it('creates a coded promotion; the code field shows only for a code', async () => {
    const state = newAdminPromotionsState();
    const { user } = await openDrawer(state);
    const drawer = screen.getByRole('dialog');

    expect(within(drawer).getByRole('heading', { name: 'New promotion' })).toBeInTheDocument();
    expect(within(drawer).queryByLabelText('Code', { exact: true })).not.toBeInTheDocument();
    expect(within(drawer).getByText('Covers the whole bill, second role included')).toBeInTheDocument();

    await user.type(within(drawer).getByLabelText('Name · Czech', { exact: true }), 'Spring offer');
    await user.type(within(drawer).getByLabelText('Value', { exact: true }), '25');

    await user.click(within(drawer).getByRole('button', { name: 'With a code' }));
    await user.type(within(drawer).getByLabelText('Code', { exact: true }), 'SPRING25');
    await user.click(within(drawer).getByRole('button', { name: 'Automatic for everyone' }));
    expect(within(drawer).queryByLabelText('Code', { exact: true })).not.toBeInTheDocument();
    await user.click(within(drawer).getByRole('button', { name: 'With a code' }));
    await user.clear(within(drawer).getByLabelText('Code', { exact: true }));
    await user.type(within(drawer).getByLabelText('Code', { exact: true }), 'SPRING25');

    // The month count shows only for "Several months".
    expect(within(drawer).queryByLabelText('Number of months')).not.toBeInTheDocument();
    await user.click(within(drawer).getByRole('button', { name: 'Several months' }));
    await user.type(within(drawer).getByLabelText('Number of months'), '3');

    await user.click(within(drawer).getByRole('button', { name: 'Save promotion' }));

    expect(await screen.findByText('Promotion saved.')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(await row(/Spring offer/)).toBeInTheDocument();

    expect(state.creates).toHaveLength(1);
    expect(state.creates[0]).toMatchObject({
      name: 'Spring offer',
      kind: 'Percent',
      value: 25,
      mode: 'Code',
      code: 'SPRING25',
      duration: 'Months',
      durationMonths: 3,
      planCodes: ['start', 'pro', 'studio'],
      newCoachesOnly: false,
      oneUsePerCoach: true,
    });
    expect(typeof state.creates[0]['claimFrom']).toBe('string');
  });

  it('sends a fixed amount in minor units and blocks an empty name before any request', async () => {
    const state = newAdminPromotionsState();
    const { user } = await openDrawer(state);
    const drawer = screen.getByRole('dialog');

    await user.click(within(drawer).getByRole('button', { name: 'Save promotion' }));
    expect(await within(drawer).findByText('Enter a name.')).toBeInTheDocument();
    expect(state.creates).toHaveLength(0);

    await user.type(within(drawer).getByLabelText('Name · Czech', { exact: true }), 'Crown off');
    await user.click(within(drawer).getByRole('button', { name: 'Fixed amount' }));
    await user.type(within(drawer).getByLabelText('Value', { exact: true }), '200');
    await user.click(within(drawer).getByRole('button', { name: 'Save promotion' }));

    expect(await screen.findByText('Promotion saved.')).toBeInTheDocument();
    expect(state.creates[0]).toMatchObject({ kind: 'FixedAmount', value: 20000, currency: 'CZK', mode: 'Automatic' });
  });

  it('flags a code already in use on the Code field and keeps the drawer open', async () => {
    const state = newAdminPromotionsState();
    const { user } = await openDrawer(state);
    server.use(http.post(apiUrl('/admin/promotions'), () => problem(409, 'PROMOTION_CODE_TAKEN')));
    const drawer = screen.getByRole('dialog');

    await user.type(within(drawer).getByLabelText('Name · Czech', { exact: true }), 'Partner again');
    await user.type(within(drawer).getByLabelText('Value', { exact: true }), '10');
    await user.click(within(drawer).getByRole('button', { name: 'With a code' }));
    await user.type(within(drawer).getByLabelText('Code', { exact: true }), 'COACH20');
    await user.click(within(drawer).getByRole('button', { name: 'Save promotion' }));

    expect(await within(drawer).findByText('Another promotion that has not ended already uses this code.')).toBeInTheDocument();
    expect(within(drawer).getByLabelText('Name · Czech', { exact: true })).toHaveValue('Partner again');
  });
});
