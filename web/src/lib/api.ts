import axios from 'axios';
import i18n from '@/i18n';
import { useAuthStore } from '@/stores/auth';
import { executeRefresh } from '@/lib/refresh';
import { PLAN_CHOICE_REQUIRED_EVENT, isPlanChoiceRequired } from '@/lib/subscriptionErrors';

const api = axios.create({
  baseURL: '/',
});

// Axios instance for NSwag-generated client — returns raw text so NSwag's JSON.parse() works
export const rawApi = axios.create({
  baseURL: '/',
  headers: { 'Content-Type': 'application/json' },
  transformResponse: (data) => data,
});

// Attach access token and language to every request
function attachToken(config: import('axios').InternalAxiosRequestConfig) {
  const token = useAuthStore.getState().accessToken;
  if (token) {
    config.headers.Authorization = `Bearer ${token}`;
  }
  config.headers['Accept-Language'] = i18n.language;

  // FastEndpoints requires a JSON body for request binding on POST/PUT/PATCH.
  // When no body is provided, send empty object so route params still bind.
  const method = config.method?.toLowerCase();
  if ((method === 'post' || method === 'put' || method === 'patch') && config.data == null) {
    config.data = {};
  }

  return config;
}

// On 401: use the shared single-flight refresh, then retry original request once.
// On 429: reject — do NOT logout. (A rate-limit toast used to fire here; the
// toast store was removed with the UI strip — see rejectWithRateLimit below.)

/**
 * Returns true if the error is an Axios 429 response.
 * Extracted so both the top-level 429 guard and the refresh catch share the
 * same logic — the refresh call itself can also be rate-limited.
 */
function isRateLimited(error: unknown): boolean {
  const err = error as import('axios').AxiosError;
  return err?.response?.status === 429;
}

/**
 * Rejects the request without logging the user out — the session remains
 * valid on a 429. NOTE: this used to also surface `errors.rateLimitRefresh`
 * via the toast store; `stores/toast.ts` was removed with the UI strip
 * (feature/ui-redesign). Re-wire a rate-limit notification here once the new
 * design system's notification surface lands.
 */
function rejectWithRateLimit(error: unknown): Promise<never> {
  console.error(i18n.t('errors.rateLimitRefresh'));
  return Promise.reject(error);
}

function handleRefresh(instance: import('axios').AxiosInstance) {
  return async (error: unknown) => {
    const err = error as import('axios').AxiosError & { config: { _retry?: boolean } };
    const original = err.config;

    // 429 on the original request — surface toast, keep the user logged in.
    if (isRateLimited(error)) {
      return rejectWithRateLimit(error);
    }

    // A coach whose given plan ended is blocked from writes until they choose a plan. Whatever request hit the
    // wall, tell the app shell once: it refetches the subscription and sends the coach to the plan picker.
    if (err.response?.status === 403 && isPlanChoiceRequired(error)) {
      window.dispatchEvent(new Event(PLAN_CHOICE_REQUIRED_EVENT));
      return Promise.reject(error);
    }

    if (err.response?.status === 401 && !original._retry) {
      original._retry = true;

      const { refreshToken, logout } = useAuthStore.getState();
      if (!refreshToken) {
        logout();
        return Promise.reject(error);
      }

      try {
        // executeRefresh() is single-flight: concurrent 401s await the same promise.
        const newAccessToken = await executeRefresh();
        original.headers.Authorization = `Bearer ${newAccessToken}`;
        return instance(original);
      } catch (refreshError) {
        // If the /auth/refresh call itself was rate-limited, show the toast
        // and keep the user logged in — same as a top-level 429.
        if (isRateLimited(refreshError)) {
          return rejectWithRateLimit(refreshError);
        }
        // Any other refresh failure (expired/invalid token, network error) →
        // the session cannot be recovered; logout.
        logout();
        return Promise.reject(error);
      }
    }
    return Promise.reject(error);
  };
}

api.interceptors.request.use(attachToken);
api.interceptors.response.use((r) => r, handleRefresh(api));

rawApi.interceptors.request.use(attachToken);
rawApi.interceptors.response.use((r) => r, handleRefresh(rawApi));

export default api;
