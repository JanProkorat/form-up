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

/** The flags the plan editor switches, in the order of the editor's Features card. */
export const EDITOR_FEATURES = [
  { flag: 'canMessage', key: 'chat' },
  { flag: 'canCreatePlans', key: 'nutritionPlans' },
  { flag: 'canUsePlanTemplates', key: 'planTemplates' },
  { flag: 'canUseRecipes', key: 'recipes' },
  { flag: 'canUseIngredients', key: 'ingredients' },
  { flag: 'canUseProgressPhotos', key: 'progressPhotos' },
  { flag: 'canUseWeightAndMeasurements', key: 'measurements' },
  { flag: 'canSendQuestionnaires', key: 'forms' },
  { flag: 'canUseWeeklyCheckIns', key: 'checkIns' },
  { flag: 'canUsePerClientCheckInConfig', key: 'perClientCheckIns' },
  { flag: 'canUseAutomations', key: 'automations' },
  { flag: 'canUsePackages', key: 'packages' },
  { flag: 'canUsePayments', key: 'payments' },
] as const;

export type EditorFeatureFlag = (typeof EDITOR_FEATURES)[number]['flag'];

/** Label and hint keys of an editor feature. */
export const editorFeatureLabelKey = (key: string): string => `adminPlans.editor.features.${key}.label`;
export const editorFeatureHintKey = (key: string): string => `adminPlans.editor.features.${key}.hint`;

/** Short chip of each shared plan feature (admin plans table). */
export const ADMIN_FEATURE_CHIP_KEY: Record<PlanFeatureKey, string> = {
  plans: 'plans',
  messaging: 'chat',
  forms: 'forms',
  checkIns: 'checkIns',
  perClientCheckIns: 'perClientCheckIns',
  payments: 'payments',
};
