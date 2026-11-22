/**
 * '/invite/accept' on a desktop browser hands over to '/register', keeping the invite token intact.
 */
import { describe, expect, it } from 'vitest';
import { waitFor } from '@testing-library/react';
import InviteAcceptPage from '@/pages/InviteAcceptPage';
import { renderWithProviders } from '@/test/render';

describe('InviteAcceptPage (desktop)', () => {
  it('forwards a base64 token with + / = unchanged', async () => {
    const token = 'ab+cd/ef==';
    const { router } = renderWithProviders(
      [
        { path: '/invite/accept', element: <InviteAcceptPage /> },
        { path: '/register', element: <div data-testid="register" /> },
      ],
      { initialEntries: [`/invite/accept?token=${encodeURIComponent(token)}`] },
    );

    await waitFor(() => expect(router.state.location.pathname).toBe('/register'));
    const params = new URLSearchParams(router.state.location.search);
    expect(params.get('role')).toBe('client');
    expect(params.get('invite')).toBe(token);
  });

  it('keeps the plain client preselect when there is no token', async () => {
    const { router } = renderWithProviders(
      [
        { path: '/invite/accept', element: <InviteAcceptPage /> },
        { path: '/register', element: <div data-testid="register" /> },
      ],
      { initialEntries: ['/invite/accept'] },
    );

    await waitFor(() => expect(router.state.location.pathname).toBe('/register'));
    expect(router.state.location.search).toBe('?role=client');
  });
});
