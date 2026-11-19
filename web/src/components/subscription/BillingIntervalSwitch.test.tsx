import { describe, expect, it, vi } from 'vitest';
import { screen } from '@testing-library/react';
import { BillingInterval } from '@/api/generated';
import BillingIntervalSwitch from '@/components/subscription/BillingIntervalSwitch';
import { renderWithProviders } from '@/test/render';

describe('BillingIntervalSwitch', () => {
  it('marks the chosen interval as pressed and reports a click on the other one', async () => {
    const onChange = vi.fn();
    const { user } = renderWithProviders(<BillingIntervalSwitch value={BillingInterval.Monthly} onChange={onChange} />);

    expect(screen.getByRole('button', { name: 'Monthly' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: 'Annual' })).toHaveAttribute('aria-pressed', 'false');

    await user.click(screen.getByRole('button', { name: 'Annual' }));

    expect(onChange).toHaveBeenCalledWith(BillingInterval.Annual);
  });

  it('shows the saving badge on Annual only for a positive saving', () => {
    const { rerender } = renderWithProviders(
      <BillingIntervalSwitch value={BillingInterval.Annual} onChange={() => undefined} savingPercent={17} />,
    );
    expect(screen.getByText('Save up to 17 %')).toBeInTheDocument();

    for (const none of [0, null, undefined]) {
      rerender(<BillingIntervalSwitch value={BillingInterval.Annual} onChange={() => undefined} savingPercent={none} />);
      expect(screen.queryByText(/Save up to/)).not.toBeInTheDocument();
    }
  });
});
