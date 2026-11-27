import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderProfilePage, serveProfile } from '@/test/profile';

async function openProfile() {
  serveProfile();
  const view = renderProfilePage();
  const city = await screen.findByRole('textbox', { name: /City/ });
  return { ...view, city };
}

describe('ProfilePage unsaved edits', () => {
  it('asks before an in-page link drops unsaved edits and stays on the page', async () => {
    const { user, router, city } = await openProfile();
    await user.type(city, ' nad Svitavou');

    await user.click(screen.getByRole('link', { name: /Set up|Manage/ }));

    expect(await screen.findByRole('dialog', { name: 'Leave without saving?' })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/profile');
  });

  it('keeps the edit when the coach chooses to keep editing', async () => {
    const { user, router, city } = await openProfile();
    await user.type(city, ' nad Svitavou');
    await user.click(screen.getByRole('link', { name: 'Clients nav' }));

    await user.click(await screen.findByRole('button', { name: 'Keep editing' }));

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/profile');
    expect(screen.getByRole('textbox', { name: /City/ })).toHaveValue('Brno nad Svitavou');
  });

  it('leaves for the target page when the coach confirms', async () => {
    const { user, router, city } = await openProfile();
    await user.type(city, ' nad Svitavou');
    await user.click(screen.getByRole('link', { name: 'Clients nav' }));

    await user.click(await screen.findByRole('button', { name: 'Leave' }));

    expect(await screen.findByTestId('clients-page')).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/clients');
  });

  it('navigates straight away when nothing was edited', async () => {
    const { user, router } = await openProfile();

    await user.click(screen.getByRole('link', { name: 'Clients nav' }));

    expect(await screen.findByTestId('clients-page')).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/clients');
  });
});
