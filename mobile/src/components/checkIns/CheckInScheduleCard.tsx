import { StyleSheet, View } from 'react-native';
import { SymbolView } from 'expo-symbols';
import { useTranslation } from 'react-i18next';

import type { CheckInScheduleItem } from '@/api/checkIns';
import { coachFullName } from '@/api/professionals';
import { CoachAvatar } from '@/components/coach/CoachAvatar';
import { scheduleTime, splitName, weekdayKey } from '@/components/checkIns/model';
import { ListCard } from '@/components/profile/ListCard';
import { SectionHeader } from '@/components/profile/SectionHeader';
import { Text } from '@/components/Text';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

type CheckInScheduleCardProps = {
  items: readonly CheckInScheduleItem[];
};

/** "When check-ins arrive": one row per coach with their chosen day, time and answer deadline. */
export function CheckInScheduleCard({ items }: CheckInScheduleCardProps) {
  const { t } = useTranslation();
  const theme = useTheme();
  const styles = makeStyles(theme);

  const rows = items.flatMap((item) => {
    const send = weekdayKey(item.sendWeekday);
    if (!send) return [];
    const until = weekdayKey(item.answerUntilWeekday);
    const name = splitName(item.professionalName);
    const coach = coachFullName(name);
    const title = t('checkInHistory.schedule.title', {
      weekday: t(`checkInHistory.schedule.every.${send}`),
      time: scheduleTime(item.sendTime),
    });
    const caption = until
      ? t('checkInHistory.schedule.caption', {
          coach,
          weekday: t(`checkInHistory.schedule.until.${until}`),
        })
      : coach;
    return [{ key: item.professionalPublicId ?? `${coach}-${send}`, name, title, caption }];
  });

  if (rows.length === 0) return null;

  return (
    <View style={styles.root}>
      <SectionHeader title={t('checkInHistory.schedule.label')} />
      <ListCard>
        {rows.map((row) => (
          <View
            key={row.key}
            accessible
            accessibilityLabel={`${row.title}, ${row.caption}`}
            style={styles.row}
          >
            <CoachAvatar coach={row.name} size={theme.sizes.chatAvatar} initialsVariant="chipSmall" />
            <View style={styles.copy}>
              <Text variant="body" numberOfLines={1}>
                {row.title}
              </Text>
              <Text variant="caption" color="muted" numberOfLines={2}>
                {row.caption}
              </Text>
            </View>
            <View style={styles.chip}>
              <SymbolView
                name="calendar"
                tintColor={theme.colors.muted}
                size={theme.sizes.listChevron}
              />
            </View>
          </View>
        ))}
      </ListCard>
    </View>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    root: {
      gap: theme.spacing.snug,
    },
    row: {
      flexDirection: 'row',
      alignItems: 'center',
      gap: theme.spacing.md,
      paddingVertical: theme.spacing.md,
    },
    copy: {
      flex: 1,
      minWidth: 0,
      gap: theme.spacing.xxs / 2,
    },
    chip: {
      width: theme.sizes.dayChip,
      height: theme.sizes.dayChip,
      borderRadius: theme.radii.pill,
      alignItems: 'center',
      justifyContent: 'center',
      backgroundColor: theme.colors.subtle,
    },
  });
