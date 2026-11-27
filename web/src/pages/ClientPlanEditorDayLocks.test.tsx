/**
 * Locked meals in the client plan editor's Day view (epic #1946): the past-day banner, "Add meal" off on a past
 * day, tick vs empty ring per item, the eaten kcal in the day total, the notice on a conflicted card with its
 * Changed / Added / Removed tags, "Mark reviewed" hiding only the notice, an eaten-but-removed meal that stays,
 * and the save-time lock 409 on a day card. Ported from the Playwright spec.
 *
 * The plan, the lock verdicts and the conflicts list come from the harness state; "today" is pinned to a Wednesday.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import { MealKind, MealLockState, OfflineSyncConflictKind } from '@/api/generated';
import {
  FOOD_ITEM_ID,
  GHOST_MEAL_ID,
  PLAN_NAME,
  conflictedLock,
  dayAfter,
  futureStart,
  listedConflict,
  mealLock,
  newPlanEditorState,
  pastStart,
  pinEditorNow,
  removedLock,
  servePlanEditor,
  thisWeekStart,
} from '@/test/clientPlanEditor';
import { renderPlanEditor } from '@/test/renderPlanEditor';
import type { PlanEditorState } from '@/test/clientPlanEditor';

beforeEach(pinEditorNow);
afterEach(() => {
  vi.useRealTimers();
});

/** Mounts the editor, switches to the Day view and opens `day`. */
async function openDayView(state: PlanEditorState, day: 'Mon' | 'Tue') {
  servePlanEditor(state);
  const view = renderPlanEditor();
  await screen.findByRole('heading', { name: PLAN_NAME });
  await screen.findAllByTestId('meal-cell');
  await view.user.click(within(screen.getByRole('group', { name: 'Range' })).getByRole('button', { name: 'Day' }));
  await view.user.click(screen.getByRole('tab', { name: new RegExp(`^${day}`) }));
  await screen.findByTestId('day-view');
  return view;
}

const SAVE_ERROR_TOAST = 'A meal was eaten or locked while you were editing';
const REVIEW_ERROR_TOAST = "Couldn't mark the conflict as reviewed. Please try again.";

