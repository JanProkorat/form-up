import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useUnsavedChangesBlocker } from '@/hooks/useUnsavedChangesBlocker';
import { useTranslation } from 'react-i18next';
import { AutoScroller, Feedback, KeyboardSensor, PointerSensor } from '@dnd-kit/dom';
import { move } from '@dnd-kit/helpers';
import { DragDropProvider, DragOverlay } from '@dnd-kit/react';
import { AlertTriangle, BookOpen, Info } from 'lucide-react';
import { MealKind } from '@/api/generated';
import { Button } from '@/components/ui/button';
import EditorSidePanel, { type EditorSideTabDef } from '@/components/editor/EditorSidePanel';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import AddMealSheet, { type AddMealMode } from '@/components/plan-editor/AddMealSheet';
import CardDropDialog from '@/components/plan-editor/CardDropDialog';
import LabelDropDialog, { type LabelChoice } from '@/components/plan-editor/LabelDropDialog';
import { useToastStore } from '@/stores/toast';
import DaySummaryCard from '@/components/plan-editor/DaySummaryCard';
import DayNavigator from '@/components/plan-editor/DayNavigator';
import DayView from '@/components/plan-editor/DayView';
import GridDragOverlay from '@/components/plan-editor/GridDragOverlay';
import LibraryPanel from '@/components/plan-editor/LibraryPanel';
import LockedMealPopover from '@/components/plan-editor/LockedMealPopover';
import MealDetail from '@/components/plan-editor/MealDetail';
import MealLockLegend from '@/components/plan-editor/MealLockLegend';
import {
  dayEatenKcal,
  dayHasLock,
  isPastDay,
  findLockConflicts,
  findStoredMeal,
  keepLocked,
  LockConflictsContext,
  LockRejectionContext,
  RestoreRejectedContext,
  mealLockView,
  MealLocksContext,
  rejectionFingerprint,
  sameFingerprint,
  slotDate,
  type LockRejection,
  type PlanMealLocks,
} from '@/components/plan-editor/meal-locks';
import MealPicker from '@/components/plan-editor/MealPicker';
import NutritionView, { NutritionLegend } from '@/components/plan-editor/NutritionView';
import { PopoverBoundaryContext } from '@/components/plan-editor/PopoverBoundary';
import PlanEditorHeader from '@/components/plan-editor/PlanEditorHeader';
import RemoveMealRowDialog, { type PendingRowRemoval } from '@/components/plan-editor/RemoveMealRowDialog';
import RemoveWeekDialog from '@/components/plan-editor/RemoveWeekDialog';
import TemplateInfoPanel, { type PlanUsage } from '@/components/plan-editor/TemplateInfoPanel';
import SyncConflictPopoverBody from '@/components/plan-editor/sync-conflicts/SyncConflictPopoverBody';
import SyncConflictStrip from '@/components/plan-editor/sync-conflicts/SyncConflictStrip';
import {
  buildSyncConflictsModel,
  SyncConflictsContext,
  type PlanEditorSyncConflicts,
} from '@/components/plan-editor/sync-conflicts/SyncConflictsContext';
import { placeConflicts, type PlacedConflict } from '@/components/plan-editor/sync-conflicts/syncConflictModel';
import WeekdayPills from '@/components/plan-editor/WeekdayPills';
import WeekGrid, { type SelectedCell } from '@/components/plan-editor/WeekGrid';
import WeekTabs from '@/components/plan-editor/WeekTabs';
import { mealKindLabelKey } from '@/components/plan-editor/plan-editor-format';
import {
  cellDragView,
  dragActivationConstraints,
  mealItemNames,
  foodDisplayName,
  gridFaceKey,
  hitBand,
  hitCellLocked,
  labelDragView,
  readCellMealDrag,
  readDayLabel,
  readMealItemDrag,
  readRowLabel,
  DAY_LABEL_DRAG,
  ROW_LABEL_DRAG,
  type CellMealDrag,
  type FrozenBand,
  type FrozenCell,
  type GridDragView,
  type LabelDragBase,
  type LabelTile,
  type MealItemDrag,
} from '@/components/plan-editor/plan-editor-library';
import { dayTotals, mealItemCount, mealTotals } from '@/components/plan-editor/plan-editor-nutrition';
import {
  addItemToCell,
  addKindToWeek,
  addMealRow,
  addMealToDay,
  CHRONOLOGICAL_KINDS,
  addWeek,
  applyLabelMove,
  applyMealKinds,
  COMMON_MEAL_KINDS,
  type CellCopyMode,
  type CellRef,
  type LabelAxis,
  type LabelMove,
  type LabelOpMode,
  columnMovePlacement,
  gapForSlot,
  labelMoveFlips,
  labelMovePlacement,
  moveLabel,
  moveMealAlongRow,
  placementDiff,
  previewLabelMove,
  rowMovePlacement,
  copyMealToCell,
  copyWeekMeals,
  dayRowIndexes,
  daySlots,
  daysWithFoodInRow,
  everyDayHasKind,
  fittingKind,
  movedSlotIndex,
  moveMealContents,
  nextSnackKind,
  removeLastWeek,
  removeMeal,
  removeMealRow,
  reorderMealItems,
  restoreStoredMeal,
  rowMeal,
  setDayNote,
  weekHasItems,
  weekRows,
} from '@/components/plan-editor/plan-editor-ops';
import {
  MAX_MEALS_PER_DAY,
  type EditorDocument,
  type EditorMeal,
  type EditorRange,
  type EditorSideTab,
  type EditorView,
  type EditorWeek,
  type LibraryItem,
  type SaveStatus,
  type WeekStatus,
} from '@/components/plan-editor/plan-editor-types';
import { usePlanEditorState, type PlanEditorState } from '@/components/plan-editor/usePlanEditorState';
import { useGridFlip, type FlipMove } from '@/components/plan-editor/useGridFlip';
import { useSlideTransition } from '@/components/plan-editor/useSlideTransition';
import { useSwipeNavigation } from '@/components/plan-editor/useSwipeNavigation';

export interface PlanEditorSaveState {
  status: SaveStatus;
  /** Shown in the error banner for `error` and `conflict`. */
  message?: string;
}

/** What the host's "copy meals from another plan" dialog needs to open, close and hand a plan back. */
export interface CopyMealsSlotProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** Copies the first week of `source` into the current, empty week. */
  onApply: (source: EditorDocument) => void;
}

/** What a host's info panel needs; the defaults are the template info panel's inputs. */
export interface InfoPanelContext {
  doc: EditorDocument;
  week: EditorWeek;
  weekNumber: number;
  readOnly: boolean;
  onEdit: PlanEditorState['edit'];
}

/** A third library tab; `render` sees the document and the selected week, so it can read and edit the plan. */
export interface PlanEditorLibraryTab {
  label: string;
  render: (context: InfoPanelContext) => ReactNode;
}

/** Replaces the header's Save button; sees the document and the selected week, e.g. for "Publish week N". */
export interface PlanEditorPrimaryAction {
  label: (weekNumber: number) => string;
  /** Resolves with the server's copy of the document (resetting the unsaved state), or null on failure. */
  run: (doc: EditorDocument, weekIndex: number) => Promise<EditorDocument | null>;
  disabled?: (weekIndex: number, doc: EditorDocument) => boolean;
  /** Tooltip for the button, e.g. why it is disabled. */
  title?: (weekIndex: number, doc: EditorDocument) => string | undefined;
}

/** What a host's header actions and banner need to act on the plan as the editor currently holds it. */
export interface PlanEditorHostContext {
  /** Saves unsaved edits first; resolves true when nothing is left unsaved, false when the save failed. */
  flush: () => Promise<boolean>;
  /** A save or the primary action is running. */
  busy: boolean;
  /** Runs a navigation without the unsaved-changes prompt, for flows that delete or have saved the plan. */
  leaveWithoutPrompt: (navigate: () => void) => void;
}

interface Props {
  /** The document to edit. Remount (change `key`) to load a different one. */
  initial: EditorDocument;
  /** How the plan is used, shown on the info tab; omit to hide the usage line. */
  usage?: PlanUsage;
  /** Renders the host's copy-meals dialog; without it the empty-week card has no copy link. */
  renderCopyMeals?: (props: CopyMealsSlotProps) => ReactNode;
  readOnly: boolean;
  readOnlyNotice?: string;
  /** `leading` renders after the back button, before the label, e.g. a client avatar. */
  breadcrumb: { label: string; onNavigate: () => void; leading?: ReactNode };
  /** Replaces the Save button; omit for the default Save. */
  primaryAction?: PlanEditorPrimaryAction;
  /** With a primary action, keeps the default Save button beside it. */
  secondarySave?: boolean;
  /** Replaces the template info panel on the info tab. */
  renderInfoPanel?: (context: InfoPanelContext) => ReactNode;
  /** Label of the info tab; defaults to the template wording. */
  infoTabLabel?: string;
  /** Adds a third tab to the library panel. */
  libraryExtraTab?: PlanEditorLibraryTab;
  /** Adds top-level tabs to the side panel, after Plan info and Library. */
  extraSideTabs?: EditorSideTabDef<EditorSideTab>[];
  /** Publication state per week: shows a dot on each pill (empty weeks get a dashed ring) and a count legend. */
  weekStatus?: (weekIndex: number) => WeekStatus | undefined;
  /** Rendered last in the header row, e.g. a plan actions menu. */
  renderHeaderActions?: (context: PlanEditorHostContext) => ReactNode;
  /** Rendered at the top of the scrolling area, e.g. a notice that suggests an action. */
  renderBanner?: (context: PlanEditorHostContext) => ReactNode;
  /** Lets the last, unpublished week be removed (needs `weekStatus`); saves the plan once confirmed. */
  allowWeekRemoval?: boolean;
  /** Unreviewed offline-sync conflicts of this plan; only the client plan editor passes them. */
  syncConflicts?: PlanEditorSyncConflicts;
  /** The server's lock verdicts of a client plan; only the client plan editor passes them. */
  mealLocks?: PlanMealLocks | null;
  /** The tile a refused save named; outlined until the coach changes that meal. Only the client plan editor passes it. */
  lockRejection?: LockRejection | null;
  /** Called when the coach changes, moves or deletes the rejected meal. */
  onClearLockRejection?: () => void;
  /** The server's current copy of the plan, for restoring the rejected meal; null or absent while it is being refetched. */
  storedDocument?: EditorDocument | null;
  /**
   * Set when the plan's client is outside the coach's plan, so nothing may change: every edit that would change the
   * document is dropped and `onBlocked` fires instead, and Save, the primary action and Add week show a lock and call it.
   * Only the client plan editor passes it.
   */
  outsidePlan?: { onBlocked: () => void };
  saveState: PlanEditorSaveState;
  /** Persists the document; resolves with the server's copy, or null when the save failed. */
  onSave: (doc: EditorDocument) => Promise<EditorDocument | null>;
  /** Drops local edits and loads the latest server version (offered after a conflict). */
  onReload: () => void;
}

