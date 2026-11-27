import { describe, expect, it, vi } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import {
  ClientPaymentKind,
  ClientPaymentStatus,
  PaymentCurrency,
  type ClientPaymentRowDto,
} from '@/api/generated';
import PaymentsTable from '@/components/client-detail/payment/PaymentsTable';
import { renderWithProviders } from '@/test/render';

const ROW: ClientPaymentRowDto = {
  paymentId: '33333333-3333-3333-3333-333333333331',
  kind: ClientPaymentKind.Recurring,
  status: ClientPaymentStatus.Overdue,
  periodStart: '2026-03-01',
  periodEnd: '2026-03-31',
  packageName: 'Basic',
  amount: 100,
  currency: PaymentCurrency.CZK,
  dueDate: '2026-03-05',
};

describe('PaymentsTable', () => {
  it('keeps "Mark paid" as the button name when its label is hidden at narrow widths', async () => {
    const onMarkPaid = vi.fn();
    renderWithProviders(
      <PaymentsTable rows={[ROW]} canRequest onMarkPaid={onMarkPaid} onRequest={vi.fn()} onWaive={vi.fn()} />,
    );

    await userEvent.click(screen.getByRole('button', { name: 'Mark paid' }));

    expect(onMarkPaid).toHaveBeenCalledWith(ROW);
  });
});
