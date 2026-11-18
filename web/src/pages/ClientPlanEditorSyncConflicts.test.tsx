/**
 * Offline-sync conflicts in the client plan editor (epics #1814 and #1946): the strip, the badges, the comparison
 * popover (Week view), the notice inside the meal card (Day view), the eaten-but-removed meal, the text-only
 * fallback for a conflict without a snapshot, and marking conflicts reviewed. Ported from the Playwright spec.
 *
 * The conflicts live in the plan's `mealLocks` (the snapshot of what the client ate and the server's item tags);
 * the list of unreviewed conflicts decides whether a notice shows. Both come from the harness state.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import { MealKind, OfflineSyncConflictKind } from '@/api/generated';
import {
  FOOD_NAME,
  GHOST_MEAL_ID,
  PLAN_ID,
  PLAN_NAME,
  conflictedLock,
  dayAfter,
  listedConflict,
  newPlanEditorState,
  pinEditorNow,
  removedLock,
  servePlanEditor,
} from '@/test/clientPlanEditor';
import { renderPlanEditor } from '@/test/renderPlanEditor';
import type { PlanEditorState } from '@/test/clientPlanEditor';

beforeEach(pinEditorNow);
afterEach(() => {
  vi.useRealTimers();
});

/** Mounts the editor and waits for the Week grid to settle. */
async function openEditor(state: PlanEditorState) {
  servePlanEditor(state);
  const view = renderPlanEditor();
  await screen.findByRole('heading', { name: PLAN_NAME });
  await screen.findAllByTestId('meal-cell');
  return view;
}

/** The Monday breakfast tile of the Week grid. */
const mondayTile = () => document.querySelector<HTMLElement>('[data-testid="meal-cell"][data-day="1"][data-row="0"]')!;

function changedState(over: Partial<PlanEditorState> = {}): PlanEditorState {
  const state = newPlanEditorState(over);
  state.locks = over.locks ?? [conflictedLock(state)];
  state.conflicts = over.conflicts ?? [listedConflict(state)];
  return state;
}

function removedState(over: Partial<PlanEditorState> = {}): PlanEditorState {
  const state = newPlanEditorState(over);
  state.locks = over.locks ?? [removedLock(state.startDate)];
  state.conflicts =
    over.conflicts ??
    [
      listedConflict(state, {
        conflictId: 'c-ghost',
        kind: OfflineSyncConflictKind.MealRemoved,
        mealId: GHOST_MEAL_ID,
        mealKind: MealKind.AfternoonSnack,
        mealLogged: true,
        logDate: dayAfter(state.startDate),
      }),
    ];
  return state;
}

