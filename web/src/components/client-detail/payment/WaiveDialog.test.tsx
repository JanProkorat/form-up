import { beforeEach, describe, expect, it, vi } from 'vitest';
import { delay } from 'msw';
import { screen, waitFor } from '@testing-library/react';
import {
  ClientPaymentKind,
  ClientPaymentStatus,
  PaymentCurrency,
  type ClientPaymentRowDto,
  type GetClientPaymentSummaryResponse,
} from '@/api/generated';
import WaiveDialog from '@/components/client-detail/payment/WaiveDialog';
import { useClientPaymentRows, useClientPaymentSummary } from '@/hooks/useClientPaymentQueries';
import { useToastStore } from '@/stores/toast';
import { Toaster } from '@/components/ui/toast';
import { renderWithProviders } from '@/test/render';
import { apiUrl, http, HttpResponse, problem } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

const CLIENT_ID = '11111111-1111-1111-1111-111111111111';
const PAYMENT_ID = '33333333-3333-3333-3333-333333333331';

function rowOf(status: ClientPaymentStatus): ClientPaymentRowDto {
  return {
    paymentId: PAYMENT_ID,
    kind: ClientPaymentKind.Recurring,
    status,
    periodStart: '2026-03-01',
    periodEnd: '2026-03-31',
    packageName: 'Basic',
    amount: 100,
    currency: PaymentCurrency.CZK,
    dueDate: '2026-03-05',
  };
}

const SUMMARY: GetClientPaymentSummaryResponse = {
  clientId: CLIENT_ID,
  clientName: 'Test Client',
  collaborationEnded: false,
  bankAccountSet: true,
  paidCount: 0,
  totalCount: 1,
};

/** Serves the page; once the save lands the row is Waived and the summary reload is slow. */
function serveWaive(saveResponse: () => Response = () => HttpResponse.json(rowOf(ClientPaymentStatus.Waived))) {
  let settled = false;
  server.use(
    http.get(apiUrl(`/payments/clients/${CLIENT_ID}/payments`), () =>
      HttpResponse.json({ payments: [rowOf(settled ? ClientPaymentStatus.Waived : ClientPaymentStatus.Requested)] }),
    ),
    http.get(apiUrl(`/payments/clients/${CLIENT_ID}/summary`), async () => {
      if (settled) {
        await delay(300);
      }
      return HttpResponse.json(SUMMARY);
    }),
    http.post(apiUrl(`/payments/clients/${CLIENT_ID}/payments/${PAYMENT_ID}/waive`), () => {
      const response = saveResponse();
      settled = response.ok;
      return response;
    }),
  );
}

/** Looks the row up in the live rows, so a refetch that settles it closes the dialog and unmounts its form. */
function LiveWaiveDialog({ onClose }: { onClose: () => void }) {
  const rowsQuery = useClientPaymentRows(CLIENT_ID, true);
  useClientPaymentSummary(CLIENT_ID);
  const row = rowsQuery.data?.find((candidate) => candidate.status === ClientPaymentStatus.Requested) ?? null;
  return <WaiveDialog clientId={CLIENT_ID} clientName="Test Client" row={row} periodLabel="March" onClose={onClose} />;
}

function renderDialog(onClose: () => void = () => undefined) {
  useToastStore.setState({ toasts: [] });
  return renderWithProviders(
    [
      {
        path: '*',
        element: (
          <>
            <LiveWaiveDialog onClose={onClose} />
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

describe('WaiveDialog save', () => {
  beforeEach(() => {
    useToastStore.setState({ toasts: [] });
  });

  it('shows the success toast even when the rows refetch unmounts the form before the summary reloads', async () => {
    serveWaive();
    const onClose = vi.fn();
    const { user } = renderDialog(onClose);

    await user.click(await screen.findByRole('button', { name: 'Waive payment' }));

    await waitFor(() => expect(toastTexts().some((text) => text.includes('Payment waived.'))).toBe(true));
    expect(onClose).toHaveBeenCalled();
  });

  it('shows an error and closes when the payment was settled elsewhere', async () => {
    serveWaive(() => problem(409, 'PAYMENT_ALREADY_WAIVED'));
    const onClose = vi.fn();
    const { user } = renderDialog(onClose);

    await user.click(await screen.findByRole('button', { name: 'Waive payment' }));

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(toastTexts().some((text) => text.includes('Payment waived.'))).toBe(false);
    expect(toastTexts()).not.toHaveLength(0);
  });
});
