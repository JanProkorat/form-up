import { useState } from 'react';
import { Share, StyleSheet, View } from 'react-native';
import { SymbolView } from 'expo-symbols';
import { useLocalSearchParams, useRouter } from 'expo-router';
import { useTranslation } from 'react-i18next';

import { getErrorStatus } from '@/api/errors';
import type { GetRecipeResponse } from '@/api/generated';
import { isNutritionId } from '@/api/nutrition';
import { DetailCard } from '@/components/nutrition/meal/DetailCard';
import { DetailLayout } from '@/components/nutrition/meal/DetailLayout';
import { DetailState } from '@/components/nutrition/meal/DetailState';
import { foodName, formatAmount, formatWhole, macrosOf, scaleMacros } from '@/components/nutrition/meal/format';
import { GlassIconButton } from '@/components/nutrition/meal/GlassIconButton';
import { useMarkEatenFooter } from '@/components/nutrition/meal/MarkEatenButton';
import { PortionStepper } from '@/components/nutrition/recipe/PortionStepper';
import { RecipeIngredients } from '@/components/nutrition/recipe/RecipeIngredients';
import { RecipeMacros } from '@/components/nutrition/recipe/RecipeMacros';
import type { MacroBasis } from '@/components/nutrition/recipe/RecipeMacros';
import { RecipeSteps } from '@/components/nutrition/recipe/RecipeSteps';
import { canCook, normalizeSteps } from '@/components/nutrition/recipe/steps';
import { Text } from '@/components/Text';
import { useRecipeDetail } from '@/hooks/useRecipeDetail';
import { isOfflineEmpty } from '@/lib/queryState';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

const MAX_PORTIONS = 99;

/** Portions the plan asks for, taken from the route when the recipe was opened from a meal. */
const parsePlanServings = (value: string | undefined): number | undefined => {
  const parsed = value === undefined ? NaN : Number(value);
  return Number.isFinite(parsed) && parsed > 0 && parsed <= MAX_PORTIONS ? parsed : undefined;
};

/** One recipe: macros, ingredients scaled to the chosen portions, and the steps. */
export default function RecipeDetailScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const params = useLocalSearchParams<{ recipeId?: string; servings?: string; mealId?: string; itemId?: string }>();
  const recipeId = typeof params.recipeId === 'string' ? params.recipeId : '';
  const valid = isNutritionId(recipeId);
  const query = useRecipeDetail(recipeId, valid);
  const goBack = () => router.back();

  if (!valid || query.isPending || query.data === undefined) {
    const notFound = !valid || (query.isError && getErrorStatus(query.error) === 404);
    const pending = valid && query.isPending;
    return (
      <DetailState
        kind={pending ? 'pending' : notFound ? 'notFound' : isOfflineEmpty(query) ? 'offline' : 'error'}
        title={notFound ? t('nutritionRecipe.notFound.title') : t('nutritionRecipe.error.title')}
        body={notFound ? t('nutritionRecipe.notFound.body') : t('nutritionRecipe.error.body')}
        onBack={goBack}
        onRetry={() => void query.refetch()}
      />
    );
  }

  return (
    <RecipeDetailContent
      recipe={query.data}
      planServings={typeof params.servings === 'string' ? parsePlanServings(params.servings) : undefined}
      mealId={typeof params.mealId === 'string' ? params.mealId : undefined}
      itemId={typeof params.itemId === 'string' ? params.itemId : undefined}
    />
  );
}

type RecipeDetailContentProps = {
  recipe: GetRecipeResponse;
  planServings?: number;
  /** The plan meal and item the recipe was opened from; present only from a row that can log today. */
  mealId?: string;
  itemId?: string;
};

function RecipeDetailContent({ recipe, planServings, mealId, itemId }: RecipeDetailContentProps) {
  const { t, i18n } = useTranslation();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const router = useRouter();
  const baseServings = recipe.servings && recipe.servings > 0 ? recipe.servings : 1;
  const [portions, setPortions] = useState(planServings ?? baseServings);
  const [basis, setBasis] = useState<MacroBasis>('portion');

  const steps = normalizeSteps(recipe.steps);
  const foods = recipe.foods ?? [];
  const perPortion = scaleMacros(macrosOf(recipe.totalNutrients), 1 / baseServings);
  const minutes = (recipe.prepTimeMinutes ?? 0) + (recipe.cookTimeMinutes ?? 0);
  const language = i18n.language;
  const markEaten = useMarkEatenFooter(mealId, itemId);

  const share = () => {
    const lines = foods.map(
      (food) =>
        `- ${foodName(food, language)}: ${t('nutritionRecipe.ingredients.grams', {
          value: formatWhole(language, ((food.amountGrams ?? 0) / baseServings) * portions),
        })}`,
    );
    const message = [
      recipe.name ?? '',
      t('nutritionRecipe.ingredients.forPortions', { value: formatAmount(language, portions) }),
      ...lines,
    ].join('\n');
    void Share.share({ message });
  };

  return (
    <DetailLayout
      heroHeight={theme.sizes.recipeHero}
      imageUri={recipe.imageUrl}
      backLabel={t('onboarding.back')}
      onBack={() => router.back()}
      footer={markEaten}
      actions={<GlassIconButton symbol="square.and.arrow.up" label={t('nutritionRecipe.share')} onPress={share} />}
    >
      <View style={styles.title}>
        <Text variant="profileName" accessibilityRole="header">
          {recipe.name ?? ''}
        </Text>
        {minutes > 0 ? (
          <View style={styles.info}>
            <SymbolView name="clock" tintColor={theme.colors.muted} size={theme.sizes.listChevron} />
            <Text variant="bodySmall" color="muted">
              {t('nutritionRecipe.minutes', { value: formatWhole(language, minutes) })}
            </Text>
          </View>
        ) : null}
      </View>

      {recipe.description ? (
        <DetailCard>
          <Text variant="body">{recipe.description}</Text>
        </DetailCard>
      ) : null}

      <PortionStepper
        portions={portions}
        planServings={planServings}
        canDecrease={portions > 1}
        canIncrease={portions < MAX_PORTIONS}
        onDecrease={() => setPortions((current) => Math.max(1, current - 1))}
        onIncrease={() => setPortions((current) => Math.min(MAX_PORTIONS, current + 1))}
      />
      <RecipeMacros basis={basis} portions={portions} perPortion={perPortion} onChangeBasis={setBasis} />
      <RecipeIngredients foods={foods} portions={portions} baseServings={baseServings} />

      {recipe.note ? (
        <DetailCard>
          <Text variant="body">{recipe.note}</Text>
        </DetailCard>
      ) : null}

      {steps.length > 0 ? (
        <RecipeSteps
          steps={steps}
          totalMinutes={minutes}
          onCookMode={
            canCook(steps) && recipe.recipeId
              ? () =>
                  router.push({
                    pathname: '/nutrition/cook/[recipeId]',
                    params: { recipeId: recipe.recipeId ?? '', portions: String(portions) },
                  })
              : undefined
          }
        />
      ) : null}
    </DetailLayout>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    title: {
      gap: theme.spacing.xxs,
      paddingHorizontal: theme.spacing.xxs,
    },
    info: {
      flexDirection: 'row',
      alignItems: 'center',
      gap: theme.spacing.xxs,
    },
  });
