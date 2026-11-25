import { useMemo, useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useDraggable } from '@dnd-kit/react';
import { GripVertical, Plus, Search } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Popover, PopoverAnchor } from '@/components/ui/popover';
import { Skeleton } from '@/components/ui/skeleton';
import { FoodSortDirection } from '@/api/food-types';
import type { FoodSummary, RecipeSummaryDto } from '@/api/generated';
import { useIngredients } from '@/hooks/useIngredientsQueries';
import { useRecipes } from '@/hooks/useRecipesQueries';
import { useDebouncedValue } from '@/hooks/useDebouncedValue';
import { useAuthStore } from '@/stores/auth';
import { perServing } from '@/lib/recipe-nutrition';
import { cn } from '@/lib/utils';
import LibraryInfoPopover, { ItemTile, type LibraryInfoSubject } from '@/components/plan-editor/LibraryInfoPopover';
import {
  IngredientFiltersContent,
  LibraryFiltersTrigger,
  RecipeFiltersContent,
} from '@/components/plan-editor/LibraryFilters';
import {
  countIngredientFilters,
  countRecipeFilters,
  EMPTY_INGREDIENT_FILTERS,
  EMPTY_RECIPE_FILTERS,
  EMPTY_TEMPLATE_FILTERS,
  TemplateTabStateContext,
  type IngredientFilterState,
  type RecipeFilterState,
  type TemplateFilterState,
  type TemplateTabState,
} from '@/components/plan-editor/plan-editor-library-filters';
import MacroDots from '@/components/plan-editor/MacroDots';
import { foodToItem, LIBRARY_CARD_SENSORS, recipeToItem } from '@/components/plan-editor/plan-editor-library';
import type { LibraryItem } from '@/components/plan-editor/plan-editor-types';

const PAGE_SIZE = 25;
const TAB_CLASS =
  'inline-flex h-8 cursor-pointer items-center rounded-full border px-3.5 text-body font-semibold outline-none transition-colors focus-visible:ring-3 focus-visible:ring-ring/50';

type LibraryTab = 'recipes' | 'ingredients';

interface CardProps {
  dragId: string;
  item: LibraryItem;
  info: LibraryInfoSubject;
  name: string;
  imageUrl?: string;
  macros: Macros;
  /** Shown after the macros, e.g. "per 100 g". */
  macrosSuffix?: string;
  canAdd: boolean;
  disabled: boolean;
  language: string;
  onAdd: (item: LibraryItem) => void;
}

/** The card drags as a whole; its main button opens the info popover and "+" stays out of the drag. */
function LibraryCard({
  dragId,
  item,
  info,
  name,
  imageUrl,
  macros,
  macrosSuffix,
  canAdd,
  disabled,
  language,
  onAdd,
}: CardProps) {
  const { t } = useTranslation();
  const [infoOpen, setInfoOpen] = useState(false);
  const infoButtonRef = useRef<HTMLButtonElement>(null);
  const { ref, isDragging } = useDraggable({ id: dragId, data: { item }, disabled, sensors: LIBRARY_CARD_SENSORS });

  return (
    <Popover open={infoOpen} onOpenChange={setInfoOpen} modal={false}>
      <PopoverAnchor asChild>
        <li
          ref={ref}
          data-testid="library-card"
          aria-label={disabled ? undefined : t('planEditor.library.drag', { name })}
          className={cn(
            'flex items-center gap-2 rounded-xl border border-line bg-card p-2 shadow-panel',
            !disabled && 'cursor-grab active:cursor-grabbing',
            isDragging && 'opacity-50',
            infoOpen && 'border-ink',
          )}
        >
          {!disabled && <GripVertical className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />}
          <button
            ref={infoButtonRef}
            type="button"
            aria-label={t('planEditor.library.info.open', { name })}
            aria-expanded={infoOpen}
            onClick={() => setInfoOpen((open) => !open)}
            className="flex min-w-0 flex-1 cursor-[inherit] items-center gap-2.5 rounded-lg text-left outline-none focus-visible:ring-3 focus-visible:ring-ring/50"
          >
            <ItemTile imageUrl={imageUrl} type={item.type} className="size-8.5 rounded-lg" iconClassName="size-4" />
            <span className="flex min-w-0 flex-1 flex-col gap-0.5">
              <span className="truncate text-copy font-semibold text-ink">{name}</span>
              <span className="flex flex-wrap items-center gap-x-2 text-meta text-muted-foreground">
                <span>{t('planEditor.cell.kcal', { kcal: Math.round(macros.kcal) })}</span>
                <MacroDots protein={macros.protein} carbs={macros.carbs} fat={macros.fat} />
                {macrosSuffix && <span>{macrosSuffix}</span>}
              </span>
            </span>
          </button>
          {!disabled && (
            <Button
              type="button"
              variant="outline"
              size="icon"
              data-no-drag
              className="shrink-0 rounded-full"
              disabled={!canAdd}
              title={canAdd ? undefined : t('planEditor.library.selectMealFirst')}
              aria-label={t('planEditor.library.add', { name })}
              onPointerDown={(event) => event.stopPropagation()}
              onClick={(event) => {
                event.stopPropagation();
                onAdd(item);
              }}
            >
              <Plus aria-hidden="true" />
            </Button>
          )}
        </li>
      </PopoverAnchor>
      {infoOpen && (
        <LibraryInfoPopover
          subject={info}
          language={language}
          onClose={() => setInfoOpen(false)}
          onInteractOutside={(event) => {
            // A press on this card's own button is handled by its click (it toggles); don't dismiss first and reopen.
            if (event.target instanceof Node && infoButtonRef.current?.contains(event.target)) {
              event.preventDefault();
            }
          }}
          onCloseAutoFocus={(event) => {
            event.preventDefault();
            infoButtonRef.current?.focus({ preventScroll: true });
          }}
        />
      )}
    </Popover>
  );
}

