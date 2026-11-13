/** Formats an amount in minor units (e.g. haléře, cents) for the given currency and UI language. */
export function formatMoney(amountMinorUnits: number, currency: string, language: string): string {
  const probe = new Intl.NumberFormat(language, { style: 'currency', currency });
  const digits = probe.resolvedOptions().maximumFractionDigits ?? 2;
  const major = amountMinorUnits / 10 ** digits;
  return new Intl.NumberFormat(language, {
    style: 'currency',
    currency,
    minimumFractionDigits: Number.isInteger(major) ? 0 : digits,
    maximumFractionDigits: digits,
  }).format(major);
}
