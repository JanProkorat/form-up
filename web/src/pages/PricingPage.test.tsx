/** "/pricing": the launch offer shows as a banner and crossed-out prices on the plans it covers, and only then. */
import { describe, expect, it, vi } from 'vitest';
import { screen, within } from '@testing-library/react';
import {
  BillingInterval,
  PromotionDuration,
  PromotionKind,
  type GetPublicSubscriptionPlansResponse,
  type PublicPromotionDto,
  type PublicSubscriptionPlanDto,
} from '@/api/generated';
import PricingPage from '@/pages/PricingPage';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { renderWithProviders } from '@/test/render';

// The page's nav reads the theme store, which asks matchMedia at import time; jsdom has none.
vi.hoisted(() => {
  window.matchMedia = (query: string) =>
    ({
      matches: false,
      media: query,
      addEventListener: () => {},
      removeEventListener: () => {},
    }) as unknown as MediaQueryList;
});

const PLANS_URL = apiUrl('/subscription-plans');

const plan = (code: string, name: string, priceMinorUnits: number, extra: Partial<PublicSubscriptionPlanDto> = {}) =>
  ({
    code,
    nameCs: name,
    nameEn: name,
    nameDe: name,
    priceMinorUnits,
    currency: 'CZK',
    billingInterval: BillingInterval.Monthly,
    ...extra,
  }) satisfies PublicSubscriptionPlanDto;

const promotion: PublicPromotionDto = {
  name: 'Launch offer',
  kind: PromotionKind.Percent,
  value: 30,
  duration: PromotionDuration.Months,
  durationMonths: 3,
  planCodes: ['pro'],
};

function servePlans(withPromotion: boolean) {
  const body: GetPublicSubscriptionPlansResponse = {
    plans: [
      plan('free', 'Free', 0),
      plan('pro', 'Pro', 49900, { discountedPriceMinorUnits: withPromotion ? 34900 : undefined }),
      plan('studio', 'Studio', 99900),
    ],
    promotion: withPromotion ? promotion : undefined,
  };
  server.use(http.get(PLANS_URL, () => HttpResponse.json(body)));
}

describe('PricingPage launch offer', () => {
  it('shows the banner and a crossed-out regular price only on the covered plan', async () => {
    servePlans(true);

    renderWithProviders(<PricingPage />, { initialEntries: ['/pricing'] });

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
    servePlans(false);

    renderWithProviders(<PricingPage />, { initialEntries: ['/pricing'] });

    expect(await screen.findByRole('article', { name: 'Pro' })).toBeInTheDocument();
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
    expect(screen.queryByText(/Regular price/)).not.toBeInTheDocument();
  });
});
