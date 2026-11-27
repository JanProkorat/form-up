import type { TFunction } from 'i18next';
import { BillingInterval, PromotionBillingPeriod, PromotionDuration, PromotionKind } from '@/api/generated';
import { formatMoney } from '@/components/subscription/money';

/** What a promotion takes off: a percentage, or a fixed amount in minor units. */
export interface PromotionValueFields {
  kind?: PromotionKind;
  value?: number;
  currency?: string;
}

/** How long a promotion lasts for a coach. */
export interface PromotionLastsFields {
  duration?: PromotionDuration;
  durationMonths?: number;
}

const DEFAULT_CURRENCY = 'CZK';

/** The value as a short label, "−30 %" or "−200 Kč". Formatting only; the server computes every price. */
export function promotionValueText(t: TFunction, promotion: PromotionValueFields, language: string): string {
  const value = promotion.value ?? 0;
  if (promotion.kind === PromotionKind.FixedAmount) {
    return t('subscription.promotion.amountOff', {
      amount: formatMoney(value, promotion.currency ?? DEFAULT_CURRENCY, language),
    });
  }
  return t('subscription.promotion.percentOff', { value });
}

/** The value with its length, "−30 % for 3 months". */
export function promotionShortText(
  t: TFunction,
  promotion: PromotionValueFields & PromotionLastsFields,
  language: string,
): string {
  const value = promotionValueText(t, promotion, language);
  switch (promotion.duration) {
    case PromotionDuration.FirstMonth:
      return t('subscription.promotion.short.firstMonth', { value });
    case PromotionDuration.Months:
      return t('subscription.promotion.short.months', { value, count: promotion.durationMonths ?? 0 });
    default:
      return t('subscription.promotion.short.forever', { value });
  }
}

/** True when a promotion with this billing period discounts `interval`; a missing period means both. */
export function promotionCoversInterval(period: PromotionBillingPeriod | undefined, interval: BillingInterval): boolean {
  if (period === undefined || period === PromotionBillingPeriod.Both) return true;
  return period === (interval === BillingInterval.Annual ? PromotionBillingPeriod.Annual : PromotionBillingPeriod.Monthly);
}

/**
 * The note under an annual price that carries a promotion, "−30 % on the first year, then 2 990 Kč".
 * A forever promotion keeps its own short text, because it does not end after the first year.
 */
export function promotionAnnualNote(
  t: TFunction,
  promotion: PromotionValueFields & PromotionLastsFields,
  regularPrice: string,
  language: string,
): string {
  if (promotion.duration === PromotionDuration.Forever) return promotionShortText(t, promotion, language);
  return t('subscription.promotion.card.thenAnnual', {
    value: promotionValueText(t, promotion, language),
    price: regularPrice,
  });
}

/** Names joined as a locale list, "Pro and Studio". */
export function formatNameList(names: string[], language: string): string {
  return new Intl.ListFormat(language, { style: 'long', type: 'conjunction' }).format(names);
}

/** Whole months from `now` to `endsAt`, rounded up and never negative. */
export function monthsUntil(endsAt: string, now: number): number {
  const end = new Date(endsAt);
  const start = new Date(now);
  let months = (end.getFullYear() - start.getFullYear()) * 12 + (end.getMonth() - start.getMonth());
  if (end.getDate() > start.getDate()) {
    months += 1;
  }
  return Math.max(months, 0);
}