const DRAG_SENSORS = [PointerSensor.configure({ activationConstraints: dragActivationConstraints }), KeyboardSensor];

/** What the editor tracks while a meal tile or a row or day label is dragged over the grid. */
interface GridDragRuntime {
  cells: FrozenCell[];
  bands: FrozenBand[];
  /** The scrolling ancestors of the grid with their offsets at drag start, to place the pointer in the frozen layout. */
  scrollers: { element: Element; left: number; top: number }[];
  pointer: { x: number; y: number };
  alt: boolean;
  /** The view last shown; a refresh that would not change it does nothing. */
  view: GridDragView;
  stop: () => void;
}

function numberAttribute(element: Element, name: string): number | null {
  const value = element.getAttribute(name);
  return value === null || value === '' ? null : Number(value);
}

/** Every meal tile and Day-view card, with the place it has on screen right now. */
function measureCells(root: HTMLElement): FrozenCell[] {
  const cells: FrozenCell[] = [];
  for (const element of root.querySelectorAll('[data-testid="meal-cell"], [data-testid="meal-block"]')) {
    const dayOfWeek = numberAttribute(element, 'data-day');
    const rowIndex = numberAttribute(element, 'data-row');
    if (dayOfWeek === null || rowIndex === null) {
      continue;
    }
    const { left, top, right, bottom } = element.getBoundingClientRect();
    cells.push({ dayOfWeek, rowIndex, left, top, right, bottom });
  }
  return cells;
}

/** The rows or days of the grid: each one's tiles and its label cell, as one rectangle. */
function measureBands(root: HTMLElement, cells: readonly FrozenCell[], axis: LabelAxis): FrozenBand[] {
  const bands = new Map<number, FrozenBand>();
  const grow = (index: number, rect: { left: number; top: number; right: number; bottom: number }) => {
    const band = bands.get(index);
    bands.set(
      index,
      band
        ? {
            index,
            left: Math.min(band.left, rect.left),
            top: Math.min(band.top, rect.top),
            right: Math.max(band.right, rect.right),
            bottom: Math.max(band.bottom, rect.bottom),
          }
        : { index, left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom },
    );
  };
  for (const cell of cells) {
    grow(axis === 'row' ? cell.rowIndex : cell.dayOfWeek, cell);
  }
  for (const frame of root.querySelectorAll(`[data-testid="${axis}-label-frame"]`)) {
    const index = numberAttribute(frame, 'data-index');
    if (index !== null) {
      grow(index, frame.getBoundingClientRect());
    }
  }
  return [...bands.values()];
}

function scrollersOf(element: Element): GridDragRuntime['scrollers'] {
  const scrollers: GridDragRuntime['scrollers'] = [];
  for (let current: Element | null = element.parentElement; current; current = current.parentElement) {
    scrollers.push({ element: current, left: current.scrollLeft, top: current.scrollTop });
  }
  return scrollers;
}

/** The pointer in the layout as it was when the drag started: scrolling since then moves the pointer, not the tiles. */
function frozenPointer(runtime: GridDragRuntime): { x: number; y: number } {
  let { x, y } = runtime.pointer;
  for (const scroller of runtime.scrollers) {
    x += scroller.element.scrollLeft - scroller.left;
    y += scroller.element.scrollTop - scroller.top;
  }
  return { x, y };
}

/** The face key of a cell: its tile in the week grid, its meal's card in the Day view. */
function cellFaceKey(week: EditorWeek, range: EditorRange, cell: CellRef): string {
  if (range === 'day') {
    const meal = rowMeal(
      week.days.find((candidate) => candidate.dayOfWeek === cell.dayOfWeek),
      weekRows(week)[cell.rowIndex],
    );
    return meal?.mealId ?? '';
  }
  return gridFaceKey(cell.dayOfWeek, cell.rowIndex);
}

/** The faces a change of what the cells show slides: each flip's contents go from one cell to another. */
function placementFlips(week: EditorWeek, range: EditorRange, flips: readonly { from: CellRef; to: CellRef }[]): FlipMove[] {
  return flips.map(({ from, to }) => ({
    from: cellFaceKey(week, range, from),
    to: cellFaceKey(week, range, to),
  }));
}

interface PendingDrop {
  weekIndex: number;
  source: { dayOfWeek: number; mealId: string };
  target: { dayOfWeek: number; rowIndex: number };
}

function isLibraryItem(value: unknown): value is LibraryItem {
  if (typeof value !== 'object' || value === null || !('type' in value)) {
    return false;
  }
  return value.type === 'recipe' || value.type === 'food';
}

function readItem(data: unknown): LibraryItem | null {
  if (typeof data !== 'object' || data === null || !('item' in data)) {
    return null;
  }
  return isLibraryItem(data.item) ? data.item : null;
}

function readCell(data: unknown): SelectedCell | null {
  if (typeof data !== 'object' || data === null) {
    return null;
  }
  const candidate = data as { type?: unknown; dayOfWeek?: unknown; rowIndex?: unknown };
  // Typed drop data is not a cell, so a library item dropped on it lands nowhere.
  if (candidate.type !== undefined) {
    return null;
  }
  return typeof candidate.dayOfWeek === 'number' && typeof candidate.rowIndex === 'number'
    ? { dayOfWeek: candidate.dayOfWeek, rowIndex: candidate.rowIndex }
    : null;
}

function itemName(item: LibraryItem): string {
  return item.type === 'recipe' ? item.recipe.recipeName : item.food.foodName;
}

/**
 * The shared plan editor: week grid, library panel and header. Knows nothing about where the plan
 * comes from; the host loads it, saves it and decides who may edit.
 */
