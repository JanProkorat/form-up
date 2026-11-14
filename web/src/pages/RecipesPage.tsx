import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Lock, Plus, Search } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { useAuthStore } from '@/stores/auth';
import { getErrorStatus } from '@/lib/api-errors';
import { isSubscriptionRequired } from '@/lib/subscriptionErrors';
import { useSubscription } from '@/hooks/useSubscriptionQueries';
import PaywallPanel from '@/components/subscription/PaywallPanel';
import { useRecipeListParams } from '@/hooks/useRecipeListParams';
import { useRecipes } from '@/hooks/useRecipesQueries';
import { useDebouncedValue } from '@/hooks/useDebouncedValue';
import { DietaryPreference, RecipeMealType, type RecipeSummaryDto } from '@/api/recipe-types';
import OptionFilterPopover from '@/components/library/OptionFilterPopover';
import OwnerFilterPopover from '@/components/library/OwnerFilterPopover';
import LibraryTagFilterPopover from '@/components/library/LibraryTagFilterPopover';
import PageHeader from '@/components/library/PageHeader';
import Pagination from '@/components/library/Pagination';
import ViewModeToggle from '@/components/library/ViewModeToggle';
import { RECIPES_VIEW_MODE_KEY, useViewMode } from '@/hooks/useViewMode';
import RecipesGrid from '@/components/recipes/RecipesGrid';
import RecipesTable from '@/components/recipes/RecipesTable';
import RecipeDrawer from '@/components/recipes/RecipeDrawer';

const MEAL_TYPE_VALUES = Object.values(RecipeMealType);
const DIETARY_PREFERENCE_VALUES = Object.values(DietaryPreference);

/**
 * The trainer-portal Recipes page — search, Meal type / Dietary preference /
 * Owner / Tags filter pills, the recipes table, pagination, and the create/edit/view
 * drawer. Mirrors `IngredientsPage`. Every /recipes endpoint is
 * nutritionist-only, so a trainer-only coach sees an explanatory state
 * instead of a failed load.
 */
