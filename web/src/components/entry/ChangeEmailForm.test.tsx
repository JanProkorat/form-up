/**
 * "Change email" on the check-your-email card of RegisterForm: edits the unverified account in place
 * (login with the held credentials -> PUT /auth/email -> best-effort logout) and never registers again.
 */
import { describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import type { LoginResponse, RegisterResponse } from '@/api/generated';
import RegisterForm from '@/components/entry/RegisterForm';
import { useAuthStore } from '@/stores/auth';
import { renderWithProviders } from '@/test/render';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

const FIRST_EMAIL = 'jan@coaching.cz';
const PASSWORD = 'Passw0rdOK1';

interface Recorded {
  method: string;
  path: string;
  auth: string | null;
  body: Record<string, unknown> | null;
}

type PutOutcome =
  | { status: 200 }
  | { status: 400; code?: string }
  | { status: 429 }
  | { status: 'network' };

interface Backend {
  log: Recorded[];
  registerCalls: () => number;
}

function serveBackend(options: { login?: 'ok' | 'fail' | 429; put?: PutOutcome[]; logoutFails?: boolean } = {}): Backend {
  const log: Recorded[] = [];
  const putOutcomes = [...(options.put ?? [])];
  let registers = 0;
  const record = async (request: Request): Promise<Recorded> => {
    const text = await request.text();
    const entry: Recorded = {
      method: request.method,
      path: new URL(request.url).pathname,
      auth: request.headers.get('Authorization'),
      body: text ? (JSON.parse(text) as Record<string, unknown>) : null,
    };
    log.push(entry);
    return entry;
  };

  server.use(
    http.post(apiUrl('/auth/register'), async ({ request }) => {
      registers += 1;
      await record(request);
      const response: RegisterResponse = {};
      return HttpResponse.json(response, { status: 201 });
    }),
    http.post(apiUrl('/auth/login'), async ({ request }) => {
      await record(request);
      if (options.login === 'fail') {
        return HttpResponse.json({ errors: [{ name: 'generalErrors', reason: 'bad', code: 'INVALID_CREDENTIALS' }] }, { status: 400 });
      }
      if (options.login === 429) {
        return new HttpResponse(null, { status: 429 });
      }
      const response: LoginResponse = { accessToken: 'throwaway-access', refreshToken: 'throwaway-refresh', emailConfirmed: false };
      return HttpResponse.json(response);
    }),
    http.put(apiUrl('/auth/email'), async ({ request }) => {
      const entry = await record(request);
      const outcome = putOutcomes.shift() ?? { status: 200 };
      if (outcome.status === 'network') {
        return HttpResponse.error();
      }
      if (outcome.status === 429) {
        return new HttpResponse(null, { status: 429 });
      }
      if (outcome.status === 400) {
        const errors = outcome.code ? [{ name: 'newEmail', reason: 'x', code: outcome.code }] : [{ name: 'newEmail', reason: 'Invalid' }];
        return HttpResponse.json({ errors }, { status: 400 });
      }
      return HttpResponse.json({ email: entry.body?.newEmail, remainingResends: 2 });
    }),
    http.post(apiUrl('/auth/logout'), async ({ request }) => {
      await record(request);
      return options.logoutFails ? new HttpResponse(null, { status: 500 }) : new HttpResponse(null, { status: 200 });
    }),
  );
  return { log, registerCalls: () => registers };
}

async function registerAndOpenChange(user: ReturnType<typeof renderWithProviders>['user']) {
  await user.type(screen.getByLabelText('First name'), 'Jan');
  await user.type(screen.getByLabelText('Last name'), 'Novak');
  await user.type(screen.getByLabelText('Email'), FIRST_EMAIL);
  await user.type(screen.getByLabelText('Password'), PASSWORD);
  await user.click(screen.getByRole('checkbox', { name: /Terms/ }));
  await user.click(screen.getByRole('checkbox', { name: /health data/ }));
  const submit = screen.getByRole('button', { name: 'Create account' });
  await waitFor(() => expect(submit).toBeEnabled());
  await user.click(submit);
  await screen.findByRole('heading', { name: 'Check your email' });
  await user.click(screen.getByRole('button', { name: 'Change email' }));
}

async function change(user: ReturnType<typeof renderWithProviders>['user'], address: string) {
  const input = screen.getByLabelText('New email address');
  await user.clear(input);
  await user.type(input, address);
  await user.click(screen.getByRole('button', { name: 'Send new link' }));
}

describe('RegisterForm change email', () => {
  it('opens the change form and Cancel returns to the unchanged check-email state', async () => {
    const backend = serveBackend();
    const { user } = renderWithProviders(<RegisterForm />, { initialEntries: ['/register?role=client'] });
    await registerAndOpenChange(user);

    expect(screen.getByRole('heading', { name: 'Change your email' })).toBeInTheDocument();
    expect(screen.getByLabelText('New email address')).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Check your email' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.getByRole('heading', { name: 'Check your email' })).toBeInTheDocument();
    expect(screen.getByText(FIRST_EMAIL)).toBeInTheDocument();
    expect(backend.log.map((entry) => entry.path)).toEqual(['/auth/register']);
  });

  it('signs in, moves the account, revokes the session and never registers again', async () => {
    const backend = serveBackend();
    const { user } = renderWithProviders(<RegisterForm />, { initialEntries: ['/register?role=client'] });
    await registerAndOpenChange(user);

    // Let the first countdown tick (it survives Cancel) so the restart is observable.
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    await screen.findByText(/available in 0:5\d/);
    await user.click(screen.getByRole('button', { name: 'Change email' }));
    await change(user, 'jan@coaching.com');

    expect(await screen.findByRole('heading', { name: 'Check your email' })).toBeInTheDocument();
    expect(screen.getByText('jan@coaching.com')).toBeInTheDocument();
    expect(screen.queryByText(FIRST_EMAIL)).not.toBeInTheDocument();
    expect(screen.getByText(/available in 1:00/)).toBeInTheDocument();

    await waitFor(() => expect(backend.log.map((entry) => entry.path)).toHaveLength(4));
    const [register, signIn, put, logout] = backend.log;
    expect(register.path).toBe('/auth/register');
    expect(signIn).toMatchObject({ method: 'POST', path: '/auth/login', body: { email: FIRST_EMAIL, password: PASSWORD } });
    expect(put).toMatchObject({
      method: 'PUT',
      path: '/auth/email',
      auth: 'Bearer throwaway-access',
      body: { newEmail: 'jan@coaching.com' },
    });
    expect(logout).toMatchObject({
      method: 'POST',
      path: '/auth/logout',
      auth: 'Bearer throwaway-access',
      body: { refreshToken: 'throwaway-refresh' },
    });
    // The only account-creating call is the original one.
    expect(backend.registerCalls()).toBe(1);
    expect(useAuthStore.getState().accessToken).toBeNull();
  });

  it('signs a second change in with the latest address', async () => {
    const backend = serveBackend();
    const { user } = renderWithProviders(<RegisterForm />, { initialEntries: ['/register?role=client'] });
    await registerAndOpenChange(user);
    await change(user, 'second@example.com');
    await screen.findByText('second@example.com');

    await user.click(screen.getByRole('button', { name: 'Change email' }));
    await change(user, 'third@example.com');
    await screen.findByText('third@example.com');

    const logins = backend.log.filter((entry) => entry.path === '/auth/login');
    expect(logins.map((entry) => entry.body?.email)).toEqual([FIRST_EMAIL, 'second@example.com']);
    expect(backend.registerCalls()).toBe(1);
  });

  it('still shows the new address when the logout revoke fails', async () => {
    serveBackend({ logoutFails: true });
    const { user } = renderWithProviders(<RegisterForm />, { initialEntries: ['/register?role=client'] });
    await registerAndOpenChange(user);
    await change(user, 'jan@coaching.com');

    expect(await screen.findByText('jan@coaching.com')).toBeInTheDocument();
  });

  it.each([
    ['EMAIL_ALREADY_IN_USE', 'This email is already registered.'],
    ['EMAIL_UNCHANGED', 'That is the address you already have. Enter a different one.'],
    [undefined, 'Enter a valid email address.'],
  ])('shows an inline message for a 400 with code %s', async (code, message) => {
    serveBackend({ put: [{ status: 400, code }] });
    const { user } = renderWithProviders(<RegisterForm />, { initialEntries: ['/register?role=client'] });
    await registerAndOpenChange(user);
    await change(user, 'other@example.com');

    expect(await screen.findByText(message)).toBeInTheDocument();
    expect(screen.getByLabelText('New email address')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByRole('heading', { name: 'Change your email' })).toBeInTheDocument();
  });

  it('blocks an empty, malformed or over-long address on the client without any request', async () => {
    const backend = serveBackend();
    const { user } = renderWithProviders(<RegisterForm />, { initialEntries: ['/register?role=client'] });
    await registerAndOpenChange(user);

    await user.click(screen.getByRole('button', { name: 'Send new link' }));
    expect(await screen.findByText('Enter the new email address.')).toBeInTheDocument();

    await change(user, 'not-an-email');
    expect(await screen.findByText('Enter a valid email address.')).toBeInTheDocument();

    await change(user, `${'a'.repeat(95)}@example.com`);
    expect(await screen.findByText('Enter a valid email address.')).toBeInTheDocument();

    expect(backend.log.map((entry) => entry.path)).toEqual(['/auth/register']);
  });

  it.each([
    ['VERIFICATION_RESEND_LIMIT_REACHED', 'Too many verification emails sent. Please try again later.'],
    ['EMAIL_ALREADY_VERIFIED', 'Your email is already verified.'],
  ])('shows %s as a banner', async (code, message) => {
    serveBackend({ put: [{ status: 400, code }] });
    const { user } = renderWithProviders(<RegisterForm />, { initialEntries: ['/register?role=client'] });
    await registerAndOpenChange(user);
    await change(user, 'other@example.com');

    expect(await within(await screen.findByRole('alert')).findByText(message)).toBeInTheDocument();
    expect(screen.getByLabelText('New email address')).not.toHaveAttribute('aria-invalid', 'true');
  });

  it('shows the rate-limit message on a 429 from the change request and does not retry', async () => {
    const backend = serveBackend({ put: [{ status: 429 }] });
    const { user } = renderWithProviders(<RegisterForm />, { initialEntries: ['/register?role=client'] });
    await registerAndOpenChange(user);
    await change(user, 'other@example.com');

    expect(await screen.findByRole('alert')).toHaveTextContent('Too many attempts — please try again shortly');
    expect(backend.log.filter((entry) => entry.path === '/auth/email')).toHaveLength(1);
  });

  it('shows the rate-limit message on a 429 from login and never sends the change', async () => {
    const backend = serveBackend({ login: 429 });
    const { user } = renderWithProviders(<RegisterForm />, { initialEntries: ['/register?role=client'] });
    await registerAndOpenChange(user);
    await change(user, 'other@example.com');

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(backend.log.some((entry) => entry.path === '/auth/email')).toBe(false);
  });

  it('shows the network message when the change request gets no response', async () => {
    serveBackend({ put: [{ status: 'network' }] });
    const { user } = renderWithProviders(<RegisterForm />, { initialEntries: ['/register?role=client'] });
    await registerAndOpenChange(user);
    await change(user, 'other@example.com');

    expect(await screen.findByRole('alert')).toHaveTextContent('Could not reach the server.');
    expect(screen.getByRole('heading', { name: 'Change your email' })).toBeInTheDocument();
  });

  it('shows a generic banner when sign-in fails and never sends the change', async () => {
    const backend = serveBackend({ login: 'fail' });
    const { user } = renderWithProviders(<RegisterForm />, { initialEntries: ['/register?role=client'] });
    await registerAndOpenChange(user);
    await change(user, 'other@example.com');

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(screen.getByLabelText('New email address')).not.toHaveAttribute('aria-invalid', 'true');
    expect(backend.log.some((entry) => entry.path === '/auth/email')).toBe(false);
  });
});
