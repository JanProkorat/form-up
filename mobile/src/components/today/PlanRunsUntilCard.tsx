import { StyleSheet, View } from 'react-native';
import { SymbolView } from 'expo-symbols';
import { useTranslation } from 'react-i18next';

import { isoDay } from '@/components/nutrition/plan/model';
import { Text } from '@/components/Text';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

import type { CoachRole } from './collaborationRoles';
import { roleLook } from './roleLook';

type PlanRunsUntilCardProps = {
  role: CoachRole;
  /** Last day the plan runs, `yyyy-MM-dd`. */
  runsUntil: string;
  /** The coach's first name; without it the copy names nobody. */
  coachName?: string;
};

/** "14 Nov" in the app language, formatted in UTC so the calendar day never shifts with the device time zone. */
export function runsUntilLabel(language: string, runsUntil: string): string {
  return new Intl.DateTimeFormat(language, { day: 'numeric', month: 'short', timeZone: 'UTC' }).format(
    new Date(`${isoDay(runsUntil)}T00:00:00Z`),
  );
}

/** Tells the client their plan stays up to a date because the coach is not adding new weeks; nothing is disabled. */
export function PlanRunsUntilCard({ role, runsUntil, coachName }: PlanRunsUntilCardProps) {
  const { t, i18n } = useTranslation();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const look = roleLook(theme, role);
  const date = runsUntilLabel(i18n.language, runsUntil);

  return (
    <View accessible accessibilityRole="summary" style={styles.card}>
      <View
        style={[styles.icon, { backgroundColor: look.soft }]}
        accessibilityElementsHidden
        importantForAccessibility="no-hide-descendants"
      >
        <SymbolView name="calendar" tintColor={look.ink} size={theme.sizes.bellGlyph} />
      </View>
      <View style={styles.copy}>
        <Text variant="bodyStrong">{t('today.runsUntil.title', { date })}</Text>
        <Text variant="caption" color="muted">
          {coachName
            ? t('today.runsUntil.body', { name: coachName, date })
            : t('today.runsUntil.bodyNoName', { date })}
        </Text>
      </View>
    </View>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    card: {
      flexDirection: 'row',
      alignItems: 'flex-start',
      gap: theme.spacing.md,
      padding: theme.spacing.lg,
      borderRadius: theme.radii.xl,
      borderWidth: theme.sizes.border,
      borderColor: theme.colors.line,
      backgroundColor: theme.colors.surface,
      boxShadow: theme.shadows.card,
    },
    icon: {
      width: theme.sizes.coachIcon,
      height: theme.sizes.coachIcon,
      borderRadius: theme.radii.pill,
      alignItems: 'center',
      justifyContent: 'center',
    },
    copy: {
      flex: 1,
      minWidth: 0,
      gap: theme.spacing.xxs,
    },
  });
