import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { WeekStatus as ApiWeekStatus, type GetPlanResponse, type UpdatePlanRequest } from '@/api/generated';
import {
  completePlanFromEditor,
  deletePlanFromEditor,
  publishPlanWeekFromEditor,
  savePlanFromEditor,
} from '@/api/plans';
import { planHasEnded, publishedWeekHasStarted } from '@/components/client-plan-editor/clientPlanDates';
import type { PlanEditorPrimaryAction, PlanEditorSaveState } from '@/components/plan-editor/PlanEditor';
import {
  nullToUndefined,
  toEditorTargets,
  toEditorWeeks,
  toGlobalSettings,
  toRequestWeeks,
} from '@/components/plan-editor/plan-editor-mapping';
import { weekHasItems } from '@/components/plan-editor/plan-editor-ops';
import { DAYS_PER_WEEK, type EditorDocument, type WeekStatus } from '@/components/plan-editor/plan-editor-types';
import { parseLockRejection, type LockRejection } from '@/components/plan-editor/meal-locks';
import { getApiErrorMessage, getErrorStatus, getProblemBody, getRfc7807ErrorCode, showApiError } from '@/lib/api-errors';
import { isClientOverPlanLimit } from '@/lib/subscriptionErrors';
import { useToastStore } from '@/stores/toast';

const DAY_MS = 24 * 60 * 60 * 1000;

/** Maps the stored plan to the editor's neutral document. Week status stays with the host. */
export function toPlanEditorDocument(plan: GetPlanResponse): EditorDocument {
  return {
    name: plan.name ?? '',
    targets: toEditorTargets(plan.globalSettings),
    startDate: plan.startDate ? plan.startDate.slice(0, 10) : undefined,
    weeks: toEditorWeeks(plan.weeks),
  };
}

/**
 * The PUT replaces the whole plan: name, start date, targets and weeks come from the editor document;
 * supplements are echoed back from the loaded copy. Goal and target weight are left out, which the
 * server reads as "keep what is stored".
 */
export function toPlanUpdateRequest(
  loaded: GetPlanResponse,
  doc: EditorDocument,
  version: number | undefined,
  preserveLockedMeals = false,
): UpdatePlanRequest {
  return {
    name: doc.name.trim(),
    globalSettings: toGlobalSettings(doc.targets),
    weeks: toRequestWeeks(doc.weeks),
    startDate: doc.startDate,
    supplements: (loaded.supplements ?? []).map((supplement) => ({
      externalId: supplement.externalId,
      name: supplement.name,
      dose: nullToUndefined(supplement.dose),
      notes: nullToUndefined(supplement.notes),
    })),
    version,
    // Sent only for the save that follows a template apply; always sending it would make the server drop edits
    // to locked meals silently instead of refusing them.
    ...(preserveLockedMeals ? { preserveLockedMeals: true } : {}),
  };
}

/** Week number -> publication state, read from a plan response. */
function weekStatusMap(plan: GetPlanResponse): Record<number, WeekStatus> {
  const map: Record<number, WeekStatus> = {};
  (plan.weeks ?? []).forEach((week, index) => {
    map[week.weekNumber ?? index + 1] = week.status === ApiWeekStatus.Published ? 'published' : 'draft';
  });
  return map;
}

/** Midnight UTC of the plan's Monday plus whole weeks; null when the plan has no start date. */
function weekStartUtc(startDate: string | undefined, weekIndex: number): number | null {
  if (!startDate) {
    return null;
  }
  const [year, month, day] = startDate.slice(0, 10).split('-').map(Number);
  if (!year || !month || !day) {
    return null;
  }
  return Date.UTC(year, month - 1, day) + weekIndex * DAYS_PER_WEEK * DAY_MS;
}

function todayUtc(): number {
  const now = new Date();
  return Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate());
}

function sameDocument(left: EditorDocument, right: EditorDocument): boolean {
  return JSON.stringify(left) === JSON.stringify(right);
}

/** Completed and Archived plans are history: opening them read-only keeps a save from reviving them. */
export function isClosedPlan(plan: GetPlanResponse): boolean {
  const status = plan.status?.toLowerCase();
  return status === 'completed' || status === 'archived';
}

/**
 * Host logic for editing a client's plan: the initial document, save, "Publish week N", the week status
 * map and the conflict state. The loaded copy is captured once, so a background refetch never changes the
 * version the next save is checked against; every save and publish response replaces it.
 */
