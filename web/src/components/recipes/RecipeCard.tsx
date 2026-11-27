import { useTranslation } from 'react-i18next';
import { Clock } from 'lucide-react';
import { Badge } from '@/components/ui/badge';
import type { RecipeSummaryDto } from '@/api/recipe-types';
import { CardLibraryPill, CardNutritionLine } from '@/components/library/CardParts';
import MacroBar from '@/components/library/MacroBar';
import Thumbnail from '@/components/library/Thumbnail';
import TagPill from '@/components/tags/TagPill';
import { recipeImageCacheKey } from '@/hooks/useRecipesQueries';
import { perServing } from '@/lib/recipe-nutrition';

interface Props {
  recipe: RecipeSummaryDto;
  onClick: (recipe: RecipeSummaryDto) => void;
  onViewPicture: (src: string, alt: string) => void;
}

/** Card for one recipe in the grid view; shows the same summary the table row does. */
export default function RecipeCard({ recipe, onClick, onViewPicture }: Props) {
  const { t } = useTranslation();
  const macros = perServing(recipe.totalNutrients, recipe.servings);
  const totalMinutes = (recipe.prepTimeMinutes ?? 0) + (recipe.cookTimeMinutes ?? 0);
  const tags = recipe.tags ?? [];
  const mealTypes = recipe.mealTypes ?? [];

  return (
    <div className="group relative flex flex-col overflow-hidden rounded-2xl border border-line bg-card shadow-panel transition-colors focus-within:ring-2 focus-within:ring-ring hover:border-ring">
      <div className="relative h-29 w-full bg-muted [&_button]:relative [&_button]:z-10">
        <Thumbnail
          cacheKey={recipe.recipeId ? recipeImageCacheKey(recipe.recipeId) : undefined}
          imageUrl={recipe.imageUrl}
          name={recipe.name ?? ''}
          onViewPicture={onViewPicture}
          className="size-full rounded-none"
        />
        <div className="absolute top-2.5 left-2.5">
          <CardLibraryPill isOwnedByCurrentUser={recipe.isOwnedByCurrentUser && !recipe.isSystem} />
        </div>
        {recipe.isHiddenByCurrentUser && (
          <Badge variant="library-system" className="absolute bottom-2.5 left-2.5">
            {t('library.badge.hidden')}
          </Badge>
        )}
        {totalMinutes > 0 && (
          <Badge variant="outline" className="absolute top-2.5 right-2.5 gap-1.5 bg-card text-meta font-semibold">
            <Clock className="size-3" aria-hidden="true" />
            {t('recipes.table.minutesValue', { count: totalMinutes })}
          </Badge>
        )}
      </div>
      <div className="flex flex-1 flex-col gap-3 p-4">
        <div className="flex flex-col gap-1">
          <button
            type="button"
            onClick={() => onClick(recipe)}
            className="truncate text-left text-copy font-semibold text-ink outline-none after:absolute after:inset-0"
          >
            {recipe.name}
          </button>
          <CardNutritionLine
            kcal={Math.round(macros.kcal)}
            unit={t('recipes.table.caloriesPerPortion')}
            protein={Math.round(macros.protein)}
            carbs={Math.round(macros.carbs)}
            fat={Math.round(macros.fat)}
          />
        </div>
        <MacroBar protein={macros.protein} carbs={macros.carbs} fat={macros.fat} />
        <div className="flex flex-wrap items-center gap-1">
          {mealTypes.map((mealType) => (
            <Badge key={mealType} variant="outline" className="px-2.5 py-1 text-meta font-semibold">
              {t(`recipes.mealType.${mealType}`)}
            </Badge>
          ))}
        </div>
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
