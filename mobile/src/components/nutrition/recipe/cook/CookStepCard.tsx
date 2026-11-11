import { StyleSheet, View } from 'react-native';
import type { LayoutChangeEvent } from 'react-native';
import { SymbolView } from 'expo-symbols';
import { useTranslation } from 'react-i18next';

import { StepTimerPill } from '@/components/nutrition/recipe/cook/StepTimerPill';
import type { RecipeStep } from '@/components/nutrition/recipe/steps';
import { Text } from '@/components/Text';
import type { StepTimerView } from '@/hooks/useStepTimers';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

export type CookStepState = 'done' | 'current' | 'upcoming';

type CookStepCardProps = {
  step: RecipeStep;
  number: number;
  state: CookStepState;
  timer: StepTimerView;
  onStartTimer: () => void;
  onLayout?: (event: LayoutChangeEvent) => void;
};

/** One step of cook mode: the current one is highlighted, finished ones are ticked and dimmed. */
export function CookStepCard({ step, number, state, timer, onStartTimer, onLayout }: CookStepCardProps) {
  const { t } = useTranslation();
  const theme = useTheme();
  const styles = makeStyles(theme);

  return (
    <View
      onLayout={onLayout}
      accessibilityState={{ selected: state === 'current' }}
      style={[styles.card, state === 'current' ? styles.current : styles.plain, state === 'done' && styles.done]}
    >
      {state === 'done' ? (
        <View style={[styles.marker, styles.tick]} accessibilityLabel={t('nutritionRecipe.cook.stepDone')}>
          <SymbolView name="checkmark" weight="bold" tintColor={theme.colors.onNutrition} size={theme.sizes.stepGlyph} />
        </View>
      ) : (
        <View style={[styles.marker, state === 'current' ? styles.markerCurrent : styles.markerIdle]}>
          <Text variant="labelStrong" color={state === 'current' ? 'nutritionInk' : 'muted'}>
            {number}
          </Text>
        </View>
      )}
      <View style={styles.copy}>
        {step.title ? <Text variant="bodyStrong">{step.title}</Text> : null}
        {step.description ? (
          <Text variant="bodySmall" color="ink2">
            {step.description}
          </Text>
        ) : null}
        {step.minutes !== undefined ? (
          <StepTimerPill minutes={step.minutes} timer={timer} onStart={onStartTimer} />
        ) : null}
      </View>
    </View>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    card: {
      flexDirection: 'row',
      gap: theme.spacing.md,
      paddingHorizontal: theme.spacing.xl,
      paddingVertical: theme.spacing.lg,
      borderRadius: theme.radii.tile,
    },
    plain: {
      borderWidth: theme.sizes.border,
      borderColor: theme.colors.line,
      backgroundColor: theme.colors.surface,
      boxShadow: theme.shadows.card,
    },
    current: {
      backgroundColor: theme.colors.nutritionSoft,
    },
    done: {
      opacity: theme.opacity.cookDone,
    },
    marker: {
      width: theme.sizes.stepNumber,
      height: theme.sizes.stepNumber,
      alignItems: 'center',
      justifyContent: 'center',
      borderRadius: theme.radii.pill,
    },
    tick: {
      backgroundColor: theme.colors.nutrition,
    },
    markerCurrent: {
      borderWidth: theme.sizes.borderStrong,
      borderColor: theme.colors.nutrition,
    },
    markerIdle: {
      borderWidth: theme.sizes.borderStrong,
      borderColor: theme.colors.lineStrong,
    },
    copy: {
      flex: 1,
      gap: theme.spacing.snug,
    },
  });
