/**
 * '/admin' — the Last 30 days tiles and their change badges, the Needs attention rows and their deep links, the
 * per-card failure states, and the two quick actions. The sign-in landing journey is pinned in the Playwright spec.
 */
import { describe, expect, it } from 'vitest';
import { screen, within } from '@testing-library/react';
import {
  adminAttentionPayload,
  allClearAttention,
  overviewReport,
  renderAdminOverview,
  serveAdminOverview,
} from '@/test/adminOverview';

/** Intl joins digit groups and the currency with no-break spaces; compare with plain ones. */
const plain = (value: string | null | undefined) => (value ?? '').replace(/\s/g, ' ').trim();
const byText = (expected: string) => (content: string) => plain(content) === expected;

/** The tile that holds this label, so a figure is read inside its own card. */
function tile(label: string): HTMLElement {
  const holder = screen.getByText(label).closest('div.rounded-2xl');
  if (!(holder instanceof HTMLElement)) {
    throw new Error(`No tile around "${label}"`);
  }
  return holder;
}

/** The row (list item) that holds this title. */
function row(title: string | RegExp): HTMLElement {
  const holder = screen.getByText(title).closest('li');
  if (!(holder instanceof HTMLElement)) {
    throw new Error(`No row around "${String(title)}"`);
  }
  return holder;
}

/** The API sends JSON null where the generated types say undefined. */
const NULL = null as unknown as undefined;

/** The change badge of a tile, or null when it has none. */
const chipOf = (holder: HTMLElement) => holder.querySelector('span.rounded-full');

const GOOD = 'bg-success-soft';
const BAD = 'bg-error-soft';

