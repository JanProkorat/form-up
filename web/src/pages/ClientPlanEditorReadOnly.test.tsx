/**
 * The client plan editor on a closed plan: the side panel stays, opens on the plan info with its dates and
 * disabled fields, and has no Library tab; an open plan keeps the Library tab.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, within } from '@testing-library/react';
import { PLAN_NAME, newPlanEditorState, pastStart, pinEditorNow, servePlanEditor } from '@/test/clientPlanEditor';
import { renderPlanEditor } from '@/test/renderPlanEditor';

beforeEach(pinEditorNow);
afterEach(() => {
  vi.useRealTimers();
});

describe('plan editor: closed plan', () => {
  it('keeps the plan info panel: Info tab open, end date shown, fields disabled, no Library tab or dates hint', async () => {
    servePlanEditor(newPlanEditorState({ status: 'Completed', startDate: pastStart() }));
    renderPlanEditor();
    await screen.findByRole('heading', { name: PLAN_NAME });

    const tabs = await screen.findByRole('tablist', { name: 'Side panel' });
    expect(within(tabs).getByRole('tab', { name: 'Plan info' })).toHaveAttribute('aria-selected', 'true');
    expect(within(tabs).queryByRole('tab', { name: 'Library' })).not.toBeInTheDocument();

    // pastStart() is Mon 8 Jun; one week ends Sun 14 Jun.
    const endDate = await screen.findByTestId('plan-end-date');
    expect(endDate).toHaveTextContent('Sun, Jun 14');
    // Fades like the disabled start date; jsdom has no stylesheets, so assert the class.
    expect(endDate).toHaveClass('opacity-60');
    expect(screen.getByLabelText(/^Plan name/)).toBeDisabled();
    expect(screen.getByLabelText('Calories')).toBeDisabled();
    expect(screen.queryByText(/add or remove weeks/)).not.toBeInTheDocument();
  });

  it('an open plan still offers the Library tab and keeps the end date unfaded', async () => {
    servePlanEditor(newPlanEditorState());
    renderPlanEditor();
    await screen.findByRole('heading', { name: PLAN_NAME });

    expect(await screen.findByRole('tab', { name: 'Library' })).toBeInTheDocument();
    expect(await screen.findByTestId('plan-end-date')).not.toHaveClass('opacity-60');
  });
});
