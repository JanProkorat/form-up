import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import type { RouteObject } from 'react-router-dom';
import AdminOnlyGuard from '@/routes/AdminOnlyGuard';
import RoleGuard from '@/routes/RoleGuard';
import { recordRequests, serveCoachShell } from '@/test/adminOnlyAccess';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { renderWithProviders } from '@/test/render';

/** The slice of the App.tsx route table that decides who may open what. */
const routes: RouteObject[] = [
  {
    element: <AdminOnlyGuard />,
    children: [
      { path: '/clients', element: <p>{'Clients page'}</p> },
      { path: '/inbox', element: <p>{'Inbox page'}</p> },
      { path: '/profile', element: <p>{'Profile page'}</p> },
      {
        element: <RoleGuard allowedRoles={['Nutritionist']} allowAdminOnly />,
        children: [
          { path: '/recipes', element: <p>{'Recipes page'}</p> },
          { path: '/ingredients', element: <p>{'Ingredients page'}</p> },
        ],
      },
      {
        element: <RoleGuard allowedRoles={['Nutritionist']} />,
        children: [{ path: '/plan-templates', element: <p>{'Plan templates page'}</p> }],
      },
      {
        element: <RoleGuard allowedRoles={['Admin']} />,
        children: [
          { path: '/admin', element: <p>{'Admin overview page'}</p> },
          { path: '/admin/plans', element: <p>{'Admin plans page'}</p> },
        ],
      },
    ],
  },
];

const FORBIDDEN = 'No access to this page';

describe('admin-only route access', () => {
  it.each(['/clients', '/inbox', '/profile', '/plan-templates'])('shows the 403 page on %s', async (path) => {
    const recorder = recordRequests();
    renderWithProviders(routes, { initialEntries: [path], auth: { roles: ['Admin'] } });

    expect(await screen.findByRole('heading', { name: FORBIDDEN })).toBeInTheDocument();
    // Admin-only is decided from the token, so no coach-roles lookup is sent.
    expect(recorder.requests).toEqual([]);
    recorder.stop();
  });

  it('links the 403 page back to the Admin landing', async () => {
    renderWithProviders(routes, { initialEntries: ['/clients'], auth: { roles: ['Admin'] } });

    const link = await screen.findByRole('link', { name: 'Back to Admin' });
    expect(link).toHaveAttribute('href', '/admin');
  });

  it.each([
    ['/recipes', 'Recipes page'],
    ['/ingredients', 'Ingredients page'],
    ['/admin', 'Admin overview page'],
    ['/admin/plans', 'Admin plans page'],
  ])('lets an admin-only account open %s', async (path, text) => {
    renderWithProviders(routes, { initialEntries: [path], auth: { roles: ['Admin'] } });

    expect(await screen.findByText(text)).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: FORBIDDEN })).not.toBeInTheDocument();
  });

  it('keeps redirecting a trainer-only coach away from the nutrition routes', async () => {
    serveCoachShell(['Trainer']);
    renderWithProviders(routes, { initialEntries: ['/recipes'], auth: { roles: ['Trainer'] } });

    expect(await screen.findByText('Clients page')).toBeInTheDocument();
  });

  it('keeps an admin with only a trainer role out of the libraries and the 403 page', async () => {
    serveCoachShell(['Trainer']);
    const { unmount } = renderWithProviders(routes, {
      initialEntries: ['/recipes'],
      auth: { roles: ['Admin', 'Trainer'] },
    });

    expect(await screen.findByText('Clients page')).toBeInTheDocument();
    unmount();

    renderWithProviders(routes, { initialEntries: ['/inbox'], auth: { roles: ['Admin', 'Trainer'] } });
    expect(await screen.findByText('Inbox page')).toBeInTheDocument();
  });

  it('lets a nutritionist, with or without Admin, open the libraries and plan templates', async () => {
    serveCoachShell(['Nutritionist']);
    const { unmount } = renderWithProviders(routes, {
      initialEntries: ['/recipes'],
      auth: { roles: ['Nutritionist'] },
    });
    expect(await screen.findByText('Recipes page')).toBeInTheDocument();
    unmount();

    renderWithProviders(routes, {
      initialEntries: ['/plan-templates'],
      auth: { roles: ['Admin', 'Nutritionist'] },
    });
    expect(await screen.findByText('Plan templates page')).toBeInTheDocument();
  });

  it('does not treat an Admin with a removed coach role as admin-only', async () => {
    // The token keeps Trainer, but no active coach role is left: behaviour stays as before, no 403 page.
    serveCoachShell([]);
    renderWithProviders(routes, { initialEntries: ['/clients'], auth: { roles: ['Admin', 'Trainer'] } });

    expect(await screen.findByText('Clients page')).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: FORBIDDEN })).not.toBeInTheDocument();
  });

  it('does not treat an Admin whose coach roles failed to load as admin-only', async () => {
    // The lookup fails: the account must not be mistaken for admin-only and get the 403 page.
    server.use(http.get(apiUrl('/users/me/roles'), () => HttpResponse.json({}, { status: 500 })));
    renderWithProviders(routes, { initialEntries: ['/clients'], auth: { roles: ['Admin', 'Trainer'] } });

    expect(await screen.findByText('Clients page')).toBeInTheDocument();
  });
});
