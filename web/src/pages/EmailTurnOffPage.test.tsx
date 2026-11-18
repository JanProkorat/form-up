/**
 * "/email-off" — the page behind the turn-off link in notification emails.
 * Ported from the Playwright spec: one turn-off POST on load, the token leaves the address bar,
 * "Turn back on" sends the same token once, and any 400 or a missing token gets the "doesn't work" card.
 */
import { describe, expect, it } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import { NotificationEvent } from '@/api/email-links';
import type { EmailLinkTurnOffResponse } from '@/api/email-links';
import EmailTurnOffPage from '@/pages/EmailTurnOffPage';
import { renderWithProviders } from '@/test/render';
import { HttpResponse, apiUrl, http, problem } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

const TURN_OFF = apiUrl('/users/email-links/turn-off');
const TURN_ON = apiUrl('/users/email-links/turn-on');

const turnedOff: EmailLinkTurnOffResponse = { email: 'coach@example.com', event: NotificationEvent.NewMessage };

function renderPage(search: string) {
  return renderWithProviders(<EmailTurnOffPage />, { initialEntries: [`/email-off${search}`] });
}

describe('EmailTurnOffPage', () => {
  it('turns the email off once, hides the token, and can turn it back on', async () => {
    const offBodies: unknown[] = [];
    const onBodies: unknown[] = [];
    server.use(
      http.post(TURN_OFF, async ({ request }) => {
        offBodies.push(await request.json());
        return HttpResponse.json(turnedOff);
      }),
      http.post(TURN_ON, async ({ request }) => {
        onBodies.push(await request.json());
        return HttpResponse.json(turnedOff);
      }),
    );

    const { user, router } = renderPage('?token=tok%2Fen%3D');

    expect(await screen.findByRole('heading', { name: 'This email is off' })).toBeInTheDocument();
    expect(screen.getByText('coach@example.com')).toBeInTheDocument();
    expect(screen.getByText('New message', { exact: true })).toBeInTheDocument();
    await waitFor(() => expect(router.state.location.search).toBe(''));
    expect(router.state.location.pathname).toBe('/email-off');
    expect(offBodies).toEqual([{ token: 'tok/en=' }]);

    await user.click(screen.getByRole('button', { name: 'Turn back on' }));
    expect(await screen.findByRole('heading', { name: 'This email is on again' })).toBeInTheDocument();
    expect(onBodies).toEqual([{ token: 'tok/en=' }]);
    expect(offBodies).toHaveLength(1);
  });

  it('shows the "doesn\'t work" card on a 400 from the API', async () => {
    server.use(http.post(TURN_OFF, () => problem(400, 'EMAIL_LINK_INVALID')));

    renderPage('?token=tampered');

    expect(await screen.findByRole('heading', { name: "This link doesn't work" })).toBeInTheDocument();
  });

  it('shows a retryable state on a 429, and "Try again" sends the turn-off request once more', async () => {
    let requests = 0;
    server.use(
      http.post(TURN_OFF, () => {
        requests += 1;
        return requests === 1 ? HttpResponse.json({}, { status: 429 }) : HttpResponse.json(turnedOff);
      }),
    );

    const { user } = renderPage('?token=tok');

    expect(await screen.findByRole('heading', { name: 'Something went wrong' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: "This link doesn't work" })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('heading', { name: 'This email is off' })).toBeInTheDocument();
    expect(requests).toBe(2);
  });

  it('shows the "doesn\'t work" card and sends nothing when the link has no token', async () => {
    let requests = 0;
    server.use(
      http.post(TURN_OFF, () => {
        requests += 1;
        return HttpResponse.error();
      }),
    );

    renderPage('');

    expect(await screen.findByRole('heading', { name: "This link doesn't work" })).toBeInTheDocument();
    expect(requests).toBe(0);
  });
});