describe('plan editor Day view: locked meals', () => {
  it('a past day is read-only: banner, Add meal off, tick vs ring, eaten kcal, nothing planned', async () => {
    const state = newPlanEditorState({ startDate: pastStart() });
    state.locks = [mealLock(state, { lockState: MealLockState.PartlyEaten, eatenItemIds: [FOOD_ITEM_ID] })];
    const { user } = await openDayView(state, 'Mon');

    const banner = screen.getByTestId('day-past-banner');
    expect(banner).toHaveTextContent('is in the past');
    // The formatted date already names the weekday, so the banner names it exactly once.
    expect(banner.textContent?.match(/\bMon(day)?\b/g)).toHaveLength(1);
    // A past day's tab carries a lock, like the week view's header.
    expect(within(screen.getByRole('tab', { name: /^Mon/ })).getByTestId('day-tab-lock')).toBeInTheDocument();
    expect(within(screen.getByRole('tab', { name: /^Tue/ })).getByTestId('day-tab-lock')).toBeInTheDocument();
    const addMeal = screen.getByTestId('day-add-meal');
    expect(addMeal).toBeDisabled();
    expect(addMeal).toHaveAttribute('title', "Meals can't be added to a day that has passed.");

    const card = screen.getByTestId('meal-block');
    expect(card).toHaveAttribute('data-lock', 'PartlyEaten');
    const items = within(card).getAllByTestId('meal-item');
    expect(items).toHaveLength(2);
    expect(items[0]).toHaveAttribute('data-eaten', 'true');
    expect(items[1]).toHaveAttribute('data-eaten', 'false');
    // Locked: no amount boxes, no remove buttons, no note field; and a partly eaten meal carries no time line.
    expect(within(card).queryByRole('textbox')).not.toBeInTheDocument();
    expect(within(card).queryByRole('button', { name: /^Remove/ })).not.toBeInTheDocument();
    expect(within(card).queryByTestId('meal-eaten-at')).not.toBeInTheDocument();
    // Only the ticked apple counts: 80 kcal, not the 187 of the whole snapshot.
    expect(screen.getByTestId('day-eaten')).toHaveTextContent('Eaten 80 kcal');

    await user.click(screen.getByRole('tab', { name: /^Tue/ }));
    expect(await screen.findByTestId('day-past-banner')).toBeInTheDocument();
    expect(screen.getByTestId('day-nothing-planned')).toHaveTextContent('Nothing planned');
    expect(screen.getByTestId('day-add-meal')).toBeDisabled();
  });

  it('a locked week tile shows the eaten kcal after its chip', async () => {
    const state = newPlanEditorState({ startDate: pastStart() });
    state.locks = [mealLock(state, { lockState: MealLockState.PartlyEaten, eatenItemIds: [FOOD_ITEM_ID] })];
    servePlanEditor(state);
    renderPlanEditor();
    await screen.findByRole('heading', { name: PLAN_NAME });

    const tiles = (await screen.findAllByTestId('meal-cell')).filter((tile) => within(tile).queryByTestId('meal-lock-chip'));
    expect(tiles).toHaveLength(1);
    // Only the ticked apple counts (80 kcal), not the 187 of the whole snapshot.
    expect(tiles[0]).toHaveTextContent('80 kcal');
  });

  it("today's tab reads Today and the days before it carry a lock", async () => {
    // The pinned "now" is a Wednesday (ISO weekday 3), so Monday and Tuesday are past.
    const isoWeekday = 3;
    await openDayView(newPlanEditorState({ startDate: thisWeekStart() }), 'Mon');

    const todayTab = screen.getByTestId('day-tab-today');
    expect(todayTab).toHaveTextContent('Today');
    expect(screen.getAllByTestId('day-tab-lock')).toHaveLength(isoWeekday - 1);
  });

  it('the notice on a conflicted card lists the plan items with their tags; Mark reviewed hides only the notice', async () => {
    const state = newPlanEditorState({ startDate: pastStart() });
    state.locks = [conflictedLock(state)];
    state.conflicts = [listedConflict(state)];
    const { user } = await openDayView(state, 'Mon');

    const card = screen.getByTestId('meal-block');
    // The card shows what the client ate (the snapshot), including the banana the coach removed.
    expect(within(card).getAllByTestId('meal-item')).toHaveLength(2);
    expect(within(card).getByTestId('meal-conflict-chip')).toBeInTheDocument();
    expect(screen.getByTestId('day-eaten')).toHaveTextContent('Eaten 187 kcal');

    const panel = await screen.findByTestId('sync-conflict-panel');
    const rows = within(within(panel).getByTestId('conflict-plan-rows')).getAllByRole('listitem');
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveAttribute('data-status', 'changed');
    expect(rows[0]).toHaveTextContent('Changed');
    expect(rows[1]).toHaveAttribute('data-status', 'removed');
    expect(rows[1]).toHaveTextContent('Removed');
    expect(within(panel).getByTestId('conflict-totals')).toHaveTextContent('ate 187 kcal');
    // Each plan row shows amount and kcal; a removed item's name is struck through (jsdom has no stylesheet: the class).
    expect(within(rows[0]).getByTestId('conflict-item-amount')).toHaveTextContent('g');
    expect(rows[0]).toHaveTextContent('kcal');
    expect(within(rows[1]).getByTestId('conflict-item-name')).toHaveClass('line-through');
    // The notice sits below the eaten item rows (jsdom has no layout: document order).
    const lastItem = within(card).getAllByTestId('meal-item').at(-1)!;
    expect(lastItem.compareDocumentPosition(panel) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();

    await user.click(within(panel).getByTestId('conflict-mark-reviewed'));
    await waitFor(() => expect(screen.queryByTestId('sync-conflict-panel')).not.toBeInTheDocument());
    expect(within(card).queryByTestId('meal-conflict-chip')).not.toBeInTheDocument();
    // The meal stays eaten and locked.
    expect(card).toHaveAttribute('data-lock', 'Eaten');
    expect(within(card).getAllByTestId('meal-item')).toHaveLength(2);
  });

  it('an eaten meal the coach removed stays on its day after Mark reviewed', async () => {
    const state = newPlanEditorState({ startDate: pastStart() });
    state.locks = [removedLock(state.startDate)];
    state.conflicts = [
      listedConflict(state, {
        conflictId: 'c-ghost',
        kind: OfflineSyncConflictKind.MealRemoved,
        mealId: GHOST_MEAL_ID,
        mealKind: MealKind.AfternoonSnack,
        logDate: dayAfter(state.startDate),
      }),
    ];
    const { user } = await openDayView(state, 'Tue');

    const ghost = await screen.findByTestId('sync-conflict-ghost-card');
    expect(within(ghost).getByTestId('eaten-item')).toHaveTextContent('Almonds');
    expect(await within(ghost).findByTestId('sync-conflict-panel')).toBeInTheDocument();
    // The removed meal counts toward what the client ate that day.
    expect(screen.getByTestId('day-eaten')).toHaveTextContent('Eaten 130 kcal');
    // It is read-only: no controls on it, and it is not a meal of the plan.
    expect(within(ghost).queryByRole('textbox')).not.toBeInTheDocument();
    expect(screen.queryByTestId('meal-block')).not.toBeInTheDocument();

    await user.click(within(ghost).getByTestId('conflict-mark-reviewed'));
    await waitFor(() => expect(within(screen.getByTestId('sync-conflict-ghost-card')).queryByTestId('sync-conflict-panel')).not.toBeInTheDocument());
    expect(screen.getAllByTestId('sync-conflict-ghost-card')).toHaveLength(1);
    expect(within(screen.getByTestId('sync-conflict-ghost-card')).getByTestId('eaten-item')).toHaveTextContent('Almonds');
    expect(screen.getByTestId('day-eaten')).toHaveTextContent('Eaten 130 kcal');
  });

  it('a 403 on the conflicts list shows no notice, but locked and removed meals still show as eaten', async () => {
    const state = newPlanEditorState({ startDate: pastStart(), conflicts: null });
    state.locks = [conflictedLock(state), removedLock(state.startDate)];
    const { user } = await openDayView(state, 'Mon');

    const card = screen.getByTestId('meal-block');
    expect(card).toHaveAttribute('data-lock', 'Eaten');
    expect(within(card).queryByTestId('meal-conflict-chip')).not.toBeInTheDocument();
    expect(within(card).getAllByTestId('meal-item')[0]).toHaveAttribute('data-eaten', 'true');
    expect(screen.queryByTestId('sync-conflict-strip')).not.toBeInTheDocument();
    expect(screen.queryByTestId('sync-conflict-panel')).not.toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: /^Tue/ }));
    expect(await screen.findAllByTestId('sync-conflict-ghost-card')).toHaveLength(1);
    expect(screen.queryByTestId('sync-conflict-panel')).not.toBeInTheDocument();
    expect(screen.getByTestId('day-eaten')).toHaveTextContent('Eaten 130 kcal');
  });

  it('a failed Mark reviewed leaves the notice and shows the error toast', async () => {
    const state = newPlanEditorState({ startDate: pastStart(), reviewStatus: 500 });
    state.locks = [conflictedLock(state)];
    state.conflicts = [listedConflict(state)];
    const { user } = await openDayView(state, 'Mon');

    await user.click(await screen.findByTestId('conflict-mark-reviewed'));

    expect(await screen.findByText(REVIEW_ERROR_TOAST)).toBeInTheDocument();
    expect(screen.getByTestId('sync-conflict-panel')).toBeInTheDocument();
    expect(screen.getByTestId('meal-conflict-chip')).toBeInTheDocument();
  });

  it('a lock 409 on save outlines the day card, keeps the edit and clears on the next change', async () => {
    const state = newPlanEditorState({ startDate: futureStart(), rejectSaveOnce: true });
    const { user } = await openDayView(state, 'Mon');

    const card = screen.getByTestId('meal-block');
    const note = within(card).getByRole('textbox', { name: 'Note for Breakfast' });
    // clear + paste is one input event, like Playwright's fill(); typing re-renders the editor per key.
    await user.clear(note);
    await user.paste('Eat slowly');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    // The same message also shows inline in the editor; the toast title is what the E2E original asserted.
    expect(
      await screen.findByText(SAVE_ERROR_TOAST, { exact: false, selector: '[data-slot="toast-title"]' }),
    ).toBeInTheDocument();
    expect(card).toHaveAttribute('data-lock-rejected', 'true');
    expect(within(card).getByTestId('meal-lock-conflict')).toBeInTheDocument();
    // The edit is still there: no reload, nothing reverted.
    expect(note).toHaveValue('Eat slowly');

    await user.clear(note);
    await user.paste('Eat slowly, please');
    await waitFor(() => expect(card).not.toHaveAttribute('data-lock-rejected', 'true'));
    expect(within(card).queryByTestId('meal-lock-conflict')).not.toBeInTheDocument();
  });

  // The live path (the client eats the meal, SignalR refetches the plan, no save) has no trigger here either, so the
  // refetch comes from the refused save; the flag itself is derived from the refetched locks and the stored copy.
  it('a locked meal with an unsaved edit shows the "!" badge; Restore in its popover puts back what is stored', async () => {
    const state = newPlanEditorState({ startDate: futureStart(), rejectSaveOnce: true });
    const { user } = await openDayView(state, 'Mon');

    const card = screen.getByTestId('meal-block');
    await user.clear(within(card).getByRole('textbox', { name: 'Note for Breakfast' }));
    await user.paste('Eat slowly');
    state.locks = [mealLock(state)];
    await user.click(screen.getByRole('button', { name: 'Save' }));

    const badge = await within(card).findByTestId('meal-lock-conflict');
    await user.click(badge);
    const popover = await screen.findByTestId('meal-detail');
    expect(within(popover).getByTestId('meal-lock-conflict-notice')).toHaveTextContent(
      'The client ate this meal before you saved your changes.',
    );

    await user.click(within(popover).getByTestId('meal-restore'));
    await waitFor(() => expect(screen.queryByTestId('meal-detail')).not.toBeInTheDocument());
    expect(within(card).queryByTestId('meal-lock-conflict')).not.toBeInTheDocument();
    expect(card).not.toHaveAttribute('data-lock-rejected', 'true');
  });
});
