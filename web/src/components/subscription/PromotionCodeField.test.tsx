import { describe, expect, it, vi } from 'vitest';
import { screen } from '@testing-library/react';
import PromotionCodeField from '@/components/subscription/PromotionCodeField';
import { renderWithProviders } from '@/test/render';

describe('PromotionCodeField', () => {
  it('Apply is off until something is typed and hands over the trimmed code', async () => {
    const onApply = vi.fn();
    const { user } = renderWithProviders(<PromotionCodeField appliedCode={null} onApply={onApply} onRemove={() => undefined} />);

    expect(screen.getByRole('button', { name: 'Apply' })).toBeDisabled();

    await user.type(screen.getByLabelText('Have a code?'), '  YEAR40 ');
    await user.click(screen.getByRole('button', { name: 'Apply' }));

    expect(onApply).toHaveBeenCalledWith('YEAR40');
  });

  it('Enter applies the code', async () => {
    const onApply = vi.fn();
    const { user } = renderWithProviders(<PromotionCodeField appliedCode={null} onApply={onApply} onRemove={() => undefined} />);

    await user.type(screen.getByLabelText('Have a code?'), 'YEAR40{Enter}');

    expect(onApply).toHaveBeenCalledWith('YEAR40');
  });

  it('shows an applied code with a remove button', async () => {
    const onRemove = vi.fn();
    const { user } = renderWithProviders(<PromotionCodeField appliedCode="YEAR40" onApply={() => undefined} onRemove={onRemove} />);

    expect(screen.getByText('Code YEAR40')).toBeInTheDocument();
    expect(screen.queryByLabelText('Have a code?')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Remove code' }));

    expect(onRemove).toHaveBeenCalledTimes(1);
  });

  it('an error brings the input back with the typed value, aria-invalid and a described alert', () => {
    renderWithProviders(
      <PromotionCodeField appliedCode="YEAR40" error="This code is for annual plans only." onApply={() => undefined} onRemove={() => undefined} />,
    );

    const input = screen.getByLabelText('Have a code?');
    const alert = screen.getByRole('alert');
    expect(input).toHaveValue('YEAR40');
    expect(input).toHaveAttribute('aria-invalid', 'true');
    expect(input).toHaveAttribute('aria-describedby', alert.id);
    expect(alert).toHaveTextContent('This code is for annual plans only.');
  });

  it('editing after an error drops the refused code', async () => {
    const onRemove = vi.fn();
    const { user } = renderWithProviders(
      <PromotionCodeField appliedCode="YEAR40" error="This code can't be used." onApply={() => undefined} onRemove={onRemove} />,
    );

    await user.type(screen.getByLabelText('Have a code?'), '1');

    expect(onRemove).toHaveBeenCalled();
  });

  it('disabled turns the input and the button off', () => {
    renderWithProviders(<PromotionCodeField appliedCode={null} disabled onApply={() => undefined} onRemove={() => undefined} />);

    expect(screen.getByLabelText('Have a code?')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Apply' })).toBeDisabled();
  });
});
