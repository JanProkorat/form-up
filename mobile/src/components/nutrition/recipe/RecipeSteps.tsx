import { Pressable, StyleSheet, View } from 'react-native';
import type { LayoutChangeEvent } from 'react-native';
import { SymbolView } from 'expo-symbols';
import { useTranslation } from 'react-i18next';

import { DetailCard } from '@/components/nutrition/meal/DetailCard';
import { formatWhole } from '@/components/nutrition/meal/format';
import type { RecipeStep } from '@/components/nutrition/recipe/steps';
import { Text } from '@/components/Text';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

type RecipeStepsProps = {
  steps: RecipeStep[];
  /** Whole recipe time, shown beside the step count when known. */
  totalMinutes?: number;
  /** Opens cook mode; the button is shown only when this is given. */
  onCookMode?: () => void;
  onLayout?: (event: LayoutChangeEvent) => void;
};

/** Numbered preparation steps, with the step's title in bold when it has one. */
export function RecipeSteps({ steps, totalMinutes, onCookMode, onLayout }: RecipeStepsProps) {
  const { t, i18n } = useTranslation();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const count = t('nutritionRecipe.steps.count', { count: steps.length });
  const summary =
    totalMinutes !== undefined && totalMinutes > 0
      ? `${count} · ${t('nutritionRecipe.steps.about', { value: formatWhole(i18n.language, totalMinutes) })}`
      : count;

  return (
    <View onLayout={onLayout}>
      <DetailCard>
        <View style={styles.header}>
          <View style={styles.headerCopy}>
            <Text variant="subtitle" accessibilityRole="header">
              {t('nutritionRecipe.steps.title')}
            </Text>
            <Text variant="caption" color="muted">
              {summary}
            </Text>
          </View>
          {onCookMode ? (
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={t('nutritionRecipe.steps.cookMode')}
              onPress={onCookMode}
              style={({ pressed }) => [styles.cook, pressed && styles.pressed]}
            >
              <SymbolView name="play.fill" tintColor={theme.colors.nutritionInk} size={theme.sizes.stepGlyph} />
              <Text variant="link" color="nutritionInk">
                {t('nutritionRecipe.steps.cookMode')}
              </Text>
            </Pressable>
          ) : null}
        </View>
        {steps.map((step, index) => (
          <View key={index} style={[styles.row, index > 0 && styles.divided]}>
            <View style={styles.badge}>
              <Text variant="captionStrong" color="nutritionInk">
                {index + 1}
              </Text>
            </View>
            <View style={styles.copy}>
              {step.title ? <Text variant="bodyStrong">{step.title}</Text> : null}
              {step.description ? <Text variant="body">{step.description}</Text> : null}
              {step.minutes !== undefined ? (
                <View style={styles.minutes}>
                  <SymbolView name="clock" tintColor={theme.colors.muted} size={theme.sizes.pendingGlyph} />
                  <Text variant="caption" color="muted">
                    {t('nutritionRecipe.minutes', { value: formatWhole(i18n.language, step.minutes) })}
                  </Text>
                </View>
              ) : null}
            </View>
          </View>
        ))}
      </DetailCard>
    </View>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    header: {
      flexDirection: 'row',
      alignItems: 'center',
      gap: theme.spacing.md,
      paddingBottom: theme.spacing.xs,
    },
    headerCopy: {
      flex: 1,
      gap: theme.spacing.hair,
    },
    cook: {
      height: theme.sizes.buttonMedium,
      flexDirection: 'row',
      alignItems: 'center',
      gap: theme.spacing.snug,
      paddingHorizontal: theme.spacing.xl,
      borderRadius: theme.radii.pill,
      borderWidth: theme.sizes.border,
      borderColor: theme.colors.nutrition,
      backgroundColor: theme.colors.nutritionSoft,
    },
    pressed: {
      opacity: theme.opacity.pressed,
    },
    row: {
      flexDirection: 'row',
      alignItems: 'flex-start',
      gap: theme.spacing.md,
      paddingVertical: theme.spacing.sm,
    },
    divided: {
      borderTopWidth: theme.sizes.border,
      borderTopColor: theme.colors.lineStrong,
    },
    badge: {
      width: theme.sizes.listIcon,
      height: theme.sizes.listIcon,
      alignItems: 'center',
      justifyContent: 'center',
      borderRadius: theme.radii.pill,
      backgroundColor: theme.colors.nutritionSoft,
    },
    copy: {
      flex: 1,
      gap: theme.spacing.hair,
    },
    minutes: {
      flexDirection: 'row',
      alignItems: 'center',
      gap: theme.spacing.xxs,
    },
  });