export default function PlanEditor({
  initial,
  usage,
  renderCopyMeals,
  readOnly,
  readOnlyNotice,
  breadcrumb,
  primaryAction,
  secondarySave,
  renderInfoPanel,
  infoTabLabel,
  libraryExtraTab,
  extraSideTabs,
  weekStatus,
  renderHeaderActions,
  renderBanner,
  allowWeekRemoval,
  syncConflicts,
  mealLocks,
  lockRejection = null,
  onClearLockRejection,
  storedDocument = null,
  outsidePlan,
  saveState,
  onSave,
  onReload,
}: Props) {
  const { t, i18n } = useTranslation();
  const { doc, dirty, canUndo, canRedo, edit: stateEdit, undo, redo, markSaved } = usePlanEditorState(initial);
  const outsidePlanRef = useRef(outsidePlan);
  const docRef = useRef(doc);
  useEffect(() => {
    outsidePlanRef.current = outsidePlan;
    docRef.current = doc;
  });
  // The one gate for every document change (edit, restore, template apply, week removal): for an outside-plan client a
  // change that would alter the document is dropped and the paywall opens instead. A no-op update passes silently.
  const rawEdit = useCallback<PlanEditorState['edit']>(
    (update, coalesceKey) => {
      const guard = outsidePlanRef.current;
      if (!guard) {
        stateEdit(update, coalesceKey);
        return;
      }
      if (update(docRef.current) !== docRef.current) {
        guard.onBlocked();
      }
    },
    [stateEdit],
  );
  const locks = mealLocks ?? null;
  const locksRef = useRef(locks);
  useEffect(() => {
    locksRef.current = locks;
  });
  // Every edit passes through here, so a locked meal or a past day never changes in the document, whichever control asked.
  const edit = useCallback<PlanEditorState['edit']>(
    (update, coalesceKey) =>
      rawEdit((current) => {
        const next = update(current);
        return locksRef.current ? keepLocked(locksRef.current, next, current) : next;
      }, coalesceKey),
    [rawEdit],
  );
  // The outline drops the moment an edit (or undo) changes the rejected meal's content or place, with no new save needed.
  const rejectionBaseline = useRef<{ rejection: LockRejection; fingerprint: readonly unknown[] } | null>(null);
  useEffect(() => {
    if (!lockRejection) {
      rejectionBaseline.current = null;
      return;
    }
    const fingerprint = rejectionFingerprint(doc, lockRejection);
    const baseline = rejectionBaseline.current;
    if (baseline?.rejection !== lockRejection) {
      rejectionBaseline.current = { rejection: lockRejection, fingerprint };
    } else if (!sameFingerprint(baseline.fingerprint, fingerprint)) {
      rejectionBaseline.current = null;
      onClearLockRejection?.();
    }
  }, [doc, lockRejection, onClearLockRejection]);
  // Restore: one undoable edit that puts the server's copy of the rejected meal back. `keepLocked` lets this one meal
  // through (the refetch has marked it locked), and only for this edit.
  const storedRejected = useMemo(
    () => (storedDocument && lockRejection?.mealId ? findStoredMeal(storedDocument, lockRejection.mealId) : undefined),
    [storedDocument, lockRejection],
  );
  // Restores one meal by id: the server's copy goes back in, or a meal the server never had (added to a past day) is
  // removed. Only the fresh stored copy is used, never the last one kept for the flags.
  const restoreMeal = useMemo(() => {
    if (!storedDocument) {
      return null;
    }
    return (mealId: string) => {
      const stored = findStoredMeal(storedDocument, mealId);
      rawEdit((current) => {
        let next = current;
        if (stored) {
          next = restoreStoredMeal(current, stored.meal, stored.weekNumber, stored.dayOfWeek);
        } else {
          current.weeks.forEach((week, weekIdx) =>
            week.days.forEach((day) => {
              if (day.meals.some((meal) => meal.mealId === mealId)) {
                next = removeMeal(next, weekIdx, day.dayOfWeek, mealId);
              }
            }),
          );
        }
        return locksRef.current ? keepLocked(locksRef.current, next, current, mealId) : next;
      });
    };
  }, [storedDocument, rawEdit]);
  const restoreRejected = useMemo(() => {
    const mealId = lockRejection?.mealId;
    if (!storedRejected || !mealId || !restoreMeal) {
      return null;
    }
    if (!doc.weeks.some((candidate) => candidate.weekNumber === storedRejected.weekNumber)) {
      return null;
    }
    return () => restoreMeal(mealId);
  }, [storedRejected, lockRejection, doc.weeks, restoreMeal]);
  const rejectedInDocument = lockRejection ? rejectionFingerprint(doc, lockRejection)[0] !== undefined : false;
  // The last non-null stored copy: while a refetch is in flight `storedDocument` is null, and the flags must not flicker.
  const [lastStored, setLastStored] = useState(storedDocument);
  if (storedDocument && storedDocument !== lastStored) {
    setLastStored(storedDocument);
  }
  // Meals whose draft clashes with a lock, found live; the server's refusal flags its meal the same way as a fallback.
  const lockConflicts = useMemo(() => {
    const found = findLockConflicts(doc, lastStored, locks);
    if (lockRejection?.mealId && !found.has(lockRejection.mealId)) {
      const [placed, weekNumber, day] = rejectionFingerprint(doc, lockRejection);
      if (placed !== undefined) {
        const view =
          locks && typeof weekNumber === 'number' && typeof day === 'number'
            ? mealLockView(locks, lockRejection.mealId, weekNumber, day)
            : undefined;
        found.set(lockRejection.mealId, view && !view.info?.locked ? 'pastDay' : 'eaten');
      }
    }
    return found;
  }, [doc, lastStored, locks, lockRejection]);
  const targets = doc.targets;
  const dailyKcalTarget = targets.kcal;
  const [weekIndex, setWeekIndex] = useState(0);
  const [range, setRange] = useState<EditorRange>('week');
  const [view, setView] = useState<EditorView>('meals');
  const [dayOfWeek, setDayOfWeek] = useState(1);
  const [detailCell, setDetailCell] = useState<SelectedCell | null>(null);
  const [copyOpen, setCopyOpen] = useState(false);
  const [selected, setSelected] = useState<SelectedCell | null>(null);
  const [libraryOpen, setLibraryOpen] = useState(true);
  const [sideTab, setSideTab] = useState<EditorSideTab>('library');
  const [pendingRemoval, setPendingRemoval] = useState<(PendingRowRemoval & { rowIndex: number }) | null>(null);
  const { blocker: leaveBlocker, leaveWithoutPrompt } = useUnsavedChangesBlocker(dirty);
  const [pendingDrop, setPendingDrop] = useState<PendingDrop | null>(null);
  const [pendingLabel, setPendingLabel] = useState<LabelMove | null>(null);
  const addToast = useToastStore((state) => state.addToast);
  const [addMeal, setAddMeal] = useState<{ open: boolean; mode: AddMealMode; preselect: MealKind | null; key: number }>({
    open: false,
    mode: 'day',
    preselect: null,
    key: 0,
  });
  const [scrollArea, setScrollArea] = useState<HTMLElement | null>(null);
  const [actionBusy, setActionBusy] = useState(false);
  const [removeWeekNumber, setRemoveWeekNumber] = useState<number | null>(null);
  const [removingWeek, setRemovingWeek] = useState(false);
  const [announcement, setAnnouncement] = useState('');
  const [openGhostId, setOpenGhostId] = useState<string | null>(null);
  const pendingConflictScroll = useRef<string | null>(null);
  const dragActive = useRef(false);
  const pendingFocus = useRef<{ dayOfWeek: number; mealId: string } | null>(null);
  const pendingLabelFocus = useRef<{ axis: LabelAxis; index: number } | null>(null);
  const slide = useSlideTransition();
  const [gridDrag, setGridDrag] = useState<GridDragView | null>(null);
  const gridRuntime = useRef<GridDragRuntime | null>(null);
  const flip = useGridFlip(scrollArea, doc, gridDrag);

  const currentIndex = Math.min(weekIndex, doc.weeks.length - 1);
  const week = doc.weeks[currentIndex];
  const rows = week ? weekRows(week) : [];
  // A selection that no longer points at a row (after a removal or an undo) selects nothing.
  const activeSelected = selected && selected.rowIndex < rows.length ? selected : null;
  const activeDay = week?.days.find((candidate) => candidate.dayOfWeek === dayOfWeek);
  const activeDayPast = locks && week ? isPastDay(locks, week.weekNumber, dayOfWeek) : false;
  const showPicker = !readOnly && rows.length === 0;
  const conflictsModel = syncConflicts
    ? buildSyncConflictsModel(
        syncConflicts,
        placeConflicts(syncConflicts.conflicts, doc, locks),
        openConflictDay,
      )
    : null;

  // The drag listeners outlive a render, so they read what changes between renders through this.
  const latest = useRef({ doc, currentIndex, range });
  useEffect(() => {
    latest.current = { doc, currentIndex, range };
  });

  // A drag cut short by leaving the editor must not leave its listeners on the window.
  useEffect(
    () => () => {
      gridRuntime.current?.stop();
    },
    [],
  );

  // After a keyboard move the contents sit in another slot; focus follows them once that slot has rendered.
  useEffect(() => {
    const target = pendingFocus.current;
    if (!target || !scrollArea) {
      return;
    }
    pendingFocus.current = null;
    const day = week?.days.find((candidate) => candidate.dayOfWeek === target.dayOfWeek);
    const row = week ? dayRowIndexes(week, day).get(target.mealId) : undefined;
    const selector =
      range === 'day'
        ? `[data-testid="meal-block"][data-meal-id="${target.mealId}"]`
        : `[data-testid="meal-cell"][data-day="${target.dayOfWeek}"][data-row="${row}"]`;
    scrollArea.querySelector<HTMLElement>(selector)?.focus();
  });

  // After a keyboard move the contents sit under the neighbouring label; focus follows them once it has rendered.
  useEffect(() => {
    const target = pendingLabelFocus.current;
    if (!target || !scrollArea) {
      return;
    }
    pendingLabelFocus.current = null;
    scrollArea
      .querySelector<HTMLElement>(`[data-testid="${target.axis}-label"][data-index="${target.index}"]`)
      ?.focus();
  });

  useEffect(() => {
    if (!dirty) {
      return;
    }
    const warn = (event: BeforeUnloadEvent) => event.preventDefault();
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [dirty]);

  // Cmd/Ctrl+Z undoes, Cmd/Ctrl+Shift+Z and Ctrl+Y redo; inside a text field the field's own undo wins.
  useEffect(() => {
    if (readOnly) {
      return;
    }
    const onKeyDown = (event: KeyboardEvent) => {
      if (!(event.metaKey || event.ctrlKey) || event.altKey) {
        return;
      }
      const target = event.target;
      if (
        target instanceof HTMLElement &&
        (target.isContentEditable || target.closest('input, textarea, select, [contenteditable], [role="dialog"], [role="alertdialog"]') !== null)
      ) {
        return;
      }
      const key = event.key.toLowerCase();
      if (key === 'z' && !event.shiftKey) {
        event.preventDefault();
        undo();
      } else if ((key === 'z' && event.shiftKey) || (key === 'y' && event.ctrlKey && !event.shiftKey)) {
        event.preventDefault();
        redo();
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [readOnly, undo, redo]);

  /** Every week or day change goes through here: it closes open popovers, then slides the content. */
  function navigate(target: { week?: number; day?: number }) {
    const toWeek = target.week ?? currentIndex;
    const toDay = target.day ?? dayOfWeek;
    if (toWeek === currentIndex && toDay === dayOfWeek) {
      return;
    }
    slide.start((toWeek !== currentIndex ? toWeek > currentIndex : toDay > dayOfWeek) ? 'next' : 'prev');
    setWeekIndex(toWeek);
    setDayOfWeek(toDay);
    setSelected(null);
    setDetailCell(null);
    setOpenGhostId(null);
    setPendingRemoval(null);
  }

  /** "Open <day>" in a conflict popover: the Day view on the conflict's weekday. */
  function openConflictDay(placed: PlacedConflict) {
    if (placed.weekIndex === null || placed.dayOfWeek === null) {
      return;
    }
    navigate({ week: placed.weekIndex, day: placed.dayOfWeek });
    setRange('day');
    setView('meals');
    setDetailCell(null);
    setOpenGhostId(null);
  }

  /** "Open <day>" in a locked meal's popover: the Day view on that weekday. */
  function openDay(dayOfWeekValue: number) {
    navigate({ day: dayOfWeekValue });
    setRange('day');
    setView('meals');
    setDetailCell(null);
  }

  /** "Show" in the strip: the conflict's popover in the Week view (switching week), its panel in the Day view. */
  function showConflict(placed: PlacedConflict) {
    if (placed.weekIndex === null || placed.dayOfWeek === null) {
      return;
    }
    if (range === 'day') {
      navigate({ week: placed.weekIndex, day: placed.dayOfWeek });
      pendingConflictScroll.current = placed.id;
      return;
    }
    navigate({ week: placed.weekIndex });
    setView('meals');
    if (placed.ghost) {
      setOpenGhostId(placed.id);
      return;
    }
    const target = doc.weeks[placed.weekIndex];
    const row =
      target && placed.meal
        ? dayRowIndexes(target, target.days.find((day) => day.dayOfWeek === placed.dayOfWeek)).get(placed.meal.mealId)
        : undefined;
    if (row !== undefined) {
      const cell = { dayOfWeek: placed.dayOfWeek, rowIndex: row };
      setSelected(cell);
      setDetailCell(cell);
    }
  }

  // After "Show" in the Day view the conflict's panel scrolls into view once its day has rendered.
  useEffect(() => {
    const id = pendingConflictScroll.current;
    if (!id || !scrollArea) {
      return;
    }
    const panel = scrollArea.querySelector<HTMLElement>(
      `[data-testid="sync-conflict-panel"][data-conflict-id="${id}"],[data-testid="sync-conflict-ghost-card"][data-conflict-id="${id}"]`,
    );
    if (panel) {
      pendingConflictScroll.current = null;
      panel.scrollIntoView({ block: 'nearest' });
    }
  });

  /** One swipe step: the day in the Day view (no wrap into the next week), otherwise the week. */
  function swipe(direction: 'next' | 'prev') {
    const step = direction === 'next' ? 1 : -1;
    if (range === 'day' && rows.length > 0) {
      const nextDay = dayOfWeek + step;
      if (nextDay >= 1 && nextDay <= 7) {
        navigate({ day: nextDay });
      }
      return;
    }
    const nextWeek = currentIndex + step;
    if (nextWeek >= 0 && nextWeek < doc.weeks.length) {
      navigate({ week: nextWeek });
    }
  }

  useSwipeNavigation(scrollArea, swipe, () => slide.isSliding() || dragActive.current);

  /** Day shows meals only and Nutrition shows the whole week, so each choice resets the other toggle. */
  function changeRange(next: EditorRange) {
    setRange(next);
    setDetailCell(null);
    if (next === 'day') {
      setView('meals');
      if (selected) {
        setDayOfWeek(selected.dayOfWeek);
      }
    }
  }

  function changeView(next: EditorView) {
    setView(next);
    setDetailCell(null);
    if (next === 'nutrition') {
      setRange('week');
    }
  }

  /** The meal a weekday has in a grid row, if any. */
  function mealAt(dayOfWeekValue: number, rowIndex: number): EditorMeal | undefined {
    return rowMeal(week?.days.find((day) => day.dayOfWeek === dayOfWeekValue), rows[rowIndex]);
  }

  /** True when the slot is on a past day or holds a meal that may not change: nothing can land on it or leave it. */
  function slotBlocked(cell: CellRef): boolean {
    if (!locks || !week) {
      return false;
    }
    if (isPastDay(locks, week.weekNumber, cell.dayOfWeek)) {
      return true;
    }
    const meal = mealAt(cell.dayOfWeek, cell.rowIndex);
    return meal ? mealLockView(locks, meal.mealId, week.weekNumber, cell.dayOfWeek).locked : false;
  }

  /** True when the weekday is past or holds a meal that may not change. */
  function dayLocked(dayOfWeekValue: number): boolean {
    if (!locks || !week) {
      return false;
    }
    const meals = week.days.find((day) => day.dayOfWeek === dayOfWeekValue)?.meals ?? [];
    return dayHasLock(locks, week.weekNumber, dayOfWeekValue, meals);
  }

  function weekHasLock(): boolean {
    return week !== undefined && week.days.some((day) => dayLocked(day.dayOfWeek));
  }

  /** Sliding a meal along its row shifts every meal between the two days, so none of those slots may be blocked. */
  function alongRowBlocked(rowIndex: number, fromDay: number, toDay: number): boolean {
    for (let day = Math.min(fromDay, toDay); day <= Math.max(fromDay, toDay); day += 1) {
      if (slotBlocked({ dayOfWeek: day, rowIndex })) {
        return true;
      }
    }
    return false;
  }

  /** Whether a tile dragged from `source` may not be dropped on `hit`: a copy only needs the target open, a slide the whole stretch. */
  function cellDropBlocked(source: CellMealDrag, hit: CellRef, copy: boolean): boolean {
    if (slotBlocked(hit)) {
      return true;
    }
    if (copy) {
      return false;
    }
    if (hit.dayOfWeek === source.dayOfWeek) {
      return dayLocked(source.dayOfWeek);
    }
    return hit.rowIndex === source.rowIndex && alongRowBlocked(source.rowIndex, source.dayOfWeek, hit.dayOfWeek);
  }

  function selectCell(cell: SelectedCell) {
    setSelected(cell);
    const meal = mealAt(cell.dayOfWeek, cell.rowIndex);
    const reopen = detailCell?.dayOfWeek === cell.dayOfWeek && detailCell.rowIndex === cell.rowIndex;
    const hasConflict = meal ? conflictsModel?.forMeal(currentIndex, meal.mealId) !== undefined : false;
    const flagged = meal ? lockConflicts.has(meal.mealId) : false;
    setDetailCell(meal && (mealItemCount(meal) > 0 || hasConflict || flagged) && !reopen ? cell : null);
  }

  function renderDetail(cell: SelectedCell): ReactNode {
    const meal = mealAt(cell.dayOfWeek, cell.rowIndex);
    if (!meal || !week) {
      return null;
    }
    // A meal with an unreviewed conflict opens the comparison first; "Open <day>" leads on to editing it.
    const conflict = conflictsModel?.forMeal(currentIndex, meal.mealId);
    if (conflict && conflictsModel) {
      return (
        <SyncConflictPopoverBody
          placed={conflict}
          model={conflictsModel}
          eaten={locks?.meals.get(meal.mealId)}
          onClose={() => setDetailCell(null)}
        />
      );
    }
    const lock = locks ? mealLockView(locks, meal.mealId, week.weekNumber, cell.dayOfWeek) : null;
    if (locks && lock?.locked) {
      return (
        <LockedMealPopover
          conflict={lockConflicts.get(meal.mealId)}
          onRestore={
            restoreMeal
              ? () => {
                  restoreMeal(meal.mealId);
                  setDetailCell(null);
                }
              : undefined
          }
          meal={meal}
          lock={lock}
          clientName={locks.clientName}
          dayOfWeek={cell.dayOfWeek}
          date={slotDate(locks.startDate, week.weekNumber, cell.dayOfWeek)}
          pastDay={isPastDay(locks, week.weekNumber, cell.dayOfWeek)}
          language={i18n.language}
          onClose={() => setDetailCell(null)}
          onOpenDay={() => openDay(cell.dayOfWeek)}
        />
      );
    }
    return (
      <MealDetail
        meal={meal}
        weekIndex={currentIndex}
        weekNumber={currentIndex + 1}
        dayOfWeek={cell.dayOfWeek}
        readOnly={readOnly}
        language={i18n.language}
        onEdit={edit}
        onClose={() => setDetailCell(null)}
      />
    );
  }

  /** An empty target takes the copy at once; a filled one asks Replace or Add first. */
  function onCellMealDrop(source: CellMealDrag, target: SelectedCell) {
    if (readOnly || source.weekIndex !== currentIndex || slotBlocked(target)) {
      return;
    }
    const targetMeal = mealAt(target.dayOfWeek, target.rowIndex);
    if (targetMeal?.mealId === source.mealId) {
      return;
    }
    const drop = { weekIndex: source.weekIndex, source, target };
    if (!targetMeal || mealItemCount(targetMeal) === 0) {
      copyCell(drop, 'replace');
      return;
    }
    setPendingDrop(drop);
  }

  function copyCell(drop: PendingDrop, mode: CellCopyMode) {
    setSelected(drop.target);
    setDetailCell(null);
    edit((current) =>
      copyMealToCell(
        current,
        drop.weekIndex,
        { dayOfWeek: drop.source.dayOfWeek, mealId: drop.source.mealId },
        { dayOfWeek: drop.target.dayOfWeek, rowIndex: drop.target.rowIndex },
        mode,
      ),
    );
  }

  function resolveDrop(mode: CellCopyMode) {
    if (pendingDrop) {
      copyCell(pendingDrop, mode);
    }
    setPendingDrop(null);
  }

  function findMeal(dayOfWeekValue: number, mealId: string) {
    return week?.days.find((day) => day.dayOfWeek === dayOfWeekValue)?.meals.find((meal) => meal.mealId === mealId);
  }

  function mealLabel(meal: EditorMeal): string {
    return t(`planEditor.rows.${mealKindLabelKey(meal.kind)}`);
  }

  /**
   * Moves a meal's contents to the gap before slot `gapIndex` of its day in one edit, selects the slot they
   * land in and announces it; `focus` also moves keyboard focus there once it has rendered. `animate` slides the
   * tiles to their new places; a drop turns it off, because the hover preview already showed the result.
   */
  function moveContents(
    dayOfWeekValue: number,
    mealId: string,
    gapIndex: number,
    options: { focus: boolean; animate: boolean },
  ) {
    const day = week?.days.find((candidate) => candidate.dayOfWeek === dayOfWeekValue);
    const slots = daySlots(day);
    const from = slots.findIndex((meal) => meal.mealId === mealId);
    if (readOnly || !week || from === -1 || dayLocked(dayOfWeekValue)) {
      return;
    }
    const to = movedSlotIndex(from, gapIndex, slots.length);
    if (to === from) {
      return;
    }
    const landing = slots[to];
    const landingRow = dayRowIndexes(week, day).get(landing.mealId);
    if (options.animate) {
      flip(placementFlips(week, range, columnMovePlacement(week, dayOfWeekValue, mealId, gapIndex)));
    }
    edit((current) => moveMealContents(current, currentIndex, dayOfWeekValue, mealId, gapIndex));
    setDetailCell(null);
    if (landingRow !== undefined) {
      setSelected({ dayOfWeek: dayOfWeekValue, rowIndex: landingRow });
    }
    if (options.focus) {
      pendingFocus.current = { dayOfWeek: dayOfWeekValue, mealId: landing.mealId };
    }
    setAnnouncement(
      t('planEditor.reorder.moved', {
        day: t(`planEditor.daysLong.${dayOfWeekValue}`),
        meal: mealLabel(landing),
        position: to + 1,
        count: slots.length,
      }),
    );
  }

  /**
   * Moves a meal's contents along its row to another weekday in one edit, as the days that have a meal in the row
   * shift by one; selects the cell they land in and announces it. `focus` and `animate` work as in `moveContents`.
   */
  function moveAlongRow(
    source: { dayOfWeek: number; mealId: string },
    toDay: number,
    options: { focus: boolean; animate: boolean },
  ) {
    if (readOnly || !week) {
      return;
    }
    const rowIndex = dayRowIndexes(
      week,
      week.days.find((candidate) => candidate.dayOfWeek === source.dayOfWeek),
    ).get(source.mealId);
    const landing = rowIndex === undefined ? undefined : mealAt(toDay, rowIndex);
    if (rowIndex === undefined || !landing || alongRowBlocked(rowIndex, source.dayOfWeek, toDay)) {
      return;
    }
    const placement = rowMovePlacement(week, rowIndex, source.dayOfWeek, toDay);
    if (placement.length === 0) {
      return;
    }
    if (options.animate) {
      flip(placementFlips(week, range, placement));
    }
    edit((current) => moveMealAlongRow(current, currentIndex, rowIndex, source.dayOfWeek, toDay));
    setDetailCell(null);
    setSelected({ dayOfWeek: toDay, rowIndex });
    if (options.focus) {
      pendingFocus.current = { dayOfWeek: toDay, mealId: landing.mealId };
    }
    setAnnouncement(
      t('planEditor.reorder.movedAcross', {
        meal: mealLabel(landing),
        from: t(`planEditor.daysLong.${source.dayOfWeek}`),
        to: t(`planEditor.daysLong.${toDay}`),
      }),
    );
  }

  /**
   * Drops a meal tile where the pointer was: a plain drop within its day or along its row slides the contents into
   * the hovered slot, anything else is a copy.
   */
  function dropCellMeal(source: CellMealDrag, view: GridDragView | undefined) {
    if (view?.kind !== 'cell' || !view.hover || view.mode === null) {
      return;
    }
    if (view.mode !== 'move') {
      onCellMealDrop(source, view.hover);
      return;
    }
    if (source.weekIndex !== currentIndex) {
      return;
    }
    if (view.hover.dayOfWeek !== source.dayOfWeek) {
      moveAlongRow(source, view.hover.dayOfWeek, { focus: false, animate: false });
      return;
    }
    const slots = daySlots(week?.days.find((day) => day.dayOfWeek === source.dayOfWeek));
    const hovered = mealAt(view.hover.dayOfWeek, view.hover.rowIndex);
    const from = slots.findIndex((meal) => meal.mealId === source.mealId);
    const to = slots.findIndex((meal) => meal.mealId === hovered?.mealId);
    if (from === -1 || to === -1) {
      return;
    }
    moveContents(source.dayOfWeek, source.mealId, gapForSlot(from, to), { focus: false, animate: false });
  }

  /** Looks at where the pointer is in the frozen layout and updates what the grid shows; slides the tiles that change place. */
  function refreshGridDrag(runtime: GridDragRuntime) {
    const { doc: currentDoc, currentIndex: index, range: currentRange } = latest.current;
    const { x, y } = frozenPointer(runtime);
    const previous = runtime.view;
    const currentWeek = currentDoc.weeks[index];
    if (previous.kind === 'label') {
      const hit = hitBand(runtime.bands, previous.axis, x, y);
      const target = hit === previous.source ? null : hit;
      if (target === previous.target && runtime.alt === previous.alt) {
        return;
      }
      const next = labelDragView(currentDoc, index, previous, target, runtime.alt);
      if (currentWeek) {
        flip(placementFlips(currentWeek, currentRange, placementDiff(previous.moves, next.moves)));
      }
      runtime.view = next;
      setGridDrag(next);
      return;
    }
    const hit = hitCellLocked(
      runtime.cells,
      { dayOfWeek: previous.source.dayOfWeek, rowIndex: previous.source.rowIndex },
      x,
      y,
    );
    // A slot nothing may land on is not a hover at all: no preview, no drop.
    const hover = hit && !cellDropBlocked(previous.source, hit, runtime.alt) ? hit : null;
    const next = cellDragView(currentDoc, index, previous.source, hover, runtime.alt);
    if (
      next.mode === previous.mode &&
      next.alt === previous.alt &&
      next.hover?.dayOfWeek === previous.hover?.dayOfWeek &&
      next.hover?.rowIndex === previous.hover?.rowIndex
    ) {
      return;
    }
    if (currentWeek) {
      flip(placementFlips(currentWeek, currentRange, placementDiff(previous.moves, next.moves)));
    }
    runtime.view = next;
    setGridDrag(next);
  }

  /**
   * Starts tracking a meal tile or label drag: measures the tiles once, so hover and drop stay put however the
   * preview reflows them, and follows the pointer and the Alt key (a held Alt copies instead of moving) until the drop.
   */
  function startGridDrag(data: unknown, handle: Element | undefined, activator: Event | null) {
    const cellSource = readCellMealDrag(data);
    const rowLabel = readRowLabel(data, ROW_LABEL_DRAG);
    const dayLabel = readDayLabel(data, DAY_LABEL_DRAG);
    if (!scrollArea || readOnly || !(cellSource || rowLabel || dayLabel)) {
      return;
    }
    gridRuntime.current?.stop();
    const cells = measureCells(scrollArea);
    const alt = activator instanceof MouseEvent && activator.altKey;
    const handleRect = handle?.getBoundingClientRect();
    const pointer =
      activator instanceof MouseEvent
        ? { x: activator.clientX, y: activator.clientY }
        : { x: handleRect?.left ?? 0, y: handleRect?.top ?? 0 };
    let view: GridDragView;
    let bands: FrozenBand[] = [];
    if (cellSource) {
      view = cellDragView(doc, currentIndex, cellSource, null, alt);
    } else {
      const axis: LabelAxis = rowLabel ? 'row' : 'day';
      const source = rowLabel ? rowLabel.rowIndex : (dayLabel?.dayOfWeek ?? 1);
      bands = measureBands(scrollArea, cells, axis);
      const tiles: LabelTile[] = cells
        .filter((cell) => (axis === 'row' ? cell.rowIndex : cell.dayOfWeek) === source)
        .map((cell) => ({
          dayOfWeek: cell.dayOfWeek,
          rowIndex: cell.rowIndex,
          left: cell.left - (handleRect?.left ?? cell.left),
          top: cell.top - (handleRect?.top ?? cell.top),
          width: cell.right - cell.left,
          height: cell.bottom - cell.top,
        }));
      const base: LabelDragBase = { axis, source, tiles };
      view = labelDragView(doc, currentIndex, base, null, alt);
    }
    const firstCell = scrollArea.querySelector('[data-testid="meal-cell"], [data-testid="meal-block"]');
    const onPointerMove = (event: PointerEvent) => {
      runtime.pointer = { x: event.clientX, y: event.clientY };
      runtime.alt = event.altKey;
      refreshGridDrag(runtime);
    };
    // Pressing or releasing Alt while the pointer rests fires no pointer event, so the key itself is watched too.
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== 'Alt') {
        return;
      }
      if (event.type === 'keydown') {
        // A bare Alt would otherwise focus the menu bar of some browsers and drop the drag.
        event.preventDefault();
      }
      runtime.alt = event.type === 'keydown';
      refreshGridDrag(runtime);
    };
    const onBlur = () => {
      runtime.alt = false;
      refreshGridDrag(runtime);
    };
    const onScroll = () => refreshGridDrag(runtime);
    const runtime: GridDragRuntime = {
      cells,
      bands,
      scrollers: firstCell ? scrollersOf(firstCell) : [],
      pointer,
      alt,
      view,
      stop: () => {
        window.removeEventListener('pointermove', onPointerMove, true);
        window.removeEventListener('keydown', onKey, true);
        window.removeEventListener('keyup', onKey, true);
        window.removeEventListener('blur', onBlur);
        window.removeEventListener('scroll', onScroll, true);
      },
    };
    window.addEventListener('pointermove', onPointerMove, true);
    window.addEventListener('keydown', onKey, true);
    window.addEventListener('keyup', onKey, true);
    window.addEventListener('blur', onBlur);
    window.addEventListener('scroll', onScroll, true);
    gridRuntime.current = runtime;
    setGridDrag(view);
  }

  /** Stops following the drag and returns what the grid last showed, for the drop. */
  function finishGridDrag(): GridDragView | undefined {
    const runtime = gridRuntime.current;
    runtime?.stop();
    gridRuntime.current = null;
    setGridDrag(null);
    return runtime?.view;
  }

  /** Alt+Arrow: one slot up or down the day; at the first or last slot nothing happens. */
  function moveByKey(dayOfWeekValue: number, mealId: string, direction: -1 | 1) {
    const slots = daySlots(week?.days.find((candidate) => candidate.dayOfWeek === dayOfWeekValue));
    const to = slots.findIndex((meal) => meal.mealId === mealId) + direction;
    if (to < 0 || to >= slots.length) {
      return;
    }
    moveContents(dayOfWeekValue, mealId, direction < 0 ? to : to + 1, { focus: true, animate: true });
  }

  /** Alt+ArrowLeft / ArrowRight: one step along the row to the next weekday that has a meal in it; at the end nothing happens. */
  function moveAlongByKey(dayOfWeekValue: number, mealId: string, direction: -1 | 1) {
    if (!week) {
      return;
    }
    const rowIndex = dayRowIndexes(
      week,
      week.days.find((candidate) => candidate.dayOfWeek === dayOfWeekValue),
    ).get(mealId);
    const row = rowIndex === undefined ? undefined : rows[rowIndex];
    const days = week.days
      .filter((day) => rowMeal(day, row) !== undefined)
      .map((day) => day.dayOfWeek)
      .sort((left, right) => left - right);
    const to = days[days.indexOf(dayOfWeekValue) + direction];
    if (to !== undefined) {
      moveAlongRow({ dayOfWeek: dayOfWeekValue, mealId }, to, { focus: true, animate: true });
    }
  }

  function rowLabelKey(rowIndex: number): string {
    const row = weekRows(week ?? { weekNumber: 0, days: [] })[rowIndex];
    return mealKindLabelKey(row?.kind ?? MealKind.Breakfast);
  }

  /** The name a row (by index) or a weekday goes by in dialogs and announcements. */
  function labelName(axis: LabelAxis, index: number): string {
    return axis === 'day' ? t(`planEditor.daysLong.${index}`) : t(`planEditor.rows.${rowLabelKey(index)}`);
  }

  /**
   * Applies a row or day copy in one edit (one Undo), clears what pointed at the old contents, and announces the
   * result. The outcome is worked out here, outside the updater, which must stay free of side effects. Days the
   * copy leaves out for the meal cap are named in the announcement, and in a toast when no dialog explained them.
   */
  function runLabelMove(move: LabelMove, mode: LabelOpMode, options: { focus: boolean; viaDialog: boolean }) {
    if (readOnly || !week || weekHasLock()) {
      return;
    }
    const outcome = previewLabelMove(week, move).outcome[mode];
    const skipped =
      outcome.skippedDays.length > 0
        ? t('planEditor.labelDrop.skipped', {
            days: outcome.skippedDays.map((day) => t(`planEditor.daysLong.${day}`)).join(', '),
            max: MAX_MEALS_PER_DAY,
          })
        : '';
    let done = '';
    if (outcome.changed) {
      setSelected(null);
      setDetailCell(null);
      flip(placementFlips(week, range, labelMoveFlips(week, move, mode)));
      edit((current) => applyLabelMove(current, currentIndex, move, mode));
      if (options.focus) {
        pendingLabelFocus.current = { axis: move.axis, index: move.to };
      }
      done = t(`planEditor.labelDrop.announce.${move.axis}.${mode}`, {
        from: labelName(move.axis, move.from),
        to: labelName(move.axis, move.to),
      });
    }
    const message = [done, skipped].filter((part) => part !== '').join(' ');
    if (message !== '') {
      setAnnouncement(message);
    }
    if (skipped !== '' && !options.viaDialog) {
      addToast(skipped, 'error');
    }
  }

  /**
   * Moves a row or day onto another one in one edit (one Undo), clears what pointed at the old contents and
   * announces it. `animate` slides the tiles; a drop turns it off, because the hover preview already showed the result.
   */
  function moveLabelContents(move: LabelMove, options: { focus: boolean; animate: boolean }) {
    if (readOnly || !week || weekHasLock() || labelMovePlacement(week, move).length === 0) {
      return;
    }
    setSelected(null);
    setDetailCell(null);
    if (options.animate) {
      flip(placementFlips(week, range, labelMovePlacement(week, move)));
    }
    edit((current) => moveLabel(current, currentIndex, move));
    if (options.focus) {
      pendingLabelFocus.current = { axis: move.axis, index: move.to };
    }
    setAnnouncement(
      t(`planEditor.labelDrop.announce.${move.axis}.move`, {
        from: labelName(move.axis, move.from),
        to: labelName(move.axis, move.to),
      }),
    );
  }

  /**
   * A label dropped on a row or day: a plain drop moves it; with Alt held a target without food is copied onto at
   * once, one with food asks Replace or Append first.
   */
  function onLabelDrop(move: LabelMove, alt: boolean) {
    if (readOnly || !week) {
      return;
    }
    if (!alt) {
      moveLabelContents(move, { focus: false, animate: false });
      return;
    }
    if (previewLabelMove(week, move).targetHasFood) {
      setPendingLabel(move);
      return;
    }
    runLabelMove(move, 'copy', { focus: false, viaDialog: false });
  }

  function resolveLabelDrop(choice: LabelChoice) {
    if (pendingLabel) {
      runLabelMove(pendingLabel, choice, { focus: false, viaDialog: true });
    }
    setPendingLabel(null);
  }

  /** Alt+Arrow on a label: moves it one step onto the neighbouring row or day, no dialog; at either end nothing happens. */
  function moveLabelByKey(axis: LabelAxis, index: number, direction: -1 | 1) {
    const to = index + direction;
    const first = axis === 'row' ? 0 : 1;
    const last = axis === 'row' ? rows.length - 1 : 7;
    if (to < first || to > last) {
      return;
    }
    moveLabelContents({ axis, from: index, to }, { focus: true, animate: true });
  }

  /** Reorders a recipe or ingredient inside its own list; a drop outside that list changes nothing. */
  function onMealItemDragEnd(source: MealItemDrag, event: Parameters<typeof move>[1]) {
    const meal = findMeal(source.dayOfWeek, source.mealId);
    const sourceId = event.operation.source?.id;
    if (!meal || readOnly || source.weekIndex !== currentIndex || typeof sourceId !== 'string') {
      return;
    }
    const list = `${source.mealId}:${source.kind}`;
    const count = source.kind === 'recipe' ? meal.recipes.length : meal.foods.length;
    const ids = Array.from({ length: count }, (_, index) => `${list}:${index}`);
    const to = move(ids, event).indexOf(sourceId);
    if (to !== -1 && to !== source.index) {
      edit((current) =>
        reorderMealItems(current, source.weekIndex, source.dayOfWeek, source.mealId, source.kind, source.index, to),
      );
    }
  }

  /** Opens the add-meal drawer; the grid rows shift once a meal lands, so nothing stays selected afterwards. */
  function openAddMeal(mode: AddMealMode, preselect: MealKind | null) {
    setAddMeal((previous) => ({ open: true, mode, preselect, key: previous.key + 1 }));
  }

  /** The "Add meal" button on a day: starts on the day's first kind it still lacks. */
  function openAddMealToDay() {
    openAddMeal(
      'day',
      CHRONOLOGICAL_KINDS.find((kind) => !activeDay?.meals.some((meal) => meal.kind === kind)) ?? null,
    );
  }

  /** An insert line between two meals: the drawer opens on the kind that fits between them, if one is free. */
  function insertBetween(above: MealKind, below: MealKind) {
    if (range === 'day') {
      openAddMeal('day', fittingKind(above, below, (kind) => !activeDay?.meals.some((meal) => meal.kind === kind)));
    } else if (week) {
      openAddMeal('week', fittingKind(above, below, (kind) => !everyDayHasKind(week, kind)));
    }
  }

  /** Adds an empty meal to the active day at its kind's place in the day's order. */
  function addMealToActiveDay(kind: MealKind, note: string) {
    setSelected(null);
    setDetailCell(null);
    edit((current) => addMealToDay(current, currentIndex, dayOfWeek, kind, note));
    setAddMeal((previous) => ({ ...previous, open: false }));
  }

  /** Adds an empty meal of the kind to every day of the week that lacks one, in one edit. */
  function addMealToWeek(kind: MealKind) {
    setSelected(null);
    setDetailCell(null);
    edit((current) => addKindToWeek(current, currentIndex, kind));
    setAddMeal((previous) => ({ ...previous, open: false }));
  }

  function addToCell(item: LibraryItem, cell: SelectedCell) {
    if (slotBlocked(cell)) {
      return;
    }
    edit((current) => addItemToCell(current, currentIndex, cell.dayOfWeek, cell.rowIndex, item));
  }

  function applyCommonMeals() {
    edit((current) =>
      applyMealKinds(
        current,
        current.weeks.map((_, index) => index),
        COMMON_MEAL_KINDS,
      ),
    );
  }

  /** Adds a row to this week and to every empty week that has the same rows. */
  function withMealKind(current: EditorDocument, kind: MealKind): EditorDocument {
    const currentWeek = current.weeks[currentIndex];
    if (!currentWeek) {
      return current;
    }
    const signature = weekRows(currentWeek)
      .map((row) => row.kind)
      .join();
    const kindToAdd = kind === MealKind.MorningSnack ? nextSnackKind(currentWeek) : kind;
    return current.weeks.reduce(
      (result, candidate, index) =>
        index === currentIndex ||
        (!weekHasItems(candidate) &&
          weekRows(candidate)
            .map((row) => row.kind)
            .join() === signature)
          ? addMealRow(result, index, kindToAdd)
          : result,
      current,
    );
  }

  /** Adds every staged meal in one edit, so a single Undo takes them all back. */
  function addMealKinds(kinds: MealKind[]) {
    if (kinds.length === 0) {
      return;
    }
    edit((current) => kinds.reduce(withMealKind, current));
  }

  function removeRow(rowIndex: number) {
    setSelected(null);
    setDetailCell(null);
    edit((current) => removeMealRow(current, currentIndex, rowIndex));
  }

  /** A row with no food goes at once; one with food asks first. */
  function requestRemoveRow(rowIndex: number) {
    if (readOnly || !week) {
      return;
    }
    if (week.days.some((day) => slotBlocked({ dayOfWeek: day.dayOfWeek, rowIndex }) && mealAt(day.dayOfWeek, rowIndex))) {
      addToast(t('planEditor.locks.rowLocked'), 'error');
      return;
    }
    const days = daysWithFoodInRow(week, rowIndex);
    if (days.length === 0) {
      removeRow(rowIndex);
      return;
    }
    setPendingRemoval({ rowIndex, rowLabelKey: rowLabelKey(rowIndex), weekNumber: currentIndex + 1, days });
  }

  function confirmRemoval() {
    if (pendingRemoval) {
      removeRow(pendingRemoval.rowIndex);
    }
    setPendingRemoval(null);
  }

  async function save() {
    if (outsidePlan) {
      outsidePlan.onBlocked();
      return;
    }
    const sent = doc;
    const saved = await onSave(sent);
    if (saved) {
      markSaved(saved, sent);
    }
  }

  async function flush(): Promise<boolean> {
    if (!dirty) {
      return true;
    }
    const sent = doc;
    const saved = await onSave(sent);
    if (saved) {
      markSaved(saved, sent);
    }
    return saved !== null;
  }

  /** Drops the last week from the document and saves it; the server drops the stored draft week with it. */
  async function confirmRemoveWeek() {
    const next = removeLastWeek(doc);
    if (next === doc || outsidePlan) {
      if (next !== doc) {
        outsidePlan?.onBlocked();
      }
      setRemoveWeekNumber(null);
      return;
    }
    setRemovingWeek(true);
    if (currentIndex >= next.weeks.length) {
      navigate({ week: next.weeks.length - 1 });
    }
    edit(() => next);
    try {
      const saved = await onSave(next);
      if (saved) {
        markSaved(saved, next);
      }
    } finally {
      setRemovingWeek(false);
      setRemoveWeekNumber(null);
    }
  }

  async function runPrimaryAction() {
    if (!primaryAction) {
      return;
    }
    if (outsidePlan) {
      outsidePlan.onBlocked();
      return;
    }
    const sent = doc;
    setActionBusy(true);
    try {
      const saved = await primaryAction.run(sent, currentIndex);
      if (saved) {
        markSaved(saved, sent);
      }
    } finally {
      setActionBusy(false);
    }
  }

  // A dirty editor is intercepted by the navigation blocker, which opens the leave dialog.
  function leave() {
    breadcrumb.onNavigate();
  }

  if (!week) {
    return null;
  }

  const hostContext: PlanEditorHostContext = { flush, busy: actionBusy || saveState.status === 'saving' || removingWeek, leaveWithoutPrompt };
  const allKindsPresent = CHRONOLOGICAL_KINDS.every((kind) => activeDay?.meals.some((meal) => meal.kind === kind));
  const pendingSource = pendingDrop ? findMeal(pendingDrop.source.dayOfWeek, pendingDrop.source.mealId) : undefined;
  const pendingTarget = pendingDrop
    ? mealAt(pendingDrop.target.dayOfWeek, pendingDrop.target.rowIndex)
    : undefined;
  const dropDialog =
    pendingDrop && pendingSource && pendingTarget
      ? {
          source: {
            dayOfWeek: pendingDrop.source.dayOfWeek,
            rowLabelKey: mealKindLabelKey(pendingSource.kind),
            meal: pendingSource,
          },
          target: {
            dayOfWeek: pendingDrop.target.dayOfWeek,
            rowLabelKey: range === 'day' ? mealKindLabelKey(pendingTarget.kind) : rowLabelKey(pendingDrop.target.rowIndex),
            meal: pendingTarget,
          },
        }
      : null;

  const labelPreview = pendingLabel ? previewLabelMove(week, pendingLabel) : null;
  const labelDialog =
    pendingLabel && labelPreview
      ? {
          from: labelName(pendingLabel.axis, pendingLabel.from),
          to: labelName(pendingLabel.axis, pendingLabel.to),
          skippedCopy: labelPreview.outcome.replace.skippedDays,
        }
      : null;

  const sideTabs: EditorSideTabDef<EditorSideTab>[] = [
    {
      id: 'info',
      label: infoTabLabel ?? t('planEditor.info.tab'),
      icon: Info,
      content: renderInfoPanel ? (
        renderInfoPanel({ doc, week, weekNumber: currentIndex + 1, readOnly, onEdit: edit })
      ) : (
        <TemplateInfoPanel
          doc={doc}
          week={week}
          weekNumber={currentIndex + 1}
          usage={usage}
          readOnly={readOnly}
          onEdit={edit}
        />
      ),
    },
    {
      id: 'library',
      label: t('planEditor.library.title'),
      icon: BookOpen,
      content: (
        <LibraryPanel
          canAdd={activeSelected !== null && !slotBlocked(activeSelected)}
          disabled={readOnly}
          onAdd={(item) => activeSelected && addToCell(item, activeSelected)}
          extraTab={
            libraryExtraTab
              ? {
                  label: libraryExtraTab.label,
                  render: () =>
                    libraryExtraTab.render({ doc, week, weekNumber: currentIndex + 1, readOnly, onEdit: edit }),
                }
              : undefined
          }
          hideTabCounts={libraryExtraTab !== undefined}
          extraTabFirst={libraryExtraTab !== undefined && !doc.weeks.some(weekHasItems)}
        />
      ),
    },
    ...(extraSideTabs ?? []),
  ];

  return (
    <SyncConflictsContext.Provider value={conflictsModel}>
    <MealLocksContext.Provider value={locks}>
    <LockRejectionContext.Provider value={lockRejection}>
    <LockConflictsContext.Provider value={lockConflicts}>
    <RestoreRejectedContext.Provider value={restoreMeal}>
    <PopoverBoundaryContext.Provider value={scrollArea}>
    <DragDropProvider
      sensors={DRAG_SENSORS}
      plugins={(defaults) => defaults.map((plugin) => (plugin === Feedback ? Feedback.configure({ dropAnimation: null }) : plugin))}
      onBeforeDragStart={(event) => {
        if (slide.isSliding()) {
          event.preventDefault();
        }
      }}
      onDragStart={(event, manager) => {
        dragActive.current = true;
        // The meal popover is a small scrolling box; auto-scroll would move its rows away from the pointer mid-drag.
        if (readMealItemDrag(event.operation.source?.data)) {
          manager.registry.plugins.get(AutoScroller)?.disable();
        }
        startGridDrag(
          event.operation.source?.data,
          event.operation.source?.element,
          event.operation.activatorEvent,
        );
      }}
      onDragEnd={(event, manager) => {
        dragActive.current = false;
        manager.registry.plugins.get(AutoScroller)?.enable();
        const dropView = finishGridDrag();
        if (event.canceled) {
          return;
        }
        // Meal tiles and labels are dropped where the pointer was in the frozen layout, not on a drop target.
        const rowLabel = readRowLabel(event.operation.source?.data, ROW_LABEL_DRAG);
        if (rowLabel) {
          if (dropView?.kind === 'label' && dropView.target !== null && rowLabel.weekIndex === currentIndex) {
            onLabelDrop({ axis: 'row', from: rowLabel.rowIndex, to: dropView.target }, dropView.alt);
          }
          return;
        }
        const dayLabel = readDayLabel(event.operation.source?.data, DAY_LABEL_DRAG);
        if (dayLabel) {
          if (dropView?.kind === 'label' && dropView.target !== null && dayLabel.weekIndex === currentIndex) {
            onLabelDrop({ axis: 'day', from: dayLabel.dayOfWeek, to: dropView.target }, dropView.alt);
          }
          return;
        }
        const cellMeal = readCellMealDrag(event.operation.source?.data);
        if (cellMeal) {
          dropCellMeal(cellMeal, dropView);
          return;
        }
        const mealItem = readMealItemDrag(event.operation.source?.data);
        if (mealItem) {
          onMealItemDragEnd(mealItem, event);
          return;
        }
        const item = readItem(event.operation.source?.data);
        const cell = readCell(event.operation.target?.data);
        if (item && cell) {
          setSelected(cell);
          addToCell(item, cell);
        }
      }}
    >
      <div className="flex h-full min-h-0">
        {!readOnly && (
          <EditorSidePanel
            tabs={sideTabs}
            tab={sideTab}
            onTabChange={setSideTab}
            open={libraryOpen}
            onOpenChange={setLibraryOpen}
            containerAttributes={{ 'data-plan-library': true }}
            labels={{
              tabs: t('planEditor.panel.tabs'),
              collapse: t('planEditor.library.collapse'),
              expand: t('planEditor.library.expand'),
              openTab: (name) => t('planEditor.panel.open', { name }),
            }}
          />
        )}

        <div className="flex min-h-0 min-w-0 flex-1 flex-col">
          <div data-testid="editor-fixed-header" className="flex shrink-0 flex-col">
            <PlanEditorHeader
                name={doc.name}
                onNameChange={(name) => edit((current) => ({ ...current, name }), 'name')}
                breadcrumbLabel={breadcrumb.label}
                onBreadcrumb={leave}
                breadcrumbLeading={breadcrumb.leading}
                readOnly={readOnly}
                dirty={dirty}
                saveStatus={saveState.status}
                canUndo={canUndo}
                canRedo={canRedo}
                onUndo={undo}
                onRedo={redo}
                onSave={() => void save()}
                primaryAction={
                  primaryAction
                    ? {
                        label: primaryAction.label(currentIndex + 1),
                        disabled: primaryAction.disabled?.(currentIndex, doc) ?? false,
                        title: primaryAction.title?.(currentIndex, doc),
                        busy: actionBusy,
                        onRun: () => void runPrimaryAction(),
                      }
                    : undefined
                }
                secondarySave={secondarySave}
                locked={outsidePlan !== undefined}
                actions={renderHeaderActions?.(hostContext)}
                range={range}
                view={view}
                onRangeChange={changeRange}
                onViewChange={changeView}
              />

            {(range === 'week' || rows.length === 0) && (
              <div className="flex items-center gap-4 px-6 py-3">
                <div className="min-w-0 flex-1">
                <WeekTabs
                  weekCount={doc.weeks.length}
                  current={currentIndex}
                  readOnly={readOnly}
                  weekStatus={
                    weekStatus
                      ? (index) => {
                          const candidate = doc.weeks[index];
                          const status = weekStatus(index);
                          return status === 'published' || (candidate && weekHasItems(candidate)) ? status : 'empty';
                        }
                      : undefined
                  }
                  onSelect={(index) => navigate({ week: index })}
                  addLocked={outsidePlan !== undefined}
                  onAddWeek={() => {
                    if (outsidePlan) {
                      outsidePlan.onBlocked();
                      return;
                    }
                    edit(addWeek);
                    navigate({ week: doc.weeks.length });
                  }}
                  onRemoveLast={allowWeekRemoval ? () => setRemoveWeekNumber(doc.weeks.length) : undefined}
                />
                </div>
                {locks && view === 'meals' && rows.length > 0 && <MealLockLegend />}
              </div>
            )}

            {rows.length > 0 && range === 'day' && (
              <div className="flex flex-col gap-3 px-6 pt-3 pb-3">
                <div className="flex flex-wrap items-center justify-between gap-x-6 gap-y-2">
                  <DayNavigator
                    weekIndex={currentIndex}
                    weekCount={doc.weeks.length}
                    onWeekChange={(delta) =>
                      navigate({ week: Math.min(Math.max(currentIndex + delta, 0), doc.weeks.length - 1) })
                    }
                  />
                  <WeekdayPills
                    week={week}
                    weekIndex={currentIndex}
                    current={dayOfWeek}
                    dailyKcalTarget={dailyKcalTarget}
                    onSelect={(day) => navigate({ day })}
                  />
                </div>
                <DaySummaryCard
                  totals={activeDay ? dayTotals(activeDay) : dayTotals({ dayOfWeek, meals: [] })}
                  kcalTarget={dailyKcalTarget}
                  note={activeDay?.note}
                  readOnly={readOnly}
                  eatenKcal={
                    locks &&
                    week &&
                    (activeDayPast ||
                      (activeDay?.meals ?? []).some((meal) => (locks.meals.get(meal.mealId)?.eatenItemIds.length ?? 0) > 0))
                      ? dayEatenKcal(locks, week.weekNumber, dayOfWeek, activeDay?.meals ?? [])
                      : undefined
                  }
                  addMealDisabled={activeDayPast || allKindsPresent || (activeDay?.meals.length ?? 0) >= MAX_MEALS_PER_DAY}
                  addMealTitle={
                    activeDayPast
                      ? t('planEditor.locks.addMealPast')
                      : allKindsPresent
                      ? t('planEditor.day.allKindsAdded')
                      : (activeDay?.meals.length ?? 0) >= MAX_MEALS_PER_DAY
                        ? t('planEditor.day.limitReached', { max: MAX_MEALS_PER_DAY })
                        : undefined
                  }
                  onAddMeal={openAddMealToDay}
                  onNoteChange={(note) =>
                    edit((current) => setDayNote(current, currentIndex, dayOfWeek, note), `daynote:${currentIndex}:${dayOfWeek}`)
                  }
                />
              </div>
            )}
          </div>

          <div
            ref={setScrollArea}
            data-testid="editor-scroll"
            className="flex min-h-0 flex-1 touch-pan-y touch-pinch-zoom flex-col gap-4 overflow-y-auto overflow-x-clip overscroll-x-contain px-6 pt-1 pb-6"
          >
          {renderBanner?.(hostContext)}

          {conflictsModel && <SyncConflictStrip model={conflictsModel} onShow={showConflict} />}

          {readOnly && readOnlyNotice && (
            <p role="note" className="rounded-xl border border-line bg-muted px-4 py-3 text-body text-ink">
              {readOnlyNotice}
            </p>
          )}

          {(saveState.status === 'conflict' || saveState.status === 'error') && saveState.message && (
            <div
              role="alert"
              className="flex flex-wrap items-center gap-3 rounded-xl border border-error bg-error-soft px-4 py-3 text-body text-error"
            >
              <AlertTriangle className="size-4 shrink-0" aria-hidden="true" />
              <span className="min-w-0 flex-1">
                {saveState.message}
                {lockRejection && (rejectedInDocument || restoreRejected) && (
                  <>
                    {' '}
                    {t(rejectedInDocument ? 'planEditor.locks.fixOutlined' : 'planEditor.locks.fixRemoved')}
                  </>
                )}
              </span>
              {/* A deleted meal has no tile to carry the action, so it sits here. */}
              {lockRejection && !rejectedInDocument && restoreRejected && (
                <Button type="button" variant="destructive" size="sm" onClick={restoreRejected}>
                  {t('planEditor.locks.restore')}
                </Button>
              )}
              {saveState.status === 'conflict' && (
                <Button type="button" variant="destructive" size="sm" onClick={onReload}>
                  {t('planEditor.conflict.reload')}
                </Button>
              )}
            </div>
          )}

          <div key={slide.slide?.key ?? 0} data-slide={slide.slide?.direction} className="flex min-w-0 flex-1 flex-col gap-4">
          {rows.length > 0 && range === 'day' && (
            <DayView
              week={week}
              weekIndex={currentIndex}
              dayOfWeek={dayOfWeek}
              readOnly={readOnly}
              selected={activeSelected}
              onSelect={setSelected}
              onEdit={edit}
              onInsertBetween={insertBetween}
              onMoveMeal={readOnly ? undefined : moveByKey}
              drag={gridDrag}
            />
          )}

          {rows.length > 0 && range === 'week' && (
            <div className="flex flex-1 flex-col gap-3">
              {view === 'meals' && (
                <WeekGrid
                  week={week}
                  weekIndex={currentIndex}
                  targets={targets}
                  selected={activeSelected}
                  readOnly={readOnly}
                  onSelect={selectCell}
                  detailCell={detailCell}
                  renderDetail={renderDetail}
                  onDetailClose={() => setDetailCell(null)}
                  openGhostId={openGhostId}
                  onOpenGhost={setOpenGhostId}
                  onOpenDay={openDay}
                  onEdit={edit}
                  onRemoveRow={requestRemoveRow}
                  onInsertBetween={insertBetween}
                  onAddMeal={() => openAddMeal('week', null)}
                  onMoveMeal={readOnly ? undefined : moveByKey}
                  onMoveMealAlong={readOnly ? undefined : moveAlongByKey}
                  onLabelKeyMove={readOnly || weekHasLock() ? undefined : moveLabelByKey}
                  drag={gridDrag}
                />
              )}
              {view === 'nutrition' && <NutritionView week={week} targets={targets} />}
              <NutritionLegend dailyKcalTarget={dailyKcalTarget} hidden={view !== 'nutrition'} />
            </div>
          )}

          {showPicker && (
            <MealPicker
              onApplyCommon={applyCommonMeals}
              onDone={addMealKinds}
              onCopyMeals={renderCopyMeals ? () => setCopyOpen(true) : undefined}
            />
          )}
          </div>
          </div>
        </div>
      </div>

      <p role="status" aria-live="polite" data-testid="plan-editor-announcer" className="sr-only">
        {announcement}
      </p>

      <DragOverlay dropAnimation={null}>
        {(source) => {
          const item = readItem(source.data);
          if (item) {
            return (
              <div className="rounded-xl border border-line bg-card px-4 py-3 text-copy font-semibold text-ink shadow-popover">
                {itemName(item)}
              </div>
            );
          }
          const rowLabel = readRowLabel(source.data, ROW_LABEL_DRAG);
          const dayLabel = readDayLabel(source.data, DAY_LABEL_DRAG);
          if (rowLabel || dayLabel) {
            if (gridDrag?.kind === 'label') {
              return <GridDragOverlay view={gridDrag} week={week} />;
            }
            return (
              <div className="rounded-xl border border-line bg-card px-4 py-2 text-copy font-semibold text-ink shadow-popover">
                {rowLabel ? labelName('row', rowLabel.rowIndex) : labelName('day', dayLabel?.dayOfWeek ?? 1)}
              </div>
            );
          }
          const mealItem = readMealItemDrag(source.data);
          if (mealItem) {
            const owner = findMeal(mealItem.dayOfWeek, mealItem.mealId);
            const label =
              mealItem.kind === 'recipe'
                ? owner?.recipes[mealItem.index]?.recipeName
                : owner?.foods[mealItem.index] && foodDisplayName(owner.foods[mealItem.index], i18n.language);
            return label ? (
              <div className="max-w-80 rounded-xl border border-line bg-card px-4 py-3 text-copy font-semibold text-ink shadow-popover [overflow-wrap:anywhere]">
                {label}
              </div>
            ) : null;
          }
          const cellMeal = readCellMealDrag(source.data);
          const meal = cellMeal ? findMeal(cellMeal.dayOfWeek, cellMeal.mealId) : undefined;
          return meal ? (
            <div className="flex max-w-60 flex-col gap-0.5 rounded-xl border border-line bg-card px-4 py-3 shadow-popover">
              <span className="truncate text-copy font-semibold text-ink">{mealItemNames(meal, i18n.language)[0]}</span>
              <span className="text-body text-muted-foreground">
                {t('planEditor.cell.kcal', { kcal: Math.round(mealTotals(meal).kcal) })}
              </span>
            </div>
          ) : null;
        }}
      </DragOverlay>

      <AddMealSheet
        open={addMeal.open && !readOnly}
        onOpenChange={(open) => setAddMeal((previous) => ({ ...previous, open }))}
        mode={addMeal.mode}
        requestKey={addMeal.key}
        preselect={addMeal.preselect}
        dayLabel={t(`planEditor.daysLong.${dayOfWeek}`)}
        weekNumber={currentIndex + 1}
        dayKinds={activeDay?.meals.map((meal) => meal.kind) ?? []}
        week={week}
        onSubmitDay={addMealToActiveDay}
        onSubmitWeek={addMealToWeek}
      />

      <CardDropDialog
        drop={dropDialog}
        language={i18n.language}
        onReplace={() => resolveDrop('replace')}
        onAdd={() => resolveDrop('add')}
        onCancel={() => setPendingDrop(null)}
      />

      <LabelDropDialog drop={labelDialog} onChoose={resolveLabelDrop} onCancel={() => setPendingLabel(null)} />

      <RemoveMealRowDialog removal={pendingRemoval} onConfirm={confirmRemoval} onCancel={() => setPendingRemoval(null)} />

      <RemoveWeekDialog
        weekNumber={removeWeekNumber}
        busy={removingWeek}
        onConfirm={() => void confirmRemoveWeek()}
        onCancel={() => setRemoveWeekNumber(null)}
      />

      {renderCopyMeals?.({
        open: copyOpen,
        onOpenChange: setCopyOpen,
        onApply: (source) => {
          const sourceWeek = source.weeks[0];
          if (sourceWeek) {
            edit((current) => copyWeekMeals(current, currentIndex, sourceWeek));
          }
          setCopyOpen(false);
        },
      })}

      <Dialog
        open={leaveBlocker.state === 'blocked'}
        onOpenChange={(open) => {
          if (!open && leaveBlocker.state === 'blocked') {
            leaveBlocker.reset();
          }
        }}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t('planEditor.leave.title')}</DialogTitle>
            <DialogDescription>{t('planEditor.leave.body')}</DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button
              type="button"
              variant="outline"
              onClick={() => leaveBlocker.state === 'blocked' && leaveBlocker.reset()}
            >
              {t('planEditor.leave.stay')}
            </Button>
            <Button
              type="button"
              variant="destructive"
              onClick={() => leaveBlocker.state === 'blocked' && leaveBlocker.proceed()}
            >
              {t('planEditor.leave.discard')}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </DragDropProvider>
    </PopoverBoundaryContext.Provider>
    </RestoreRejectedContext.Provider>
    </LockConflictsContext.Provider>
    </LockRejectionContext.Provider>
    </MealLocksContext.Provider>
    </SyncConflictsContext.Provider>
  );
}
