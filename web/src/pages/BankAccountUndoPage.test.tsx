/**
 * "/bank-account/undo" — the page behind the link in the bank-account-change email.
 * Ported from the Playwright spec: nothing is sent on load, the token leaves the address bar, one click
 * sends exactly one request, and the two documented refusals map to their own cards.
 */
import { describe, expect, it } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import BankAccountUndoPage from '@/pages/BankAccountUndoPage';
import { renderWithProviders } from '@/test/render';
import { HttpResponse, apiUrl, http, problem } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

const UNDO = apiUrl('/payments/bank-account/undo');

function renderPage(search: string) {
  return renderWithProviders(<BankAccountUndoPage />, { initialEntries: [`/bank-account/undo${search}`] });
}

describe('BankAccountUndoPage', () => {
  it('acts only on an explicit click, once, and leaves no token in the address bar', async () => {
    const bodies: unknown[] = [];
    server.use(
      http.post(UNDO, async ({ request }) => {
        bodies.push(await request.json());
        return new HttpResponse(null, { status: 204 });
      }),
    );

    // A token with characters that URL decoding must hand over verbatim.
    const { user, router } = renderPage('?token=tok%2Fen%3D');

    const undoButton = await screen.findByRole('button', { name: 'Undo change' });
    await waitFor(() => expect(router.state.location.search).toBe(''));
    expect(router.state.location.pathname).toBe('/bank-account/undo');
    expect(bodies).toHaveLength(0);

    await user.click(undoButton);
    expect(await screen.findByRole('heading', { name: 'Previous account restored' })).toBeInTheDocument();

    expect(bodies).toEqual([{ token: 'tok/en=' }]);
  });

  it('an expired link and a superseded change each get their own card', async () => {
    server.use(http.post(UNDO, () => problem(400, 'BANK_ACCOUNT_UNDO_INVALID')));
    const first = renderPage('?token=expired');
    await first.user.click(await screen.findByRole('button', { name: 'Undo change' }));
    expect(await screen.findByRole('heading', { name: 'This link no longer works' })).toBeInTheDocument();
    first.unmount();

    server.use(
      http.post(UNDO, () =>
        HttpResponse.json(
          { status: 409, errorCode: 'BANK_ACCOUNT_UNDO_SUPERSEDED' },
          { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    );
    const second = renderPage('?token=superseded');
    await second.user.click(await screen.findByRole('button', { name: 'Undo change' }));
    expect(await screen.findByRole('heading', { name: 'The account changed again' })).toBeInTheDocument();
  });
});
