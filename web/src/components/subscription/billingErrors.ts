import i18n from '@/i18n';
import { getErrorCode, getErrorStatus, getProblemBody, getRfc7807ErrorCode } from '@/lib/api-errors';
import { useToastStore } from '@/stores/toast';

/** Code the billing routes answer with a bare 429 (rate limit) that carries no body code. */
export const BILLING_RATE_LIMITED = 'BILLING_RATE_LIMITED';

/**
 * Every code a billing error can carry, in lookup order. The billing routes put a code in the
 * top-level `errorCode` (SendProblemAsync) or, for the promotion-code and plan 400s, in `errors[].reason`;
 * a bare 429 has none and maps to {@link BILLING_RATE_LIMITED}.
 */
export function billingErrorCodes(error: unknown): string[] {
  const codes: string[] = [];
  const add = (value: unknown) => {
    if (typeof value === 'string' && value && !codes.includes(value)) codes.push(value);
  };

  const body = getProblemBody(error);
  const errors = body?.errors;
  if (Array.isArray(errors)) {
    for (const entry of errors) {
      if (typeof entry === 'object' && entry !== null) {
        add((entry as { code?: unknown }).code);
        add((entry as { reason?: unknown }).reason);
      }
    }
  }
  add(body?.errorCode);
  add(getErrorCode(error));
  add(getRfc7807ErrorCode(error));
  if (codes.length === 0 && getErrorStatus(error) === 429) add(BILLING_RATE_LIMITED);
  return codes;
}

/** First code that has an `apiErrors.<code>` translation, or the first code at all. */
export function billingErrorCode(error: unknown): string | null {
  const codes = billingErrorCodes(error);
  return codes.find((code) => i18n.exists(`apiErrors.${code}`)) ?? codes[0] ?? null;
}

/** Translated message for a billing error, or the fallback key's text when the code is unknown. */
export function billingErrorMessage(error: unknown, fallbackKey: string): string {
  const code = billingErrorCodes(error).find((candidate) => i18n.exists(`apiErrors.${candidate}`));
  return code ? i18n.t(`apiErrors.${code}`) : i18n.t(fallbackKey);
}

/** Shows a billing error as a toast. */
export function showBillingError(error: unknown, fallbackKey: string): void {
  useToastStore.getState().addToast(billingErrorMessage(error, fallbackKey), 'error');
}
