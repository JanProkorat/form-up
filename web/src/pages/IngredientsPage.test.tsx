import { beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import { FoodCategory, FoodVisibility } from '@/api/generated';
import type { FoodSummary } from '@/api/generated';
import IngredientsPage from '@/pages/IngredientsPage';
import { recordRequests, serveCoachShell } from '@/test/adminOnlyAccess';
import { HttpResponse, apiUrl, http, problem } from '@/test/msw/handlers';
import { server } from '@/test/msw/server';
import { renderWithProviders } from '@/test/render';
import { useToastStore } from '@/stores/toast';

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

const FOREIGN_ID = 'f0000000-0000-0000-0000-000000000002';

/** Another coach's shared ingredient: not a system row, not owned by the caller. */
function foreignFood(isHiddenByCurrentUser = false): FoodSummary {
  return {
    ...systemFood(),
    foodId: FOREIGN_ID,
    name: 'Foreign Oats',
    rawName: 'Foreign Oats',
    isSystem: false,
    isOwnedByCurrentUser: false,
    isHiddenByCurrentUser,
  };
}

/** Stateful fake of the library: hide / unhide / copy change what the next search returns. */
function serveForeignLibrary() {
  const hiddenIds = new Set<string>();
  const copies: FoodSummary[] = [];
  const state = {
    searches: [] as URLSearchParams[],
    copyBodies: [] as unknown[],
    hideBodies: [] as unknown[],
    unhideBodies: [] as unknown[],
  };
  server.use(
    http.get(apiUrl('/foods/search'), ({ request }) => {
      const params = new URL(request.url).searchParams;
      state.searches.push(params);
      const foods = params.get('hidden') === 'true'
        ? [...hiddenIds].map(() => foreignFood(true))
        : [...(hiddenIds.has(FOREIGN_ID) ? [] : [foreignFood()]), ...copies];
      return HttpResponse.json({ foods, totalCount: foods.length, page: 1, pageSize: 25 });
    }),
    http.get(apiUrl(`/foods/${FOREIGN_ID}`), () => HttpResponse.json(foreignFood(hiddenIds.has(FOREIGN_ID)))),
    http.get(apiUrl('/trainer/food-tags'), () => HttpResponse.json({ tags: [] })),
    http.post(apiUrl(`/foods/${FOREIGN_ID}/copy`), async ({ request }) => {
      state.copyBodies.push(await request.json());
      copies.push({ ...foreignFood(), foodId: 'f0000000-0000-0000-0000-000000000003', isOwnedByCurrentUser: true });
      return HttpResponse.json(copies[copies.length - 1], { status: 201 });
    }),
    http.put(apiUrl(`/foods/${FOREIGN_ID}/hide`), async ({ request }) => {
      state.hideBodies.push(await request.json());
      hiddenIds.add(FOREIGN_ID);
      return new HttpResponse(null, { status: 204 });
    }),
    http.put(apiUrl(`/foods/${FOREIGN_ID}/unhide`), async ({ request }) => {
      state.unhideBodies.push(await request.json());
      hiddenIds.delete(FOREIGN_ID);
      return new HttpResponse(null, { status: 204 });
    }),
  );
  return state;
}

describe('IngredientsPage copy and hide', () => {
  beforeEach(() => {
    localStorage.removeItem('ingredients-view-mode');
    useToastStore.setState({ toasts: [] });
    serveCoachShell(['Nutritionist']);
  });

  it("opens another coach's ingredient read-only", async () => {
    serveForeignLibrary();
    const { user } = renderWithProviders(<IngredientsPage />, { auth: { roles: ['Nutritionist'] } });

    await user.click(await screen.findByText('Foreign Oats'));

    const drawer = await screen.findByRole('dialog', { name: 'Ingredient' });
    expect(within(drawer).queryByRole('button', { name: 'Save Ingredient' })).not.toBeInTheDocument();
    expect(within(drawer).queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();
    expect(within(drawer).getAllByRole('button', { name: 'Close' }).length).toBeGreaterThan(0);
  });

  it('copies into the own library with an empty JSON body, toasts, and refreshes the list', async () => {
    const state = serveForeignLibrary();
    const { user } = renderWithProviders(<IngredientsPage />, { auth: { roles: ['Nutritionist'] } });

    await user.click(await screen.findByText('Foreign Oats'));
    const drawer = await screen.findByRole('dialog', { name: 'Ingredient' });
    const searchesBefore = state.searches.length;
    await user.click(within(drawer).getByRole('button', { name: 'Copy to my ingredients' }));

    await waitFor(() => expect(state.copyBodies).toEqual([{}]));
    await vi.waitFor(() => expect(useToastStore.getState().toasts[0]?.message).toBe('Ingredient copied to your library.'));
    // The refetch brings the new private copy into the list next to the original.
    await waitFor(() => expect(screen.getAllByText('Foreign Oats')).toHaveLength(2));
    expect(state.searches.length).toBeGreaterThan(searchesBefore);
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Ingredient' })).not.toBeInTheDocument());
  });

  it('hides a foreign ingredient with an empty JSON body and refreshes the list', async () => {
    const state = serveForeignLibrary();
    const { user } = renderWithProviders(<IngredientsPage />, { auth: { roles: ['Nutritionist'] } });

    await user.click(await screen.findByText('Foreign Oats'));
    const drawer = await screen.findByRole('dialog', { name: 'Ingredient' });
    expect(within(drawer).queryByRole('button', { name: 'Unhide' })).not.toBeInTheDocument();
    await user.click(within(drawer).getByRole('button', { name: 'Hide' }));

    await waitFor(() => expect(state.hideBodies).toEqual([{}]));
    await vi.waitFor(() => expect(useToastStore.getState().toasts[0]?.message).toBe('Ingredient hidden.'));
    await waitFor(() => expect(screen.queryByText('Foreign Oats')).not.toBeInTheDocument());
  });

  it('shows hidden ingredients with a badge and lets the coach unhide them', async () => {
    const state = serveForeignLibrary();
    const { user } = renderWithProviders(<IngredientsPage />, { auth: { roles: ['Nutritionist'] } });

    // Hide it first, so the show-hidden view has something to list.
    await user.click(await screen.findByText('Foreign Oats'));
    await user.click(within(await screen.findByRole('dialog', { name: 'Ingredient' })).getByRole('button', { name: 'Hide' }));
    await waitFor(() => expect(screen.queryByText('Foreign Oats')).not.toBeInTheDocument());
    expect(state.searches.at(-1)?.get('hidden')).toBe('false');

    await user.click(screen.getByRole('button', { name: /Show hidden/ }));

    expect(await screen.findByText('Foreign Oats')).toBeInTheDocument();
    expect(screen.getByText('Hidden')).toBeInTheDocument();
    expect(state.searches.at(-1)?.get('hidden')).toBe('true');

    await user.click(screen.getByText('Foreign Oats'));
    const drawer = await screen.findByRole('dialog', { name: 'Ingredient' });
    expect(within(drawer).queryByRole('button', { name: 'Hide' })).not.toBeInTheDocument();
    const searchesBefore = state.searches.length;
    await user.click(within(drawer).getByRole('button', { name: 'Unhide' }));

    await waitFor(() => expect(state.unhideBodies).toEqual([{}]));
    await vi.waitFor(() => expect(useToastStore.getState().toasts.at(-1)?.message).toBe('Ingredient is visible again.'));
    await waitFor(() => expect(state.searches.length).toBeGreaterThan(searchesBefore));
    await waitFor(() => expect(screen.queryByText('Foreign Oats')).not.toBeInTheDocument());
  });

  it('shows a translated error toast when hiding an own ingredient is refused, and stays on the page', async () => {
    serveForeignLibrary();
    server.use(http.put(apiUrl(`/foods/${FOREIGN_ID}/hide`), () => problem(400, 'FOOD_HIDE_OWN_NOT_ALLOWED')));
    const { user, router } = renderWithProviders(<IngredientsPage />, { auth: { roles: ['Nutritionist'] } });

    await user.click(await screen.findByText('Foreign Oats'));
    const drawer = await screen.findByRole('dialog', { name: 'Ingredient' });
    await user.click(within(drawer).getByRole('button', { name: 'Hide' }));

    await vi.waitFor(() => expect(useToastStore.getState().toasts[0]?.message).toBe("You can't hide your own ingredients."));
    expect(useToastStore.getState().toasts[0].type).toBe('error');
    // The drawer and the page stay as they were: nothing reloads or navigates.
    expect(screen.getByRole('dialog', { name: 'Ingredient' })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/');
  });
});

describe('IngredientsPage copy and hide for an admin-only account', () => {
  it('offers no show-hidden chip and ignores a hidden filter left in the URL', async () => {
    const searches: URLSearchParams[] = [];
    server.use(
      http.get(apiUrl('/foods/search'), ({ request }) => {
        searches.push(new URL(request.url).searchParams);
        return HttpResponse.json({ foods: [systemFood()], totalCount: 1, page: 1, pageSize: 25 });
      }),
      http.get(apiUrl(`/foods/${FOOD_ID}`), () => HttpResponse.json(systemFood())),
    );
    const { user } = renderWithProviders(<IngredientsPage />, {
      auth: { roles: ['Admin'] },
      initialEntries: ['/?hidden=1'],
    });

    await user.click(await screen.findByText('System Oats'));
    const drawer = await screen.findByRole('dialog', { name: 'Edit Ingredient' });

    expect(screen.queryByRole('button', { name: /Show hidden/ })).not.toBeInTheDocument();
    for (const name of ['Copy to my ingredients', 'Hide', 'Unhide']) {
      expect(within(drawer).queryByRole('button', { name })).not.toBeInTheDocument();
    }
    expect(searches.every((params) => params.get('hidden') === 'false')).toBe(true);
  });
});
