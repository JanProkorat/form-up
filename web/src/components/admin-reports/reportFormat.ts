import { PaymentCurrency } from '@/api/generated';
import { formatCoachMoney } from '@/components/admin-coaches/coachFormat';
import { formatMoney } from '@/lib/money';

const DASH = '—';
const MINUS = '−';

/** Formats an amount in minor units. CZK and EUR use the portal money format; other codes fall back to Intl. */
export function reportMoney(minorUnits: number, currency: string | undefined, language: string): string {
  if (currency === PaymentCurrency.CZK || currency === PaymentCurrency.EUR) {
    return formatMoney(minorUnits / 100, currency, language);
  }
  return formatCoachMoney(minorUnits, currency, language);
}

/** A signed amount: "+1 196 Kč", "−300 Kč", or the plain amount when it is zero. */
export function signedReportMoney(minorUnits: number, currency: string | undefined, language: string): string {
  const text = reportMoney(Math.abs(minorUnits), currency, language);
  if (minorUnits > 0) {
    return `+${text}`;
  }
  return minorUnits < 0 ? `${MINUS}${text}` : text;
}

/** Change against a baseline in percent; null when there is no usable baseline (missing or 0). */
export function deltaPercent(value: number | undefined, previous: number | undefined): number | null {
  // The API sends JSON null for a missing baseline even though the generated type says undefined.
  if (value === undefined || previous === undefined || previous === null || previous === 0) {
    return null;
  }
  return ((value - previous) / previous) * 100;
}

/** "+8.2 %" / "−1.4 %" with the UI language's decimal mark; "0 %" when unchanged. */
export function formatPercentDelta(percent: number, language: string): string {
  const text = new Intl.NumberFormat(language, { maximumFractionDigits: 1, signDisplay: 'exceptZero' }).format(percent);
  return `${text.replace('-', MINUS)} %`;
}

/** "+1" / "−2" / "0" for a count change. */
export function formatCountDelta(delta: number): string {
  if (delta > 0) {
    return `+${delta}`;
  }
  return delta < 0 ? `${MINUS}${Math.abs(delta)}` : '0';
}

/** A whole-number percentage such as "91 %" with no sign. */
export function formatPercent(percent: number, language: string): string {
  return `${new Intl.NumberFormat(language, { maximumFractionDigits: 0 }).format(percent)} %`;
}

/** Step share compared with the step before; a dash when the step before is 0 or missing. */
export function stepShare(step: number | undefined, previousStep: number | undefined, language: string): string {
  if (step === undefined || !previousStep) {
    return DASH;
  }
  return formatPercent((step / previousStep) * 100, language);
}

export function formatNumber(value: number, language: string): string {
  return new Intl.NumberFormat(language, { maximumFractionDigits: 1 }).format(value);
}

/** A UTC day such as "15 Nov 2026"; empty for a missing or unreadable value. */
export function formatReportDay(iso: string | undefined, language: string): string {
  if (!iso) {
    return '';
  }
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) {
    return '';
  }
  return new Intl.DateTimeFormat(language, { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' }).format(date);
}

/** Short axis label for a UTC day: month only over a year, day and month otherwise. */
export function formatAxisDay(iso: string, monthOnly: boolean, language: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) {
    return '';
  }
  const options: Intl.DateTimeFormatOptions = monthOnly
    ? { month: 'short', timeZone: 'UTC' }
    : { day: 'numeric', month: 'short', timeZone: 'UTC' };
  return new Intl.DateTimeFormat(language, options).format(date);
}

/** Plan name in the UI language, falling back to English and then the plan code. */
export function reportPlanName(
  plan: { nameCs?: string; nameEn?: string; nameDe?: string; planCode?: string },
  language: string,
): string {
  const byLanguage: Record<string, string | undefined> = { cs: plan.nameCs, de: plan.nameDe, en: plan.nameEn };
  return byLanguage[language.slice(0, 2)] || plan.nameEn || plan.planCode || '';
}

/** Largest "round" axis maximum (1, 2, 5 times a power of ten) that fits `max` in three gridline steps. */
export function niceAxisMax(max: number): { step: number; top: number } {
  if (max <= 0) {
    return { step: 1, top: 3 };
  }
  const raw = max / 3;
  const magnitude = 10 ** Math.floor(Math.log10(raw));
  const normalised = raw / magnitude;
  const factor = normalised <= 1 ? 1 : normalised <= 2 ? 2 : normalised <= 5 ? 5 : 10;
  const step = factor * magnitude;
  return { step, top: step * 3 };
}

/** Axis tick text: "0", "500", "10 k". */
export function formatAxisValue(value: number, language: string): string {
  if (value >= 1000) {
    return `${new Intl.NumberFormat(language, { maximumFractionDigits: 1 }).format(value / 1000)} k`;
  }
  return new Intl.NumberFormat(language, { maximumFractionDigits: 0 }).format(value);
}

/** The https URL of the Stripe dashboard, or null when the value is unset or not https. */
export function stripeDashboardUrl(value: string | undefined): string | null {
  const trimmed = value?.trim();
  return trimmed && /^https:\/\//i.test(trimmed) ? trimmed : null;
}
