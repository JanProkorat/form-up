import axios from 'axios';
import { create } from 'zustand';
import { executeRefresh } from '@/lib/refresh';
import { queryClient } from '@/lib/queryClient';
import { useCheckoutActivationStore } from '@/stores/checkoutActivation';

interface User {
  publicId: string;
  email: string;
  firstName: string;
  lastName: string;
  roles: string[];
  emailConfirmed: boolean;
  avatarBlobUrl?: string | null;
}

interface AuthState {
  user: User | null;
  accessToken: string | null;
  refreshToken: string | null;
  isAuthenticated: boolean;
  isInitialized: boolean;
  /**
   * `persist` picks where the refresh token lives: `true` = localStorage
   * (survives a browser restart), `false` = sessionStorage (cleared when the
   * browser closes). Omit it on a token rotation to stay in the store the
   * session already started in.
   */
  setTokens: (accessToken: string, refreshToken: string, persist?: boolean) => void;
  setUser: (user: User) => void;
  login: (user: User, accessToken: string, refreshToken: string, persist?: boolean) => void;
  logout: () => void;
  restoreSession: () => Promise<void>;
}

const REFRESH_TOKEN_KEY = 'refreshToken';

// Guard against concurrent restoreSession calls (React 18 StrictMode runs effects twice)
let restorePromise: Promise<void> | null = null;

/** sessionStorage wins over localStorage: an unticked "keep me signed in" session lives there. */
function readStoredRefreshToken(): string | null {
  return sessionStorage.getItem(REFRESH_TOKEN_KEY) ?? localStorage.getItem(REFRESH_TOKEN_KEY);
}

/** True when the current session's refresh token lives in sessionStorage. */
function isSessionScoped(): boolean {
  return sessionStorage.getItem(REFRESH_TOKEN_KEY) !== null;
}

/**
 * Writes the token to exactly one store and clears the other, so a stale copy
 * can never outlive the choice. `persist` undefined keeps the store the
 * session already started in (token rotation after a refresh).
 */
function writeRefreshToken(refreshToken: string, persist?: boolean): void {
  const usePersistent = persist ?? !isSessionScoped();
  if (usePersistent) {
    sessionStorage.removeItem(REFRESH_TOKEN_KEY);
    localStorage.setItem(REFRESH_TOKEN_KEY, refreshToken);
  } else {
    localStorage.removeItem(REFRESH_TOKEN_KEY);
    sessionStorage.setItem(REFRESH_TOKEN_KEY, refreshToken);
  }
}

function clearStoredRefreshToken(): void {
  sessionStorage.removeItem(REFRESH_TOKEN_KEY);
  localStorage.removeItem(REFRESH_TOKEN_KEY);
}

export const useAuthStore = create<AuthState>((set, get) => ({
  user: null,
  accessToken: null,
  refreshToken: readStoredRefreshToken(),
  isAuthenticated: false,
  isInitialized: false,

  setTokens: (accessToken, refreshToken, persist) => {
    writeRefreshToken(refreshToken, persist);
    set({ accessToken, refreshToken });
  },

  setUser: (user) => set({ user, isAuthenticated: true }),

  login: (user, accessToken, refreshToken, persist) => {
    // Wipe any query cache left over from a previous session in this tab
    // before the new session populates it. Login is a client-side navigation
    // (no page reload), so without this a prior coach's cached data (client
    // list, dashboards, messages) would be served to the coach logging in now.
    // See issue #769.
    queryClient.clear();
    useCheckoutActivationStore.getState().stop();
    writeRefreshToken(refreshToken, persist);
    set({ user, accessToken, refreshToken, isAuthenticated: true });
  },

  logout: () => {
    // Drop all cached queries so the next user in this tab never sees the
    // previous session's data. See issue #769.
    queryClient.clear();
    useCheckoutActivationStore.getState().stop();
    clearStoredRefreshToken();
    set({
      user: null,
      accessToken: null,
      refreshToken: null,
      isAuthenticated: false,
    });
  },

  restoreSession: () => {
    if (restorePromise) return restorePromise;

    restorePromise = (async () => {
      const { refreshToken } = get();
      if (!refreshToken) {
        set({ isInitialized: true });
        return;
      }

      try {
        // Use the shared single-flight helper so an app-start refresh and a
        // concurrent interceptor refresh cannot both fire with the same token.
        const newAccessToken = await executeRefresh();

        const { data: profile } = await axios.get('/users/me', {
          headers: { Authorization: `Bearer ${newAccessToken}` },
        });

        set({
          user: {
            publicId: profile.userId,
            email: profile.email,
            firstName: profile.firstName,
            lastName: profile.lastName,
            roles: profile.roles ?? [],
            emailConfirmed: profile.emailConfirmed ?? true,
            avatarBlobUrl: profile.avatarBlobUrl ?? null,
          },
          isAuthenticated: true,
          isInitialized: true,
        });
      } catch {
        clearStoredRefreshToken();
        set({
          user: null,
          accessToken: null,
          refreshToken: null,
          isAuthenticated: false,
          isInitialized: true,
        });
      }
    })().finally(() => {
      restorePromise = null;
    });

    return restorePromise;
  },
}));
