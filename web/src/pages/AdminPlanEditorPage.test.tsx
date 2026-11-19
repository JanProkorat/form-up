/**
 * '/admin/plans/:planId' — the plan editor's yearly prices. The regression that matters most: a PUT is a full
 * update and the server closes a plan's annual price when the body omits it, so a save that never touched
 * "Per year" must still carry the loaded annual prices.
 */
import { describe, expect, it } from 'vitest';
import { screen, within } from '@testing-library/react';
import {
  freePlan,
  newAdminPlansState,
  paidPlan,
  renderAdminPlanEditor,
  serveAdminPlans,
  type AdminPlansState,
} from '@/test/adminPlans';
import { apiUrl, http, problem } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';

async function openEditor(state: AdminPlansState, planCode: string | 'new' = 'pro') {
  serveAdminPlans(state);
  const rendered = renderAdminPlanEditor(planCode);
  if (planCode !== 'new') {
    await screen.findByRole('textbox', { name: 'Per month' });
  }
  return rendered;
}

const save = (user: ReturnType<typeof renderAdminPlanEditor>['user']) =>
  user.click(screen.getByRole('button', { name: 'Save plan' }));

async function retype(
  user: ReturnType<typeof renderAdminPlanEditor>['user'],
  name: string,
  text: string,
) {
  const box = screen.getByRole('textbox', { name });
  await user.clear(box);
  if (text) {
    await user.type(box, text);
  }
}

