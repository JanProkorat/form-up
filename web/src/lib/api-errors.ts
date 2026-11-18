import { AxiosError } from 'axios';
import i18n from '@/i18n';
import { useToastStore } from '@/stores/toast';

interface ProblemDetailsError {
  name: string;
  reason: string;
  code?: string;
}

interface ProblemDetails {
  errors?: ProblemDetailsError[];
  /** RFC 7807 Extensions field used by non-FastEndpoints error paths. */
  errorCode?: string;
}

/**
 * Extracts the first error code from a ProblemDetails API response.
 *
 * FastEndpoints returns errors as { name, reason, code } where:
 *   - `reason` contains the human-readable message (FastEndpoints
 *     serializes `ValidationFailure.ErrorMessage` here)
 *   - `code`   contains the machine-readable error code (FastEndpoints
 *     serializes `ValidationFailure.ErrorCode` here — see
 *     backend `Domain/Extensions/EndpointErrorExtensions.cs`, which
 *     constructs `new ValidationFailure("", message) { ErrorCode = errorCode }`)
 *
 * This mapping was previously documented backwards here (reason/code
 * swapped) — that bug meant every lookup fed the human message into the
 * `apiErrors.<code>` translation table, always missed, and silently fell
 * back to the caller's generic message. Read `code` first; `reason` is
 * kept only as a fallback for a shape this function hasn't been verified
 * against everywhere in the app. Do not swap this back — see #1055.
 *
 * NOTE: Non-FastEndpoints RFC 7807 errors (e.g. 409 session_locked) put the
 * code in `response.data.errorCode` (camelCase), not in `errors[0].code`.
 * Use `getRfc7807ErrorCode()` to read those.
 *
 * SECOND PATH — the generated NSwag client's error shape. Every call made
 * through `apiClient.*` (`@/api/client.ts`) that hits a non-2xx status
 * calls `throwException(...)` in `generated.ts`, which does
 * `if (result !== null && result !== undefined) throw result;` — it throws
 * the *parsed response body itself*, not an `AxiosError`. That body is
 * still ProblemDetails-shaped (`{ errors: [...] }` for FastEndpoints
 * validation, or `{ errorCode: ... }` for `SendProblemAsync`), so the
 * lookup below is additive: check the `AxiosError` shape first (unchanged
 * from above), then fall back to reading the thrown body directly via
 * `asThrownProblemBody`. Without this, every `apiClient.*` caller (9 modules
 * as of #1115: client-tags, conversations, diary-requests, broadcast,
 * foods, clients, client-photos, photos, auth) always got `null` here and
 * silently fell back to its generic fallback message.
 */
export function getErrorCode(error: unknown): string | null {
  const axiosError = error as AxiosError<ProblemDetails>;
  const errors = axiosError?.response?.data?.errors;
  if (errors?.length) {
    return errors[0].code ?? errors[0].reason ?? null;
  }

  const thrownBody = asThrownProblemBody(error);
  if (thrownBody?.errors?.length) {
    return thrownBody.errors[0].code ?? thrownBody.errors[0].reason ?? null;
  }

  return null;
}

/**
 * Extracts the `errorCode` from an RFC 7807 ProblemDetails Extensions field.
 *
 * Used for endpoints that set `errorCode` at the top level of the problem JSON
 * (e.g. 409 session_locked from UpdateTrainingPlan, UnlockTrainingSession).
 * FastEndpoints validation errors use `errors[0].code` instead — use
 * `getErrorCode()` for those.
 *
 * Same additive NSwag-thrown-body fallback as `getErrorCode()` above — see
 * its doc comment for why `error` can be a raw thrown body instead of an
 * `AxiosError`.
 */
export function getRfc7807ErrorCode(error: unknown): string | null {
  const axiosError = error instanceof AxiosError ? error : null;
  const axiosCode = (axiosError?.response?.data as ProblemDetails | undefined)?.errorCode;
  if (axiosCode) {
    return axiosCode;
  }

  return asThrownProblemBody(error)?.errorCode ?? null;
}

/**
 * The raw ProblemDetails body of a failed request as an untyped object, for reading extension members that
 * the generated client does not declare (the caller narrows each member). Covers the `AxiosError` shape,
 * a thrown body object and a bare `ApiException` whose `.response` is a JSON string.
 */
