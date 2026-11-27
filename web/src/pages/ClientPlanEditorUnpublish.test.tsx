/**
 * "Unpublish week N" in the client plan editor's actions menu: offered only for a published, not-yet-started week
 * of an Active plan; the confirm sends one POST and the week turns back into a draft; a refused unpublish keeps it.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { WeekStatus } from '@/api/generated';
import {
  PLAN_ID,
  PLAN_NAME,
  editorPlan,
  newPlanEditorState,
  pastStart,
  pinEditorNow,
  servePlanEditor,
} from '@/test/clientPlanEditor';
import type { PlanEditorState } from '@/test/clientPlanEditor';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { renderPlanEditor } from '@/test/renderPlanEditor';

beforeEach(pinEditorNow);
afterEach(() => {
  vi.useRealTimers();
});

const publishedActive = (over: Partial<PlanEditorState> = {}) =>
  newPlanEditorState({ status: 'Active', weekStatus: WeekStatus.Published, ...over });

/** Answers the unpublish POST and counts the calls; `refuse` answers a coded 409 instead. */
function serveUnpublish(state: PlanEditorState, refuse?: string) {
  const calls = { count: 0 };
  server.use(
    http.post(apiUrl(`/nutrition/plans/${PLAN_ID}/weeks/1/unpublish`), () => {
      calls.count += 1;
      if (refuse) {
        return HttpResponse.json(
          { title: 'Refused', status: 409, errorCode: refuse },
          { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
        );
      }
      return HttpResponse.json(editorPlan({ ...state, weekStatus: WeekStatus.Draft, version: state.version + 1 }));
    }),
  );
  return calls;
}

async function openMenu() {
  await screen.findByRole('heading', { name: PLAN_NAME });
  await userEvent.click(await screen.findByTestId('plan-actions-trigger'));
}

describe('plan editor: unpublish week', () => {
  it('is not offered on a draft week or on a closed plan', async () => {
    servePlanEditor(newPlanEditorState({ status: 'Active' }));
    const draft = renderPlanEditor();
    await openMenu();
    expect(screen.getByRole('menuitem', { name: 'Finish plan' })).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Unpublish week 1' })).not.toBeInTheDocument();
    draft.unmount();

    servePlanEditor(publishedActive({ status: 'Completed', startDate: pastStart() }));
    renderPlanEditor();
    await screen.findByRole('heading', { name: PLAN_NAME });
    expect(screen.queryByTestId('plan-actions-trigger')).not.toBeInTheDocument();
  });

  it('confirming sends one request and the week becomes a draft again', async () => {
    const state = publishedActive();
    servePlanEditor(state);
    const calls = serveUnpublish(state);
    renderPlanEditor();
    await openMenu();
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Unpublish week 1' }));

    const dialog = await screen.findByTestId('unpublish-week-dialog');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Back to draft' }));

    expect(await screen.findByText('Week 1 is unpublished.')).toBeInTheDocument();
    expect(calls.count).toBe(1);
    expect(await screen.findByRole('button', { name: 'Publish week 1' })).toBeEnabled();
  });

  it('a refused unpublish shows the translated reason and keeps the week published', async () => {
    const state = publishedActive();
    servePlanEditor(state);
    const calls = serveUnpublish(state, 'WEEK_HAS_MEAL_LOGS');
    renderPlanEditor();
    await openMenu();
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Unpublish week 1' }));
    await userEvent.click(within(await screen.findByTestId('unpublish-week-dialog')).getByRole('button', { name: 'Back to draft' }));

    expect(await screen.findByText(/already logged meals in this week/)).toBeInTheDocument();
    expect(calls.count).toBe(1);
    await waitFor(() => expect(screen.queryByTestId('unpublish-week-dialog')).not.toBeInTheDocument());
    await openMenu();
    expect(screen.getByRole('menuitem', { name: 'Unpublish week 1' })).toBeInTheDocument();
  });
});
