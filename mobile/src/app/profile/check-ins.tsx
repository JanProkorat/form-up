import { useEffect, useMemo, useState } from 'react';
import { ActivityIndicator, FlatList, StyleSheet, View } from 'react-native';
import { useRouter } from 'expo-router';
import { useTranslation } from 'react-i18next';

import { Button } from '@/components/Button';
import { CheckInRow } from '@/components/checkIns/CheckInRow';
import { CheckInScheduleCard } from '@/components/checkIns/CheckInScheduleCard';
import { OpenNowCard } from '@/components/checkIns/OpenNowCard';
import {
  buildHistory,
  coachOptions,
  monthLabel,
  parseCalendarDate,
} from '@/components/checkIns/model';
import type { MonthGroup } from '@/components/checkIns/model';
import { Segmented } from '@/components/onboarding/Segmented';
import type { SegmentOption } from '@/components/onboarding/Segmented';
import { ListCard } from '@/components/profile/ListCard';
import { SectionHeader } from '@/components/profile/SectionHeader';
import { Screen } from '@/components/Screen';
import { ScreenHeader } from '@/components/ScreenHeader';
import { Text } from '@/components/Text';
import { useCheckInHistory } from '@/hooks/useCheckInHistory';
import { useCheckInSchedule } from '@/hooks/useCheckInSchedule';
import { useCollaborations } from '@/hooks/useCollaborations';
import { useProfileSummary } from '@/hooks/useProfileSummary';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

const ALL = 'all';

/** With a coach picked, pages keep loading until at least this many entries are visible. */
const MIN_VISIBLE_ENTRIES = 10;

