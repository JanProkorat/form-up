/**
 * '/admin/promotion-offers' — the billing period of a new promotion: which cadences it discounts, the hint that
 * explains how a duration works on yearly invoices, and the server refusal for an annual-only promotion on a
 * plan that has no yearly price.
 */
import { describe, expect, it } from 'vitest';
import { screen, within } from '@testing-library/react';
import {
  newAdminPromotionsState,
  renderAdminPromotions,
  serveAdminPromotions,
  type AdminPromotionsState,
} from '@/test/adminPromotions';
import { apiUrl, http, problem } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

type Rendered = ReturnType<typeof renderAdminPromotions>;

async function openDrawer(state: AdminPromotionsState): Promise<Rendered> {
  serveAdminPromotions(state);
  const rendered = renderAdminPromotions();
  await rendered.user.click(await screen.findByRole('button', { name: 'New promotion' }));
  await screen.findByRole('textbox', { name: 'Name · Czech' });
  return rendered;
}

async function fillRequired({ user }: Rendered) {
  await user.type(screen.getByRole('textbox', { name: 'Name · Czech' }), 'Launch');
  await user.type(screen.getByRole('textbox', { name: 'Value' }), '20');
}

const periodGroup = () => screen.getByRole('group', { name: 'Billing period' });

const HINT = /takes off the first yearly invoice only.*Forever takes off every yearly invoice/;

describe('AdminPromotionsPage billing period', () => {
  it('covers both cadences by default and sends Both', async () => {
    const state = newAdminPromotionsState();
    const rendered = await openDrawer(state);

    await fillRequired(rendered);
    await rendered.user.click(screen.getByRole('button', { name: 'Save promotion' }));

    expect(await screen.findByText('Promotion saved.')).toBeInTheDocument();
    expect(state.creates).toHaveLength(1);
    expect(state.creates[0]).toMatchObject({ billingPeriod: 'Both' });
  });

  it('sends the trimmed English and German names, and leaves an empty one out', async () => {
    const state = newAdminPromotionsState();
    const rendered = await openDrawer(state);

    await fillRequired(rendered);
    await rendered.user.type(screen.getByRole('textbox', { name: 'Name · English' }), '  Launch offer ');
    await rendered.user.type(screen.getByRole('textbox', { name: 'Name · German' }), '   ');
    await rendered.user.click(screen.getByRole('button', { name: 'Save promotion' }));
    await screen.findByText('Promotion saved.');

    expect(state.creates[0]).toMatchObject({ name: 'Launch', nameEn: 'Launch offer' });
    expect(state.creates[0]).not.toHaveProperty('nameDe');
  });

  it('sends Annual and Monthly as chosen', async () => {
    const state = newAdminPromotionsState();
    const rendered = await openDrawer(state);

    await fillRequired(rendered);
    await rendered.user.click(within(periodGroup()).getByRole('button', { name: 'Annual' }));
    await rendered.user.click(screen.getByRole('button', { name: 'Save promotion' }));
    await screen.findByText('Promotion saved.');

    expect(state.creates[0]).toMatchObject({ billingPeriod: 'Annual' });
  });

  it('explains how durations work on yearly invoices for Annual and Both, and hides that for Monthly', async () => {
    const rendered = await openDrawer(newAdminPromotionsState());

    expect(screen.getByText(HINT)).toBeInTheDocument();

    await rendered.user.click(within(periodGroup()).getByRole('button', { name: 'Monthly' }));
    expect(screen.queryByText(HINT)).not.toBeInTheDocument();

    await rendered.user.click(within(periodGroup()).getByRole('button', { name: 'Annual' }));
    expect(screen.getByText(HINT)).toBeInTheDocument();
  });

  it('shows the refusal under Plans and keeps the drawer when a plan has no yearly price', async () => {
    const state = newAdminPromotionsState();
    const rendered = await openDrawer(state);
    server.use(http.post(apiUrl('/admin/promotions'), () => problem(400, 'PROMOTION_PLANS_INVALID')));

    await fillRequired(rendered);
    await rendered.user.click(within(periodGroup()).getByRole('button', { name: 'Annual' }));
    await rendered.user.click(screen.getByRole('button', { name: 'Save promotion' }));

    expect(
      await screen.findByText(
        'Pick at least one paid plan. An annual-only promotion needs plans that have a yearly price.',
      ),
    ).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Name · Czech' })).toHaveValue('Launch');
  });
});
