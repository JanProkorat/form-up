import { beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import { RecipeMealType, RecipeVisibility } from '@/api/generated';
import type { GetRecipeResponse, RecipeSummaryDto } from '@/api/generated';
import RecipesPage from '@/pages/RecipesPage';
import { recordRequests, serveCoachShell } from '@/test/adminOnlyAccess';
import { HttpResponse, apiUrl, http, problem } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { renderWithProviders } from '@/test/render';
import { useToastStore } from '@/stores/toast';

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

const FOREIGN_ID = 'e0000000-0000-0000-0000-000000000002';

/** Another coach's shared recipe: not a system row, not owned by the caller. */
function foreignRecipe(isHiddenByCurrentUser = false): RecipeSummaryDto {
  return {
    ...systemRecipe(false),
    recipeId: FOREIGN_ID,
    name: 'Foreign Porridge',
    isSystem: false,
    isHiddenByCurrentUser,
  };
}

/** Stateful fake of the library: hide / unhide / copy change what the next search returns. */
function serveForeignLibrary() {
  const hiddenIds = new Set<string>();
  const copies: RecipeSummaryDto[] = [];
  const state = {
    searches: [] as URLSearchParams[],
    copyBodies: [] as unknown[],
    hideBodies: [] as unknown[],
    unhideBodies: [] as unknown[],
  };
  server.use(
    http.get(apiUrl('/recipes'), ({ request }) => {
      const params = new URL(request.url).searchParams;
      state.searches.push(params);
      const recipes = params.get('hidden') === 'true'
        ? [...hiddenIds].map(() => foreignRecipe(true))
        : [...(hiddenIds.has(FOREIGN_ID) ? [] : [foreignRecipe()]), ...copies];
      return HttpResponse.json({ recipes, totalCount: recipes.length, page: 1, pageSize: 25 });
    }),
    http.get(apiUrl(`/recipes/${FOREIGN_ID}`), () => HttpResponse.json({ ...recipeDetail(false), ...foreignRecipe() })),
    http.get(apiUrl('/trainer/food-tags'), () => HttpResponse.json({ tags: [] })),
    http.post(apiUrl(`/recipes/${FOREIGN_ID}/copy`), async ({ request }) => {
      state.copyBodies.push(await request.json());
      copies.push({ ...foreignRecipe(), recipeId: 'e0000000-0000-0000-0000-000000000003', isOwnedByCurrentUser: true });
      return HttpResponse.json(copies[copies.length - 1], { status: 201 });
    }),
    http.put(apiUrl(`/recipes/${FOREIGN_ID}/hide`), async ({ request }) => {
      state.hideBodies.push(await request.json());
      hiddenIds.add(FOREIGN_ID);
      return new HttpResponse(null, { status: 204 });
    }),
    http.put(apiUrl(`/recipes/${FOREIGN_ID}/unhide`), async ({ request }) => {
      state.unhideBodies.push(await request.json());
      hiddenIds.delete(FOREIGN_ID);
      return new HttpResponse(null, { status: 204 });
    }),
  );
  return state;
}

describe('RecipesPage copy and hide', () => {
  beforeEach(() => {
    localStorage.removeItem('recipes-view-mode');
    useToastStore.setState({ toasts: [] });
    serveCoachShell(['Nutritionist']);
  });

  it("opens another coach's recipe read-only", async () => {
    serveForeignLibrary();
    const { user } = renderWithProviders(<RecipesPage />, { auth: { roles: ['Nutritionist'] } });

    await user.click(await screen.findByText('Foreign Porridge'));

    const drawer = await screen.findByRole('dialog', { name: 'Recipe' });
    expect(await within(drawer).findByRole('button', { name: 'Copy to my recipes' })).toBeInTheDocument();
    expect(within(drawer).queryByRole('button', { name: 'Save Recipe' })).not.toBeInTheDocument();
    expect(within(drawer).queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();
    expect(within(drawer).getAllByRole('button', { name: 'Close' }).length).toBeGreaterThan(0);
  });

  it('copies into the own library with an empty JSON body, toasts, and refreshes the list', async () => {
    const state = serveForeignLibrary();
    const { user } = renderWithProviders(<RecipesPage />, { auth: { roles: ['Nutritionist'] } });

    await user.click(await screen.findByText('Foreign Porridge'));
    const drawer = await screen.findByRole('dialog', { name: 'Recipe' });
    const searchesBefore = state.searches.length;
    await user.click(await within(drawer).findByRole('button', { name: 'Copy to my recipes' }));

    await waitFor(() => expect(state.copyBodies).toEqual([{}]));
    await vi.waitFor(() => expect(useToastStore.getState().toasts[0]?.message).toBe('Recipe copied to your library.'));
    await waitFor(() => expect(screen.getAllByText('Foreign Porridge')).toHaveLength(2));
    expect(state.searches.length).toBeGreaterThan(searchesBefore);
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Recipe' })).not.toBeInTheDocument());
  });

  it('hides a foreign recipe with an empty JSON body and refreshes the list', async () => {
    const state = serveForeignLibrary();
    const { user } = renderWithProviders(<RecipesPage />, { auth: { roles: ['Nutritionist'] } });

    await user.click(await screen.findByText('Foreign Porridge'));
    const drawer = await screen.findByRole('dialog', { name: 'Recipe' });
    await user.click(await within(drawer).findByRole('button', { name: 'Hide' }));

    await waitFor(() => expect(state.hideBodies).toEqual([{}]));
    await vi.waitFor(() => expect(useToastStore.getState().toasts[0]?.message).toBe('Recipe hidden.'));
    await waitFor(() => expect(screen.queryByText('Foreign Porridge')).not.toBeInTheDocument());
  });

  it('shows hidden recipes with a badge and lets the coach unhide them', async () => {
    const state = serveForeignLibrary();
    const { user } = renderWithProviders(<RecipesPage />, { auth: { roles: ['Nutritionist'] } });

    await user.click(await screen.findByText('Foreign Porridge'));
    await user.click(
      await within(await screen.findByRole('dialog', { name: 'Recipe' })).findByRole('button', { name: 'Hide' }),
    );
    await waitFor(() => expect(screen.queryByText('Foreign Porridge')).not.toBeInTheDocument());
    expect(state.searches.at(-1)?.get('hidden')).toBe('false');

    await user.click(screen.getByRole('button', { name: /Show hidden/ }));

    expect(await screen.findByText('Foreign Porridge')).toBeInTheDocument();
    expect(screen.getByText('Hidden')).toBeInTheDocument();
    expect(state.searches.at(-1)?.get('hidden')).toBe('true');

    await user.click(screen.getByText('Foreign Porridge'));
    const drawer = await screen.findByRole('dialog', { name: 'Recipe' });
    const unhide = await within(drawer).findByRole('button', { name: 'Unhide' });
    expect(within(drawer).queryByRole('button', { name: 'Hide' })).not.toBeInTheDocument();
    const searchesBefore = state.searches.length;
    await user.click(unhide);

    await waitFor(() => expect(state.unhideBodies).toEqual([{}]));
    await vi.waitFor(() => expect(useToastStore.getState().toasts.at(-1)?.message).toBe('Recipe is visible again.'));
    await waitFor(() => expect(state.searches.length).toBeGreaterThan(searchesBefore));
    await waitFor(() => expect(screen.queryByText('Foreign Porridge')).not.toBeInTheDocument());
  });

  it('shows a translated error toast when hiding an own recipe is refused, and stays on the page', async () => {
    serveForeignLibrary();
    server.use(http.put(apiUrl(`/recipes/${FOREIGN_ID}/hide`), () => problem(400, 'RECIPE_HIDE_OWN_NOT_ALLOWED')));
    const { user, router } = renderWithProviders(<RecipesPage />, { auth: { roles: ['Nutritionist'] } });

    await user.click(await screen.findByText('Foreign Porridge'));
    const drawer = await screen.findByRole('dialog', { name: 'Recipe' });
    await user.click(await within(drawer).findByRole('button', { name: 'Hide' }));

    await vi.waitFor(() => expect(useToastStore.getState().toasts[0]?.message).toBe("You can't hide your own recipes."));
    expect(useToastStore.getState().toasts[0].type).toBe('error');
    expect(screen.getByRole('dialog', { name: 'Recipe' })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/');
  });
});

describe('RecipesPage copy and hide for an admin-only account', () => {
  it('offers no show-hidden chip and ignores a hidden filter left in the URL', async () => {
    const searches: URLSearchParams[] = [];
    server.use(
      http.get(apiUrl('/recipes'), ({ request }) => {
        searches.push(new URL(request.url).searchParams);
        return HttpResponse.json({ recipes: [systemRecipe(true)], totalCount: 1, page: 1, pageSize: 25 });
      }),
      http.get(apiUrl(`/recipes/${RECIPE_ID}`), () => HttpResponse.json(recipeDetail(true))),
    );
    const { user } = renderWithProviders(<RecipesPage />, {
      auth: { roles: ['Admin'] },
      initialEntries: ['/?hidden=1'],
    });

    await user.click(await screen.findByText('System Porridge'));
    const drawer = await screen.findByRole('dialog', { name: 'Edit Recipe' });
    await within(drawer).findByRole('button', { name: 'Save Recipe' });

    expect(screen.queryByRole('button', { name: /Show hidden/ })).not.toBeInTheDocument();
    for (const name of ['Copy to my recipes', 'Hide', 'Unhide']) {
      expect(within(drawer).queryByRole('button', { name })).not.toBeInTheDocument();
    }
    expect(searches.every((params) => params.get('hidden') === 'false')).toBe(true);
  });
});
