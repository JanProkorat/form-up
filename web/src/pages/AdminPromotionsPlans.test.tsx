/**
 * '/admin/promotion-offers' — the plans a promotion can cover come from the plan catalog: the drawer offers the
 * active paid plans by their catalog names, and the table names the plans a promotion covers.
 */
import { describe, expect, it } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
import { PromotionDuration, PromotionMode, PromotionStatus } from '@/api/generated';
import type { PromotionDto } from '@/api/generated';
import { adminPlansKey } from '@/api/admin-subscription-plans';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { paidPlan } from '@/test/adminPlans';
import {
  newAdminPromotionsState,
  renderAdminPromotions,
  serveAdminPromotions,
  type AdminPromotionsState,
} from '@/test/adminPromotions';

type Rendered = ReturnType<typeof renderAdminPromotions>;

const promotion = (planCodes: string[]): PromotionDto => ({
  promotionId: 'promo-1',
  name: 'Spring',
  mode: PromotionMode.Automatic,
  duration: PromotionDuration.Forever,
  claimFrom: '2026-01-01T00:00:00Z',
  status: PromotionStatus.Active,
  planCodes,
  usedCount: 0,
});

async function openDrawer(state: AdminPromotionsState): Promise<Rendered> {
  serveAdminPromotions(state);
  const rendered = renderAdminPromotions();
  await rendered.user.click(await screen.findByRole('button', { name: 'New promotion' }));
  return rendered;
}

const plansGroup = () => screen.findByRole('group', { name: 'Plans' });

describe('AdminPromotionsPage plans from the catalog', () => {
  it('offers the active paid plans by catalog name, cheapest first, with Free and inactive plans left out', async () => {
    const state = newAdminPromotionsState();
    state.plans.push(paidPlan({ code: 'old', nameEn: 'Legacy', isActive: false }));
    await openDrawer(state);

    const group = await plansGroup();
    expect(within(group).getAllByText(/^\w+$/).map((label) => label.textContent)).toEqual(['Start', 'Pro', 'Studio']);
  });

  it('offers a plan added later and sends its code with the others', async () => {
    const state = newAdminPromotionsState();
    state.plans.push(paidPlan({ code: 'elite', nameEn: 'Elite', priceMinorUnits: 149900 }));
    const rendered = await openDrawer(state);

    expect(within(await plansGroup()).getByRole('checkbox', { name: 'Elite' })).toBeChecked();
    await rendered.user.type(screen.getByRole('textbox', { name: 'Name · Czech' }), 'Launch');
    await rendered.user.type(screen.getByRole('textbox', { name: 'Value' }), '20');
    await rendered.user.click(screen.getByRole('button', { name: 'Save promotion' }));
    await screen.findByText('Promotion saved.');

    expect(state.creates[0]).toMatchObject({ planCodes: ['start', 'pro', 'studio', 'elite'] });
  });

  it('says no paid plan is offered when the catalog has none', async () => {
    const state = newAdminPromotionsState();
    state.plans = state.plans.filter((plan) => plan.code === 'free');
    await openDrawer(state);

    expect(await screen.findByText(/No paid plans are offered/)).toBeInTheDocument();
  });

  it('shows a plans error with Retry instead of the form, and recovers on retry', async () => {
    const state = newAdminPromotionsState();
    state.plansFail = true;
    const rendered = await openDrawer(state);

    expect(await screen.findByText('Could not load the plans. Please try again.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save promotion' })).not.toBeInTheDocument();

    state.plansFail = false;
    await rendered.user.click(screen.getByRole('button', { name: 'Try again' }));

    expect(await within(await plansGroup()).findByRole('checkbox', { name: 'Pro' })).toBeChecked();
    expect(screen.getByRole('button', { name: 'Save promotion' })).toBeInTheDocument();
  });

  it('keeps the form and what was typed when a background refetch of the plans fails', async () => {
    const state = newAdminPromotionsState();
    const rendered = await openDrawer(state);
    let plansRequests = 0;
    server.use(
      http.get(apiUrl('/admin/subscription-plans'), () => {
        plansRequests += 1;
        return state.plansFail ? HttpResponse.json({}, { status: 500 }) : HttpResponse.json({ plans: state.plans });
      }),
    );
    await plansGroup();
    await rendered.user.type(screen.getByRole('textbox', { name: 'Name · Czech' }), 'Launch');

    state.plansFail = true;
    await act(() => rendered.queryClient.invalidateQueries({ queryKey: adminPlansKey }));

    await waitFor(() => expect(plansRequests).toBe(1));
    await waitFor(() => expect(rendered.queryClient.getQueryState(adminPlansKey)?.status).toBe('error'));
    expect(screen.queryByText('Could not load the plans. Please try again.')).not.toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Name · Czech' })).toHaveValue('Launch');
    expect(screen.getByRole('button', { name: 'Save promotion' })).toBeInTheDocument();
  });

  it('names the plans a promotion covers from the catalog', async () => {
    serveAdminPromotions(newAdminPromotionsState([promotion(['start', 'pro'])]));
    renderAdminPromotions();

    expect(await screen.findByText('Start and Pro')).toBeInTheDocument();
  });

  it('says All paid plans only when the promotion covers every offered plan', async () => {
    const state = newAdminPromotionsState([promotion(['start', 'pro', 'studio'])]);
    state.plans.push(paidPlan({ code: 'elite', nameEn: 'Elite', priceMinorUnits: 149900 }));
    serveAdminPromotions(state);
    renderAdminPromotions();

    expect(await screen.findByText('Start, Pro, and Studio')).toBeInTheDocument();
    expect(screen.queryByText('All paid plans')).not.toBeInTheDocument();
  });

  it('says All paid plans for a promotion covering the whole catalog', async () => {
    serveAdminPromotions(newAdminPromotionsState([promotion(['start', 'pro', 'studio'])]));
    renderAdminPromotions();

    expect(await screen.findByText('All paid plans')).toBeInTheDocument();
  });

  it('falls back to the plan codes and never says All paid plans when the plans fail to load', async () => {
    const state = newAdminPromotionsState([promotion(['start', 'pro', 'studio'])]);
    state.plansFail = true;
    serveAdminPromotions(state);
    renderAdminPromotions();

    expect(await screen.findByText('start, pro, and studio')).toBeInTheDocument();
    expect(screen.queryByText('All paid plans')).not.toBeInTheDocument();
  });
});
