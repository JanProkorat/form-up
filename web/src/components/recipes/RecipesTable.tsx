import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { ImageLightbox } from '@/components/ui/image-lightbox';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { FoodSortDirection, RecipeSortField, type RecipeSummaryDto } from '@/api/recipe-types';
import LibraryBadge from '@/components/library/LibraryBadge';
import Thumbnail from '@/components/library/Thumbnail';
import NutrientDots from '@/components/library/NutrientDots';
import TagPill from '@/components/tags/TagPill';
import SortableColumnHeader from '@/components/library/SortableColumnHeader';
import { sortAriaValue } from '@/components/library/sortAria';
import { recipeImageCacheKey } from '@/hooks/useRecipesQueries';
import { perServing } from '@/lib/recipe-nutrition';

const SKELETON_ROW_COUNT = 5;
const COLUMN_COUNT = 7;

const HEAD_CLASS = 'px-4 py-3.5 text-meta font-semibold';
const CELL_CLASS = 'px-4 py-2.75';

interface LightboxPicture {
  src: string;
  alt: string;
}

interface Props {
  recipes: RecipeSummaryDto[];
  isPending: boolean;
  isError: boolean;
  onRetry: () => void;
  hasActiveFilter: boolean;
  onClearFilters: () => void;
  onRowClick: (recipe: RecipeSummaryDto) => void;
  /** Current sort column, or `null` for the server default (newest first). */
  sortBy: RecipeSortField | null;
  sortDir: FoodSortDirection;
  onSortChange: (field: RecipeSortField) => void;
}