/** The client's check-ins: those still open, then past ones grouped by month. */
export default function CheckInHistoryScreen() {
  const { t, i18n } = useTranslation();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const router = useRouter();
  const language = i18n.language;

  const history = useCheckInHistory();
  const schedule = useCheckInSchedule();
  const summary = useProfileSummary().data;
  const collaborations = useCollaborations().data;
  const [coachId, setCoachId] = useState<string>(ALL);

  const { items, hasNextPage, isFetchingNextPage, isNextPageError, fetchNextPage } = history;
  const coaches = useMemo(() => coachOptions(items), [items]);
  const showFilter = coaches.length > 1 || (collaborations?.length ?? 0) > 1;
  const activeCoach = coaches.some((coach) => coach.id === coachId) ? coachId : ALL;

  const [now] = useState(() => Date.now());
  const { open, months } = useMemo(() => {
    const visible =
      activeCoach === ALL ? items : items.filter((item) => item.professionalUserId === activeCoach);
    return buildHistory(visible, now);
  }, [items, activeCoach, now]);

  const visibleCount = open.length + months.reduce((sum, month) => sum + month.rows.length, 0);

  useEffect(() => {
    if (
      activeCoach !== ALL &&
      visibleCount < MIN_VISIBLE_ENTRIES &&
      hasNextPage &&
      !isFetchingNextPage &&
      !isNextPageError
    ) {
      fetchNextPage();
    }
  }, [activeCoach, visibleCount, hasNextPage, isFetchingNextPage, isNextPageError, fetchNextPage]);

  const options: SegmentOption<string>[] = [
    { value: ALL, label: t('checkInHistory.filterAll') },
    ...coaches.map((coach) => ({ value: coach.id, label: coach.firstName || coach.fullName })),
  ];

  const subtitle = (() => {
    if (!summary) return null;
    const count = summary.checkInCount ?? 0;
    const oldest = hasNextPage
      ? null
      : items
          .map((item) => parseCalendarDate(item.weekStartDate))
          .reduce<Date | null>((min, date) => (date && (!min || date < min) ? date : min), null);
    return oldest
      ? t('checkInHistory.countSince', { count, since: monthLabel(language, oldest, now) })
      : t('checkInHistory.count', { count });
  })();

  const header = (
    <View style={styles.header}>
      {showFilter && !history.isPending && !history.isError ? (
        <Segmented
          options={options}
          value={activeCoach}
          onChange={setCoachId}
          accessibilityLabel={t('checkInHistory.filterLabel')}
        />
      ) : null}
      {open.length > 0 ? (
        <View style={styles.openCards}>
          {open.map((item) => (
            <OpenNowCard key={item.id ?? `${item.professionalUserId}-${item.weekStartDate}`} item={item} />
          ))}
        </View>
      ) : null}
      <CheckInScheduleCard items={schedule.items} />
    </View>
  );

  const stillLoading = isFetchingNextPage || (activeCoach !== ALL && hasNextPage && !isNextPageError);

  const emptyState = (() => {
    if (history.isPending) {
      return (
        <View style={styles.state}>
          <ActivityIndicator color={theme.colors.accent} />
        </View>
      );
    }
    if (history.isError) {
      return (
        <View style={styles.state}>
          <Text variant="body" color="muted" style={styles.centered}>
            {t('checkInHistory.error')}
          </Text>
          <Button label={t('checkInHistory.retry')} onPress={history.refetch} />
        </View>
      );
    }
    if (open.length === 0 && months.length === 0 && !stillLoading && !isNextPageError) {
      return (
        <View style={styles.state}>
          <Text variant="body" color="muted" style={styles.centered}>
            {t('checkInHistory.empty')}
          </Text>
        </View>
      );
    }
    return null;
  })();

  const footer = isNextPageError ? (
    <View style={styles.state}>
      <Text variant="body" color="muted" style={styles.centered}>
        {t('checkInHistory.error')}
      </Text>
      <Button label={t('checkInHistory.retry')} onPress={fetchNextPage} />
    </View>
  ) : stillLoading ? (
    <ActivityIndicator color={theme.colors.accent} style={styles.footer} />
  ) : null;

  return (
    <Screen gutter={theme.spacing.xl} topGap={theme.spacing.safeAreaGap}>
      <View style={styles.topBar}>
        <ScreenHeader
          title={t('checkInHistory.title')}
          subtitle={subtitle ?? undefined}
          backLabel={t('onboarding.back')}
          onBack={() => router.back()}
        />
      </View>
      <FlatList<MonthGroup>
        data={history.isPending || history.isError ? [] : months}
        keyExtractor={(group) => group.key}
        renderItem={({ item: group }) => (
          <View style={styles.month}>
            <SectionHeader title={monthLabel(language, group.month, now)} />
            <ListCard>
              {group.rows.map(({ item, status }) => (
                <CheckInRow
                  key={item.id ?? `${item.professionalUserId}-${item.weekStartDate}`}
                  item={item}
                  status={status}
                />
              ))}
            </ListCard>
          </View>
        )}
        ListHeaderComponent={header}
        ListEmptyComponent={emptyState}
        ListFooterComponent={footer}
        onEndReached={() => {
          if (hasNextPage && !isFetchingNextPage && !isNextPageError) fetchNextPage();
        }}
        onEndReachedThreshold={0.4}
        showsVerticalScrollIndicator={false}
        style={styles.list}
        contentContainerStyle={styles.listContent}
      />
    </Screen>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    topBar: {
      paddingBottom: theme.spacing.lg,
    },
    list: {
      // Bleed into the screen gutter so the card shadow is not clipped at the list edge.
      marginHorizontal: -theme.spacing.xl,
    },
    listContent: {
      paddingHorizontal: theme.spacing.xl,
      paddingBottom: theme.spacing.huge,
      gap: theme.spacing.lg,
    },
    header: {
      gap: theme.spacing.lg,
    },
    openCards: {
      gap: theme.spacing.md,
    },
    month: {
      gap: theme.spacing.snug,
    },
    state: {
      alignItems: 'stretch',
      gap: theme.spacing.xl,
      paddingVertical: theme.spacing.huge,
    },
    centered: {
      textAlign: 'center',
    },
    footer: {
      paddingVertical: theme.spacing.xl,
    },
  });
