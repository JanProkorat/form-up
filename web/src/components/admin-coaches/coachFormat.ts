import { AdminCoachStatus, type AdminCoachPlanDto } from '@/api/admin-coaches';

/** Plan name in the UI language, falling back to English and then the plan code. */
export function coachPlanName(plan: AdminCoachPlanDto | undefined, language: string): string {
  if (!plan) {
    return '';
  }
  const byLanguage: Record<string, string | undefined> = { cs: plan.nameCs, de: plan.nameDe, en: plan.nameEn };
  return byLanguage[language.slice(0, 2)] || plan.nameEn || plan.code || '';
}

/** Formats an amount in minor units in any ISO 4217 currency; an unknown code falls back to "<amount> <code>". */
export function formatCoachMoney(minorUnits: number, currency: string | undefined, language: string): string {
  if (!currency) {
    return String(minorUnits);
  }
  try {
    const formatter = new Intl.NumberFormat(language, { style: 'currency', currency });
    const digits = formatter.resolvedOptions().maximumFractionDigits ?? 2;
    return formatter.format(minorUnits / 10 ** digits);
  } catch {
    return `${minorUnits / 100} ${currency}`;
  }
}

/** Short date such as "17 Oct 2026"; empty for a missing or unreadable value. */
export function formatCoachDate(iso: string | undefined, language: string): string {
  if (!iso) {
    return '';
  }
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) {
    return '';
  }
  return new Intl.DateTimeFormat(language, { day: 'numeric', month: 'short', year: 'numeric' }).format(date);
}

/** Badge colours per status; every pair is a design token. */
export const STATUS_BADGE_CLASS: Record<AdminCoachStatus, string> = {
  [AdminCoachStatus.Paying]: 'bg-success-soft text-success-ink',
  [AdminCoachStatus.PaymentFailed]: 'bg-error-soft text-error',
  [AdminCoachStatus.Cancelling]: 'bg-caution-soft text-caution-ink',
  [AdminCoachStatus.Free]: 'bg-sunken text-ink-2',
  [AdminCoachStatus.GivenFree]: 'bg-accent text-accent-foreground',
};

/** Whether the role string is one the portal has a label for. */
export const KNOWN_ROLES = ['Trainer', 'Nutritionist'] as const;

/** First letters of the first and last word of a name, for an avatar disc. */
export function coachInitials(name: string | undefined): string {
  const words = (name ?? '').trim().split(/\s+/).filter(Boolean);
  if (words.length === 0) {
    return '?';
  }
  const first = words[0][0] ?? '';
  const last = words.length > 1 ? (words[words.length - 1][0] ?? '') : '';
  return (first + last).toUpperCase();
}