describe('AdminPlanEditorPage yearly prices', () => {
  it('saving without touching Per year sends the loaded annual prices back', async () => {
    const state = newAdminPlansState();
    const { user } = await openEditor(state);

    expect(screen.getByRole('textbox', { name: 'Per year' })).toHaveValue('6490');
    expect(screen.getByRole('textbox', { name: 'Second role per year' })).toHaveValue('2990');

    await save(user);
    await screen.findByTestId('plans-list');

    expect(state.updates).toHaveLength(1);
    expect(state.updates[0]?.body).toMatchObject({
      priceMinorUnits: 64900,
      secondRoleAddOnPriceMinorUnits: 29900,
      annualPriceMinorUnits: 649000,
      annualSecondRoleAddOnPriceMinorUnits: 299000,
    });
  });

  it('sends an edited yearly price in minor units', async () => {
    const state = newAdminPlansState();
    const { user } = await openEditor(state);

    await retype(user, 'Per year', '6000');
    await retype(user, 'Second role per year', '2 500,50');
    await save(user);
    await screen.findByTestId('plans-list');

    expect(state.updates[0]?.body).toMatchObject({
      annualPriceMinorUnits: 600000,
      annualSecondRoleAddOnPriceMinorUnits: 250050,
    });
  });

  it('an empty Per year means not sold annually: neither annual field is sent', async () => {
    const state = newAdminPlansState();
    const { user } = await openEditor(state);

    await retype(user, 'Per year', '');
    await save(user);
    await screen.findByTestId('plans-list');

    const body = state.updates[0]?.body ?? {};
    expect(body).not.toHaveProperty('annualPriceMinorUnits');
    expect(body).not.toHaveProperty('annualSecondRoleAddOnPriceMinorUnits');
  });

  it('a plan without an annual price loads with an empty Per year and saves without one', async () => {
    const state = newAdminPlansState([
      paidPlan({ annualPriceMinorUnits: undefined, annualSecondRoleAddOnPriceMinorUnits: undefined }),
    ]);
    const { user } = await openEditor(state);

    expect(screen.getByRole('textbox', { name: 'Per year' })).toHaveValue('');
    await save(user);
    await screen.findByTestId('plans-list');

    expect(state.updates[0]?.body).not.toHaveProperty('annualPriceMinorUnits');
  });

  it('requires the yearly add-on price when a yearly price and a paid add-on are set', async () => {
    const state = newAdminPlansState();
    const { user } = await openEditor(state);

    await retype(user, 'Second role per year', '');
    await save(user);

    expect(await screen.findByText('Enter a second-role price per year above 0.')).toBeInTheDocument();
    expect(state.updates).toHaveLength(0);
  });

  it('rejects a yearly price of 0', async () => {
    const state = newAdminPlansState();
    const { user } = await openEditor(state);

    await retype(user, 'Per year', '0');
    await save(user);

    expect(await screen.findByText(/Enter a yearly price above 0/)).toBeInTheDocument();
    expect(state.updates).toHaveLength(0);
  });

  it('with the second role included there is no yearly add-on field and none is sent', async () => {
    const state = newAdminPlansState();
    const { user } = await openEditor(state);

    await user.click(screen.getByRole('button', { name: 'Included in the price' }));
    expect(screen.queryByRole('textbox', { name: 'Second role per year' })).not.toBeInTheDocument();
    await save(user);
    await screen.findByTestId('plans-list');

    expect(state.updates[0]?.body).toMatchObject({ bothRolesIncluded: true, annualPriceMinorUnits: 649000 });
    expect(state.updates[0]?.body).not.toHaveProperty('annualSecondRoleAddOnPriceMinorUnits');
  });

  it('the Free plan has no yearly inputs and sends no annual fields', async () => {
    const state = newAdminPlansState([freePlan()]);
    const { user } = await openEditor(state, 'free');

    expect(screen.queryByRole('textbox', { name: 'Per year' })).not.toBeInTheDocument();
    await save(user);
    await screen.findByTestId('plans-list');

    expect(state.updates[0]?.body).not.toHaveProperty('annualPriceMinorUnits');
    expect(state.updates[0]?.body).not.toHaveProperty('annualSecondRoleAddOnPriceMinorUnits');
  });

  it('shows the saving against twelve months, and hides it when the yearly price is not lower', async () => {
    const state = newAdminPlansState();
    const { user } = await openEditor(state);

    expect(screen.getByRole('textbox', { name: 'Per year' })).toHaveAccessibleDescription(
      /Saves 17 % against 12 × .*649.*\(.*788.*\)\./,
    );
    expect(screen.getByRole('textbox', { name: 'Second role per year' })).toHaveAccessibleDescription(
      /Saves 17 % against 12 × .*299/,
    );

    await retype(user, 'Per year', '7788');
    expect(screen.getByRole('textbox', { name: 'Per year' })).not.toHaveAccessibleDescription(/Saves/);
  });

  it('previews the yearly price for coaches', async () => {
    const state = newAdminPlansState();
    await openEditor(state);

    const preview = screen.getByRole('region', { name: 'How coaches see it' });
    expect(within(preview).getByText(/Annual: .*6.*490.* \/ year, second role \+.*2.*990.* \/ year/)).toBeInTheDocument();
  });

  it('does not warn about an annual-only price change', async () => {
    const state = newAdminPlansState([paidPlan({ coachCount: 4 })]);
    const { user } = await openEditor(state);

    await retype(user, 'Per year', '6000');

    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });

  it('creates a plan with its yearly prices', async () => {
    const state = newAdminPlansState([]);
    const { user } = await openEditor(state, 'new');

    await user.type(await screen.findByRole('textbox', { name: 'Name · Czech' }), 'Elite');
    await user.type(screen.getByRole('textbox', { name: 'Name · English' }), 'Elite');
    await user.type(screen.getByRole('textbox', { name: 'Name · German' }), 'Elite');
    await user.type(screen.getByRole('textbox', { name: 'Code' }), 'elite');
    await user.type(screen.getByRole('textbox', { name: 'Per month' }), '1000');
    await user.type(screen.getByRole('textbox', { name: 'Per year' }), '10000');
    await user.type(screen.getByRole('textbox', { name: 'Second role per month' }), '500');
    await user.type(screen.getByRole('textbox', { name: 'Second role per year' }), '5000');
    await user.type(screen.getByRole('textbox', { name: 'Active clients' }), '10');
    await user.click(screen.getByRole('button', { name: 'Create plan' }));
    await screen.findByTestId('plans-list');

    expect(state.creates[0]).toMatchObject({
      code: 'elite',
      priceMinorUnits: 100000,
      secondRoleAddOnPriceMinorUnits: 50000,
      annualPriceMinorUnits: 1000000,
      annualSecondRoleAddOnPriceMinorUnits: 500000,
    });
    expect(state.creates[0]).not.toHaveProperty('billingInterval');
  });

  it('keeps the form and shows a translated toast when the provider cannot open the yearly price', async () => {
    const state = newAdminPlansState();
    const { user, router } = await openEditor(state);
    server.use(http.put(apiUrl('/admin/subscription-plans/:code'), () => problem(502, 'BILLING_PROVIDER_ERROR')));

    await retype(user, 'Per year', '6000');
    await save(user);

    expect(await screen.findByText("The payment provider didn't answer. Please try again in a moment.")).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Per year' })).toHaveValue('6000');
    expect(router.state.location.pathname).toBe('/admin/plans/pro');
  });
});
