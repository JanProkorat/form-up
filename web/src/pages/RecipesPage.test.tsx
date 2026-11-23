import { beforeEach, describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import { RecipeMealType, RecipeVisibility } from '@/api/generated';
import type { GetRecipeResponse, RecipeSummaryDto } from '@/api/generated';
import RecipesPage from '@/pages/RecipesPage';
import { recordRequests, serveCoachShell } from '@/test/adminOnlyAccess';
import { HttpResponse, apiUrl, http } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { renderWithProviders } from '@/test/render';

const RECIPE_ID = 'e0000000-0000-0000-0000-000000000001';

/** A system recipe the way an admin-only caller gets it: the backend marks it as owned by the caller. */
function systemRecipe(isOwnedByCurrentUser: boolean): RecipeSummaryDto {
  return {
    recipeId: RECIPE_ID,
    name: 'System Porridge',
    foodCount: 1,
    totalNutrients: { kcal: 300, protein: 10, carbs: 50, fat: 5 },
    visibility: RecipeVisibility.Public,
    isOwnedByCurrentUser,
    isSystem: true,
    version: 1,
    servings: 1,
    mealTypes: [RecipeMealType.Breakfast],
    foodCategories: [],
    tags: [],
  };
}

function recipeDetail(isOwnedByCurrentUser: boolean): GetRecipeResponse {
  return {
    ...systemRecipe(isOwnedByCurrentUser),
    foods: [],
    steps: [],
    galleryImageUrls: [],
    dietaryPreferences: [],
    allergens: [],
  };
}

/** Serves the list, the detail read and the delete; records what the page sends. */
function serveLibrary(isOwnedByCurrentUser: boolean) {
  const deleted: string[] = [];
  server.use(
    http.get(apiUrl('/recipes'), () =>
      HttpResponse.json({ recipes: [systemRecipe(isOwnedByCurrentUser)], totalCount: 1, page: 1, pageSize: 20 }),
    ),
    http.get(apiUrl(`/recipes/${RECIPE_ID}`), () => HttpResponse.json(recipeDetail(isOwnedByCurrentUser))),
    http.delete(apiUrl(`/recipes/${RECIPE_ID}`), () => {
      deleted.push(RECIPE_ID);
      return new HttpResponse(null, { status: 204 });
    }),
  );
  return { deleted };
}

describe('RecipesPage for an admin-only account', () => {
  beforeEach(() => {
    localStorage.removeItem('recipes-view-mode');
  });

  it('lets the admin create, edit and delete a system recipe with no tag UI or tag request', async () => {
    const { deleted } = serveLibrary(true);
    const recorder = recordRequests();
    const { user } = renderWithProviders(<RecipesPage />, { auth: { roles: ['Admin'] } });

    // Owner and tag filters make no sense on a list of system recipes only.
    expect(await screen.findByText('System Porridge')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /owner/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /tags/i })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'New Recipe' })).toBeInTheDocument();

    await user.click(screen.getByText('System Porridge'));

    const drawer = await screen.findByRole('dialog', { name: 'Edit Recipe' });
    expect(await within(drawer).findByRole('button', { name: 'Save Recipe' })).toBeInTheDocument();
    expect(within(drawer).queryByText('My Tags')).not.toBeInTheDocument();
    expect(within(drawer).queryByRole('switch')).not.toBeInTheDocument();

    await user.click(within(drawer).getByRole('button', { name: 'Delete' }));
    const confirm = await screen.findByRole('dialog', { name: 'Delete recipe' });
    await user.click(within(confirm).getByRole('button', { name: 'Delete' }));

    await waitFor(() => expect(deleted).toEqual([RECIPE_ID]));
    recorder.stop();
    expect(recorder.requests.filter((request) => request.includes('tag'))).toEqual([]);
    expect(recorder.requests).not.toContain('GET /users/me/roles');
  });

  it('ignores an owner or tag filter left in the URL', async () => {
    const seen: string[] = [];
    server.use(
      http.get(apiUrl('/recipes'), ({ request }) => {
        seen.push(new URL(request.url).search);
        return HttpResponse.json({ recipes: [systemRecipe(true)], totalCount: 1, page: 1, pageSize: 20 });
      }),
    );
    renderWithProviders(<RecipesPage />, {
      auth: { roles: ['Admin'] },
      initialEntries: ['/?owner=Mine&tags=11111111-1111-1111-1111-111111111111'],
    });

    expect(await screen.findByText('System Porridge')).toBeInTheDocument();
    expect(seen).toHaveLength(1);
    expect(seen[0]).not.toMatch(/owner|tag/i);
  });
});

describe('RecipesPage for a nutritionist', () => {
  it('still shows a system recipe read-only, with the tag section', async () => {
    serveLibrary(false);
    serveCoachShell(['Nutritionist']);
    server.use(http.get(apiUrl('/trainer/food-tags'), () => HttpResponse.json({ tags: [] })));
    const { user } = renderWithProviders(<RecipesPage />, { auth: { roles: ['Nutritionist'] } });

    await user.click(await screen.findByText('System Porridge'));

    const drawer = await screen.findByRole('dialog', { name: 'Recipe' });
    expect(await within(drawer).findByText('My Tags')).toBeInTheDocument();
    expect(within(drawer).queryByRole('button', { name: 'Save Recipe' })).not.toBeInTheDocument();
    expect(within(drawer).queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();
  });
});
