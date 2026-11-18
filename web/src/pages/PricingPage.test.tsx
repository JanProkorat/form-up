/**
 * '/pricing' — the public pricing page. Ported from the Playwright spec: cards and compare table from the
 * offered plans, the landing nav link, and the error / retry / empty states of the plans request.
 * Plans are admin-editable, so the fixture is ours; the assertions never depend on a seeded plan.
 * Also the launch offer: a banner and crossed-out prices on the plans it covers, and only then.
 */
import { describe, expect, it } from 'vitest';
import { screen, within } from '@testing-library/react';
import { BillingInterval, PromotionDuration, PromotionKind } from '@/api/generated';
import type { GetPublicSubscriptionPlansResponse, PublicPromotionDto, PublicSubscriptionPlanDto } from '@/api/generated';
import EntryPage from '@/pages/EntryPage';
import PricingPage from '@/pages/PricingPage';
import { renderWithProviders } from '@/test/render';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

const PLANS = apiUrl('/subscription-plans');

function plan(overrides: Partial<PublicSubscriptionPlanDto>): PublicSubscriptionPlanDto {
  return {
    code: 'free',
    nameEn: 'Free',
    priceMinorUnits: 0,
    currency: 'CZK',
    billingInterval: BillingInterval.Monthly,
    maxActiveClients: 3,
    bothRolesIncluded: false,
    isMostPopular: false,
    canMessage: true,
    ...overrides,
  };
}

const offered: GetPublicSubscriptionPlansResponse = {
  plans: [
    plan({}),
    plan({ code: 'pro', nameEn: 'Pro', priceMinorUnits: 49900, maxActiveClients: undefined, isMostPopular: true }),
  ],
};

function renderPricing() {
  return renderWithProviders(<PricingPage />, { initialEntries: ['/pricing'] });
}

describe('PricingPage', () => {
  it('shows the heading and a card per plan without redirecting or opening a dialog', async () => {
    server.use(http.get(PLANS, () => HttpResponse.json(offered)));

    const { router } = renderPricing();

    expect(await screen.findByRole('heading', { level: 1, name: 'Plans and pricing' })).toBeInTheDocument();
    expect(await screen.findAllByRole('article')).toHaveLength(2);
    expect(router.state.location.pathname).toBe('/pricing');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('the landing nav Pricing link opens /pricing', async () => {
    server.use(http.get(PLANS, () => HttpResponse.json(offered)));

    const { user, router } = renderWithProviders(
      [
        { path: '/', element: <EntryPage />, children: [{ index: true, element: null }] },
        { path: '/pricing', element: <PricingPage /> },
      ],
      { initialEntries: ['/'] },
    );

    await user.click(within(await screen.findByRole('navigation', { name: 'Main' })).getByRole('link', { name: 'Pricing' }));

    expect(await screen.findByRole('heading', { level: 1, name: 'Plans and pricing' })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/pricing');
  });

  it('the compare table lists all 13 features', async () => {
    server.use(http.get(PLANS, () => HttpResponse.json(offered)));

    renderPricing();

    expect(await screen.findAllByTestId('pricing-feature-row')).toHaveLength(13);
  });

  it('shows the Most popular badge only on the plan flagged as such', async () => {
    server.use(http.get(PLANS, () => HttpResponse.json(offered)));

    renderPricing();

    const pro = await screen.findByRole('article', { name: 'Pro' });
    expect(within(pro).getByText('Most popular', { exact: true })).toBeInTheDocument();
    expect(screen.getAllByText('Most popular', { exact: true })).toHaveLength(1);
  });

  it('a failing plans request shows an alert, and Try again recovers', async () => {
    let requests = 0;
    server.use(
      http.get(PLANS, () => {
        requests += 1;
        return requests === 1 ? HttpResponse.json({}, { status: 500 }) : HttpResponse.json({ plans: [] });
      }),
    );

    const { user } = renderPricing();

    const alert = await screen.findByRole('alert');
    await user.click(within(alert).getByRole('button', { name: 'Try again' }));

    expect(await screen.findByText(/No plans are offered right now/)).toBeInTheDocument();
    expect(requests).toBe(2);
  });

  it('a rate-limited plans request shows the error state and does not retry on its own', async () => {
    let requests = 0;
    server.use(
      http.get(PLANS, () => {
        requests += 1;
        return HttpResponse.json({}, { status: 429 });
      }),
    );

    const { router } = renderPricing();

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/pricing');
    expect(requests).toBe(1);
  });

  it('an empty plan list shows the empty state and no compare table', async () => {
    server.use(http.get(PLANS, () => HttpResponse.json({ plans: [] })));

    renderPricing();

    expect(await screen.findByText(/No plans are offered right now/)).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(screen.queryByRole('article')).not.toBeInTheDocument();
  });
});

const promotion: PublicPromotionDto = {
  name: 'Launch offer',
  kind: PromotionKind.Percent,
  value: 30,
  duration: PromotionDuration.Months,
  durationMonths: 3,
  planCodes: ['pro'],
};

function serveOfferPlans(withPromotion: boolean) {
  const body: GetPublicSubscriptionPlansResponse = {
    plans: [
      plan({}),
      plan({
        code: 'pro',
        nameEn: 'Pro',
        priceMinorUnits: 49900,
        discountedPriceMinorUnits: withPromotion ? 34900 : undefined,
      }),
      plan({ code: 'studio', nameEn: 'Studio', priceMinorUnits: 99900 }),
    ],
    promotion: withPromotion ? promotion : undefined,
  };
  server.use(http.get(PLANS, () => HttpResponse.json(body)));
}

describe('PricingPage launch offer', () => {
  it('shows the banner and a crossed-out regular price only on the covered plan', async () => {
    serveOfferPlans(true);

    renderPricing();

    expect(await screen.findByRole('status')).toHaveTextContent('Launch offer');
    const pro = screen.getByRole('article', { name: 'Pro' });
    expect(within(pro).getByText(/349/)).toBeInTheDocument();
    const regular = within(pro).getByText(/Regular price/).closest('s');
    expect(regular).toHaveTextContent(/499/);
    expect(within(pro).getByText(/then/)).toHaveTextContent(/499/);

    for (const name of ['Free', 'Studio']) {
      const card = screen.getByRole('article', { name });
      expect(within(card).queryByText(/Regular price/)).not.toBeInTheDocument();
      expect(within(card).queryByText(/then/)).not.toBeInTheDocument();
    }
  });

  it('shows neither banner nor crossed prices without a promotion', async () => {
    serveOfferPlans(false);

    renderPricing();

    expect(await screen.findByRole('article', { name: 'Pro' })).toBeInTheDocument();
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
    expect(screen.queryByText(/Regular price/)).not.toBeInTheDocument();
  });
});
