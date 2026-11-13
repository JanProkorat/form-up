import { useCallback, useRef, useState } from 'react';
import { ScrollView, StyleSheet, View } from 'react-native';
import type { LayoutChangeEvent } from 'react-native';
import { useKeepAwake } from 'expo-keep-awake';
import { SymbolView } from 'expo-symbols';
import { useLocalSearchParams, useRouter } from 'expo-router';
import { useTranslation } from 'react-i18next';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import Svg, { Defs, LinearGradient, Rect, Stop } from 'react-native-svg';

import { getErrorStatus } from '@/api/errors';
import type { GetRecipeResponse } from '@/api/generated';
import { isNutritionId } from '@/api/nutrition';
import { Button } from '@/components/Button';
import { DetailState } from '@/components/nutrition/meal/DetailState';
import { formatAmount } from '@/components/nutrition/meal/format';
import { CookStepCard } from '@/components/nutrition/recipe/cook/CookStepCard';
import type { CookStepState } from '@/components/nutrition/recipe/cook/CookStepCard';
import { normalizeSteps } from '@/components/nutrition/recipe/steps';
import type { RecipeStep } from '@/components/nutrition/recipe/steps';
import { ScreenHeader } from '@/components/ScreenHeader';
import { Text } from '@/components/Text';
import { useRecipeDetail } from '@/hooks/useRecipeDetail';
import { useStepTimers } from '@/hooks/useStepTimers';
import { isOfflineEmpty } from '@/lib/queryState';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

const MAX_PORTIONS = 99;

/** Portions chosen on the recipe screen; anything unusable falls back to the recipe's own servings. */
const parsePortions = (value: string | undefined, fallback: number): number => {
  const parsed = value === undefined ? NaN : Number(value);
  return Number.isFinite(parsed) && parsed > 0 && parsed <= MAX_PORTIONS ? parsed : fallback;
};

/** Cooks a recipe one step at a time, with optional timers; the screen stays on while it is open. */
export default function CookModeScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const params = useLocalSearchParams<{ recipeId?: string; portions?: string }>();
  const recipeId = typeof params.recipeId === 'string' ? params.recipeId : '';
  const valid = isNutritionId(recipeId);
  const query = useRecipeDetail(recipeId, valid);
  const goBack = () => router.back();
  useKeepAwake();

  const steps = normalizeSteps(query.data?.steps);

  if (!valid || query.isPending || query.data === undefined || steps.length === 0) {
    const notFound = !valid || (query.data !== undefined && steps.length === 0) || (query.isError && getErrorStatus(query.error) === 404);
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
    <CookModeContent
      recipe={query.data}
      steps={steps}
      portions={parsePortions(
        typeof params.portions === 'string' ? params.portions : undefined,
        query.data.servings && query.data.servings > 0 ? Math.min(MAX_PORTIONS, query.data.servings) : 1,
      )}
      onExit={goBack}
    />
  );
}

type CookModeContentProps = {
  recipe: GetRecipeResponse;
  steps: RecipeStep[];
  portions: number;
  onExit: () => void;
};

