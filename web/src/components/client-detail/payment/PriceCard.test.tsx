import { beforeEach, describe, expect, it } from 'vitest';
import { delay } from 'msw';
import { screen, waitFor } from '@testing-library/react';
import {
  PackageBillingInterval,
  PaymentCurrency,
  type ClientPaymentSummaryTermsDto,
  type CoachPackageDto,
  type GetClientPaymentSummaryResponse,
  type SetClientPaymentTermsRequest,
} from '@/api/generated';
import PriceCard from '@/components/client-detail/payment/PriceCard';
import { useClientPaymentSummary } from '@/hooks/useClientPaymentQueries';
import { useToastStore } from '@/stores/toast';
import { Toaster } from '@/components/ui/toast';
import { renderWithProviders } from '@/test/render';
import { apiUrl, http, HttpResponse, problem } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

const CLIENT_ID = '11111111-1111-1111-1111-111111111111';
const PACKAGE_A = '22222222-2222-2222-2222-222222222221';
const PACKAGE_B = '22222222-2222-2222-2222-222222222222';

function packageDto(packageId: string, name: string, price: number): CoachPackageDto {
  return {
    packageId,
    name,
    price,
    currency: PaymentCurrency.CZK,
    billing: PackageBillingInterval.Monthly,
    showOnProfile: false,
    clientCount: 0,
  };
}

function termsOf(packageId: string, name: string, price: number): ClientPaymentSummaryTermsDto {
  return {
    packageId,
    packageName: name,
    packagePrice: price,
    currency: PaymentCurrency.CZK,
    billing: PackageBillingInterval.Monthly,
    effectiveFromUtc: packageId === PACKAGE_A ? '2026-01-01T00:00:00Z' : '2026-02-01T00:00:00Z',
  };
}

function summaryOf(terms: ClientPaymentSummaryTermsDto): GetClientPaymentSummaryResponse {
  return {
    clientId: CLIENT_ID,
    clientName: 'Test Client',
    collaborationEnded: false,
    bankAccountSet: true,
    terms,
    price: { basePrice: terms.packagePrice, discountAmount: 0, isCustomPrice: false, amount: terms.packagePrice },
    paidCount: 0,
    totalCount: 0,
  };
}

/** Feeds PriceCard the live summary, like the payment tab does, so a refetch can change its key. */
function LivePriceCard() {
  const summaryQuery = useClientPaymentSummary(CLIENT_ID);
  return summaryQuery.data ? <PriceCard clientId={CLIENT_ID} clientName="Test Client" summary={summaryQuery.data} /> : null;
}

/** Serves the page; the summary switches to package B once the save lands, and the packages reload is slow. */
function servePriceCard(saveResponse: () => Response = () => HttpResponse.json({ packageId: PACKAGE_B })) {
  const saved: SetClientPaymentTermsRequest[] = [];
  let hasSaved = false;
  server.use(
    http.get(apiUrl(`/payments/clients/${CLIENT_ID}/summary`), () =>
      HttpResponse.json(hasSaved ? summaryOf(termsOf(PACKAGE_B, 'Gold', 200)) : summaryOf(termsOf(PACKAGE_A, 'Basic', 100))),
    ),
    http.get(apiUrl('/payments/packages'), async () => {
      if (hasSaved) {
        await delay(300);
      }
      return HttpResponse.json({ items: [packageDto(PACKAGE_A, 'Basic', 100), packageDto(PACKAGE_B, 'Gold', 200)] });
    }),
    http.get(apiUrl('/payments/discounts'), () => HttpResponse.json({ items: [] })),
    http.put(apiUrl(`/payments/clients/${CLIENT_ID}/terms`), async ({ request }) => {
      saved.push((await request.json()) as SetClientPaymentTermsRequest);
      const response = saveResponse();
      hasSaved = response.ok;
      return response;
    }),
  );
  return saved;
}

function renderPriceCard() {
  useToastStore.setState({ toasts: [] });
  return renderWithProviders(
    [
      {
        path: '*',
        element: (
          <>
            <LivePriceCard />
            <Toaster />
          </>
        ),
      },
    ],
    { auth: { roles: ['Trainer'] } },
  );
}

function toastTexts() {
  return Array.from(document.querySelectorAll('[data-slot="toast"]')).map((toast) => toast.textContent ?? '');
}

describe('PriceCard save', () => {
  beforeEach(() => {
    useToastStore.setState({ toasts: [] });
  });

  it('shows the saved toast even when the summary refetch re-keys the form before the packages reload', async () => {
    const saved = servePriceCard();
    const { user } = renderPriceCard();

    await user.selectOptions(await screen.findByLabelText('Package'), PACKAGE_B);
    await user.click(screen.getByRole('button', { name: 'Save price' }));

    await waitFor(() => expect(toastTexts().some((text) => text.includes('Price saved.'))).toBe(true));
    expect(saved).toHaveLength(1);
    expect(saved[0].packageId).toBe(PACKAGE_B);
  });

  it('shows the currency mismatch toast when the save is rejected', async () => {
    const saved = servePriceCard(() => problem(400, 'CURRENCY_MISMATCH'));
    const { user } = renderPriceCard();

    await user.selectOptions(await screen.findByLabelText('Package'), PACKAGE_B);
    await user.click(screen.getByRole('button', { name: 'Save price' }));

    await waitFor(() =>
      expect(
        toastTexts().some((text) => text.includes('A fixed discount must be in the same currency as the package.')),
      ).toBe(true),
    );
    expect(saved).toHaveLength(1);
    expect(toastTexts().some((text) => text.includes('Price saved.'))).toBe(false);
  });
});