export default function RecipesPage() {
  const { t } = useTranslation();
  const {
    filters,
    setSearch,
    setMealTypes,
    setDietaryPreferences,
    setOwners,
    setTags,
    cycleSort,
    setPage,
    clearFilters,
  } = useRecipeListParams();
  const roles = useAuthStore((s) => s.user?.roles ?? []);
  const isNutritionist = roles.includes('Nutritionist');

  const { viewMode, setViewMode } = useViewMode(RECIPES_VIEW_MODE_KEY, 'grid');

  const [searchInput, setSearchInput] = useState(filters.search);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [selectedRecipe, setSelectedRecipe] = useState<RecipeSummaryDto | null>(null);

  // Re-sync the local input when the URL's search value changes from
  // elsewhere (back/forward, clearFilters) — "adjust state during render",
  // same pattern as IngredientsPage.
  const [syncedSearch, setSyncedSearch] = useState(filters.search);
  if (filters.search !== syncedSearch) {
    setSyncedSearch(filters.search);
    setSearchInput(filters.search);
  }

  useDebouncedValue(searchInput, 300, () => {
    if (searchInput !== filters.search) {
      setSearch(searchInput);
    }
  });

  const { isEntitled } = useSubscription();
  const planAllows = isEntitled('canUseRecipes');
  const recipesQuery = useRecipes(filters, isNutritionist && planAllows);
  // isEntitled is true while the plan loads, so a refusal from the server locks the page as well.
  const isLocked =
    isNutritionist &&
    (!planAllows || (recipesQuery.isError && isSubscriptionRequired(recipesQuery.error)));
  const isForbidden =
    !isNutritionist || (!isLocked && recipesQuery.isError && getErrorStatus(recipesQuery.error) === 403);

  const recipes = recipesQuery.data?.recipes ?? [];
  const totalCount = recipesQuery.data?.totalCount ?? 0;
  const hasActiveFilter =
    filters.search !== '' ||
    filters.mealTypes.length > 0 ||
    filters.dietaryPreferences.length > 0 ||
    filters.owners.length > 0 ||
    filters.tags.length > 0;
  const showEmptyState = !recipesQuery.isPending && !recipesQuery.isError && totalCount === 0 && !hasActiveFilter;

  function openCreateDrawer() {
    setSelectedRecipe(null);
    setDrawerOpen(true);
  }

  function openRowDrawer(recipe: RecipeSummaryDto) {
    setSelectedRecipe(recipe);
    setDrawerOpen(true);
  }

  // Creating (selectedRecipe === null) is always editable; the "not owned"
  // half only applies to an EXISTING recipe (system row, or a shared one).
  const drawerReadOnly = selectedRecipe !== null && !selectedRecipe.isOwnedByCurrentUser;

  if (isLocked) {
    return (
      <div className="flex h-full flex-col gap-4">
        <PageHeader eyebrow={t('library.eyebrow')} title={t('recipes.title')} />
        <PaywallPanel feature="recipes" />
      </div>
    );
  }

  if (isForbidden) {
    return (
      <div className="flex h-full flex-col gap-4">
        <PageHeader eyebrow={t('library.eyebrow')} title={t('recipes.title')} />
        <div className="flex flex-1 flex-col items-center justify-center gap-3 text-center">
          <Lock className="size-8 text-muted-foreground" aria-hidden="true" />
          <p className="text-copy font-bold text-ink">{t('recipes.nutritionistsOnly.title')}</p>
          <p className="max-w-search text-body text-muted-foreground">{t('recipes.nutritionistsOnly.body')}</p>
        </div>
      </div>
    );
  }

  return (
    <div className="flex h-full flex-col gap-4">
      <PageHeader eyebrow={t('library.eyebrow')} title={t('recipes.title')} />

      <div className="flex flex-wrap items-center gap-3">
        <div className="relative min-w-40 max-w-search flex-1">
          <Search
            className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground"
            aria-hidden="true"
          />
          <Input
            type="search"
            value={searchInput}
            onChange={(event) => setSearchInput(event.target.value)}
            placeholder={t('recipes.searchPlaceholder')}
            className="h-9 pl-9"
            aria-label={t('recipes.searchPlaceholder')}
          />
        </div>
        {/* The design's empty state has no filters; with nothing to narrow they only add noise. */}
        {!showEmptyState && (
          <>
            <OptionFilterPopover
              label={t('recipes.filters.mealType')}
              options={MEAL_TYPE_VALUES.map((value) => ({ value, label: t(`recipes.mealType.${value}`) }))}
              selected={filters.mealTypes}
              onChange={setMealTypes}
            />
            <OptionFilterPopover
              label={t('recipes.filters.dietaryPreference')}
              options={DIETARY_PREFERENCE_VALUES.map((value) => ({
                value,
                label: t(`ingredients.dietaryPreference.${value}`),
              }))}
              selected={filters.dietaryPreferences}
              onChange={setDietaryPreferences}
            />
            <OwnerFilterPopover selectedOwners={filters.owners} onChange={setOwners} />
            <LibraryTagFilterPopover selectedTagIds={filters.tags} onChange={setTags} />
          </>
        )}
        <div className="ml-auto flex items-center gap-3">
          {!showEmptyState && <ViewModeToggle value={viewMode} onChange={setViewMode} />}
          <Button type="button" size="lg" className="gap-1.75 px-3.5 font-semibold" onClick={openCreateDrawer}>
            <Plus aria-hidden="true" />
            {t('recipes.newRecipe')}
          </Button>
        </div>
      </div>

      {showEmptyState ? (
        <div className="flex flex-1 flex-col items-center justify-center gap-4 text-center">
          <div className="flex size-14 items-center justify-center rounded-full border border-border bg-card">
            <Plus className="size-6 text-muted-foreground" aria-hidden="true" />
          </div>
          <div className="flex flex-col gap-1">
            <p className="text-copy font-bold text-ink">{t('recipes.empty.title')}</p>
            <p className="text-body text-muted-foreground">{t('recipes.empty.hint')}</p>
          </div>
          <Button type="button" onClick={openCreateDrawer}>
            <Plus className="size-4" aria-hidden="true" />
            {t('recipes.empty.create')}
          </Button>
        </div>
      ) : (
        <div
          className={
            viewMode === 'grid'
              ? 'flex min-h-0 flex-1 flex-col'
              : 'flex min-h-0 flex-1 flex-col overflow-hidden rounded-2xl border border-line bg-card shadow-panel'
          }
        >
          {/* The grid scroller has a 1-unit pad/negative margin so card focus rings are not clipped. */}
          <div
            className={
              viewMode === 'grid'
                ? '-m-1 flex-1 overflow-y-auto p-1'
                : 'flex-1 overflow-y-auto [&_[data-slot=table-container]]:overflow-visible'
            }
          >
            {viewMode === 'grid' ? (
              <RecipesGrid
                recipes={recipes}
                isPending={recipesQuery.isPending}
                isError={recipesQuery.isError}
                onRetry={() => void recipesQuery.refetch()}
                hasActiveFilter={hasActiveFilter}
                onClearFilters={clearFilters}
                onCardClick={openRowDrawer}
              />
            ) : (
              <RecipesTable
                recipes={recipes}
                isPending={recipesQuery.isPending}
                isError={recipesQuery.isError}
                onRetry={() => void recipesQuery.refetch()}
                hasActiveFilter={hasActiveFilter}
                onClearFilters={clearFilters}
                onRowClick={openRowDrawer}
                sortBy={filters.sortBy}
                sortDir={filters.sortDir}
                onSortChange={cycleSort}
              />
            )}
          </div>
          <Pagination
            rowCount={recipes.length}
            page={filters.page}
            pageSize={filters.pageSize}
            totalCount={totalCount}
            onPageChange={setPage}
            bare={viewMode === 'grid'}
          />
        </div>
      )}

      <RecipeDrawer open={drawerOpen} onOpenChange={setDrawerOpen} recipe={selectedRecipe} readOnly={drawerReadOnly} />
    </div>
  );
}
