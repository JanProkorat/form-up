/**
 * Harness for the coach Profile page tests: renders the real ProfilePage on `/profile` inside a layout with a
 * sidebar-style link, and serves the two profile loads.
 */
import { Link, Outlet } from 'react-router-dom';
import type { RouteObject } from 'react-router-dom';
import type { GetProfessionalProfileResponse, GetProfileResponse } from '@/api/generated';
import ProfilePage from '@/pages/ProfilePage';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { renderWithProviders } from '@/test/render';

export function coachMe(overrides: Partial<GetProfileResponse> = {}): GetProfileResponse {
  return {
    userId: '00000000-0000-0000-0000-000000000001',
    email: 'coach@example.com',
    firstName: 'Alex',
    lastName: 'Coach',
    roles: ['Trainer'],
    emailConfirmed: true,
    ...overrides,
  };
}

export function coachProfile(overrides: Partial<GetProfessionalProfileResponse> = {}): GetProfessionalProfileResponse {
  return {
    bio: 'Strength coach.',
    city: 'Brno',
    specializations: '[]',
    certificates: '[]',
    languages: '[]',
    showInSearch: true,
    acceptNewClients: true,
    ...overrides,
  };
}

export function serveProfile(
  me: GetProfileResponse = coachMe(),
  trainer: GetProfessionalProfileResponse = coachProfile(),
): void {
  server.use(
    http.get(apiUrl('/users/me'), () => HttpResponse.json(me)),
    http.get(apiUrl('/trainer/profile'), () => HttpResponse.json(trainer)),
  );
}

/** Mounts ProfilePage on `/profile`, with `/clients` and `/packages` as navigation targets. */
export function renderProfilePage(initialEntries: string[] = ['/profile']) {
  const routes: RouteObject[] = [
    {
      element: (
        <>
          <Link to="/clients" aria-label="Clients nav" />
          <Outlet />
        </>
      ),
      children: [
        { path: '/profile', element: <ProfilePage /> },
        { path: '/packages', element: <div data-testid="packages-page" /> },
        { path: '/clients', element: <div data-testid="clients-page" /> },
      ],
    },
  ];
  return renderWithProviders(routes, { initialEntries, auth: { roles: ['Trainer'] } });
}