interface Macros {
  kcal: number;
  protein: number;
  carbs: number;
  fat: number;
}

/** A host-supplied third tab; while active, `render` replaces the search, filters and list below the tab row. */
export interface LibraryExtraTab {
  label: string;
  count?: number;
  render: () => ReactNode;
}

interface Props {
  /** Whether a grid cell is selected, i.e. whether "+" has somewhere to add to. */
  canAdd: boolean;
  disabled: boolean;
  onAdd: (item: LibraryItem) => void;
  extraTab?: LibraryExtraTab;
  /** Leaves the totals off the Recipes and Ingredients chips. */
  hideTabCounts?: boolean;
  /** Opens on the extra tab instead of Recipes; read once, when the panel mounts. */
  extraTabFirst?: boolean;
}

/** Side-panel tab with the nutritionist's recipes and ingredients, draggable onto the week grid. */
export default function LibraryPanel({
  canAdd,
  disabled,
  onAdd,
  extraTab,
  hideTabCounts = false,
  extraTabFirst = false,
}: Props) {
  const { t, i18n } = useTranslation();
  const [tab, setTab] = useState<LibraryTab>('recipes');
  const [extraActive, setExtraActive] = useState(extraTabFirst);
  const showExtra = extraActive && extraTab !== undefined;
  const [searchInput, setSearchInput] = useState('');
  const search = useDebouncedValue(searchInput, 300);
  const isNutritionist = useAuthStore((state) => state.user?.roles.includes('Nutritionist') ?? false);
  // Filters are per tab and stay while switching tabs.
  const [recipeFilters, setRecipeFilters] = useState<RecipeFilterState>(EMPTY_RECIPE_FILTERS);
  const [ingredientFilters, setIngredientFilters] = useState<IngredientFilterState>(EMPTY_INGREDIENT_FILTERS);
  // The host's extra tab (Templates) unmounts when another tab is picked, so its search and filters live here.
  const [templateSearch, setTemplateSearch] = useState('');
  const [templateFilters, setTemplateFilters] = useState<TemplateFilterState>(EMPTY_TEMPLATE_FILTERS);
  const templateTabState = useMemo<TemplateTabState>(
    () => ({
      searchInput: templateSearch,
      setSearchInput: setTemplateSearch,
      filters: templateFilters,
      setFilters: setTemplateFilters,
    }),
    [templateSearch, templateFilters],
  );

  const recipesQuery = useRecipes(
    {
      search,
      ...recipeFilters,
      hidden: false,
      sortBy: null,
      sortDir: FoodSortDirection.Ascending,
      page: 1,
      pageSize: PAGE_SIZE,
    },
    true,
  );
  const ingredientsQuery = useIngredients({
    search,
    categories: ingredientFilters.categories,
    // Owner and Tags are nutritionist-only, as on the Ingredients page.
    tags: isNutritionist ? ingredientFilters.tags : [],
    owners: isNutritionist ? ingredientFilters.owners : [],
    hidden: false,
    sortBy: null,
    sortDir: FoodSortDirection.Ascending,
    page: 1,
    pageSize: PAGE_SIZE,
  });

  const activeQuery = tab === 'recipes' ? recipesQuery : ingredientsQuery;
  const recipes: RecipeSummaryDto[] = recipesQuery.data?.recipes ?? [];
  const foods: FoodSummary[] = ingredientsQuery.data?.foods ?? [];

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-3 overflow-hidden px-4 pt-3 pb-4.5">
      <div
        role="tablist"
        aria-label={t('planEditor.library.kinds')}
        className="flex justify-center gap-1.5"
      >
        {(['recipes', 'ingredients'] as const).map((value) => (
          <button
            key={value}
            type="button"
            role="tab"
            aria-selected={!showExtra && tab === value}
            onClick={() => {
              setExtraActive(false);
              setTab(value);
            }}
            className={cn(
              TAB_CLASS,
              !showExtra && tab === value
                ? 'border-ink bg-ink text-primary-foreground'
                : 'border-line bg-card text-ink hover:bg-muted',
            )}
          >
            {t(`planEditor.library.${value}`)}
            {!hideTabCounts && (
              <span className="ml-1.5 font-medium opacity-60">
                {(value === 'recipes' ? recipesQuery : ingredientsQuery).data?.totalCount ?? 0}
              </span>
            )}
          </button>
        ))}
        {extraTab && (
          <button
            type="button"
            role="tab"
            aria-selected={showExtra}
            onClick={() => setExtraActive(true)}
            className={cn(
              TAB_CLASS,
              showExtra ? 'border-ink bg-ink text-primary-foreground' : 'border-line bg-card text-ink hover:bg-muted',
            )}
          >
            {extraTab.label}
            {extraTab.count !== undefined && <span className="ml-1.5 font-medium opacity-60">{extraTab.count}</span>}
          </button>
        )}
      </div>

      {showExtra && (
        <TemplateTabStateContext.Provider value={templateTabState}>{extraTab.render()}</TemplateTabStateContext.Provider>
      )}

      {!showExtra && (
      <>
      <Popover>
        <PopoverAnchor asChild>
          <div className="flex items-center gap-2">
            <div className="relative min-w-0 flex-1">
              <Search
                className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground"
                aria-hidden="true"
              />
              <Input
                type="search"
                value={searchInput}
                onChange={(event) => setSearchInput(event.target.value)}
                placeholder={t(`planEditor.library.search_${tab}`)}
                aria-label={t(`planEditor.library.search_${tab}`)}
                className="h-9 pl-9"
              />
            </div>
            <LibraryFiltersTrigger
              count={tab === 'recipes' ? countRecipeFilters(recipeFilters) : countIngredientFilters(ingredientFilters, isNutritionist)}
            />
          </div>
        </PopoverAnchor>
        {tab === 'recipes' ? (
          <RecipeFiltersContent
            filters={recipeFilters}
            onChange={setRecipeFilters}
            matchCount={recipesQuery.data?.totalCount}
          />
        ) : (
          <IngredientFiltersContent
            filters={ingredientFilters}
            onChange={setIngredientFilters}
            matchCount={ingredientsQuery.data?.totalCount}
            isNutritionist={isNutritionist}
          />
        )}
      </Popover>

      <p className="text-meta text-muted-foreground">{t('planEditor.library.hint')}</p>

      <div className="-mr-1 min-h-0 flex-1 overflow-y-auto pr-1">
        {activeQuery.isPending && (
          <div className="flex flex-col gap-2">
            {Array.from({ length: 5 }, (_, index) => (
              <Skeleton key={index} className="h-16 w-full rounded-xl" />
            ))}
          </div>
        )}
        {activeQuery.isError && (
          <div className="flex flex-col items-start gap-2">
            <p className="text-body text-muted-foreground">{t('planEditor.library.loadError')}</p>
            <Button type="button" variant="outline" size="sm" onClick={() => void activeQuery.refetch()}>
              {t('planEditor.library.retry')}
            </Button>
          </div>
        )}
        {!activeQuery.isPending && !activeQuery.isError && (
          <ul className="flex flex-col gap-2">
            {tab === 'recipes' &&
              recipes.map((recipe) => (
                <LibraryCard
                  key={recipe.recipeId}
                  dragId={`recipe:${recipe.recipeId}`}
                  item={recipeToItem(recipe)}
                  info={{ kind: 'recipe', recipe }}
                  name={recipe.name ?? ''}
                  macros={perServing(recipe.totalNutrients, recipe.servings)}
                  imageUrl={recipe.imageUrl}
                  canAdd={canAdd}
                  disabled={disabled}
                  language={i18n.language}
                  onAdd={onAdd}
                />
              ))}
            {tab === 'ingredients' &&
              foods.map((food) => {
                const localized =
                  i18n.language.startsWith('cs') ? food.nameCs : i18n.language.startsWith('de') ? food.nameDe : food.nameEn;
                const name = localized || food.name || food.rawName || '';
                return (
                  <LibraryCard
                    key={food.foodId}
                    dragId={`food:${food.foodId}`}
                    item={foodToItem(food)}
                    info={{ kind: 'food', food, name }}
                    name={name}
                    macros={{
                      kcal: food.nutrientValue?.kcal ?? 0,
                      protein: food.nutrientValue?.protein ?? 0,
                      carbs: food.nutrientValue?.carbs ?? 0,
                      fat: food.nutrientValue?.fat ?? 0,
                    }}
                    macrosSuffix={t('planEditor.library.per100')}
                    imageUrl={food.imageUrl}
                    canAdd={canAdd}
                    disabled={disabled}
                    language={i18n.language}
                    onAdd={onAdd}
                  />
                );
              })}
            {((tab === 'recipes' && recipes.length === 0) || (tab === 'ingredients' && foods.length === 0)) && (
              <li className="text-body text-muted-foreground">{t('planEditor.library.empty')}</li>
            )}
          </ul>
        )}
      </div>
      </>
      )}
    </div>
  );
}
