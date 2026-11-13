import type { PlanFeatureKey } from '@/components/subscription/planFeatures';

/** Admin wording of each shared plan feature (plan cards in the admin dialogs). */
export const ADMIN_FEATURE_LABEL_KEY: Record<PlanFeatureKey, string> = {
  plans: 'adminPlans.features.plans',
  messaging: 'adminPlans.features.chat',
  forms: 'adminPlans.features.forms',
  checkIns: 'adminPlans.features.checkIns',
  perClientCheckIns: 'adminPlans.features.perClientCheckIns',
  payments: 'adminPlans.features.payments',
};

/** Short chip of each shared plan feature (admin plans table). */
export const ADMIN_FEATURE_CHIP_KEY: Record<PlanFeatureKey, string> = {
  plans: 'plans',
  messaging: 'chat',
  forms: 'forms',
  checkIns: 'checkIns',
  perClientCheckIns: 'perClientCheckIns',
  payments: 'payments',
};