function CookModeContent({ recipe, steps, portions, onExit }: CookModeContentProps) {
  const { t, i18n } = useTranslation();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const insets = useSafeAreaInsets();
  const { start, viewOf } = useStepTimers();
  const [current, setCurrent] = useState(0);
  const scrollRef = useRef<ScrollView>(null);
  const offsets = useRef<number[]>([]);
  const last = current >= steps.length - 1;
  const clearance = theme.sizes.heroActionClearance + insets.bottom;

  const next = () => {
    if (last) {
      onExit();
      return;
    }
    const target = current + 1;
    setCurrent(target);
    scrollRef.current?.scrollTo({ y: Math.max(0, (offsets.current[target] ?? 0) - theme.spacing.xl), animated: true });
  };

  const measure = useCallback(
    (index: number) => (event: LayoutChangeEvent) => {
      offsets.current[index] = event.nativeEvent.layout.y;
    },
    [],
  );

  return (
    <View style={styles.root}>
      <View
        style={[
          styles.header,
          {
            paddingTop: insets.top + theme.spacing.safeAreaGap,
            paddingLeft: insets.left + theme.spacing.xl,
            paddingRight: insets.right + theme.spacing.xl,
          },
        ]}
      >
        <ScreenHeader
          title={recipe.name ?? ''}
          subtitle={t('nutritionRecipe.steps.title')}
          backLabel={t('nutritionRecipe.cook.back')}
          onBack={onExit}
        />
      </View>

      <ScrollView
        ref={scrollRef}
        style={styles.scroll}
        contentInsetAdjustmentBehavior="never"
        contentContainerStyle={[styles.content, { paddingBottom: clearance }]}
      >
        <View style={styles.meta}>
          <View style={styles.chip}>
            <Text variant="captionStrong" color="nutritionInk">
              {t('nutritionRecipe.cook.forPortions', { value: formatAmount(i18n.language, portions) })}
            </Text>
          </View>
          <Text variant="caption" color="muted" accessibilityLiveRegion="polite">
            {t('nutritionRecipe.cook.progress', { current: current + 1, total: steps.length })}
          </Text>
          <View style={styles.awake}>
            <SymbolView name="sun.max" tintColor={theme.colors.muted} size={theme.sizes.pendingGlyph} />
            <Text variant="caption" color="muted">
              {t('nutritionRecipe.cook.awake')}
            </Text>
          </View>
        </View>

        <View style={styles.steps}>
          {steps.map((step, index) => {
            const state: CookStepState = index < current ? 'done' : index === current ? 'current' : 'upcoming';
            return (
              <CookStepCard
                key={index}
                step={step}
                number={index + 1}
                state={state}
                timer={viewOf(index)}
                onStartTimer={() => (step.minutes === undefined ? undefined : start(index, step.minutes))}
                onLayout={measure(index)}
              />
            );
          })}
        </View>
      </ScrollView>

      <View pointerEvents="none" style={[styles.fade, { height: clearance }]}>
        <Svg width="100%" height="100%">
          <Defs>
            <LinearGradient id="cookFade" x1="0" y1="0" x2="0" y2="1">
              <Stop offset="0" stopColor={theme.colors.ground} stopOpacity={0} />
              <Stop offset="0.55" stopColor={theme.colors.ground} stopOpacity={1} />
              <Stop offset="1" stopColor={theme.colors.ground} stopOpacity={1} />
            </LinearGradient>
          </Defs>
          <Rect width="100%" height="100%" fill="url(#cookFade)" />
        </Svg>
      </View>

      <View style={[styles.footer, { bottom: insets.bottom + theme.spacing.xxl }]}>
        <Button
          variant="nutrition"
          label={last ? t('nutritionRecipe.cook.finish') : t('nutritionRecipe.cook.next')}
          onPress={next}
          style={styles.next}
          icon={
            <SymbolView
              name={last ? 'checkmark' : 'chevron.right'}
              weight="semibold"
              tintColor={theme.colors.onNutrition}
              size={theme.sizes.mealChevron}
            />
          }
        />
      </View>
    </View>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    root: {
      flex: 1,
      backgroundColor: theme.colors.ground,
    },
    header: {
      paddingBottom: theme.spacing.xl,
    },
    scroll: {
      flex: 1,
    },
    content: {
      gap: theme.spacing.md,
    },
    meta: {
      flexDirection: 'row',
      alignItems: 'center',
      gap: theme.spacing.sm,
      paddingHorizontal: theme.spacing.xl,
    },
    chip: {
      paddingVertical: theme.spacing.xxs,
      paddingHorizontal: theme.spacing.md,
      borderRadius: theme.radii.pill,
      backgroundColor: theme.colors.nutritionSoft,
    },
    awake: {
      flexDirection: 'row',
      alignItems: 'center',
      gap: theme.spacing.xxs,
      marginLeft: 'auto',
    },
    steps: {
      gap: theme.spacing.xs,
      paddingHorizontal: theme.spacing.xl,
    },
    fade: {
      position: 'absolute',
      left: 0,
      right: 0,
      bottom: 0,
    },
    footer: {
      position: 'absolute',
      left: theme.spacing.xxl,
      right: theme.spacing.xxl,
      flexDirection: 'row',
    },
    next: {
      flex: 1,
    },
  });
