import { z } from 'zod';
import {
  PromotionDuration,
  PromotionKind,
  PromotionMode,
  type CreatePromotionRequest,
} from '@/api/admin-promotions';
import { inputToMinor } from '@/components/admin-plans/planForm';
import { parseAmount } from '@/lib/money';

export const PROMOTION_NAME_MAX = 200;
export const PROMOTION_CODE_MAX = 50;
export const PROMOTION_MONTHS_MIN = 2;
export const PROMOTION_MONTHS_MAX = 36;
const FIXED_AMOUNT_CURRENCY = 'CZK';

/** The paid plans a promotion can cover, in the order the drawer lists them. */
export const PROMOTION_PLAN_CODES = ['start', 'pro', 'studio'] as const;

const CODE_PATTERN = /^[A-Za-z0-9]+$/;
const INT_PATTERN = /^\d+$/;
const DATE_PATTERN = /^(\d{4})-(\d{2})-(\d{2})$/;

const promotionShape = z.object({
  name: z.string(),
  kind: z.enum(['percent', 'fixed']),
  value: z.string(),
  mode: z.enum(['automatic', 'code']),
  code: z.string(),
  duration: z.enum(['first', 'months', 'forever']),
  durationMonths: z.string(),
  claimFrom: z.string(),
  claimUntil: z.string(),
  planCodes: z.array(z.string()),
  maxUses: z.string(),
  newCoachesOnly: z.boolean(),
  oneUsePerCoach: z.boolean(),
});

export type PromotionFormValues = z.infer<typeof promotionShape>;

/** `yyyy-mm-dd` for a local calendar date. */
export function toDateInput(date: Date): string {
  const pad = (part: number) => String(part).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

function parseDateInput(raw: string, endOfDay: boolean): Date | null {
  const match = DATE_PATTERN.exec(raw);
  if (!match) {
    return null;
  }
  const [year, month, day] = [Number(match[1]), Number(match[2]) - 1, Number(match[3])];
  return endOfDay ? new Date(year, month, day, 23, 59, 59, 999) : new Date(year, month, day);
}

export function defaultPromotionValues(now: Date = new Date()): PromotionFormValues {
  return {
    name: '',
    kind: 'percent',
    value: '',
    mode: 'automatic',
    code: '',
    duration: 'first',
    durationMonths: '',
    claimFrom: toDateInput(now),
    claimUntil: '',
    planCodes: [...PROMOTION_PLAN_CODES],
    maxUses: '',
    newCoachesOnly: false,
    oneUsePerCoach: true,
  };
}

/** The form's validation; messages are i18n keys shown under the field. */
export function createPromotionSchema(now: () => Date = () => new Date()) {
  return promotionShape.superRefine((values, ctx) => {
    const fail = (path: keyof PromotionFormValues, message: string) =>
      ctx.addIssue({ code: 'custom', path: [path], message });

    const name = values.name.trim();
    if (!name) {
      fail('name', 'adminPromotions.validation.nameRequired');
    } else if (name.length > PROMOTION_NAME_MAX) {
      fail('name', 'adminPromotions.validation.nameTooLong');
    }

    if (values.kind === 'percent') {
      const percent = values.value.trim();
      if (!INT_PATTERN.test(percent) || Number(percent) < 1 || Number(percent) > 100) {
        fail('value', 'adminPromotions.validation.valuePercent');
      }
    } else {
      const amount = parseAmount(values.value);
      if (amount === null || amount <= 0) {
        fail('value', 'adminPromotions.validation.valueAmount');
      }
    }

    if (values.mode === 'code') {
      const code = values.code.trim();
      if (!code) {
        fail('code', 'adminPromotions.validation.codeRequired');
      } else if (!CODE_PATTERN.test(code) || code.length > PROMOTION_CODE_MAX) {
        fail('code', 'adminPromotions.validation.codeInvalid');
      }
    }

    if (values.duration === 'months') {
      const months = values.durationMonths.trim();
      if (
        !INT_PATTERN.test(months) ||
        Number(months) < PROMOTION_MONTHS_MIN ||
        Number(months) > PROMOTION_MONTHS_MAX
      ) {
        fail('durationMonths', 'adminPromotions.validation.monthsInvalid');
      }
    }

    const from = parseDateInput(values.claimFrom, false);
    if (!from) {
      fail('claimFrom', 'adminPromotions.validation.claimFromRequired');
    }
    if (values.claimUntil) {
      const until = parseDateInput(values.claimUntil, true);
      if (!until || (from && until <= from) || until <= now()) {
        fail('claimUntil', 'adminPromotions.validation.windowInvalid');
      }
    }

    if (values.planCodes.length === 0) {
      fail('planCodes', 'adminPromotions.validation.plansRequired');
    }

    const maxUses = values.maxUses.trim();
    if (maxUses && (!INT_PATTERN.test(maxUses) || Number(maxUses) < 1)) {
      fail('maxUses', 'adminPromotions.validation.maxUsesInvalid');
    }
  });
}

const DURATION_BY_OPTION: Record<PromotionFormValues['duration'], PromotionDuration> = {
  first: PromotionDuration.FirstMonth,
  months: PromotionDuration.Months,
  forever: PromotionDuration.Forever,
};

/** Builds the create body: percent as typed, a fixed amount in minor units (crowns x 100), code only in code mode. */
export function formValuesToCreateRequest(values: PromotionFormValues): CreatePromotionRequest {
  const percent = values.kind === 'percent';
  const claimFrom = parseDateInput(values.claimFrom, false) ?? new Date();
  const claimUntil = values.claimUntil ? parseDateInput(values.claimUntil, true) : null;
  const maxUses = values.maxUses.trim();
  return {
    name: values.name.trim(),
    kind: percent ? PromotionKind.Percent : PromotionKind.FixedAmount,
    value: percent ? Number(values.value.trim()) : inputToMinor(values.value),
    currency: percent ? undefined : FIXED_AMOUNT_CURRENCY,
    mode: values.mode === 'code' ? PromotionMode.Code : PromotionMode.Automatic,
    code: values.mode === 'code' ? values.code.trim() : undefined,
    duration: DURATION_BY_OPTION[values.duration],
    durationMonths: values.duration === 'months' ? Number(values.durationMonths.trim()) : undefined,
    claimFrom: claimFrom.toISOString(),
    claimUntil: claimUntil ? claimUntil.toISOString() : undefined,
    planCodes: PROMOTION_PLAN_CODES.filter((code) => values.planCodes.includes(code)),
    maxUses: maxUses ? Number(maxUses) : undefined,
    newCoachesOnly: values.newCoachesOnly,
    oneUsePerCoach: values.oneUsePerCoach,
  };
}

/** The form field a backend promotion error code belongs to; the message is the code's `apiErrors` text. */
export const SERVER_FIELD_BY_CODE: Record<string, keyof PromotionFormValues> = {
  PROMOTION_VALUE_INVALID: 'value',
  PROMOTION_CODE_INVALID: 'code',
  PROMOTION_MODE_INVALID: 'code',
  PROMOTION_CODE_TAKEN: 'code',
  PROMOTION_MONTHS_INVALID: 'durationMonths',
  PROMOTION_WINDOW_INVALID: 'claimUntil',
  PROMOTION_PLANS_INVALID: 'planCodes',
  PROMOTION_MAX_USES_INVALID: 'maxUses',
};
