import type { Collaboration } from '@/api/collaborations';
import type { ClientOwnPlanItem } from '@/api/generated';
import { isActivePlan, planKind } from '@/api/plans';
import type { PlanKind } from '@/api/plans';
import { isoDay, planEndDate } from '@/components/nutrition/plan/model';

import { COACH_ROLES } from './collaborationRoles';
import type { CoachRole, CoveredCollaboration } from './collaborationRoles';

/** What one area (training or nutrition) of Today shows. */
export type AreaState =
  /** An Active plan is under way. */
  | { kind: 'running'; plan: ClientOwnPlanItem; next?: ClientOwnPlanItem }
  /** An Active plan with a published week whose start date is still ahead. */
  | { kind: 'startsLater'; plan: ClientOwnPlanItem }
  /** A coach covers the area but no plan is published yet. */
  | { kind: 'building'; collaboration: Collaboration }
  /** Nobody covers the area. */
  | { kind: 'noCoach' };

export type AreaBlock = { role: CoachRole; state: AreaState };

const startOf = (plan: ClientOwnPlanItem): string => isoDay(plan.startDate);

/**
 * The earliest Active plan of `kind` with a published week that starts after `today` (`yyyy-MM-dd`).
 * Same rule as the server uses when it serves an upcoming plan.
 */
export function pickUpcomingPlan(
  plans: readonly ClientOwnPlanItem[] | undefined,
  kind: PlanKind,
  today: string,
): ClientOwnPlanItem | undefined {
  let best: ClientOwnPlanItem | undefined;
  for (const plan of plans ?? []) {
    if (planKind(plan) !== kind || !isActivePlan(plan)) continue;
    if ((plan.publishedWeekCount ?? 0) <= 0) continue;
    const start = startOf(plan);
    if (start === '' || start <= today) continue;
    if (!best || start < startOf(best)) best = plan;
  }
  return best;
}

/**
 * True when `plan` is under way on `today`: Active, at least one published week, started on or before today and
 * its window not yet ended. Shared by Today and the "Your plans" list so they cannot disagree.
 */
export function isRunningPlan(plan: ClientOwnPlanItem, today: string): boolean {
  if (!isActivePlan(plan) || (plan.publishedWeekCount ?? 0) <= 0) return false;
  const start = startOf(plan);
  if (start === '' || start > today) return false;
  return planEndDate(start, Math.max(plan.totalWeeks ?? 0, 1)) >= today;
}

/**
 * The last day a running plan stays open, when the server says it is capped and that day has not passed
 * (`yyyy-MM-dd`, compared as strings). Undefined for any other area state.
 */
export function planRunsUntil(block: AreaBlock, today: string): string | undefined {
  if (block.state.kind !== 'running') return undefined;
  const runsUntil = isoDay(block.state.plan.runsUntil);
  return runsUntil !== '' && runsUntil >= today ? runsUntil : undefined;
}

/** True when any area has a plan under way. */
export const hasRunningPlan = (blocks: readonly AreaBlock[]): boolean =>
  blocks.some((block) => block.state.kind === 'running');

/** True when an area is waiting on a plan (it starts later or the coach is still building it). */
export const hasUnstartedArea = (blocks: readonly AreaBlock[]): boolean =>
  blocks.some((block) => block.state.kind === 'startsLater' || block.state.kind === 'building');

/**
 * One block state per area, training first. Null when no area has an Active plan, which leaves
 * Today to its coach-progress view. `covering` holds the collaborations that cover at least one role.
 */
export function selectAreaBlocks(
  plans: readonly ClientOwnPlanItem[],
  covering: readonly CoveredCollaboration[],
  today: string,
): AreaBlock[] | null {
  if (!plans.some(isActivePlan)) return null;

  return COACH_ROLES.map((role): AreaBlock => {
    const kind: PlanKind = role === 'Nutritionist' ? 'nutrition' : 'training';
    const upcoming = pickUpcomingPlan(plans, kind, today);
    const running = plans.find((plan) => planKind(plan) === kind && isRunningPlan(plan, today));
    if (running) {
      const lastWeek =
        running.currentWeek !== undefined && running.totalWeeks !== undefined && running.currentWeek === running.totalWeeks;
      return { role, state: { kind: 'running', plan: running, next: lastWeek ? upcoming : undefined } };
    }
    if (upcoming) return { role, state: { kind: 'startsLater', plan: upcoming } };
    const coach = covering.find((entry) => entry.roles.includes(role));
    if (coach) return { role, state: { kind: 'building', collaboration: coach.collaboration } };
    return { role, state: { kind: 'noCoach' } };
  });
}
