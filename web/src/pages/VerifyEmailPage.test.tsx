/**
 * "/verify-email" — the page behind the link in the verification email. Ported from the Playwright
 * account-pages spec: no token and no session calls no /auth/ endpoint, and a token is verified exactly once
 * (the endpoint consumes it) while the page still settles on an answer.
 */
import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import VerifyEmailPage from '@/pages/VerifyEmailPage';
import { renderWithProviders } from '@/test/render';
import { HttpResponse, apiUrl, http, problem } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

describe('VerifyEmailPage', () => {
  it('with no token and no session renders the invalid-link state and calls no /auth/ endpoint', async () => {
    // Regression: there is no email to verify against, so ANY /auth/ call here is a bug.
    const authCalls: string[] = [];
    server.use(
      http.all(apiUrl('/auth/*'), ({ request }) => {
        authCalls.push(request.url);
        return HttpResponse.json({});
      }),
    );

    renderWithProviders(<VerifyEmailPage />, { initialEntries: ['/verify-email'] });

    expect(await screen.findByRole('heading', { name: "This link isn't valid" })).toBeInTheDocument();
    expect(authCalls).toEqual([]);
  });

  it('with a bogus token calls /auth/verify-email exactly once and settles on the invalid-link state', async () => {
    // Regression: StrictMode double-invokes effects in dev, and VerifyEmailEndpoint CONSUMES the token, so a
    // second call would turn a real verification into a false failure. Assert BOTH halves (one call, and a
    // settled result): the broken version satisfied the first on its own.
    const verifyCalls: string[] = [];
    server.use(
      http.post(apiUrl('/auth/verify-email'), () => {
        verifyCalls.push('verify-email');
        return problem(400, 'INVALID_VERIFICATION_TOKEN');
      }),
    );

    renderWithProviders(<VerifyEmailPage />, { initialEntries: ['/verify-email?token=definitely-bogus-token-1234'] });

    expect(await screen.findByRole('heading', { name: "This link isn't valid" })).toBeInTheDocument();
    expect(verifyCalls).toHaveLength(1);
  });
});
