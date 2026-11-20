import { StrictMode } from 'react';
import type { ReactElement } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { RouterProvider, createMemoryRouter } from 'react-router-dom';
import type { RouteObject } from 'react-router-dom';
import i18n from '@/i18n';
import { useAuthStore } from '@/stores/auth';

interface RenderOptions {
  /** Memory-router history; the last entry is the current location. */
  initialEntries?: string[];
  /** Language for t(); the portal's E2E specs assert on English copy. */
  language?: 'cs' | 'en' | 'de';
  /** Signs a user in with these roles before the first render. */
  auth?: { roles: string[] };
}

/**
 * Renders a page (or a route table) with a fresh QueryClient, a data router (so `useBlocker` works) and i18n.
 * A bare element is mounted on `*`; pass `routes` to test navigation between pages.
 */
export function renderWithProviders(
  ui: ReactElement | RouteObject[],
  { initialEntries = ['/'], language = 'en', auth }: RenderOptions = {},
) {
  // Not the app singleton in src/lib/queryClient.ts: it retries and is shared across tests.
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  void i18n.changeLanguage(language);

  useAuthStore.setState(
    auth
      ? {
          isAuthenticated: true,
          isInitialized: true,
          accessToken: 'test-access-token',
          user: {
            publicId: '00000000-0000-0000-0000-000000000001',
            email: 'user@example.com',
            firstName: 'Test',
            lastName: 'User',
            roles: auth.roles,
            emailConfirmed: true,
          },
        }
      : { isAuthenticated: false, isInitialized: true, accessToken: null, user: null },
  );

  const routes = Array.isArray(ui) ? ui : [{ path: '*', element: ui }];
  const router = createMemoryRouter(routes, { initialEntries });
  const user = userEvent.setup();

  // StrictMode like src/main.tsx: effects run twice, so "sent once" assertions catch double requests.
  const result = render(
    <StrictMode>
      <QueryClientProvider client={queryClient}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </StrictMode>,
  );

  return { ...result, user, router, queryClient };
}
