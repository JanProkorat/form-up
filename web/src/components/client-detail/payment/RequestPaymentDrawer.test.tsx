import { beforeEach, describe, expect, it } from 'vitest';
import { delay } from 'msw';
import { screen, waitFor } from '@testing-library/react';
import {
  ClientPaymentKind,
  ClientPaymentStatus,
  PaymentCurrency,
  type ClientPaymentRowDto,
  type GetClientPaymentSummaryResponse,
  type PaymentSettingsDto,
  type RequestClientPaymentResponse,
} from '@/api/generated';
import RequestPaymentDrawer from '@/components/client-detail/payment/RequestPaymentDrawer';
import { useClientPaymentRows, useClientPaymentSummary } from '@/hooks/useClientPaymentQueries';
import { useToastStore } from '@/stores/toast';
import { Toaster } from '@/components/ui/toast';
import { renderWithProviders } from '@/test/render';
import { apiUrl, http, HttpResponse, problem } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

const CLIENT_ID = '11111111-1111-1111-1111-111111111111';
const PAYMENT_ID = '33333333-3333-3333-3333-333333333331';

const ROW: ClientPaymentRowDto = {
  paymentId: PAYMENT_ID,
  kind: ClientPaymentKind.Recurring,
  status: ClientPaymentStatus.Upcoming,
  periodStart: '2026-03-01',
  periodEnd: '2026-03-31',
  packageName: 'Basic',
  amount: 100,
  currency: PaymentCurrency.CZK,
  dueDate: '2026-03-05',
};

const SETTINGS: PaymentSettingsDto = { iban: 'CZ6508000000192000145399', paymentDueDays: 7 };

function summaryOf(collaborationEnded: boolean): GetClientPaymentSummaryResponse {
  return {
    clientId: CLIENT_ID,
    clientName: 'Test Client',
    collaborationEnded,
    bankAccountSet: true,
    paidCount: 0,
    totalCount: 1,
  };
}

/**
 * Serves the page. Once the request lands the summary says the collaboration ended, which unmounts the drawer
 * before the slow rows reload finishes.
 */
function serveRequest(saveResponse: () => Response = () => HttpResponse.json({ payment: ROW, emailSent: true })) {
  let hasSaved = false;
  server.use(
    http.get(apiUrl('/payments/settings'), () => HttpResponse.json(SETTINGS)),
    http.get(apiUrl(`/payments/clients/${CLIENT_ID}/summary`), () => HttpResponse.json(summaryOf(hasSaved))),
    http.get(apiUrl(`/payments/clients/${CLIENT_ID}/payments`), async () => {
      if (hasSaved) {
        await delay(300);
      }
      return HttpResponse.json({ payments: [ROW] });
    }),
    http.post(apiUrl(`/payments/clients/${CLIENT_ID}/payments/${PAYMENT_ID}/request`), () => {
      const response = saveResponse();
      hasSaved = response.ok;
      return response;
    }),
  );
}

function LiveRequestDrawer() {
  const summaryQuery = useClientPaymentSummary(CLIENT_ID);
  useClientPaymentRows(CLIENT_ID, true);
  if (!summaryQuery.data || summaryQuery.data.collaborationEnded) {
    return null;
  }
  return (
    <RequestPaymentDrawer
      open
      onOpenChange={() => undefined}
      clientId={CLIENT_ID}
      clientName="Test Client"
      rows={[ROW]}
      initialPaymentId={PAYMENT_ID}
    />
  );
}

function renderDrawer() {
  useToastStore.setState({ toasts: [] });
  return renderWithProviders(
    [
      {
        path: '*',
        element: (
          <>
            <LiveRequestDrawer />
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

describe('RequestPaymentDrawer send', () => {
  beforeEach(() => {
    useToastStore.setState({ toasts: [] });
  });

  it('shows the sent toast even when the summary refetch unmounts the drawer before the rows reload', async () => {
    serveRequest();
    const { user } = renderDrawer();

    await user.click(await screen.findByRole('button', { name: 'Send request' }));

    await waitFor(() => expect(toastTexts().some((text) => text.includes('Payment request sent.'))).toBe(true));
  });

  it('says so when the notification went out but the email did not', async () => {
    serveRequest(() => HttpResponse.json({ payment: ROW, emailSent: false } satisfies RequestClientPaymentResponse));
    const { user } = renderDrawer();

    await user.click(await screen.findByRole('button', { name: 'Send request' }));

    await waitFor(() =>
      expect(toastTexts().some((text) => text.includes('Notification sent, but the email could not be delivered.'))).toBe(
        true,
      ),
    );
  });

  it('keeps the drawer open with an inline message when the request is rate limited', async () => {
    serveRequest(() => problem(429, 'PAYMENT_REQUEST_RATE_LIMITED'));
    const { user } = renderDrawer();

    await user.click(await screen.findByRole('button', { name: 'Send request' }));

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Send request' })).toBeInTheDocument();
    expect(toastTexts().some((text) => text.includes('Payment request sent.'))).toBe(false);
  });
});