describe('plan editor offline-sync conflicts', () => {
  it('a changed meal: strip, badges, comparison popover and mark reviewed', async () => {
    const state = changedState();
    const { user } = await openEditor(state);

    const strip = await screen.findByTestId('sync-conflict-strip');
    expect(strip).toHaveTextContent('ate 1 meal offline on an older version of this plan');
    expect(within(screen.getByRole('tab', { name: /Week 1/ })).getByTestId('sync-conflict-badge')).toBeInTheDocument();
    const tile = mondayTile();
    expect(tile).toHaveAttribute('data-conflict', 'c-1');
    expect(within(tile).getByTestId('sync-conflict-badge')).toBeInTheDocument();

    await user.click(tile);
    const popover = await screen.findByTestId('sync-conflict-popover');
    const saw = within(popover).getByTestId('conflict-column-saw');
    const now = within(popover).getByTestId('conflict-column-now');
    expect(saw).toHaveTextContent('ate · v4');
    // "Your change" is the version of the plan the editor loaded, not the conflict's own currentPlanVersion (6).
    expect(now).toHaveTextContent(`v${state.version}`);
    expect(now).toHaveTextContent('not applied');
    // Client-written names render as plain text: the markup is shown, not interpreted.
    expect(saw).toHaveTextContent('Banana <b>bold</b>');
    expect(saw).toHaveTextContent('150 g');
    expect(saw).toHaveTextContent('Eaten 187 kcal');
    // The coach's side tags each item from the server's tags: the banana the coach removed is struck and tagged.
    expect(now).toHaveTextContent('Removed');
    expect(now).toHaveTextContent(FOOD_NAME);
    expect(now).toHaveTextContent('100 g');

    await user.click(within(popover).getByTestId('conflict-mark-reviewed'));

    await waitFor(() => expect(screen.queryByTestId('sync-conflict-strip')).not.toBeInTheDocument());
    expect(mondayTile()).not.toHaveAttribute('data-conflict');
    // The marker goes; the meal stays eaten and locked.
    expect(mondayTile()).toHaveAttribute('data-lock', 'Eaten');
    expect(state.reviewed).toEqual(['c-1']);
  });

  it('the Day view card shows what the client ate, with the notice listing the plan items and their tags', async () => {
    const { user } = await openEditor(changedState());

    await user.click(within(screen.getByRole('group', { name: 'Range' })).getByRole('button', { name: 'Day' }));
    await user.click(screen.getByRole('tab', { name: /^Mon/ }));

    const card = await screen.findByTestId('meal-block');
    expect(card).toHaveAttribute('data-conflict', 'c-1');
    // The card is the snapshot: the banana the coach removed is still on it, ticked.
    const items = within(card).getAllByTestId('meal-item');
    expect(items).toHaveLength(2);
    expect(items.find((item) => item.textContent?.includes('Banana <b>bold</b>'))).toHaveAttribute('data-eaten', 'true');
    expect(within(card).getByTestId('meal-conflict-chip')).toBeInTheDocument();

    const panel = screen.getByTestId('sync-conflict-panel');
    expect(screen.getByTestId('sync-conflict-strip')).toBeInTheDocument();
    expect(within(screen.getByRole('tab', { name: /^Mon/ })).getByTestId('sync-conflict-badge')).toBeInTheDocument();
    const rows = within(within(panel).getByTestId('conflict-plan-rows')).getAllByRole('listitem');
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveAttribute('data-status', 'changed');
    expect(rows[1]).toHaveAttribute('data-status', 'removed');
    expect(within(panel).getByTestId('conflict-totals')).toHaveTextContent('ate 187 kcal');
  });

  it('a removed but eaten meal is a read-only tile in a row of its own and stays after Mark reviewed', async () => {
    const { user } = await openEditor(removedState());

    // The plan has no snack row, so the tile gets one appended after the last row, on Tuesday. No cell takes a drop there.
    const ghost = await screen.findByTestId('sync-conflict-ghost');
    expect(ghost).toHaveAttribute('data-day', '2');
    expect(ghost).toHaveAttribute('data-unreviewed', 'true');
    expect(screen.getAllByTestId('sync-conflict-ghost-row')).toHaveLength(1);
    expect(screen.getAllByTestId('meal-cell')).toHaveLength(7);

    await user.click(ghost);
    const popover = await screen.findByTestId('sync-conflict-popover');
    expect(popover).toHaveTextContent('removed, eaten offline');
    expect(within(popover).getByTestId('conflict-column-saw')).toHaveTextContent('Almonds');
    expect(within(popover).getByTestId('conflict-column-now')).toHaveTextContent('Not in your plan');

    await user.click(within(popover).getByTestId('conflict-mark-reviewed'));
    await waitFor(() => expect(screen.queryByTestId('sync-conflict-strip')).not.toBeInTheDocument());
    // Reviewed: the notice goes, the eaten meal stays where it was.
    const stillThere = screen.getByTestId('sync-conflict-ghost');
    expect(stillThere).not.toHaveAttribute('data-unreviewed', 'true');
    await user.click(stillThere);
    expect(await screen.findByTestId('removed-meal-popover')).toHaveTextContent('Almonds');
  });

  it('without access to the conflicts list the removed meal still shows as eaten, with no notice', async () => {
    await openEditor(removedState({ conflicts: [] }));

    const ghost = await screen.findByTestId('sync-conflict-ghost');
    expect(screen.queryByTestId('sync-conflict-strip')).not.toBeInTheDocument();
    expect(ghost).not.toHaveAttribute('data-unreviewed', 'true');
    expect(screen.queryByTestId('sync-conflict-badge')).not.toBeInTheDocument();
  });

  it('a conflict without a snapshot shows the text only', async () => {
    const state = changedState();
    state.locks = [conflictedLock(state, { eatenSnapshot: [] })];
    const { user } = await openEditor(state);

    await user.click(mondayTile());
    const popover = await screen.findByTestId('sync-conflict-popover');
    expect(within(popover).getByTestId('conflict-text-only')).toHaveTextContent('saw version 4');
    expect(within(popover).getByTestId('conflict-text-only')).toHaveTextContent(`you saved version ${state.version}`);
    expect(within(popover).queryByTestId('conflict-comparison')).not.toBeInTheDocument();
  });

  it('"Show" opens the first conflict and "Mark all reviewed" clears the strip', async () => {
    const state = changedState();
    const { user } = await openEditor(state);

    await user.click(await screen.findByTestId('sync-conflict-show'));
    expect(await screen.findByTestId('sync-conflict-popover')).toBeInTheDocument();

    await user.click(screen.getByTestId('sync-conflict-mark-all'));
    await waitFor(() => expect(screen.queryByTestId('sync-conflict-strip')).not.toBeInTheDocument());
    expect(state.reviewAllBodies).toEqual([{ planId: PLAN_ID }]);
  });

  it('a failed review keeps the conflict visible and shows an error toast', async () => {
    const state = changedState({ reviewStatus: 500 });
    const { user } = await openEditor(state);

    await user.click(mondayTile());
    await user.click(await screen.findByTestId('conflict-mark-reviewed'));

    expect(await screen.findByText("Couldn't mark the conflict as reviewed. Please try again.")).toBeInTheDocument();
    expect(state.reviewed).toEqual(['c-1']);
    expect(screen.getByTestId('sync-conflict-strip')).toBeInTheDocument();
    expect(screen.getByTestId('sync-conflict-popover')).toBeInTheDocument();
  });
});
