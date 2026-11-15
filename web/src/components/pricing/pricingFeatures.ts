import type { PublicSubscriptionPlanDto } from '@/api/generated';
import { EDITOR_FEATURES } from '@/components/admin-plans/planFeatureLabels';

export type PricingFeatureKey = (typeof EDITOR_FEATURES)[number]['key'];

/** Public wording of each plan feature. Typed over the editor's flag list, so a new flag fails the build until it has copy here. */
export const PRICING_FEATURE_COPY: Record<PricingFeatureKey, { label: string; hint: string }> = {
  chat: { label: 'pricing.features.chat.label', hint: 'pricing.features.chat.hint' },
  nutritionPlans: { label: 'pricing.features.nutritionPlans.label', hint: 'pricing.features.nutritionPlans.hint' },
  planTemplates: { label: 'pricing.features.planTemplates.label', hint: 'pricing.features.planTemplates.hint' },
  recipes: { label: 'pricing.features.recipes.label', hint: 'pricing.features.recipes.hint' },
  ingredients: { label: 'pricing.features.ingredients.label', hint: 'pricing.features.ingredients.hint' },
  progressPhotos: { label: 'pricing.features.progressPhotos.label', hint: 'pricing.features.progressPhotos.hint' },
  measurements: { label: 'pricing.features.measurements.label', hint: 'pricing.features.measurements.hint' },
  forms: { label: 'pricing.features.forms.label', hint: 'pricing.features.forms.hint' },
  checkIns: { label: 'pricing.features.checkIns.label', hint: 'pricing.features.checkIns.hint' },
  perClientCheckIns: {
    label: 'pricing.features.perClientCheckIns.label',
    hint: 'pricing.features.perClientCheckIns.hint',
  },
  automations: { label: 'pricing.features.automations.label', hint: 'pricing.features.automations.hint' },
  packages: { label: 'pricing.features.packages.label', hint: 'pricing.features.packages.hint' },
  payments: { label: 'pricing.features.payments.label', hint: 'pricing.features.payments.hint' },
};

/** The features in the order of the compare table. */
export const PRICING_FEATURES = EDITOR_FEATURES;

/** How many of the features a plan includes. */
export function includedFeatureCount(plan: PublicSubscriptionPlanDto): number {
  return PRICING_FEATURES.filter(({ flag }) => plan[flag] === true).length;
}

/** DOM id of the compare table; the cards' "see all" links jump here. */
export const PRICING_COMPARE_ID = 'pricing-compare';