/** The Recipes table. Mirrors `IngredientsTable`; values are per serving. */
export default function RecipesTable({
  recipes,
  isPending,
  isError,
  onRetry,
  hasActiveFilter,
  onClearFilters,
  onRowClick,
  sortBy,
  sortDir,
  onSortChange,
}: Props) {
  const { t } = useTranslation();
  const [lightboxPicture, setLightboxPicture] = useState<LightboxPicture | null>(null);

  function sortableHeaderProps(field: RecipeSortField, label: string) {
    return {
      field,
      label,
      sortBy,
      sortDir,
      onSortChange,
      sortButtonLabel: t('library.sortButtonLabel', { column: label }),
    };
  }

  return (
    <>
      <Table className="min-w-280">
        <TableHeader className="sticky top-0 z-10 bg-card">
          <TableRow>
            <TableHead className={HEAD_CLASS} aria-sort={sortAriaValue(sortBy, sortDir, RecipeSortField.Name)}>
              <SortableColumnHeader {...sortableHeaderProps(RecipeSortField.Name, t('recipes.table.columnName'))} />
            </TableHead>
            <TableHead
              className={`w-28 ${HEAD_CLASS}`}
              aria-sort={sortAriaValue(sortBy, sortDir, RecipeSortField.CaloriesPerServing)}
            >
              <SortableColumnHeader
                {...sortableHeaderProps(RecipeSortField.CaloriesPerServing, t('recipes.table.columnCalories'))}
              />
            </TableHead>
            <TableHead className={HEAD_CLASS}>{t('recipes.table.columnNutrients')}</TableHead>
            <TableHead className={`w-36 ${HEAD_CLASS}`}>{t('recipes.table.columnMealType')}</TableHead>
            <TableHead className={`w-24 ${HEAD_CLASS}`} aria-sort={sortAriaValue(sortBy, sortDir, RecipeSortField.Servings)}>
              <SortableColumnHeader
                {...sortableHeaderProps(RecipeSortField.Servings, t('recipes.table.columnServings'))}
              />
            </TableHead>
            <TableHead className={`w-32 ${HEAD_CLASS}`}>{t('recipes.table.columnTags')}</TableHead>
            <TableHead className={`w-28 ${HEAD_CLASS}`} aria-sort={sortAriaValue(sortBy, sortDir, RecipeSortField.Library)}>
              <SortableColumnHeader
                {...sortableHeaderProps(RecipeSortField.Library, t('recipes.table.columnLibrary'))}
              />
            </TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {isPending &&
            Array.from({ length: SKELETON_ROW_COUNT }).map((_, index) => (
              <TableRow key={index}>
                <TableCell colSpan={COLUMN_COUNT}>
                  <Skeleton className="h-10 w-full" />
                </TableCell>
              </TableRow>
            ))}

          {!isPending && isError && (
            <TableRow>
              <TableCell colSpan={COLUMN_COUNT} className="py-10 text-center">
                <div className="flex flex-col items-center gap-3">
                  <p className="text-body text-muted-foreground">{t('common.dataLoadError')}</p>
                  <Button type="button" variant="outline" size="sm" onClick={onRetry}>
                    {t('recipes.retry')}
                  </Button>
                </div>
              </TableCell>
            </TableRow>
          )}

          {!isPending && !isError && recipes.length === 0 && (
            <TableRow>
              <TableCell colSpan={COLUMN_COUNT} className="py-10 text-center">
                <div className="flex flex-col items-center gap-2">
                  <p className="text-body text-muted-foreground">{t('recipes.noResultsForFilters')}</p>
                  {hasActiveFilter && (
                    <Button type="button" variant="outline" size="sm" onClick={onClearFilters}>
                      {t('recipes.clearFilters')}
                    </Button>
                  )}
                </div>
              </TableCell>
            </TableRow>
          )}

          {!isPending &&
            !isError &&
            recipes.map((recipe) => {
              const macros = perServing(recipe.totalNutrients, recipe.servings);
              const subtitleParts = [
                t('recipes.table.ingredientsCount', { count: recipe.foodCount ?? 0 }),
                ...(recipe.prepTimeMinutes || recipe.cookTimeMinutes
                  ? [
                      t('recipes.table.minutesValue', {
                        count: (recipe.prepTimeMinutes ?? 0) + (recipe.cookTimeMinutes ?? 0),
                      }),
                    ]
                  : []),
              ];
              return (
                <TableRow key={recipe.recipeId} className="cursor-pointer border-line" onClick={() => onRowClick(recipe)}>
                  <TableCell className={`${CELL_CLASS} text-copy font-semibold text-ink`}>
                    <div className="flex items-center gap-3">
                      <Thumbnail
                        cacheKey={recipe.recipeId ? recipeImageCacheKey(recipe.recipeId) : undefined}
                        imageUrl={recipe.imageUrl}
                        name={recipe.name ?? ''}
                        onViewPicture={(src, alt) => setLightboxPicture({ src, alt })}
                      />
                      <div className="flex min-w-0 flex-col">
                        <span>{recipe.name}</span>
                        <span className="text-meta font-normal text-muted-foreground">{subtitleParts.join(' · ')}</span>
                      </div>
                    </div>
                  </TableCell>
                  <TableCell className={`${CELL_CLASS} text-copy`}>
                    <span className="font-semibold text-ink">{Math.round(macros.kcal)}</span>{' '}
                    <span className="text-ink-2">{t('recipes.table.caloriesUnit')}</span>
                  </TableCell>
                  <TableCell className={CELL_CLASS}>
                    <NutrientDots
                      protein={Math.round(macros.protein)}
                      carbs={Math.round(macros.carbs)}
                      fat={Math.round(macros.fat)}
                      fibre={Math.round(macros.fiber)}
                    />
                  </TableCell>
                  <TableCell className={CELL_CLASS}>
                    <div className="flex flex-wrap items-center gap-1">
                      {(recipe.mealTypes ?? []).map((mealType) => (
                        <Badge key={mealType} variant="outline" className="px-2.5 py-1 text-meta font-semibold">
                          {t(`recipes.mealType.${mealType}`)}
                        </Badge>
                      ))}
                    </div>
                  </TableCell>
                  <TableCell className={`${CELL_CLASS} text-body text-muted-foreground`}>{recipe.servings}</TableCell>
                  <TableCell className={CELL_CLASS}>
                    <div className="flex flex-wrap items-center gap-1">
                      {(recipe.tags ?? []).map((tag) => (
                        <TagPill key={tag.tagId} name={tag.name ?? ''} colorHex={tag.colorHex} variant="table" />
                      ))}
                    </div>
                  </TableCell>
                  <TableCell className={CELL_CLASS}>
                    <LibraryBadge
                      isOwnedByCurrentUser={recipe.isOwnedByCurrentUser}
                      isSystem={recipe.isSystem}
                      isHidden={recipe.isHiddenByCurrentUser}
                    />
                  </TableCell>
                </TableRow>
              );
            })}
        </TableBody>
      </Table>
      <ImageLightbox
        open={lightboxPicture !== null}
        onOpenChange={(open) => {
          if (!open) {
            setLightboxPicture(null);
          }
        }}
        src={lightboxPicture?.src ?? ''}
        alt={lightboxPicture?.alt ?? ''}
      />
    </>
  );
}
