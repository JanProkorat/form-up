import { z } from 'zod';
import {
  BillingInterval,
  type CreateSubscriptionPlanRequest,
  type SubscriptionPlanDto,
  type UpdateSubscriptionPlanBody,
} from '@/api/admin-subscription-plans';
import { PaymentCurrency } from '@/api/generated';
import { formatMoney, parseAmount } from '@/lib/money';

/** The plan every coach starts on: price fixed at 0, always offered. */
export const FREE_PLAN_CODE = 'free';
export const DEFAULT_CURRENCY = 'CZK';
export const PLAN_NAME_MAX = 200;
/** Highest price or add-on, in major currency units. */
export const PLAN_PRICE_MAX = 1_000_000;

const CODE_PATTERN = /^[a-z0-9-]{1,50}$/;
const INT_PATTERN = /^\d+$/;

export type SecondRoleOption = 'addon' | 'included';

export const isFreePlan = (plan?: Pick<SubscriptionPlanDto, 'code'>): boolean => plan?.code === FREE_PLAN_CODE;

const planShape = z.object({
  nameCs: z.string(),
  nameEn: z.string(),
  nameDe: z.string(),
  code: z.string(),
  price: z.string(),
  secondRole: z.enum(['addon', 'included']),
  addOnPrice: z.string(),
  unlimited: z.boolean(),
  maxActiveClients: z.string(),
  canCreatePlans: z.boolean(),
  canMessage: z.boolean(),
  canSendQuestionnaires: z.boolean(),
  canUseWeeklyCheckIns: z.boolean(),
  canUsePerClientCheckInConfig: z.boolean(),
  canUsePayments: z.boolean(),
  isActive: z.boolean(),
  moveExistingCoaches: z.boolean(),
});

export type PlanFormValues = z.infer<typeof planShape>;

/** The form's validation; the code is only checked while creating, because it is locked afterwards. */
export function createPlanSchema(isNew: boolean) {
  return planShape.superRefine((values, ctx) => {
    const fail = (path: keyof PlanFormValues, message: string) =>
      ctx.addIssue({ code: 'custom', path: [path], message });

    (['nameCs', 'nameEn', 'nameDe'] as const).forEach((field) => {
      const name = values[field].trim();
      if (!name) {
        fail(field, 'adminPlans.validation.nameRequired');
      } else if (name.length > PLAN_NAME_MAX) {
        fail(field, 'adminPlans.validation.nameTooLong');
      }
    });

    if (isNew && !CODE_PATTERN.test(values.code.trim())) {
      fail('code', 'adminPlans.validation.codeInvalid');
    }

    const price = parseAmount(values.price);
    if (price === null || price > PLAN_PRICE_MAX) {
      fail('price', 'adminPlans.validation.priceInvalid');
    }

    if (values.secondRole === 'addon') {
      const addOn = parseAmount(values.addOnPrice);
      if (addOn === null || addOn <= 0 || addOn > PLAN_PRICE_MAX) {
        fail('addOnPrice', 'adminPlans.validation.addOnInvalid');
      }
    }

    if (!values.unlimited) {
      const cap = values.maxActiveClients.trim();
      if (!INT_PATTERN.test(cap) || Number(cap) < 1 || Number(cap) > 100_000) {
        fail('maxActiveClients', 'adminPlans.validation.clientsInvalid');
      }
    }
  });
}

export function defaultPlanValues(): PlanFormValues {
  return {
    nameCs: '',
    nameEn: '',
    nameDe: '',
    code: '',
    price: '',
    secondRole: 'addon',
    addOnPrice: '',
    unlimited: false,
    maxActiveClients: '',
    canCreatePlans: true,
    canMessage: true,
    canSendQuestionnaires: false,
    canUseWeeklyCheckIns: false,
    canUsePerClientCheckInConfig: false,
    canUsePayments: false,
    isActive: true,
    moveExistingCoaches: false,
  };
}

/** Minor units (cents/haléře) to the text shown in a price box, without a trailing ".00". */
export function minorToInput(minor: number | undefined): string {
  if (minor == null) {
    return '';
  }
  const major = minor / 100;
  return Number.isInteger(major) ? String(major) : major.toFixed(2).replace('.', ',');
}

/** A typed amount to minor units; 0 when it cannot be read (the schema rejects that first). */
export function inputToMinor(raw: string): number {
  const amount = parseAmount(raw);
  return amount === null ? 0 : Math.round(amount * 100);
}