describe('AdminOverviewPage Last 30 days', () => {
  it('shows the four figures with a change badge against the previous 30 days', async () => {
    const state = serveAdminOverview();
    renderAdminOverview();

    expect(await screen.findByRole('heading', { name: 'Last 30 days', level: 2 })).toBeInTheDocument();
    expect(await screen.findByText(byText('21 860 Kč'))).toBeInTheDocument();

    const mrr = tile('Monthly recurring revenue');
    expect(within(mrr).getByText(byText('21 860 Kč'))).toBeInTheDocument();
    expect(within(mrr).getByText('+8.2 %')).toHaveClass(GOOD);

    const signups = tile('New sign-ups');
    expect(within(signups).getByText('14')).toBeInTheDocument();
    expect(within(signups).getByText('+27.3 %')).toHaveClass(GOOD);

    // The value is the backend count; the note is the per-plan breakdown and need not add up to it.
    const paying = tile('Paying coaches');
    expect(within(paying).getByText('37')).toBeInTheDocument();
    expect(within(paying).getByText('+5.7 %')).toHaveClass(GOOD);
    expect(within(paying).getByText('Start 18 · Pro 13')).toBeInTheDocument();

    // Churn: a one-decimal value, a percentage-point badge, and less churn is good news.
    const churn = tile('Churn');
    expect(within(churn).getByText('2.7 %')).toBeInTheDocument();
    expect(within(churn).getByText('−0.4 pp')).toHaveClass(GOOD);

    expect(state.reportPeriods).toEqual(['Days30']);
  });

  it('puts the Full reports link beside the title and sends churn going up to the bad tone', async () => {
    serveAdminOverview(
      adminAttentionPayload(),
      overviewReport({
        money: { ...overviewReport().money, churnPercent: 3.7, churnPercentPrevious: 3.1 },
      }),
    );
    renderAdminOverview();

    expect(await screen.findByText('+0.6 pp')).toHaveClass(BAD);
    expect(screen.getByRole('link', { name: 'Full reports' })).toHaveAttribute('href', '/admin/business-reports');
  });

  it.each([
    ['MRR baseline is null', 'Monthly recurring revenue', { monthlyRevenueAfterPromotions: [{ currency: 'CZK', value: 2186000, previous: NULL }] }],
    ['MRR baseline is 0', 'Monthly recurring revenue', { monthlyRevenueAfterPromotions: [{ currency: 'CZK', value: 2186000, previous: 0 }] }],
    ['paying coaches baseline is null', 'Paying coaches', { payingCoaches: { value: 37, previous: NULL } }],
    ['paying coaches baseline is 0', 'Paying coaches', { payingCoaches: { value: 37, previous: 0 } }],
    ['churn baseline is null', 'Churn', { churnPercentPrevious: NULL }],
  ])('shows no badge when the %s', async (_name, label, money) => {
    serveAdminOverview(adminAttentionPayload(), overviewReport({ money: { ...overviewReport().money, ...money } }));
    renderAdminOverview();

    await screen.findByText(byText('21 860 Kč'));
    expect(chipOf(tile(label))).toBeNull();
  });

  it('shows no sign-ups badge when the previous window had none, and a dash for unknown churn', async () => {
    serveAdminOverview(
      adminAttentionPayload(),
      overviewReport({
        funnel: { signedUp: 14, signedUpPrevious: 0 },
        money: { ...overviewReport().money, churnPercent: NULL },
      }),
    );
    renderAdminOverview();

    await screen.findByText(byText('21 860 Kč'));
    const signups = tile('New sign-ups');
    expect(within(signups).getByText('14')).toBeInTheDocument();
    expect(chipOf(signups)).toBeNull();
    const churn = tile('Churn');
    expect(within(churn).getByText('—')).toBeInTheDocument();
    expect(chipOf(churn)).toBeNull();
  });

  it('shows the section error with Retry while Needs attention still renders', async () => {
    serveAdminOverview(adminAttentionPayload(), null);
    const { user } = renderAdminOverview();

    const alert = await screen.findByRole('alert');
    expect(within(alert).getByText('Could not load the figures.')).toBeInTheDocument();
    expect(await screen.findByText('5 payments failed')).toBeInTheDocument();
    expect(screen.getAllByRole('alert')).toHaveLength(1);

    serveAdminOverview(adminAttentionPayload(), overviewReport());
    await user.click(within(alert).getByRole('button', { name: 'Try again' }));

    expect(await screen.findByText(byText('21 860 Kč'))).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});

describe('AdminOverviewPage Needs attention', () => {
  it('lists the non-zero rows with their deep links', async () => {
    const state = serveAdminOverview();
    renderAdminOverview();

    expect(await screen.findByText('5 payments failed')).toBeInTheDocument();
    expect(screen.getByText('4 items')).toBeInTheDocument();

    const failed = row('5 payments failed');
    expect(within(failed).getByText('Klara Novakova, Studio Forma and 3 more')).toBeInTheDocument();
    expect(within(failed).getByRole('link', { name: 'Open coaches' })).toHaveAttribute(
      'href',
      '/admin/coach-accounts?status=PaymentFailed',
    );

    const cancelling = row('3 coaches cancelled');
    expect(
      within(cancelling).getByText('Their paid plans end between Nov 28, 2026 and Dec 5, 2026.'),
    ).toBeInTheDocument();
    expect(within(cancelling).getByRole('link', { name: 'Open coaches' })).toHaveAttribute(
      'href',
      '/admin/coach-accounts?status=Cancelling',
    );

    const gifts = row('4 free plans you gave end within 14 days');
    expect(within(gifts).getByRole('link', { name: 'Open coaches' })).toHaveAttribute(
      'href',
      '/admin/coach-accounts?status=GivenFree',
    );

    const promotion = row('“Black Friday” is about to run out');
    expect(
      within(promotion).getByText('Coaches can claim it until Nov 30, 2026. 46 of 50 uses taken.'),
    ).toBeInTheDocument();
    expect(within(promotion).getByRole('link', { name: 'Open promotion' })).toHaveAttribute(
      'href',
      '/admin/promotion-offers',
    );
    expect(state.attentionRequests).toBe(1);
  });

  it('hides each zero-count row on its own', async () => {
    serveAdminOverview(
      adminAttentionPayload({
        paymentFailedCount: 0,
        paymentFailedCoaches: [],
        givenFreeEndingSoonCount: 0,
        promotionsEndingSoon: [],
        cancellingCount: 1,
      }),
    );
    renderAdminOverview();

    expect(await screen.findByText('1 coach cancelled')).toBeInTheDocument();
    expect(screen.getByText('1 item')).toBeInTheDocument();
    expect(screen.queryByText(/payments? failed/)).not.toBeInTheDocument();
    expect(screen.queryByText(/free plans? you gave/)).not.toBeInTheDocument();
    expect(screen.queryByText(/is about to run out/)).not.toBeInTheDocument();
  });

  it('shows the all-clear state when every count is zero', async () => {
    serveAdminOverview(allClearAttention());
    renderAdminOverview();

    expect(await screen.findByText('Nothing needs you')).toBeInTheDocument();
    expect(screen.queryByRole('listitem')).not.toBeInTheDocument();
  });

  it('shows the card error with Retry while the figures still render', async () => {
    serveAdminOverview(null, overviewReport());
    const { user } = renderAdminOverview();

    const alert = await screen.findByRole('alert');
    expect(within(alert).getByText('Could not load what needs attention.')).toBeInTheDocument();
    expect(await screen.findByText(byText('21 860 Kč'))).toBeInTheDocument();
    expect(screen.getAllByRole('alert')).toHaveLength(1);

    serveAdminOverview(adminAttentionPayload(), overviewReport());
    await user.click(within(alert).getByRole('button', { name: 'Try again' }));

    expect(await screen.findByText('5 payments failed')).toBeInTheDocument();
  });

  it('uses the Czech plural forms and says trenér', async () => {
    serveAdminOverview(adminAttentionPayload({ cancellingCount: 3, paymentFailedCount: 2, paymentFailedCoaches: [] }));
    renderAdminOverview('cs');

    expect(await screen.findByText('3 trenéři zrušili')).toBeInTheDocument();
    expect(screen.getByText('2 platby selhaly')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Posledních 30 dní', level: 2 })).toBeInTheDocument();
  });
});

describe('AdminOverviewPage quick actions', () => {
  it('orders the sections header, Last 30 days, Needs attention, Quick actions', async () => {
    serveAdminOverview();
    renderAdminOverview();

    await screen.findByText('5 payments failed');
    const names = screen.getAllByRole('heading').map((heading) => heading.textContent);
    expect(names).toEqual(['Overview', 'Last 30 days', 'Needs attention', 'Quick actions']);
  });

  it('opens the Coaches list searched for the typed text', async () => {
    serveAdminOverview();
    const { user, router } = renderAdminOverview();

    await user.type(await screen.findByRole('searchbox', { name: 'Find a coach' }), 'klara n{Enter}');

    expect(await screen.findByTestId('coaches-page')).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/admin/coach-accounts');
    expect(router.state.location.search).toBe('?q=klara+n');
  });

  it('opens the promotions page with the new-promotion flag', async () => {
    serveAdminOverview();
    const { user, router } = renderAdminOverview();

    await user.click(await screen.findByRole('button', { name: 'New promotion' }));

    expect(await screen.findByTestId('promotions-page')).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/admin/promotion-offers');
    expect(router.state.location.search).toBe('?new=1');
  });
});
