import { describe, expect, it, vi } from 'vitest';
import { screen } from '@testing-library/react';
import { useQuery } from '@tanstack/react-query';
import type { RouteObject } from 'react-router-dom';
import api from '@/lib/api';
import VerifyEmailPage from '@/pages/VerifyEmailPage';
import ProtectedRoute from '@/routes/ProtectedRoute';
import { PLAN_CHOICE_REQUIRED_EVENT } from '@/lib/subscriptionErrors';
import { useAuthStore } from '@/stores/auth';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { renderWithProviders } from '@/test/render';

/** A protected page that sends one request through the real axios instance, like any screen's first load. */
function PingPage() {
  const ping = useQuery({ queryKey: ['ping'], queryFn: () => api.get('/ping') });
  return <p>{ping.isError ? 'Ping failed' : 'Ping page'}</p>;
}

const routes: RouteObject[] = [
  { element: <ProtectedRoute />, children: [{ path: '/', element: <PingPage /> }] },
  { path: '/verify-email', element: <VerifyEmailPage /> },
];

function refuseWith(body: Record<string, unknown> | null) {
  server.use(
    http.get(apiUrl('/ping'), () =>
      body
        ? HttpResponse.json(
            { status: 403, title: 'Forbidden', ...body },
            { status: 403, headers: { 'Content-Type': 'application/problem+json' } },
          )
        : HttpResponse.json({}, { status: 403 }),
    ),
  );
}

describe('ProtectedRoute with a server-side refusal', () => {
  it('sends an unverified account to /verify-email on 403 EMAIL_NOT_VERIFIED', async () => {
    refuseWith({ errorCode: 'EMAIL_NOT_VERIFIED' });
    const { router } = renderWithProviders(routes, { auth: { roles: ['Trainer'] } });

    expect(await screen.findByRole('heading', { name: 'Check your email' })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/verify-email');
    expect(useAuthStore.getState().user?.emailConfirmed).toBe(false);
  });

  it.each([
    ['403 PLAN_CHOICE_REQUIRED', { errorCode: 'PLAN_CHOICE_REQUIRED' }, 1],
    ['a bare 403', null, 0],
  ])('keeps the account verified on %s', async (_name, body, events) => {
    refuseWith(body);
    const onPlanChoice = vi.fn();
    window.addEventListener(PLAN_CHOICE_REQUIRED_EVENT, onPlanChoice);
    const { router } = renderWithProviders(routes, { auth: { roles: ['Trainer'] } });

    expect(await screen.findByText('Ping failed')).toBeInTheDocument();
    window.removeEventListener(PLAN_CHOICE_REQUIRED_EVENT, onPlanChoice);
    expect(router.state.location.pathname).toBe('/');
    expect(useAuthStore.getState().user?.emailConfirmed).toBe(true);
    expect(onPlanChoice).toHaveBeenCalledTimes(events);
  });
});