export function getProblemBody(error: unknown): Record<string, unknown> | null {
  if (error instanceof AxiosError) {
    const data: unknown = error.response?.data;
    if (typeof data === 'string') {
      return parseJsonObject(data) as Record<string, unknown> | null;
    }
    return typeof data === 'object' && data !== null && !Array.isArray(data) ? (data as Record<string, unknown>) : null;
  }
  if (typeof error !== 'object' || error === null) {
    return null;
  }
  const response = (error as { response?: unknown }).response;
  if (typeof response === 'string') {
    return parseJsonObject(response) as Record<string, unknown> | null;
  }
  return error as Record<string, unknown>;
}

/**
 * HTTP status of a failed request, for both error shapes: the NSwag
 * `ApiException` (`.status`) and an `AxiosError` (`.response.status`).
 * `null` when the value carries no status (e.g. a network failure, or a
 * ProblemDetails body that NSwag threw directly without its status).
 */
export function getErrorStatus(error: unknown): number | null {
  if (typeof error !== 'object' || error === null) {
    return null;
  }
  const candidate = error as { status?: unknown; response?: { status?: unknown } };
  if (typeof candidate.status === 'number') {
    return candidate.status;
  }
  return typeof candidate.response?.status === 'number' ? candidate.response.status : null;
}

/**
 * Narrows an unknown thrown value to a ProblemDetails shape — the NSwag
 * `throwException(...)` path described in `getErrorCode`'s doc comment.
 *
 * THIRD PATH — statuses NSwag didn't special-case (e.g. 409
 * CLIENT_TAG_NAME_ALREADY_EXISTS) throw a bare `ApiException` whose
 * `.response` holds the body as an unparsed JSON string; parse it here too.
 */
function asThrownProblemBody(error: unknown): ProblemDetails | null {
  if (typeof error !== 'object' || error === null) {
    return null;
  }
  const candidate = error as { errors?: unknown; errorCode?: unknown; response?: unknown };
  const nested = typeof candidate.response === 'string' ? parseJsonObject(candidate.response) : null;

  const errors = Array.isArray(candidate.errors)
    ? (candidate.errors as ProblemDetailsError[])
    : Array.isArray(nested?.errors)
      ? (nested.errors as ProblemDetailsError[])
      : undefined;
  const errorCode =
    typeof candidate.errorCode === 'string'
      ? candidate.errorCode
      : typeof nested?.errorCode === 'string'
        ? nested.errorCode
        : undefined;

  if (errors === undefined && errorCode === undefined) {
    return null;
  }
  return { errors, errorCode };
}

/**
 * Parses a string as JSON, returning it only if it resolves to a non-null
 * object — never throws, and never returns an array/primitive that would
 * make the `nested.errors`/`nested.errorCode` reads above unsafe.
 */
function parseJsonObject(value: string): { errors?: unknown; errorCode?: unknown } | null {
  try {
    const parsed: unknown = JSON.parse(value);
    return typeof parsed === 'object' && parsed !== null && !Array.isArray(parsed)
      ? (parsed as { errors?: unknown; errorCode?: unknown })
      : null;
  } catch {
    return null;
  }
}

/**
 * Returns a translated error message for an API error.
 *
 * Checks error codes in order:
 *   1. FastEndpoints `errors[0].code` (e.g. validation errors)
 *   2. RFC 7807 top-level `errorCode` (e.g. 409 SESSION_ALREADY_COMPLETED
 *      from UnlockTrainingSession, UpdateTrainingPlan)
 *
 * Falls back to the provided fallback key when no code is present or the
 * code has no translation entry.
 */
export function getApiErrorMessage(error: unknown, fallbackKey: string): string {
  const code = getErrorCode(error) ?? getRfc7807ErrorCode(error);
  if (code) {
    const translated = i18n.t(`apiErrors.${code}`, { defaultValue: '' });
    if (translated) return translated;
  }
  return i18n.t(fallbackKey);
}

/**
 * Shows a toast with a translated API error message.
 */
export function showApiError(error: unknown, fallbackKey: string) {
  const message = getApiErrorMessage(error, fallbackKey);
  useToastStore.getState().addToast(message, 'error');
}

/**
 * Shows an error toast with a translated message.
 */
export function showError(messageKey: string) {
  useToastStore.getState().addToast(i18n.t(messageKey), 'error');
}

/**
 * Shows a success toast.
 */
export function showSuccess(messageKey: string, params?: Record<string, string | number>) {
  useToastStore.getState().addToast(i18n.t(messageKey, params), 'success');
}
