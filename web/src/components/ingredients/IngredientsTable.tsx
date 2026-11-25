import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import { ImageLightbox } from '@/components/ui/image-lightbox';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import type { FoodSummary } from '@/api/food-types';
import { FoodSortDirection, FoodSortField } from '@/api/food-types';
import LibraryBadge from '@/components/library/LibraryBadge';
import Thumbnail from '@/components/library/Thumbnail';
import SortableColumnHeader from '@/components/library/SortableColumnHeader';
import { sortAriaValue } from '@/components/library/sortAria';
import { foodImageCacheKey } from '@/hooks/useIngredientsQueries';
import NutrientDots from '@/components/library/NutrientDots';
import TagPill from '@/components/tags/TagPill';

/** The picture currently shown in the table-level lightbox (#1140), or
 * `null` when it's closed. One instance serves every row's thumbnail. */
interface LightboxPicture {
  src: string;
  alt: string;
}

const SKELETON_ROW_COUNT = 5;
const COLUMN_COUNT = 6;

const HEAD_CLASS = 'px-4 py-3.5 text-meta font-semibold';
const CELL_CLASS = 'px-4 py-2.75';

interface Props {
  foods: FoodSummary[];
  isPending: boolean;
  isError: boolean;
  onRetry: () => void;
  hasActiveFilter: boolean;
  onClearFilters: () => void;
  onRowClick: (food: FoodSummary) => void;
  /** Current sort column, or `null` when the list is unsorted (#1139). */
  sortBy: FoodSortField | null;
  sortDir: FoodSortDirection;
  /** Cycles the clicked column's sort state: ascending → descending → cleared. */
  onSortChange: (field: FoodSortField) => void;
}

/** The Ingredients table. Mirrors `ClientsTable` (`@/components/clients/ClientsTable.tsx`). */
export default function IngredientsTable({
  foods,
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

  function sortableHeaderProps(field: FoodSortField, label: string) {
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
      <Table className="min-w-240">
      {/* `sticky top-0` pins the header to the page's own vertical scroller
          (see IngredientsPage.tsx's `overflow-visible` override on
          `ui/table.tsx`'s horizontal-scroll wrapper). `bg-card` is already
          opaque, so scrolled rows don't show through underneath. */}
      <TableHeader className="sticky top-0 z-10 bg-card">
        <TableRow className="border-line hover:bg-transparent">
          {/* Name has no fixed width — it takes all remaining space. The
              other columns are fixed widths via Tailwind's spacing scale,
              which is itself token-driven off the single `--spacing` base —
              not a one-off literal. */}
          <TableHead className={HEAD_CLASS} aria-sort={sortAriaValue(sortBy, sortDir, FoodSortField.Name)}>
            <SortableColumnHeader {...sortableHeaderProps(FoodSortField.Name, t('ingredients.table.columnName'))} />
          </TableHead>
          <TableHead className={`w-36 ${HEAD_CLASS}`} aria-sort={sortAriaValue(sortBy, sortDir, FoodSortField.Calories)}>
            <SortableColumnHeader
              {...sortableHeaderProps(FoodSortField.Calories, t('ingredients.table.columnCalories'))}
            />
          </TableHead>
          <TableHead className={HEAD_CLASS}>{t('ingredients.table.columnNutrients')}</TableHead>
          <TableHead className={`w-40 ${HEAD_CLASS}`} aria-sort={sortAriaValue(sortBy, sortDir, FoodSortField.Category)}>
            <SortableColumnHeader
              {...sortableHeaderProps(FoodSortField.Category, t('ingredients.table.columnCategory'))}
            />
          </TableHead>
          <TableHead className={`w-40 ${HEAD_CLASS}`}>{t('ingredients.table.columnTags')}</TableHead>
          <TableHead className={`w-28 ${HEAD_CLASS}`} aria-sort={sortAriaValue(sortBy, sortDir, FoodSortField.Library)}>
            <SortableColumnHeader
              {...sortableHeaderProps(FoodSortField.Library, t('ingredients.table.columnLibrary'))}
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
                  {t('ingredients.retry')}
                </Button>
              </div>
            </TableCell>
          </TableRow>
        )}

        {!isPending && !isError && foods.length === 0 && (
          <TableRow>
            <TableCell colSpan={COLUMN_COUNT} className="py-10 text-center">
              <div className="flex flex-col items-center gap-2">
                <p className="text-body text-muted-foreground">{t('ingredients.noResultsForFilters')}</p>
                {hasActiveFilter && (
                  <Button type="button" variant="outline" size="sm" onClick={onClearFilters}>
                    {t('ingredients.clearFilters')}
                  </Button>
                )}
              </div>
            </TableCell>
          </TableRow>
        )}

        {!isPending &&
          !isError &&
          foods.map((food) => (
            <TableRow
              key={food.foodId}
              className="cursor-pointer border-line"
              onClick={() => onRowClick(food)}
            >
              {/* Name: SemiBold 14 dark. `text-copy` is the existing 14px
                  token (audience-panel body copy) reused here for its size,
                  not its original semantic name — no second 14px token. */}
              <TableCell className={`${CELL_CLASS} text-copy font-semibold text-ink`}>
                <div className="flex items-center gap-3">
                  <Thumbnail
                    cacheKey={food.foodId ? foodImageCacheKey(food.foodId) : undefined}
                    imageUrl={food.imageUrl}
                    name={food.name ?? ''}
                    onViewPicture={(src, alt) => setLightboxPicture({ src, alt })}
                  />
                  <span>{food.name}</span>
                </div>
              </TableCell>
              <TableCell className={`${CELL_CLASS} text-copy`}>
                <span className="font-semibold text-ink">{food.nutrientValue?.kcal ?? 0}</span>{' '}
                <span className="text-ink-2">{t('ingredients.table.caloriesUnit')}</span>
              </TableCell>
              <TableCell className={CELL_CLASS}>
                <NutrientDots
                  protein={food.nutrientValue?.protein ?? 0}
                  carbs={food.nutrientValue?.carbs ?? 0}
                  fat={food.nutrientValue?.fat ?? 0}
                  fibre={food.nutrientValue?.fiber ?? 0}
                />
              </TableCell>
              <TableCell className={`${CELL_CLASS} text-copy text-ink-2`}>
                {food.category && t(`ingredients.category.${food.category}`)}
              </TableCell>
              <TableCell className={CELL_CLASS}>
                <div className="flex flex-wrap items-center gap-1">
                  {(food.tags ?? []).map((tag) => (
                    <TagPill key={tag.tagId} name={tag.name ?? ''} colorHex={tag.colorHex} variant="table" />
                  ))}
                </div>
              </TableCell>
              <TableCell className={CELL_CLASS}>
                <LibraryBadge isOwnedByCurrentUser={food.isOwnedByCurrentUser} isSystem={food.isSystem}
                  isHidden={food.isHiddenByCurrentUser}
                />
              </TableCell>
            </TableRow>
          ))}
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
