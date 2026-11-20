/**
 * '/admin/business-reports' — the numbers each section shows from the payload, the percentages computed from it,
 * the honest empty and failure states, and the period switch. The request the switch sends is pinned in the
 * Playwright spec, not here.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, within } from '@testing-library/react';
import { AdminReportPeriod } from '@/api/generated';
import { adminReportsPayload, renderAdminReports, serveAdminReports } from '@/test/adminReports';
import { apiUrl, http, problem } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

/** Intl joins digit groups and the currency with no-break spaces; compare with plain ones. */
const plain = (value: string | null | undefined) => (value ?? '').replace(/\s/g, ' ').trim();
const byText = (expected: string) => (content: string) => plain(content) === expected;

/** The card or panel that holds this label, so a figure is read inside its own block. */
function block(label: string): HTMLElement {
  const holder = screen.getByText(label).closest('section, div.rounded-2xl');
  if (!(holder instanceof HTMLElement)) {
    throw new Error(`No block around "${label}"`);
  }
  return holder;
}

afterEach(() => {
  vi.unstubAllEnvs();
});

describe('AdminReportsPage', () => {
  it('shows the money, coaches and usage figures of the payload', async () => {
    serveAdminReports({ [AdminReportPeriod.Days30]: adminReportsPayload() });
    renderAdminReports();

    expect(await screen.findByRole('heading', { name: 'Money', level: 2 })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Reports', level: 1 })).toBeInTheDocument();
    expect(screen.getByText(byText('Snapshot of Nov 15, 2026 · change against Oct 16, 2026'))).toBeInTheDocument();

    // Each currency has its own line; the EUR line has no baseline, so no chip.
    const revenue = block('Monthly revenue');
    expect(within(revenue).getByText(byText('21 860 Kč'))).toBeInTheDocument();
    expect(within(revenue).getByText('+8.2 %')).toBeInTheDocument();
    expect(within(revenue).getByText(byText('€150'))).toBeInTheDocument();
    expect(within(revenue).getByText(byText('After promotions · full price 26 480 Kč'))).toBeInTheDocument();
    expect(within(revenue).getAllByText(/%$/)).toHaveLength(1);

    const collected = block('Collected this month');
    expect(within(collected).getByText(byText('19 940 Kč'))).toBeInTheDocument();
    expect(within(collected).getByText('+6.5 %')).toBeInTheDocument();

    // Lost to cancellations: the chip is the change in the count, not in the money.
    const lost = block('Lost to cancellations');
    expect(within(lost).getByText(byText('1 196 Kč'))).toBeInTheDocument();
    expect(within(lost).getByText('+1')).toBeInTheDocument();
    expect(within(lost).getByText('Cancellations: 3 · churn 7.5 %')).toBeInTheDocument();

    const average = block('Average per paying coach');
    expect(within(average).getByText(byText('591 Kč'))).toBeInTheDocument();
    expect(within(average).getByText('+2.1 %')).toBeInTheDocument();

    const plans = block('Paying coaches by plan');
    expect(within(plans).getByText('Start')).toBeInTheDocument();
    expect(within(plans).getByText(byText('5 382 Kč / mo'))).toBeInTheDocument();
    expect(within(plans).getByText('18')).toBeInTheDocument();
    expect(within(plans).getByText('Second-role add-on')).toBeInTheDocument();
    expect(within(plans).getByText(byText('2 697 Kč / mo'))).toBeInTheDocument();
    expect(within(plans).getByText('9')).toBeInTheDocument();
    expect(screen.getByRole('img', { name: /Monthly recurring revenue after promotions, from .* to / })).toBeInTheDocument();

    const changes = block('Plan changes');
    expect(within(changes).getByText(byText('+1 196 Kč'))).toBeInTheDocument();
    expect(within(changes).getByText(byText('−300 Kč'))).toBeInTheDocument();
    expect(within(changes).getByText(byText('−1 196 Kč'))).toBeInTheDocument();
    expect(within(changes).getByText('Stripe is retrying')).toBeInTheDocument();

    expect(within(block('Active coaches')).getByText('61 / 87')).toBeInTheDocument();
    const perCoach = block('Clients per coach');
    expect(within(perCoach).getByText('6')).toBeInTheDocument();
    expect(within(perCoach).getByText('Median over all coaches')).toBeInTheDocument();
    expect(within(block('Free coaches at the cap')).getByText('12')).toBeInTheDocument();
    const clients = block('Active clients in the app');
    expect(within(clients).getByText('412')).toBeInTheDocument();
    expect(within(clients).getByText('+5.1 %')).toBeInTheDocument();
    expect(within(block('Plan weeks published')).getByText('138')).toBeInTheDocument();
  });

  it('computes each funnel percentage against the step above it', async () => {
    serveAdminReports({ [AdminReportPeriod.Days30]: adminReportsPayload() });
    renderAdminReports();

    await screen.findByText('New coach funnel');
    const funnel = block('New coach funnel');
    const row =(label: string) => within(funnel).getByText(label).closest('li') as HTMLElement;
    expect(within(row('Signed up')).getByText('64')).toBeInTheDocument();
    expect(within(row('Verified email')).getByText('91 %')).toBeInTheDocument();
    expect(within(row('Chose a plan')).getByText('88 %')).toBeInTheDocument();
    expect(within(row('Paid plan')).getByText('18 %')).toBeInTheDocument();
  });

  it('shows a dash instead of a percentage when the step above is empty', async () => {
    const payload = adminReportsPayload({ funnel: { signedUp: 0, emailVerified: 0, chosePlan: 0, startedPaidPlan: 0 } });
    serveAdminReports({ [AdminReportPeriod.Days30]: payload });
    renderAdminReports();

    await screen.findByText('New coach funnel');
    const funnel = block('New coach funnel');
    for(const label of ['Verified email', 'Chose a plan', 'Paid plan']) {
      const row = within(funnel).getByText(label).closest('li') as HTMLElement;
      expect(within(row).getByText('—')).toBeInTheDocument();
    }
    expect(funnel).not.toHaveTextContent(/NaN|Infinity/);
  });

  it('shows the payload of the period picked in the switch', async () => {
    const ninety = adminReportsPayload({
      period: AdminReportPeriod.Days90,
      comparedFrom: '2026-08-17',
      funnel: { signedUp: 200, emailVerified: 150, chosePlan: 100, startedPaidPlan: 50 },
    });
    serveAdminReports({ [AdminReportPeriod.Days30]: adminReportsPayload(), [AdminReportPeriod.Days90]: ninety });
    const { user } = renderAdminReports();

    await screen.findByText('New coach funnel');
    const funnel30 = block('New coach funnel');
    expect(funnel30).toHaveTextContent('Coaches who signed up in the last 30 days');

    await user.click(screen.getByRole('button', { name: '90 days' }));

    expect(screen.getByRole('button', { name: '90 days' })).toHaveAttribute('aria-pressed', 'true');
    await screen.findByText('Coaches who signed up in the last 90 days');
    const funnel90 = block('New coach funnel');
    expect(within(funnel90).getByText('200')).toBeInTheDocument();
    expect(funnel90).toHaveTextContent('Coaches who signed up in the last 90 days');
    expect(screen.getByText(byText('Snapshot of Nov 15, 2026 · change against Aug 17, 2026'))).toBeInTheDocument();
  });

  it('says there is nothing to compare with when the period has no baseline snapshot', async () => {
    serveAdminReports({ [AdminReportPeriod.Days30]: adminReportsPayload({ comparedFrom: undefined }) });
    renderAdminReports();

    expect(await screen.findByText(byText('Snapshot of Nov 15, 2026 · no earlier snapshot to compare'))).toBeInTheDocument();
  });

  it('shows an empty Usage state and no change chips when there is no snapshot yet', async () => {
    const payload = adminReportsPayload({
      snapshotDay: undefined,
      usage: undefined,
      money: {
        monthlyRevenueAfterPromotions: [{ currency: 'CZK', value: 0 }],
        lostToCancellations: { count: { value: 0 }, amounts: [] },
      },
    });
    serveAdminReports({ [AdminReportPeriod.Days30]: payload });
    renderAdminReports();

    expect(await screen.findByText('No daily snapshot yet')).toBeInTheDocument();
    expect(screen.getByText(/No daily snapshot exists yet, so there are no usage figures/)).toBeInTheDocument();
    expect(screen.queryByText('Active coaches')).not.toBeInTheDocument();
    expect(block('Monthly revenue')).not.toHaveTextContent('%');
  });

  it('counts promotion codes without naming them', async () => {
    serveAdminReports({ [AdminReportPeriod.Days30]: adminReportsPayload() });
    renderAdminReports();

    const row = (await screen.findByText('Promotion codes used')).parentElement as HTMLElement;
    expect(row).toHaveTextContent(/^Promotion codes used6$/);
  });

  it('names plans in the UI language', async () => {
    serveAdminReports({ [AdminReportPeriod.Days30]: adminReportsPayload() });
    renderAdminReports('cs');

    expect(await screen.findByText('Start CS')).toBeInTheDocument();
    expect(screen.getByText('Pro CS')).toBeInTheDocument();
  });

  it('shows an error without numbers and loads the reports on retry', async () => {
    server.use(http.get(apiUrl('/admin/reports'), () => problem(500, 'REPORTS_FAILED')));
    const { user } = renderAdminReports();

    expect(await screen.findByText('Could not load the reports.')).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Money', level: 2 })).not.toBeInTheDocument();

    serveAdminReports({ [AdminReportPeriod.Days30]: adminReportsPayload() });
    await user.click(screen.getByRole('button', { name: 'Try again' }));

    expect(await screen.findByRole('heading', { name: 'Money', level: 2 })).toBeInTheDocument();
    expect(screen.queryByText('Could not load the reports.')).not.toBeInTheDocument();
  });

  it('hides the Stripe dashboard link when no URL is configured', async () => {
    serveAdminReports({ [AdminReportPeriod.Days30]: adminReportsPayload() });
    renderAdminReports();

    await screen.findByRole('heading', { name: 'Money', level: 2 });
    expect(screen.queryByRole('link', { name: /Stripe dashboard/ })).not.toBeInTheDocument();
  });

  it('opens the configured https Stripe dashboard in a new tab and ignores a non-https URL', async () => {
    vi.stubEnv('VITE_STRIPE_DASHBOARD_URL', 'https://dashboard.stripe.com/');
    serveAdminReports({ [AdminReportPeriod.Days30]: adminReportsPayload() });
    const first = renderAdminReports();

    const link = await screen.findByRole('link', { name: /Stripe dashboard/ });
    expect(link).toHaveAttribute('href', 'https://dashboard.stripe.com/');
    expect(link).toHaveAttribute('target', '_blank');
    expect(link).toHaveAttribute('rel', 'noopener noreferrer');
    first.unmount();

    vi.stubEnv('VITE_STRIPE_DASHBOARD_URL', 'http://dashboard.stripe.com/');
    renderAdminReports();
    await screen.findByRole('heading', { name: 'Money', level: 2 });
    expect(screen.queryByRole('link', { name: /Stripe dashboard/ })).not.toBeInTheDocument();
  });
});
