import { describe, expect, it } from 'vitest';
import { screen, within } from '@testing-library/react';
import Sidebar from '@/components/layout/Sidebar';
import { recordRequests, serveCoachShell } from '@/test/adminOnlyAccess';
import { renderWithProviders } from '@/test/render';

const COACH_ONLY_ENTRIES = ['Clients', 'Inbox', 'Forms', 'Automations', 'Check-ins', 'Packages'];
const ADMIN_ENTRIES = ['Overview', 'Plans', 'Promotions', 'Coaches', 'Reports'];

function navLinkNames(): string[] {
  const nav = screen.getByRole('navigation');
  return within(nav)
    .getAllByRole('link')
    .map((link) => link.textContent ?? '');
}

describe('Sidebar entries per role', () => {
  it('shows an admin-only account just the libraries and the Admin section, and sends no coach request', async () => {
    const recorder = recordRequests();
    renderWithProviders(<Sidebar />, { auth: { roles: ['Admin'] } });

    const names = navLinkNames();
    expect(names).toEqual(expect.arrayContaining(['Recipes', 'Ingredients', ...ADMIN_ENTRIES]));
    for (const hidden of [...COACH_ONLY_ENTRIES, 'Plan templates', 'Exercises']) {
      expect(names).not.toContain(hidden);
    }
    expect(screen.queryByRole('button', { name: 'Notifications' })).not.toBeInTheDocument();
    // Nothing is fetched: an unregistered request would also fail the test as unhandled.
    expect(recorder.requests).toEqual([]);
    recorder.stop();
  });

  it('shows a trainer the coach entries but neither the nutrition nor the Admin section', async () => {
    serveCoachShell(['Trainer']);
    renderWithProviders(<Sidebar />, { auth: { roles: ['Trainer'] } });

    expect(await screen.findByRole('link', { name: 'Clients' })).toBeInTheDocument();
    const names = navLinkNames();
    expect(names).toEqual(expect.arrayContaining(COACH_ONLY_ENTRIES));
    for (const hidden of ['Recipes', 'Ingredients', 'Plan templates', ...ADMIN_ENTRIES]) {
      expect(names).not.toContain(hidden);
    }
  });

  it('shows a nutritionist the coach entries and the full nutrition section', async () => {
    serveCoachShell(['Nutritionist']);
    renderWithProviders(<Sidebar />, { auth: { roles: ['Nutritionist'] } });

    expect(await screen.findByRole('link', { name: 'Recipes' })).toBeInTheDocument();
    const names = navLinkNames();
    expect(names).toEqual(expect.arrayContaining([...COACH_ONLY_ENTRIES, 'Recipes', 'Ingredients', 'Plan templates']));
    for (const hidden of ADMIN_ENTRIES) {
      expect(names).not.toContain(hidden);
    }
  });

  it('shows an admin who is also a nutritionist everything, Admin section included', async () => {
    serveCoachShell(['Nutritionist']);
    renderWithProviders(<Sidebar />, { auth: { roles: ['Admin', 'Nutritionist'] } });

    expect(await screen.findByRole('link', { name: 'Plans' })).toBeInTheDocument();
    expect(navLinkNames()).toEqual(
      expect.arrayContaining([...COACH_ONLY_ENTRIES, 'Recipes', 'Ingredients', 'Plan templates', ...ADMIN_ENTRIES]),
    );
  });
});

describe('Admin Overview entry', () => {
  it('comes first in the Admin section', () => {
    renderWithProviders(<Sidebar />, { auth: { roles: ['Admin'] }, initialEntries: ['/admin'] });

    const names = navLinkNames();
    expect(names.indexOf('Overview')).toBeGreaterThanOrEqual(0);
    expect(names.indexOf('Overview')).toBe(names.indexOf('Plans') - 1);
  });

  it('is current on /admin only, not on the pages below it', () => {
    const { unmount } = renderWithProviders(<Sidebar />, { auth: { roles: ['Admin'] }, initialEntries: ['/admin'] });
    expect(screen.getByRole('link', { name: 'Overview' })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('link', { name: 'Plans' })).not.toHaveAttribute('aria-current');
    unmount();

    renderWithProviders(<Sidebar />, { auth: { roles: ['Admin'] }, initialEntries: ['/admin/plans'] });
    expect(screen.getByRole('link', { name: 'Overview' })).not.toHaveAttribute('aria-current');
    expect(screen.getByRole('link', { name: 'Plans' })).toHaveAttribute('aria-current', 'page');
  });
});

describe('account menu links per role', () => {
  it('hides profile and settings from an admin-only account but keeps log out reachable', async () => {
    const { user } = renderWithProviders(<Sidebar />, { auth: { roles: ['Admin'] } });

    await user.click(screen.getByRole('button', { name: 'Account menu' }));

    const menu = await screen.findByRole('dialog', { name: 'Account' });
    expect(within(menu).queryByRole('link', { name: 'Your profile' })).not.toBeInTheDocument();
    expect(within(menu).queryByRole('link', { name: 'Settings' })).not.toBeInTheDocument();
    expect(within(menu).getByRole('button', { name: 'Log out' })).toBeInTheDocument();
    expect(within(menu).getByRole('group', { name: 'Theme' })).toBeInTheDocument();
  });

  it('keeps profile and settings for an admin who is also a trainer', async () => {
    serveCoachShell(['Trainer']);
    const { user } = renderWithProviders(<Sidebar />, { auth: { roles: ['Admin', 'Trainer'] } });

    await user.click(await screen.findByRole('button', { name: 'Account menu' }));

    const menu = await screen.findByRole('dialog', { name: 'Account' });
    expect(within(menu).getByRole('link', { name: 'Your profile' })).toBeInTheDocument();
    expect(within(menu).getByRole('link', { name: 'Settings' })).toBeInTheDocument();
  });
});
