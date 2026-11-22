/**
 * RegisterForm with an invite link ('/register?invite=<token>'): the token is resolved once, the invited
 * email and the Client role are locked and the token rides along with the register request.
 */
import { describe, expect, it } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import type { RegisterRequest, RegisterResponse, ResolveInviteTokenResponse } from '@/api/generated';
import RegisterForm from '@/components/entry/RegisterForm';
import { renderWithProviders } from '@/test/render';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

const INVITED_EMAIL = 'invited@example.com';
const TOKEN = 'ab+cd/ef==';

function serveRegister() {
  const bodies: RegisterRequest[] = [];
  server.use(
    http.post(apiUrl('/auth/register'), async ({ request }) => {
      bodies.push((await request.json()) as RegisterRequest);
      const response: RegisterResponse = {};
      return HttpResponse.json(response, { status: 201 });
    }),
  );
  return bodies;
}

function serveResolve(outcome: 'ok' | 'notFound') {
  let calls = 0;
  server.use(
    http.post(apiUrl('/auth/invite/resolve'), async () => {
      calls += 1;
      if (outcome === 'notFound') {
        return new HttpResponse(null, { status: 404 });
      }
      const response: ResolveInviteTokenResponse = { email: INVITED_EMAIL };
      return HttpResponse.json(response);
    }),
  );
  return () => calls;
}

async function fillAndSubmit(user: ReturnType<typeof renderWithProviders>['user'], email?: string) {
  await user.type(screen.getByLabelText('First name'), 'Jan');
  await user.type(screen.getByLabelText('Last name'), 'Novak');
  if (email) {
    await user.type(screen.getByLabelText('Email'), email);
  }
  await user.type(screen.getByLabelText('Password'), 'Passw0rdOK1');
  await user.click(screen.getByRole('checkbox', { name: /Terms/ }));
  await user.click(screen.getByRole('checkbox', { name: /health data/ }));
  const submit = screen.getByRole('button', { name: 'Create account' });
  await waitFor(() => expect(submit).toBeEnabled());
  await user.click(submit);
}

describe('RegisterForm invite link', () => {
  it('locks the resolved email and the Client role and sends the token', async () => {
    const resolveCalls = serveResolve('ok');
    const bodies = serveRegister();
    const { user } = renderWithProviders(<RegisterForm />, {
      initialEntries: [`/register?role=client&invite=${encodeURIComponent(TOKEN)}`],
    });

    const email = screen.getByLabelText('Email');
    await waitFor(() => expect(email).toHaveValue(INVITED_EMAIL));
    expect(email).toHaveAttribute('readonly');
    expect(email).toHaveAttribute('aria-readonly', 'true');
    expect(screen.getByText('This is the email your invitation was sent to.')).toBeInTheDocument();

    const client = screen.getByRole('button', { name: /I train for myself/ });
    expect(client).toHaveAttribute('aria-pressed', 'true');
    expect(client).toBeDisabled();
    expect(screen.getByRole('button', { name: /Personal trainer/ })).toBeDisabled();

    await fillAndSubmit(user);

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0]).toMatchObject({ email: INVITED_EMAIL, roles: ['Client'], inviteToken: TOKEN });
    // StrictMode mounts twice; the query must still hit the endpoint once.
    expect(resolveCalls()).toBe(1);
  });

  it('falls back to the normal form and drops the token when the invite cannot be resolved', async () => {
    serveResolve('notFound');
    const bodies = serveRegister();
    const { user } = renderWithProviders(<RegisterForm />, {
      initialEntries: [`/register?role=client&invite=${encodeURIComponent(TOKEN)}`],
    });

    expect(
      await screen.findByText(
        "This invitation link can't be used any more. You can still create an account below.",
      ),
    ).toBeInTheDocument();

    const email = screen.getByLabelText('Email');
    expect(email).not.toHaveAttribute('readonly');
    expect(email).toBeEnabled();
    const client = screen.getByRole('button', { name: /I train for myself/ });
    expect(client).toBeEnabled();
    // The ?role=client preselect still applies and the picker is usable.
    expect(client).toHaveAttribute('aria-pressed', 'true');
    await user.click(screen.getByRole('button', { name: /Personal trainer/ }));
    expect(screen.getByRole('button', { name: /Personal trainer/ })).toHaveAttribute('aria-pressed', 'true');
    await user.click(screen.getByRole('button', { name: /Personal trainer/ }));
    expect(client).toHaveAttribute('aria-pressed', 'false');
    await user.click(client);

    await fillAndSubmit(user, 'someone@example.com');

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0].email).toBe('someone@example.com');
    expect(bodies[0]).not.toHaveProperty('inviteToken');
  });

  it('does not resolve anything without an invite parameter', () => {
    const resolveCalls = serveResolve('ok');
    renderWithProviders(<RegisterForm />, { initialEntries: ['/register'] });

    expect(screen.getByLabelText('Email')).toBeEnabled();
    expect(resolveCalls()).toBe(0);
  });
});
