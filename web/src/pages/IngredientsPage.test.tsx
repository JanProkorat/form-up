import { beforeEach, describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import { FoodCategory, FoodVisibility } from '@/api/generated';
import type { FoodSummary } from '@/api/generated';
import IngredientsPage from '@/pages/IngredientsPage';
import { recordRequests, serveCoachShell } from '@/test/adminOnlyAccess';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { renderWithProviders } from '@/test/render';

const FOOD_ID = 'f0000000-0000-0000-0000-000000000001';

function systemFood(): FoodSummary {
  return {
    foodId: FOOD_ID,
    name: 'System Oats',
    rawName: 'System Oats',
    category: FoodCategory.Fruit,
    nutrientValue: { kcal: 370, protein: 13, carbs: 60, fat: 7, fiber: 10 },
    commonServings: [{ label: 'portion', weightGrams: 40 }],
    visibility: FoodVisibility.Public,
    isSystem: true,
    // Foods carry no owner marker for the caller on system rows.
    isOwnedByCurrentUser: false,
    tags: [],
    allergens: [],
    dietaryPreferences: [],
    galleryImageUrls: [],
  };
}

/** Serves the list, the detail read and the delete; records what the library page sends. */
function serveLibrary() {
  const deleted: string[] = [];
  server.use(
    http.get(apiUrl('/foods/search'), () =>
      HttpResponse.json({ foods: [systemFood()], totalCount: 1, page: 1, pageSize: 20 }),
    ),
    http.get(apiUrl(`/foods/${FOOD_ID}`), () => HttpResponse.json(systemFood())),
    http.delete(apiUrl(`/foods/${FOOD_ID}`), () => {
      deleted.push(FOOD_ID);
      return new HttpResponse(null, { status: 204 });
    }),
  );
  return { deleted };
}

describe('IngredientsPage for an admin-only account', () => {
  beforeEach(() => {
    localStorage.removeItem('ingredients-view-mode');
  });

  it('lets the admin create, edit and delete a system ingredient with no tag UI or tag request', async () => {
    const { deleted } = serveLibrary();
    const recorder = recordRequests();
    const { user } = renderWithProviders(<IngredientsPage />, { auth: { roles: ['Admin'] } });

    // The list holds only system items; owner and tag filters make no sense and are not offered.
    expect(await screen.findByText('System Oats')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /owner/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /tags/i })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'New Ingredient' })).toBeInTheDocument();

    await user.click(screen.getByText('System Oats'));

    const drawer = await screen.findByRole('dialog', { name: 'Edit Ingredient' });
    expect(within(drawer).getByRole('button', { name: 'Save Ingredient' })).toBeInTheDocument();
    expect(within(drawer).queryByText('My Tags')).not.toBeInTheDocument();
    expect(within(drawer).queryByRole('switch')).not.toBeInTheDocument();

    await user.click(within(drawer).getByRole('button', { name: 'Delete' }));
    const confirm = await screen.findByRole('dialog', { name: 'Delete ingredient' });
    await user.click(within(confirm).getByRole('button', { name: 'Delete' }));

    await waitFor(() => expect(deleted).toEqual([FOOD_ID]));
    recorder.stop();
    expect(recorder.requests.filter((request) => request.includes('tag'))).toEqual([]);
    expect(recorder.requests).not.toContain('GET /users/me/roles');
  });

  it('opens the create drawer for the admin', async () => {
    serveLibrary();
    const { user } = renderWithProviders(<IngredientsPage />, { auth: { roles: ['Admin'] } });

    await user.click(await screen.findByRole('button', { name: 'New Ingredient' }));

    const drawer = await screen.findByRole('dialog', { name: 'New Ingredient' });
    expect(within(drawer).getByLabelText(/^Name/)).toBeEnabled();
  });
});

describe('IngredientsPage for a nutritionist', () => {
  it('still shows a system ingredient read-only, with the tag section', async () => {
    serveLibrary();
    serveCoachShell(['Nutritionist']);
    server.use(http.get(apiUrl('/trainer/food-tags'), () => HttpResponse.json({ tags: [] })));
    const { user } = renderWithProviders(<IngredientsPage />, { auth: { roles: ['Nutritionist'] } });

    await user.click(await screen.findByText('System Oats'));

    const drawer = await screen.findByRole('dialog', { name: 'Ingredient' });
    expect(within(drawer).queryByRole('button', { name: 'Save Ingredient' })).not.toBeInTheDocument();
    expect(within(drawer).queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();
    expect(within(drawer).getAllByRole('button', { name: 'Close' }).length).toBeGreaterThan(0);
    expect(within(drawer).getByText('My Tags')).toBeInTheDocument();
  });
});
