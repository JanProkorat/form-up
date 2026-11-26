import { beforeEach, describe, expect, it } from 'vitest';
import { delay } from 'msw';
import { screen, waitFor } from '@testing-library/react';
import {
  ClientPaymentKind,
  ClientPaymentStatus,
  PaymentCurrency,
  type AddClientOneOffPaymentRequest,
  type ClientPaymentRowDto,
  type GetClientPaymentSummaryResponse,
} from '@/api/generated';
import OneOffDialog from '@/components/client-detail/payment/OneOffDialog';
import { useClientPaymentRows, useClientPaymentSummary } from '@/hooks/useClientPaymentQueries';
import { useToastStore } from '@/stores/toast';
import { Toaster } from '@/components/ui/toast';
import { renderWithProviders } from '@/test/render';
import { apiUrl, http, HttpResponse, problem } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

const CLIENT_ID = '11111111-1111-1111-1111-111111111111';

const ONE_OFF_ROW: ClientPaymentRowDto = {
  paymentId: '33333333-3333-3333-3333-333333333339',
  kind: ClientPaymentKind.OneOff,
  status: ClientPaymentStatus.Upcoming,
  title: 'Consultation',
  amount: 500,
  currency: PaymentCurrency.CZK,
  dueDate: '2026-12-10',
};

function summaryOf(collaborationEnded: boolean): GetClientPaymentSummaryResponse {
  return {
    clientId: CLIENT_ID,
    clientName: 'Test Client',
    collaborationEnded,
    bankAccountSet: true,
    paidCount: 0,
    totalCount: 0,
  };
}

/**
 * Serves the page. Once the add lands the summary says the collaboration ended, which unmounts the dialog
 * the way ClientPaymentTab does (`!ended &&`), while the rows reload is still slow.
 */
function serveOneOff(saveResponse: () => Response = () => HttpResponse.json(ONE_OFF_ROW)) {
  const saved: AddClientOneOffPaymentRequest[] = [];
  let hasSaved = false;
  server.use(
    http.get(apiUrl(`/payments/clients/${CLIENT_ID}/summary`), () => HttpResponse.json(summaryOf(hasSaved))),
    http.get(apiUrl(`/payments/clients/${CLIENT_ID}/payments`), async () => {
      if (hasSaved) {
        await delay(300);
      }
      return HttpResponse.json({ payments: [] });
    }),
    http.post(apiUrl(`/payments/clients/${CLIENT_ID}/one-off`), async ({ request }) => {
      saved.push((await request.json()) as AddClientOneOffPaymentRequest);
      const response = saveResponse();
      hasSaved = response.ok;
      return response;
    }),
  );
  return saved;
}

function LiveOneOffDialog() {
  const summaryQuery = useClientPaymentSummary(CLIENT_ID);
  useClientPaymentRows(CLIENT_ID, true);
  if (!summaryQuery.data || summaryQuery.data.collaborationEnded) {
    return null;
  }
  return (
    <OneOffDialog
      open
      onOpenChange={() => undefined}
      clientId={CLIENT_ID}
      clientName="Test Client"
      defaultCurrency={PaymentCurrency.CZK}
      defaultDueDays={7}
    />
  );
}

function renderDialog() {
  useToastStore.setState({ toasts: [] });
  return renderWithProviders(
    [
      {
        path: '*',
        element: (
          <>
            <LiveOneOffDialog />
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

async function fillAndSubmit(user: ReturnType<typeof renderDialog>['user']) {
  await user.type(await screen.findByLabelText('What it is for'), 'Consultation');
  await user.type(screen.getByLabelText('Amount'), '500');
  await user.click(screen.getByRole('button', { name: 'Add payment' }));
}

describe('OneOffDialog save', () => {
  beforeEach(() => {
    useToastStore.setState({ toasts: [] });
  });

  it('shows the success toast even when the summary refetch unmounts the dialog before the rows reload', async () => {
    const saved = serveOneOff();
    const { user } = renderDialog();

    await fillAndSubmit(user);

    await waitFor(() => expect(toastTexts().some((text) => text.includes('One-off payment added.'))).toBe(true));
    expect(saved).toHaveLength(1);
    expect(saved[0].title).toBe('Consultation');
    expect(saved[0].amount).toBe(500);
  });

  it('shows an error toast and keeps the dialog open when the add is rejected', async () => {
    const saved = serveOneOff(() => problem(400, 'VALIDATION_FAILED'));
    const { user } = renderDialog();

    await fillAndSubmit(user);

    await waitFor(() => expect(toastTexts()).not.toHaveLength(0));
    expect(saved).toHaveLength(1);
    expect(toastTexts().some((text) => text.includes('One-off payment added.'))).toBe(false);
    expect(screen.getByRole('button', { name: 'Add payment' })).toBeInTheDocument();
  });
});
