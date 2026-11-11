import { Pressable, StyleSheet } from 'react-native';
import { SymbolView } from 'expo-symbols';
import { useTranslation } from 'react-i18next';

import { formatWhole } from '@/components/nutrition/meal/format';
import { Text } from '@/components/Text';
import type { StepTimerView } from '@/hooks/useStepTimers';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

const SECONDS_PER_MINUTE = 60;
const SECOND_DIGITS = 2;

const clock = (seconds: number): string =>
  `${Math.floor(seconds / SECONDS_PER_MINUTE)}:${String(seconds % SECONDS_PER_MINUTE).padStart(SECOND_DIGITS, '0')}`;

type StepTimerPillProps = {
  minutes: number;
  timer: StepTimerView;
  onStart: () => void;
};

/** "Start N min", then the time left, then "Done · N min". Only the idle pill can be pressed. */
export function StepTimerPill({ minutes, timer, onStart }: StepTimerPillProps) {
  const { t, i18n } = useTranslation();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const value = formatWhole(i18n.language, minutes);
  const label =
    timer.status === 'running'
      ? t('nutritionRecipe.cook.running', { time: clock(timer.remainingSeconds) })
      : timer.status === 'done'
        ? t('nutritionRecipe.cook.done', { value })
        : t('nutritionRecipe.cook.start', { value });
  const idle = timer.status === 'idle';

  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={label}
      accessibilityState={{ disabled: !idle }}
      disabled={!idle}
      onPress={onStart}
      style={({ pressed }) => [styles.pill, timer.status === 'running' && styles.running, pressed && styles.pressed]}
    >
      <SymbolView name="clock" tintColor={theme.colors.nutritionInk} size={theme.sizes.pendingGlyph} />
      <Text variant="labelStrong" color="nutritionInk">
        {label}
      </Text>
    </Pressable>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    pill: {
      alignSelf: 'flex-start',
      height: theme.sizes.chip,
      flexDirection: 'row',
      alignItems: 'center',
      gap: theme.spacing.snug,
      paddingHorizontal: theme.spacing.md,
      borderRadius: theme.radii.pill,
      borderWidth: theme.sizes.border,
      borderColor: theme.colors.lineStrong,
    },
    running: {
      borderColor: theme.colors.nutrition,
    },
    pressed: {
      opacity: theme.opacity.pressed,
    },
  });
