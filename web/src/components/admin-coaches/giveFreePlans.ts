import { Crown, Gem, Rocket, Sprout, Star, Zap, type LucideIcon } from 'lucide-react';
import type { SubscriptionPlanDto } from '@/api/admin-subscription-plans';
import { isFreePlan } from '@/components/admin-plans/planForm';

/**
 * Class sets of one plan colour. Every class is spelled out in full so Tailwind can see it, and
 * every colour is a theme token (tints are token + opacity), so both themes follow.
 */
export interface PlanColor {
  /** Tinted header band of the card. */
  band: string;
  /** Icon tile glyph colour. */
  tile: string;
  /** Radio accent. */
  accent: string;
  /** Border, soft shadow and background of the selected card. */
  selected: string;
  /** The "Selected" pill. */
  pill: string;
  /** The dot in the dialog footer. */
  dot: string;
  icon: LucideIcon;
}

/** Fixed sequence: green, orange, red, blue, purple, teal. */
export const PLAN_COLORS: readonly PlanColor[] = [
  {
    band: 'bg-nutrition/10',
    tile: 'text-nutrition',
    accent: 'accent-nutrition',
    selected: 'border-nutrition bg-linear-to-b from-nutrition/10 to-card shadow-lg shadow-nutrition/25',
    pill: 'border-nutrition/40 bg-nutrition/15',
    dot: 'bg-nutrition',
    icon: Sprout,
  },
  {
    band: 'bg-training/10',
    tile: 'text-training',
    accent: 'accent-training',
    selected: 'border-training bg-linear-to-b from-training/10 to-card shadow-lg shadow-training/25',
    pill: 'border-training/40 bg-training/15',
    dot: 'bg-training',
    icon: Zap,
  },
  {
    band: 'bg-error/10',
    tile: 'text-error',
    accent: 'accent-error',
    selected: 'border-error bg-linear-to-b from-error/10 to-card shadow-lg shadow-error/25',
    pill: 'border-error/40 bg-error/15',
    dot: 'bg-error',
    icon: Rocket,
  },
  {
    band: 'bg-macro-protein/10',
    tile: 'text-macro-protein',
    accent: 'accent-macro-protein',
    selected:
      'border-macro-protein bg-linear-to-b from-macro-protein/10 to-card shadow-lg shadow-macro-protein/25',
    pill: 'border-macro-protein/40 bg-macro-protein/15',
    dot: 'bg-macro-protein',
    icon: Gem,
  },
  {
    band: 'bg-macro-carbs/10',
    tile: 'text-macro-carbs',
    accent: 'accent-macro-carbs',
    selected: 'border-macro-carbs bg-linear-to-b from-macro-carbs/10 to-card shadow-lg shadow-macro-carbs/25',
    pill: 'border-macro-carbs/40 bg-macro-carbs/15',
    dot: 'bg-macro-carbs',
    icon: Crown,
  },
  {
    band: 'bg-macro-fibre/10',
    tile: 'text-macro-fibre',
    accent: 'accent-macro-fibre',
    selected: 'border-macro-fibre bg-linear-to-b from-macro-fibre/10 to-card shadow-lg shadow-macro-fibre/25',
    pill: 'border-macro-fibre/40 bg-macro-fibre/15',
    dot: 'bg-macro-fibre',
    icon: Star,
  },
];

export interface GiveFreeOption {
  plan: SubscriptionPlanDto;
  color: PlanColor;
}

/**
 * The plans an admin can give for free: not Free, still offered, cheapest first (code breaks ties).
 * Colours follow the position after sorting, wrapping if there are more plans than colours.
 */
export function buildGiveFreeOptions(plans: SubscriptionPlanDto[]): GiveFreeOption[] {
  return plans
    .filter((plan) => plan.code && !isFreePlan(plan) && plan.isActive)
    .sort((a, b) => (a.priceMinorUnits ?? 0) - (b.priceMinorUnits ?? 0) || (a.code ?? '').localeCompare(b.code ?? ''))
    .map((plan, index) => ({ plan, color: PLAN_COLORS[index % PLAN_COLORS.length] }));
}