export function planToFormValues(plan: SubscriptionPlanDto): PlanFormValues {
  const unlimited = plan.maxActiveClients == null;
  return {
    nameCs: plan.nameCs ?? '',
    nameEn: plan.nameEn ?? '',
    nameDe: plan.nameDe ?? '',
    code: plan.code ?? '',
    price: minorToInput(plan.priceMinorUnits ?? 0),
    secondRole: plan.bothRolesIncluded ? 'included' : 'addon',
    addOnPrice: minorToInput(plan.secondRoleAddOnPriceMinorUnits),
    unlimited,
    maxActiveClients: unlimited ? '' : String(plan.maxActiveClients),
    canCreatePlans: plan.canCreatePlans ?? false,
    canMessage: plan.canMessage ?? false,
    canSendQuestionnaires: plan.canSendQuestionnaires ?? false,
    canUseWeeklyCheckIns: plan.canUseWeeklyCheckIns ?? false,
    canUsePerClientCheckInConfig: plan.canUsePerClientCheckInConfig ?? false,
    canUsePayments: plan.canUsePayments ?? false,
    isActive: plan.isActive ?? true,
    moveExistingCoaches: false,
  };
}

/** Per-client check-in settings only make sense with weekly check-ins; the server does not enforce it. */
function featureFlags(values: PlanFormValues) {
  return {
    canCreatePlans: values.canCreatePlans,
    canMessage: values.canMessage,
    canSendQuestionnaires: values.canSendQuestionnaires,
    canUseWeeklyCheckIns: values.canUseWeeklyCheckIns,
    canUsePerClientCheckInConfig: values.canUseWeeklyCheckIns && values.canUsePerClientCheckInConfig,
    canUsePayments: values.canUsePayments,
  };
}

const secondRoleFields = (values: PlanFormValues) =>
  values.secondRole === 'included'
    ? { bothRolesIncluded: true, secondRoleAddOnPriceMinorUnits: undefined }
    : { bothRolesIncluded: false, secondRoleAddOnPriceMinorUnits: inputToMinor(values.addOnPrice) };

export function formValuesToCreateRequest(values: PlanFormValues): CreateSubscriptionPlanRequest {
  return {
    code: values.code.trim(),
    nameCs: values.nameCs.trim(),
    nameEn: values.nameEn.trim(),
    nameDe: values.nameDe.trim(),
    ...secondRoleFields(values),
    ...featureFlags(values),
    maxActiveClients: values.unlimited ? undefined : Number(values.maxActiveClients),
    priceMinorUnits: inputToMinor(values.price),
    currency: DEFAULT_CURRENCY,
    billingInterval: BillingInterval.Monthly,
    isActive: values.isActive,
  };
}

/**
 * Builds the update body. Fields the drawer does not edit (currency, billing, external price id)
 * keep the saved plan's value, and the cap key is always present.
 */
export function formValuesToUpdateRequest(
  values: PlanFormValues,
  plan: SubscriptionPlanDto,
): UpdateSubscriptionPlanBody {
  const priceMinorUnits = isFreePlan(plan) ? 0 : inputToMinor(values.price);
  const priceChanged = priceMinorUnits !== (plan.priceMinorUnits ?? 0);
  return {
    nameCs: values.nameCs.trim(),
    nameEn: values.nameEn.trim(),
    nameDe: values.nameDe.trim(),
    ...secondRoleFields(values),
    ...featureFlags(values),
    moveExistingCoachesAtNextRenewal: priceChanged && values.moveExistingCoaches,
    maxActiveClients: values.unlimited ? null : Number(values.maxActiveClients),
    priceMinorUnits,
    currency: plan.currency ?? DEFAULT_CURRENCY,
    billingInterval: plan.billingInterval ?? BillingInterval.Monthly,
    externalPriceId: plan.externalPriceId,
    isActive: isFreePlan(plan) ? true : values.isActive,
  };
}

/** Plan name in the UI language, falling back to English. */
export function planName(plan: SubscriptionPlanDto, language: string): string {
  const byLanguage: Record<string, string | undefined> = { cs: plan.nameCs, de: plan.nameDe, en: plan.nameEn };
  return byLanguage[language.slice(0, 2)] || plan.nameEn || plan.code || '';
}

/** Formats minor units in the plan's currency; only CZK and EUR are supported by the server. */
export function formatPlanMoney(minor: number, currency: string | undefined, language: string): string {
  const code = currency === PaymentCurrency.EUR ? PaymentCurrency.EUR : PaymentCurrency.CZK;
  return formatMoney(minor / 100, code, language);
}