export function useClientPlanEditor(loadedPlan: GetPlanResponse, clientId: string, onOverPlanLimit?: () => void) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const planId = loadedPlan.planId ?? '';
  const [initial] = useState(() => toPlanEditorDocument(loadedPlan));
  const [plan, setPlan] = useState(loadedPlan);
  const planRef = useRef(loadedPlan);
  const lastSavedRef = useRef(initial);
  // Set when a template was applied in the browser; the next save tells the server to keep locked meals and past days.
  const preserveNextSaveRef = useRef(false);
  const [saveState, setSaveState] = useState<PlanEditorSaveState>({ status: 'idle' });
  // The tile the server refused to save; the editor outlines it until the coach changes that meal or a save succeeds.
  const [lockRejection, setLockRejection] = useState<LockRejection | null>(null);

  const saveMutation = useMutation({
    mutationFn: (request: UpdatePlanRequest) => savePlanFromEditor(planId, request),
  });
  const publishMutation = useMutation({
    mutationFn: (args: { weekNumber: number; version: number | undefined }) =>
      publishPlanWeekFromEditor(planId, args.weekNumber, args.version),
  });

  /** Takes a server response as the new baseline: version, week statuses and the saved document. */
  function adopt(response: GetPlanResponse): EditorDocument {
    const document = toPlanEditorDocument(response);
    planRef.current = response;
    lastSavedRef.current = document;
    setLockRejection(null);
    setPlan(response);
    void queryClient.invalidateQueries({ queryKey: ['nutrition-plan-detail', planId] });
    void queryClient.invalidateQueries({ queryKey: ['client-plans', clientId] });
    void queryClient.invalidateQueries({ queryKey: ['client-dashboard', clientId] });
    return document;
  }

  /** 403 CLIENT_OVER_PLAN_LIMIT: opens the paywall instead of an error; false when it is another error. */
  function handleOverPlanLimit(error: unknown): boolean {
    if (!onOverPlanLimit || !isClientOverPlanLimit(error)) {
      return false;
    }
    onOverPlanLimit();
    return true;
  }

  function fail(error: unknown) {
    if (handleOverPlanLimit(error)) {
      setSaveState({ status: 'idle' });
      return;
    }
    if (getErrorStatus(error) === 409) {
      const code = getRfc7807ErrorCode(error);
      if (code === 'MEAL_LOCKED' || code === 'PAST_DAY_LOCKED') {
        // The plan moved on under the editor (a meal was eaten, midnight passed). The coach's edits stay: say what
        // happened, outline the refused tile and refresh the lock data; the coach fixes it and saves again.
        const message = t(code === 'MEAL_LOCKED' ? 'planEditor.locks.mealLockedError' : 'planEditor.locks.pastDayLockedError');
        setSaveState({ status: 'error', message });
        setLockRejection(parseLockRejection(getProblemBody(error)));
        useToastStore.getState().addToast(message, 'error');
        // Safe: the editor keeps its own document and `loadedPlan` is captured once, so this only refreshes the lock verdicts.
        void queryClient.invalidateQueries({ queryKey: ['nutrition-plan-detail', planId] });
        return;
      }
      setSaveState({ status: 'conflict', message: t('planEditor.conflict.message') });
    } else {
      setSaveState({ status: 'error', message: getApiErrorMessage(error, 'planEditor.saveError') });
    }
  }

  async function save(doc: EditorDocument): Promise<EditorDocument | null> {
    setSaveState({ status: 'saving' });
    try {
      const saved = await saveMutation.mutateAsync(
        toPlanUpdateRequest(planRef.current, doc, planRef.current.version, preserveNextSaveRef.current),
      );
      preserveNextSaveRef.current = false;
      setSaveState({ status: 'idle' });
      return adopt(saved);
    } catch (error) {
      fail(error);
      return null;
    }
  }

  const deleteMutation = useMutation({ mutationFn: () => deletePlanFromEditor(planId) });
  const completeMutation = useMutation({
    mutationFn: (version: number | undefined) => completePlanFromEditor(planId, version),
  });

  /** Hard-deletes the plan; resolves true on success, after dropping its cached copy. */
  async function deletePlan(): Promise<boolean> {
    try {
      await deleteMutation.mutateAsync();
    } catch (error) {
      if (!handleOverPlanLimit(error)) {
        showApiError(error, 'clientPlanEditor.delete.error');
      }
      return false;
    }
    queryClient.removeQueries({ queryKey: ['nutrition-plan-detail', planId] });
    void queryClient.invalidateQueries({ queryKey: ['client-plans', clientId] });
    void queryClient.invalidateQueries({ queryKey: ['client-dashboard', clientId] });
    return true;
  }

  /**
   * Finishes the plan. `flush` saves pending edits first, so the version sent is the one that save returned;
   * resolves true on success.
   */
  async function finishPlan(flush: () => Promise<boolean>): Promise<boolean> {
    if (!(await flush())) {
      return false;
    }
    try {
      await completeMutation.mutateAsync(planRef.current.version);
    } catch (error) {
      if (handleOverPlanLimit(error)) {
        return false;
      }
      if (getErrorStatus(error) === 409) {
        setSaveState({ status: 'conflict', message: t('planEditor.conflict.message') });
      } else {
        showApiError(error, 'clientPlanEditor.finish.error');
      }
      return false;
    }
    void queryClient.invalidateQueries({ queryKey: ['client-plans', clientId] });
    void queryClient.invalidateQueries({ queryKey: ['nutrition-plan-detail', planId] });
    void queryClient.invalidateQueries({ queryKey: ['client-dashboard', clientId] });
    return true;
  }

  /** Saves the document when it has unsaved changes, then publishes the selected week. */
  async function publishWeek(doc: EditorDocument, weekIndex: number): Promise<EditorDocument | null> {
    const weekNumber = doc.weeks[weekIndex]?.weekNumber;
    if (weekNumber === undefined) {
      return null;
    }
    setSaveState({ status: 'saving' });
    try {
      if (!sameDocument(doc, lastSavedRef.current)) {
        const saved = await saveMutation.mutateAsync(
          toPlanUpdateRequest(planRef.current, doc, planRef.current.version, preserveNextSaveRef.current),
        );
        preserveNextSaveRef.current = false;
        adopt(saved);
      }
      const published = await publishMutation.mutateAsync({ weekNumber, version: planRef.current.version });
      setSaveState({ status: 'idle' });
      return adopt(published);
    } catch (error) {
      fail(error);
      return null;
    }
  }

  const statuses = weekStatusMap(plan);

  /** Why a week cannot be published right now, or null when it can. */
  function publishBlocker(weekIndex: number, doc: EditorDocument): 'published' | 'empty' | 'noStart' | 'past' | null {
    const week = doc.weeks[weekIndex];
    if (!week || statuses[week.weekNumber] === 'published') {
      return 'published';
    }
    if (!weekHasItems(week)) {
      return 'empty';
    }
    const start = weekStartUtc(plan.startDate, weekIndex);
    if (start === null) {
      return 'noStart';
    }
    return start < todayUtc() ? 'past' : null;
  }

  const primaryAction: PlanEditorPrimaryAction = {
    label: (weekNumber) => t('clientPlanEditor.publishWeek', { week: weekNumber }),
    run: publishWeek,
    disabled: (weekIndex, doc) => publishBlocker(weekIndex, doc) !== null,
    title: (weekIndex, doc) => {
      const blocker = publishBlocker(weekIndex, doc);
      if (blocker === 'noStart') {
        return t('apiErrors.START_DATE_REQUIRED');
      }
      return blocker === 'past' ? t('apiErrors.WEEK_START_IN_PAST') : undefined;
    },
  };

  /** A week that is not stored yet counts as a draft. */
  function weekStatus(weekIndex: number): WeekStatus {
    return statuses[weekIndex + 1] ?? 'draft';
  }

  const savedStartDate = plan.startDate ? plan.startDate.slice(0, 10) : undefined;
  const storedWeeks = plan.weeks ?? [];
  const isActive = plan.status?.toLowerCase() === 'active';
  const started = publishedWeekHasStarted(
    savedStartDate,
    storedWeeks.map((week, index) => ({
      weekNumber: week.weekNumber ?? index + 1,
      published: week.status === ApiWeekStatus.Published,
    })),
  );

  return {
    initial,
    saveState,
    lockRejection,
    clearLockRejection: () => setLockRejection(null),
    save,
    primaryAction,
    weekStatus,
    savedStartDate,
    /** Deleting is offered until a published week has started. */
    canDelete: !isClosedPlan(plan) && !started,
    /** Finishing is offered while the plan is Active. */
    canFinish: isActive,
    /** An Active plan whose last week is over: the nutritionist is nudged to finish it. */
    ended: isActive && planHasEnded(savedStartDate, storedWeeks.length),
    /** Called after a template lands in the document: the next save sends `preserveLockedMeals` once. */
    markTemplateApplied: () => {
      preserveNextSaveRef.current = true;
    },
    deletePlan,
    finishPlan,
    lifecycleBusy: deleteMutation.isPending || completeMutation.isPending,
  };
}
