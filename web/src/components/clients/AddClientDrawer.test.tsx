/**
 * The "Invite client" drawer on the Clients page: what the invite request carries and the live slot check
 * (#1148). Ported from the Playwright spec (trainer/clients): the invite journey against the real backend
 * stays there.
 */
import { describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import { QuestionnaireType } from '@/api/generated';
import {
  coachPackage,
  newClientsListState,
  renderClientsPage,
  serveClientsList,
} from '@/test/clientsList';
import type { ClientsListState } from '@/test/clientsList';

const TAKEN_EMAIL = 'taken.client@formup.test';
const PACKAGE = coachPackage();
const DEFAULT_FORM_ID = 'bbbbbbbb-0000-0000-0000-000000001930';

type User = ReturnType<typeof renderClientsPage>['user'];

const emailBox = () => screen.getByRole('textbox', { name: 'Email' });

async function openDrawer(user: User) {
  await user.click(await screen.findByRole('button', { name: 'Invite client' }));
  await screen.findByRole('heading', { name: 'Invite client' });
}

async function openDrawerAndType(user: User, email: string) {
  await openDrawer(user);
  await user.type(emailBox(), email);
}

async function typeEmailAndTab(user: User, email: string) {
  await user.type(emailBox(), email);
  await user.tab();
}

async function sent(state: ClientsListState) {
  await waitFor(() => expect(state.invites).toHaveLength(1));
  return state.invites[0];
}

describe('Invite drawer: the invite request', () => {
  it('choosing a package tile sends its id and shows what the client pays', async () => {
    const state = newClientsListState({ packages: [PACKAGE] });
    serveClientsList(state);
    const { user } = renderClientsPage();
    await openDrawer(user);

    const packageTile = await within(await screen.findByTestId('offer-package-tiles')).findByRole('radio', {
      name: /Monthly coaching/,
    });
    await typeEmailAndTab(user, 'qa.invite.pkg@formup.test');

    await user.click(packageTile);
    expect(packageTile).toBeChecked();
    expect(screen.getByTestId('offer-payable')).toBeVisible();

    await user.click(screen.getByRole('button', { name: 'Send invitation' }));

    await waitFor(() => expect(screen.queryByRole('heading', { name: 'Invite client' })).not.toBeInTheDocument());
    expect((await sent(state)).packageId).toBe(PACKAGE.packageId);
  });

  it('an untouched onboarding form row sends the default form and no skip flag', async () => {
    const state = newClientsListState({
      forms: [
        {
          publicId: DEFAULT_FORM_ID,
          title: 'Default onboarding',
          type: QuestionnaireType.Onboarding,
          isActive: true,
          isDefault: true,
          questionCount: 4,
          usedForClientCount: 0,
        },
      ],
    });
    serveClientsList(state);
    const { user } = renderClientsPage();
    await openDrawer(user);

    await waitFor(() => expect(screen.getByTestId('offer-form-row')).toHaveTextContent('Default onboarding'));
    await typeEmailAndTab(user, 'qa.invite.form@formup.test');

    await user.click(screen.getByRole('button', { name: 'Send invitation' }));

    await waitFor(() => expect(screen.queryByRole('heading', { name: 'Invite client' })).not.toBeInTheDocument());
    const body = await sent(state);
    expect(body.questionnairePublicId).toBe(DEFAULT_FORM_ID);
    expect(body).not.toHaveProperty('skipOnboardingForm');
  });
});

describe('Invite drawer: live slot check (#1148)', () => {
  it('a taken training slot shows the label with the typed email in place of the helper', async () => {
    serveClientsList(newClientsListState({ slotCheck: { nutritionTaken: false, trainingTaken: true } }));
    const { user } = renderClientsPage();
    await openDrawerAndType(user, TAKEN_EMAIL);

    expect(await screen.findByText(`User with email ${TAKEN_EMAIL} already has a coach for training.`)).toBeVisible();
    expect(screen.queryByText('Already on Form Up?', { exact: false })).not.toBeInTheDocument();
  });

  it('free slots keep the helper line and show no label', async () => {
    const state = newClientsListState({ slotCheck: { nutritionTaken: false, trainingTaken: false } });
    serveClientsList(state);
    const { user } = renderClientsPage();
    await openDrawerAndType(user, TAKEN_EMAIL);
    await waitFor(() => expect(state.slotChecks).toHaveLength(1));

    expect(screen.getByText('Already on Form Up?', { exact: false })).toBeVisible();
    expect(screen.queryByTestId('invite-slot-taken')).not.toBeInTheDocument();
  });

  it('an invalid email sends no slot check', async () => {
    const state = newClientsListState();
    serveClientsList(state);
    const { user } = renderClientsPage();
    await openDrawerAndType(user, 'not-an-email');
    await user.tab();

    expect(screen.queryByTestId('invite-slot-taken')).not.toBeInTheDocument();
    // The debounce is 400 ms; waiting past it means a premature request would be counted.
    await new Promise((resolve) => setTimeout(resolve, 700));
    expect(state.slotChecks).toHaveLength(0);
  });

  it('a rate-limited check shows no label and keeps Send enabled', async () => {
    const state = newClientsListState({ slotCheck: 429 });
    serveClientsList(state);
    const { user, queryClient } = renderClientsPage();
    await openDrawerAndType(user, TAKEN_EMAIL);
    await user.tab();
    await waitFor(() =>
      expect(queryClient.getQueryState(['pendingInvites', 'slotCheck', TAKEN_EMAIL])?.status).toBe('error'),
    );

    expect(screen.queryByTestId('invite-slot-taken')).not.toBeInTheDocument();
    expect(screen.getByText('Already on Form Up?', { exact: false })).toBeVisible();
    expect(screen.getByRole('button', { name: 'Send invitation' })).toBeEnabled();
  });

  // A coach holding both roles picks the invite scope, and the label follows it.
  it('for a both-roles coach the label lists only the professions inside the selected scope', async () => {
    const email = 'taken.both@formup.test';
    serveClientsList(newClientsListState({ slotCheck: { nutritionTaken: true, trainingTaken: true } }));
    const { user } = renderClientsPage({ roles: ['Trainer', 'Nutritionist'] });
    await openDrawerAndType(user, email);

    const training = () => screen.queryByText(`User with email ${email} already has a coach for training.`);
    const nutrition = () => screen.queryByText(`User with email ${email} already has a coach for nutrition.`);

    // Default scope is Both.
    await waitFor(() => expect(training()).toBeVisible());
    expect(nutrition()).toBeVisible();

    const scopeGroup = screen.getByRole('group', { name: "What you'll coach" });
    await user.click(within(scopeGroup).getByRole('button', { name: 'Training' }));
    expect(training()).toBeVisible();
    expect(nutrition()).not.toBeInTheDocument();

    await user.click(within(scopeGroup).getByRole('button', { name: 'Nutrition' }));
    expect(nutrition()).toBeVisible();
    expect(training()).not.toBeInTheDocument();
  });
});
