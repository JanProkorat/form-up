/**
 * The one list of plan features, in display order. Every plan card, feature list and chip row reads it, so a
 * feature added here shows everywhere; the per-place label maps are typed by `PlanFeatureKey`, so a new key
 * without its labels fails the build.
 */
export const PLAN_FEATURES = [
  { flag: 'canCreatePlans', key: 'plans' },
  { flag: 'canMessage', key: 'messaging' },
  { flag: 'canSendQuestionnaires', key: 'forms' },
  { flag: 'canUseWeeklyCheckIns', key: 'checkIns' },
  { flag: 'canUsePerClientCheckInConfig', key: 'perClientCheckIns' },
  { flag: 'canUsePayments', key: 'payments' },
] as const;

export type PlanFeatureKey = (typeof PLAN_FEATURES)[number]['key'];
export type PlanFeatureFlag = (typeof PLAN_FEATURES)[number]['flag'];

/** Coach-facing label of each feature (plan picker, Plan and billing). */
export function coachFeatureLabelKey(key: PlanFeatureKey): string {
  return `subscription.plan.features.${key}`;
}
