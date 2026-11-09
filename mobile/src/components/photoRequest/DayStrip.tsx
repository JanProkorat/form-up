import { StyleSheet, View } from 'react-native';
import { SymbolView } from 'expo-symbols';
import { useTranslation } from 'react-i18next';

import { Text } from '@/components/Text';
import { dayLetter, formatRequestDay } from '@/components/photoRequest/format';
import type { DayCell } from '@/components/photoRequest/model';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

const DAYS_PER_ROW = 7;

type DayStripProps = {
  cells: readonly DayCell[];
};

/** One dot per day of an every-day request: sent, missed, today or still to come. */
export function DayStrip({ cells }: DayStripProps) {
  const { t, i18n } = useTranslation();
  const theme = useTheme();
  const styles = makeStyles(theme);

  return (
    <View style={styles.strip}>
      {cells.map((cell) => {
        const date = formatRequestDay(i18n.language, cell.date);
        const label =
          cell.state === 'sent' || cell.state === 'missed'
            ? t(`photoRequest.daily.day.${cell.state}`, { date })
            : cell.isToday
              ? t('photoRequest.daily.dayToday', { date })
              : date;
        return (
          <View key={cell.day} style={styles.cell}>
            <Text variant={cell.isToday ? 'captionStrong' : 'caption'} color={cell.isToday ? 'ink' : 'muted'}>
              {dayLetter(i18n.language, cell.date)}
            </Text>
            <View
              accessible
              accessibilityLabel={label}
              style={[
                styles.dot,
                cell.state === 'sent' ? styles.dotSent : null,
                cell.state === 'missed' ? styles.dotMissed : null,
                cell.state === 'upcoming' || cell.state === 'unknown' ? styles.dotQuiet : null,
                cell.isToday && cell.state !== 'sent' ? styles.dotToday : null,
              ]}
            >
              {cell.state === 'sent' ? (
                <SymbolView name="checkmark" weight="bold" tintColor={theme.colors.onNutrition} size={theme.sizes.checkGlyph} />
              ) : cell.state === 'missed' ? (
                <Text variant="labelStrong" color="muted">
                  –
                </Text>
              ) : (
                <Text variant={cell.isToday ? 'bodyStrong' : 'label'} color={cell.isToday ? 'ink' : 'muted'}>
                  {cell.date.getDate()}
                </Text>
              )}
            </View>
          </View>
        );
      })}
    </View>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    strip: {
      flexDirection: 'row',
      flexWrap: 'wrap',
      rowGap: theme.spacing.md,
    },
    cell: {
      width: `${100 / DAYS_PER_ROW}%`,
      alignItems: 'center',
      gap: theme.spacing.snug,
    },
    dot: {
      width: theme.sizes.rowIcon,
      height: theme.sizes.rowIcon,
      borderRadius: theme.radii.pill,
      alignItems: 'center',
      justifyContent: 'center',
    },
    dotSent: {
      backgroundColor: theme.colors.nutrition,
    },
    dotMissed: {
      borderWidth: theme.sizes.border,
      borderStyle: 'dashed',
      borderColor: theme.colors.muted,
    },
    dotQuiet: {
      backgroundColor: theme.colors.quietPill,
    },
    dotToday: {
      borderWidth: theme.sizes.selectionRing,
      borderColor: theme.colors.ink,
    },
  });
