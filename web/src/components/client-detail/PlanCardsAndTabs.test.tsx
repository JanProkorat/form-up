/**
 * Client detail has no disabled "coming soon" control: the tab row has no Workouts tab, the meal plan card's
 * "Open plan" links to the client's Nutrition tab, and the training card shows no such link.
 */
import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import type { ClientPlanItem, GetMySubscriptionResponse } from '@/api/generated';
import ClientDetailTabs from '@/components/client-detail/ClientDetailTabs';
import LatestWorkoutCard from '@/components/client-detail/LatestWorkoutCard';
import MealPlanCard from '@/components/client-detail/MealPlanCard';
import { CLIENT_ID, nutritionPlan, servePlan } from '@/test/clientDetail';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { renderWithProviders } from '@/test/render';

const PLAN: ClientPlanItem = { planId: 'plan-1', planType: 'Nutrition', status: 'Active', name: 'Cut' };

describe('ClientDetailTabs', () => {
  it('renders only live tabs: no Workouts tab and nothing disabled', async () => {
    const subscription: GetMySubscriptionResponse = { features: {} };
    server.use(http.get(apiUrl('/users/me/subscription'), () => HttpResponse.json(subscription)));
    renderWithProviders([
      {
        path: '/',
        element: <ClientDetailTabs activeTab="overview" onTabChange={() => {}} canViewNutritionPlans />,
      },
    ]);

    const tabs = await screen.findAllByRole('tab');

    expect(tabs.map((tab) => tab.textContent?.trim())).toEqual([
      'Overview',
      'Nutrition',
      'Photos',
      'Payment',
      'Automations',
      'Forms',
    ]);
    for (const tab of tabs) {
      expect(tab).toBeEnabled();
    }
  });
});

describe('plan cards', () => {
  it('links the meal plan card to the client Nutrition tab', async () => {
    servePlan('plan-1', () => nutritionPlan('plan-1'));
    renderWithProviders([
      {
        path: '/',
        element: <MealPlanCard clientId={CLIENT_ID} plan={PLAN} canView isPending={false} isError={false} />,
      },
    ]);

    const link = await screen.findByRole('link', { name: 'Open plan' });

    expect(link).toHaveAttribute('href', `/clients/${CLIENT_ID}/nutrition`);
  });

  it('shows no link on the meal plan card without an active plan', () => {
    renderWithProviders([
      {
        path: '/',
        element: <MealPlanCard clientId={CLIENT_ID} canView isPending={false} isError={false} />,
      },
    ]);

    expect(screen.queryByRole('link', { name: 'Open plan' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Open plan' })).not.toBeInTheDocument();
  });

  it('shows no Open plan control on the training card', () => {
    renderWithProviders([
      {
        path: '/',
        element: <LatestWorkoutCard canView={false} isPending={false} isError={false} />,
      },
    ]);

    expect(screen.queryByRole('link', { name: 'Open plan' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Open plan' })).not.toBeInTheDocument();
  });
});
