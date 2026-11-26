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
 * Machine-readable code of a failed request, for every error shape the app sees: an `AxiosError` (object or raw
 * JSON-string body), a thrown ProblemDetails body and an NSwag `ApiException` with a string `.response`.
 * Reads the top-level `errorCode` first, then FastEndpoints' `errors[0].code` (`reason` as a last resort).
 */
export function getErrorCode(error: unknown): string | null {
  const body = getProblemBody(error);
  if (typeof body?.errorCode === 'string' && body.errorCode) {
    return body.errorCode;
  }
  const first: unknown = Array.isArray(body?.errors) ? body.errors[0] : undefined;
  if (typeof first === 'object' && first !== null) {
    const { code, reason } = first as { code?: unknown; reason?: unknown };
    if (typeof code === 'string') return code;
    if (typeof reason === 'string') return reason;
  }
  return null;
}

/** Top-level `errorCode` only (RFC 7807 extension); `getErrorCode()` also reads `errors[0].code`. */
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
 * Narrows a thrown value to a ProblemDetails shape: a thrown body object (NSwag `throwException`) or a bare
 * `ApiException` whose `.response` is an unparsed JSON string.
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

/** Translated `apiErrors.<code>` message for an API error, or the fallback key's text when there is none. */
export function getApiErrorMessage(error: unknown, fallbackKey: string): string {
  const code = getErrorCode(error);
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
