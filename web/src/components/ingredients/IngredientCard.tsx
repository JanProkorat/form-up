import { useTranslation } from 'react-i18next';
import type { FoodSummary } from '@/api/food-types';
import { CardLibraryPill, CardNutritionLine } from '@/components/library/CardParts';
import { Badge } from '@/components/ui/badge';
import MacroBar from '@/components/library/MacroBar';
import Thumbnail from '@/components/library/Thumbnail';
import TagPill from '@/components/tags/TagPill';
import { foodImageCacheKey } from '@/hooks/useIngredientsQueries';

interface Props {
  food: FoodSummary;
  onClick: (food: FoodSummary) => void;
  onViewPicture: (src: string, alt: string) => void;
}

/** Card for one ingredient in the grid view; shows the same summary the table row does. */
export default function IngredientCard({ food, onClick, onViewPicture }: Props) {
  const { t } = useTranslation();
  const nutrients = food.nutrientValue;
  const tags = food.tags ?? [];

  return (
    <div className="group relative flex flex-col overflow-hidden rounded-2xl border border-line bg-card shadow-panel transition-colors focus-within:ring-2 focus-within:ring-ring hover:border-ring">
      <div className="relative h-29 w-full bg-muted [&_button]:relative [&_button]:z-10">
        <Thumbnail
          cacheKey={food.foodId ? foodImageCacheKey(food.foodId) : undefined}
          imageUrl={food.imageUrl}
          name={food.name ?? ''}
          onViewPicture={onViewPicture}
          className="size-full rounded-none"
        />
        <div className="absolute top-2.5 left-2.5">
          <CardLibraryPill isOwnedByCurrentUser={food.isOwnedByCurrentUser} />
        </div>
        {food.isHiddenByCurrentUser && (
          <Badge variant="library-system" className="absolute bottom-2.5 left-2.5">
            {t('library.badge.hidden')}
          </Badge>
        )}
      </div>
      <div className="flex flex-1 flex-col gap-3 p-4">
        <div className="flex flex-col gap-1">
          <button
            type="button"
            onClick={() => onClick(food)}
            className="truncate text-left text-copy font-semibold text-ink outline-none after:absolute after:inset-0"
          >
            {food.name}
          </button>
          <CardNutritionLine
            kcal={nutrients?.kcal ?? 0}
            unit={t('ingredients.table.caloriesUnit')}
            protein={nutrients?.protein ?? 0}
            carbs={nutrients?.carbs ?? 0}
            fat={nutrients?.fat ?? 0}
          />
        </div>
        <MacroBar protein={nutrients?.protein ?? 0} carbs={nutrients?.carbs ?? 0} fat={nutrients?.fat ?? 0} />
        {food.category && (
          <span className="text-body text-ink-2">{t(`ingredients.category.${food.category}`)}</span>
        )}
        <div className="mt-auto flex flex-wrap items-center gap-1 border-t border-line pt-2">
          {tags.length > 0 ? (
            tags.map((tag) => <TagPill key={tag.tagId} name={tag.name ?? ''} colorHex={tag.colorHex} variant="table" />)
          ) : (
            <span className="text-meta text-muted-foreground">{t('library.viewMode.noTags')}</span>
          )}
        </div>
      </div>
    </div>
  );
}
