/**
 * Account-flow routes: the sign-in dialog routes ("/login", "/forgot-password", "/auth/reset-password") under
 * EntryPage, and the "/register" page. Ported from the Playwright account-pages spec; the routes mirror App.tsx.
 */
import { beforeEach, describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import type { RouteObject } from 'react-router-dom';
import ForgotPasswordForm from '@/components/entry/ForgotPasswordForm';
import LoginForm from '@/components/entry/LoginForm';
import ResetPasswordForm from '@/components/entry/ResetPasswordForm';
import EntryPage from '@/pages/EntryPage';
import RegisterPage from '@/pages/RegisterPage';
import { renderWithProviders } from '@/test/render';
import type { GetProfileResponse, LoginResponse } from '@/api/generated';
import { HttpResponse, apiUrl, http, problem } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

const PASSWORD_RESET = apiUrl('/auth/password/reset');
const VALID_RESET = '/auth/reset-password?token=some-reset-token-value&email=someone%40example.com';

const routes: RouteObject[] = [
  {
    element: <EntryPage />,
    children: [
      { path: '/', element: null },
      { path: '/login', element: <LoginForm /> },
      { path: '/forgot-password', element: <ForgotPasswordForm /> },
      { path: '/auth/reset-password', element: <ResetPasswordForm /> },
    ],
  },
  { path: '/register', element: <RegisterPage /> },
];

function renderAt(path: string, options: { auth?: { roles: string[] } } = {}) {
  return renderWithProviders(routes, { initialEntries: [path], ...options });
}

const passwordInputs = () => document.querySelectorAll('input[type="password"]');

beforeEach(() => {
  localStorage.removeItem('refreshToken');
});

describe('entry routes', () => {
  it('/forgot-password renders its form in a dialog with an accessible name, and /register is a page without one', async () => {
    const first = renderAt('/forgot-password');
    expect(await screen.findByRole('dialog', { name: 'Reset your password' })).toBeInTheDocument();
    first.unmount();

    renderAt('/register');
    expect(await screen.findByRole('heading', { name: 'Join our community' })).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('swapping between /login and /forgot-password keeps one dialog mounted and focuses the container, never a field', async () => {
    // Regression: focusing the first field of a form (React Hook Form, `mode: 'onTouched'`) makes the user's
    // next click blur it first, render its "required" error, grow the form and swallow that click. On open,
    // only /login focuses a field (Email); /forgot-password focuses the dialog container, and a client-side
    // swap inside the open dialog must do the same without closing and reopening it.
    const { user, router } = renderAt('/forgot-password');
    const dialog = await screen.findByRole('dialog');
    await waitFor(() => expect(dialog).toHaveFocus());
    expect(screen.getByLabelText('Email')).not.toHaveFocus();

    await user.click(within(dialog).getAllByRole('link', { name: 'Back to sign in' })[0]);
    await waitFor(() => expect(router.state.location.pathname).toBe('/login'));
    expect(await within(dialog).findByLabelText('Password')).toBeInTheDocument();
    expect(dialog.isConnected).toBe(true);
    expect(screen.getByLabelText('Email')).not.toHaveFocus();

    await user.click(within(dialog).getByRole('link', { name: 'Forgot password?' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/forgot-password'));
    await waitFor(() => expect(dialog).toHaveFocus());
    expect(screen.getByLabelText('Email')).not.toHaveFocus();
    expect(dialog.isConnected).toBe(true);
    expect(screen.getAllByRole('dialog')).toHaveLength(1);
  });

  it('the login form has no client app signpost', async () => {
    // Reverses the #1058 AC 8 signpost: the sign-in board has none.
    renderAt('/login');

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).queryByText('Looking for a coach or nutritionist?')).not.toBeInTheDocument();
    expect(within(dialog).queryByRole('link', { name: 'App Store' })).not.toBeInTheDocument();
    expect(within(dialog).queryByRole('link', { name: 'Google Play' })).not.toBeInTheDocument();
    expect(within(dialog).getByText('New to Form Up?')).toBeInTheDocument();
  });

  it('/forgot-password shows an inline error for an unknown address and the sent step for a known one', async () => {
    // The API answers 404 EMAIL_NOT_REGISTERED for an unknown address and 200 for a known one.
    const requested: unknown[] = [];
    server.use(
      http.post(PASSWORD_RESET, async ({ request }) => {
        const body = (await request.json()) as { email: string };
        requested.push(body.email);
        return body.email === 'existing-owner@example.com'
          ? HttpResponse.json({})
          : HttpResponse.json(
              { status: 404, errorCode: 'EMAIL_NOT_REGISTERED' },
              { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
            );
      }),
    );

    const { user } = renderAt('/forgot-password');
    const dialog = await screen.findByRole('dialog', { name: 'Reset your password' });
    const email = screen.getByLabelText('Email');

    await user.type(email, 'definitely-does-not-exist@example.com');
    await user.click(screen.getByRole('button', { name: 'Send reset link' }));
    expect(await within(dialog).findByText(/No account uses this email\./)).toBeInTheDocument();
    expect(email).toHaveAttribute('aria-invalid', 'true');
    expect(within(dialog).getByRole('link', { name: 'Create account' })).toHaveAttribute('href', '/register');
    expect(within(dialog).queryByRole('heading', { name: 'Check your email' })).not.toBeInTheDocument();

    await user.clear(email);
    await user.type(email, 'existing-owner@example.com');
    await user.click(screen.getByRole('button', { name: 'Send reset link' }));
    expect(await screen.findByRole('dialog', { name: 'Check your email' })).toBeInTheDocument();
    expect(screen.getByText('existing-owner@example.com')).toBeInTheDocument();

    // "Send again" stays on the sent step.
    await user.click(screen.getByRole('button', { name: 'Send again' }));
    await waitFor(() => expect(requested).toHaveLength(3));
    expect(screen.getByRole('button', { name: 'Send again' })).toBeInTheDocument();
    expect(screen.getByText('existing-owner@example.com')).toBeInTheDocument();
  });

  it('/auth/reset-password with no query params renders the expired step with no password inputs; with token+email it renders the form', async () => {
    // ResetPasswordForm reads `token` and `email` from the query ONCE; a missing/malformed either one must
    // render the expired step immediately, WITHOUT mounting the password form or calling the API. A literally
    // empty `?token=&email=` reads as falsy, so it shows the expired step too.
    const first = renderAt('/auth/reset-password');
    expect(await screen.findByRole('dialog', { name: 'This link has expired' })).toBeInTheDocument();
    expect(passwordInputs()).toHaveLength(0);
    first.unmount();

    const second = renderAt('/auth/reset-password?token=&email=');
    expect(await screen.findByRole('dialog', { name: 'This link has expired' })).toBeInTheDocument();
    expect(passwordInputs()).toHaveLength(0);
    second.unmount();

    renderAt(VALID_RESET);
    const dialog = await screen.findByRole('dialog', { name: 'Set a new password' });
    expect(within(dialog).getByText(/For someone@example\.com\./)).toBeInTheDocument();
    expect(passwordInputs()).toHaveLength(2);
  });

  it('/auth/reset-password: a bogus token with a valid password lands on the expired step', async () => {
    // The endpoint answers one generic 400 for a bad, expired or used token and for an unknown email.
    server.use(http.put(PASSWORD_RESET, () => problem(400, 'INVALID_RESET_TOKEN')));

    const { user } = renderAt('/auth/reset-password?token=definitely-bogus-token&email=someone%40example.com');

    await user.type(await screen.findByLabelText('New password', { exact: true }), 'CorrectHorse9');
    await user.type(screen.getByLabelText('Repeat new password'), 'CorrectHorse9');
    await user.click(screen.getByRole('button', { name: 'Save new password' }));

    expect(await screen.findByRole('dialog', { name: 'This link has expired' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Send a new link' })).toBeInTheDocument();
    expect(passwordInputs()).toHaveLength(0);
  });

  it('/auth/reset-password: Show/Hide toggles each field and a mismatch is flagged', async () => {
    const { user } = renderAt(VALID_RESET);

    const newPassword = await screen.findByLabelText('New password', { exact: true });
    await user.click(screen.getByRole('button', { name: 'Show new password' }));
    expect(newPassword).toHaveAttribute('type', 'text');
    await user.click(screen.getByRole('button', { name: 'Hide new password' }));
    expect(newPassword).toHaveAttribute('type', 'password');

    await user.type(newPassword, 'CorrectHorse9');
    await user.type(screen.getByLabelText('Repeat new password'), 'Different9x');
    await user.click(screen.getByRole('button', { name: 'Save new password' }));
    expect(await screen.findByText("Passwords don't match.")).toBeInTheDocument();
  });

  it('/auth/reset-password: a signed-in browser still reaches the form and is signed out after a reset', async () => {
    // EntryPage must not bounce an authenticated visitor to /clients on the reset route, and a successful
    // reset clears the local session so "Sign in" opens /login.
    localStorage.setItem('refreshToken', 'stored-refresh-token');
    server.use(http.put(PASSWORD_RESET, () => HttpResponse.json({})));

    const { user, router } = renderAt(VALID_RESET, { auth: { roles: ['Trainer'] } });

    expect(await screen.findByRole('dialog', { name: 'Set a new password' })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/auth/reset-password');

    await user.type(screen.getByLabelText('New password', { exact: true }), 'CorrectHorse9');
    await user.type(screen.getByLabelText('Repeat new password'), 'CorrectHorse9');
    await user.click(screen.getByRole('button', { name: 'Save new password' }));

    const done = await screen.findByRole('dialog', { name: 'Password changed' });
    expect(localStorage.getItem('refreshToken')).toBeNull();

    await user.click(within(done).getByRole('button', { name: 'Sign in' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/login'));
    expect(await screen.findByRole('dialog', { name: 'Welcome back' })).toBeInTheDocument();
  });

  describe('post-login landing', () => {
    const landingRoutes: RouteObject[] = [
      ...routes,
      { path: '/clients', element: <div data-testid="clients-page" /> },
      { path: '/admin/plans', element: <div data-testid="admin-plans-page" /> },
      { path: '/admin/business-reports', element: <div data-testid="admin-reports-page" /> },
    ];

    async function signInAs(roles: string[], path = '/login') {
      const login: LoginResponse = { accessToken: 'test-access-token', refreshToken: 'test-refresh-token' };
      const profile: GetProfileResponse = {
        userId: '00000000-0000-0000-0000-000000000002',
        email: 'someone@example.com',
        firstName: 'Some',
        lastName: 'One',
        roles,
        emailConfirmed: true,
      };
      server.use(
        http.post(apiUrl('/auth/login'), () => HttpResponse.json(login)),
        http.get(apiUrl('/users/me'), () => HttpResponse.json(profile)),
      );

      const { user, router } = renderWithProviders(landingRoutes, { initialEntries: [path] });
      const visited: string[] = [router.state.location.pathname];
      router.subscribe((state) => visited.push(state.location.pathname));

      await user.type(await screen.findByLabelText('Email'), 'someone@example.com');
      await user.type(screen.getByLabelText('Password'), 'CorrectHorse9');
      await user.click(screen.getByRole('button', { name: 'Sign in' }));
      return { router, visited };
    }

    it('sends an admin-only user to /admin/plans and never visits /clients', async () => {
      const { router, visited } = await signInAs(['Admin']);

      await waitFor(() => expect(router.state.location.pathname).toBe('/admin/plans'));
      expect(await screen.findByTestId('admin-plans-page')).toBeInTheDocument();
      expect(visited).not.toContain('/clients');
    });

    it('sends a trainer to /clients', async () => {
      const { router } = await signInAs(['Trainer']);

      await waitFor(() => expect(router.state.location.pathname).toBe('/clients'));
    });

    it('sends an admin who is also a trainer to /clients', async () => {
      const { router } = await signInAs(['Admin', 'Trainer']);

      await waitFor(() => expect(router.state.location.pathname).toBe('/clients'));
    });

    it('keeps an explicit returnTo for an admin-only user', async () => {
      const { router, visited } = await signInAs(['Admin'], '/login?returnTo=/admin/business-reports');

      await waitFor(() => expect(router.state.location.pathname).toBe('/admin/business-reports'));
      expect(visited).not.toContain('/clients');
    });
  });
});
