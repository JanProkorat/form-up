/**
 * A version conflict on save in the client plan editor: the banner talks about the plan, not a template,
 * keeps the edits and offers "Reload latest".
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, within } from '@testing-library/react';
import {
  PLAN_ID,
  PLAN_NAME,
  futureStart,
  newPlanEditorState,
  pinEditorNow,
  servePlanEditor,
} from '@/test/clientPlanEditor';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { renderPlanEditor } from '@/test/renderPlanEditor';

beforeEach(pinEditorNow);
afterEach(() => {
  vi.useRealTimers();
});

describe('client plan editor: version conflict on save', () => {
  it('shows the plan wording (not the template one) and the reload button', async () => {
    servePlanEditor(newPlanEditorState({ startDate: futureStart() }));
    // Registered after the shared handlers, so it answers the save first.
    server.use(
      http.put(apiUrl(`/nutrition/plans/${PLAN_ID}`), () =>
        HttpResponse.json(
          { title: 'Conflict', status: 409, errorCode: 'PLAN_VERSION_CONFLICT' },
          { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    );
    const { user } = renderPlanEditor();
    await screen.findByRole('heading', { name: PLAN_NAME });
    await screen.findAllByTestId('meal-cell');
    await user.click(within(screen.getByRole('group', { name: 'Range' })).getByRole('button', { name: 'Day' }));
    await user.click(screen.getByRole('tab', { name: /^Mon/ }));
    const card = await screen.findByTestId('meal-block');
    const note = within(card).getByRole('textbox', { name: 'Note for Breakfast' });
    await user.clear(note);
    await user.paste('Eat slowly');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    const banner = await screen.findByRole('alert');
    expect(banner).toHaveTextContent('This plan was changed somewhere else since you opened it.');
    expect(banner).not.toHaveTextContent(/template/i);
    expect(within(banner).getByRole('button', { name: 'Reload latest' })).toBeInTheDocument();
    expect(note).toHaveValue('Eat slowly');
  });
});
