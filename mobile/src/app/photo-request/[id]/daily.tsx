import { ActivityIndicator, Pressable, StyleSheet, View } from 'react-native';
import { Redirect, useRouter } from 'expo-router';
import { SymbolView } from 'expo-symbols';
import { useTranslation } from 'react-i18next';

import { getErrorStatus } from '@/api/errors';
import { Button } from '@/components/Button';
import { Card } from '@/components/Card';
import { Text } from '@/components/Text';
import { DayPhotoGroup } from '@/components/photoRequest/DayPhotoGroup';
import { DayStrip } from '@/components/photoRequest/DayStrip';
import { RequestMessage } from '@/components/photoRequest/RequestMessage';
import { coachLabel, formatClock } from '@/components/photoRequest/format';
import {
  useLeaveRequest,
  usePhotoRequest,
  useRequestCoach,
  useRequestId,
  useRequestProgress,
} from '@/components/photoRequest/hooks';
import { closedRoute, dailyAddRoute, flowRoute, isAnswerable, requestMode } from '@/components/photoRequest/model';
import { FlowFrame } from '@/components/questionnaire/FlowFrame';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

/** The reminder goes out at noon, so it is only worth showing before then. */
const REMINDER_HOUR = 12;

/** Every-day request: day N of M, the day strip, today's photos and the earlier days. */
export default function PhotoRequestDailyScreen() {
  const { t, i18n } = useTranslation();
  const router = useRouter();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const id = useRequestId();
  const leave = useLeaveRequest();
  const query = usePhotoRequest(id);
  const coach = useRequestCoach(query.data);
  const { progress, photos } = useRequestProgress(query.data);

  if (query.isPending) return <RequestMessage kind="loading" onClose={leave} />;

  if (query.isError) {
    // A request that is gone (or not the caller's) is a closed request, not a failure.
    if (getErrorStatus(query.error) === 404) return <Redirect href={closedRoute(id)} />;
    return (
      <RequestMessage kind="retry" body={t('photoRequest.errors.load')} onRetry={() => void query.refetch()} onClose={leave} />
    );
  }

  const current = query.data;
  // The backend completes an ended request up to an hour late: until then the app's own clock decides.
  if (!isAnswerable(current) || !progress) return <Redirect href={closedRoute(id)} />;
  const mode = requestMode(current);
  if (mode !== 'everyDay') return <Redirect href={flowRoute(current.id, mode)} />;

  const name = coachLabel(t, coach);
  const message = current.message?.trim();
  const showReminder = !progress.sentToday && new Date().getHours() < REMINDER_HOUR;
  const summary = progress.known
    ? [
        t('photoRequest.daily.sentDays', { count: progress.sentDays }),
        progress.missedDays > 0 ? t('photoRequest.daily.missedDays', { count: progress.missedDays }) : null,
      ]
        .filter(Boolean)
        .join(' · ')
    : '';
  const goAdd = () => router.push(dailyAddRoute(current.id));

  return (
    <FlowFrame
      header={{ onClose: leave }}
      contentStyle={styles.content}
      footer={
        <Button
          label={t(progress.sentToday ? 'photoRequest.daily.addMore' : 'photoRequest.daily.addToday')}
          onPress={goAdd}
          icon={<SymbolView name="photo.badge.plus" weight="medium" tintColor={theme.colors.onPrimary} size={theme.sizes.rowGlyph} />}
        />
      }
    >
      <Text variant="title" accessibilityRole="header">
        {t('photoRequest.daily.title')}
      </Text>

      <Card style={styles.card}>
        <View style={styles.heading}>
          <Text variant="subtitle" accessibilityRole="header">
            {t('photoRequest.daily.dayOf', { day: progress.day, total: progress.total })}
          </Text>
          {summary ? (
            <Text variant="caption" color="muted" style={styles.aside}>
              {summary}
            </Text>
          ) : null}
        </View>
        <DayStrip cells={progress.cells} />
      </Card>

      <Card style={styles.card}>
        <View style={styles.heading}>
          <Text variant="subtitle" accessibilityRole="header">
            {t('photoRequest.daily.today')}
          </Text>
          {progress.sentToday && progress.sentAt ? (
            <Text variant="caption" color="muted">
              {t('photoRequest.daily.sentAt', { time: formatClock(i18n.language, progress.sentAt) })}
            </Text>
          ) : showReminder ? (
            <Text variant="caption" color="muted">
              {t('photoRequest.daily.reminder')}
            </Text>
          ) : null}
        </View>
        {photos.isPending ? (
          <View accessibilityRole="progressbar" accessibilityLabel={t('photoRequest.loading')} style={styles.waiting}>
            <ActivityIndicator color={theme.colors.accent} />
          </View>
        ) : progress.sentToday ? (
          <DayPhotoGroup date={progress.today.date} photos={progress.today.photos} />
        ) : (
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t('photoRequest.daily.addToday')}
            onPress={goAdd}
            style={({ pressed }) => [styles.empty, pressed ? styles.pressed : null]}
          >
            <SymbolView name="camera" weight="medium" tintColor={theme.colors.muted} size={theme.sizes.paymentGlyph} />
            <Text variant="label" color="muted">
              {t('photoRequest.daily.none')}
            </Text>
          </Pressable>
        )}
        {message ? (
          <Text variant="bodySmall" color="ink2">
            <Text variant="bodySmall" color="ink2" style={styles.strong}>
              {t('photoRequest.daily.coachSays', { name })}
            </Text>{' '}
            {message}
          </Text>
        ) : null}
      </Card>

      {photos.isError ? (
        <Card style={styles.card}>
          <Text variant="subtitle" accessibilityRole="header">
            {t('photoRequest.daily.earlier')}
          </Text>
          <Text variant="body" color="muted" accessibilityRole="alert">
            {t('photoRequest.daily.earlierError')}
          </Text>
          <Button label={t('photoRequest.actions.retry')} size="small" onPress={() => void photos.refetch()} style={styles.retry} />
        </Card>
      ) : progress.earlier.length > 0 ? (
        <Card style={styles.card}>
          <Text variant="subtitle" accessibilityRole="header">
            {t('photoRequest.daily.earlier')}
          </Text>
          {progress.earlier.map((group) => (
            <DayPhotoGroup key={group.day} date={group.date} photos={group.photos} />
          ))}
        </Card>
      ) : null}
    </FlowFrame>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    content: {
      gap: theme.spacing.xl,
    },
    card: {
      gap: theme.spacing.md,
      paddingVertical: theme.spacing.lg,
      paddingHorizontal: theme.spacing.roomy,
    },
    heading: {
      flexDirection: 'row',
      alignItems: 'baseline',
      justifyContent: 'space-between',
      gap: theme.spacing.md,
    },
    aside: {
      flexShrink: 1,
      textAlign: 'right',
    },
    waiting: {
      paddingVertical: theme.spacing.xl,
    },
    empty: {
      alignItems: 'center',
      justifyContent: 'center',
      gap: theme.spacing.snug,
      paddingVertical: theme.spacing.huge,
      borderRadius: theme.radii.lg,
      borderWidth: theme.sizes.border,
      borderStyle: 'dashed',
      borderColor: theme.colors.muted,
    },
    pressed: {
      opacity: theme.opacity.pressed,
    },
    strong: {
      fontFamily: theme.typography.bodyStrong.fontFamily,
    },
    retry: {
      alignSelf: 'flex-start',
    },
  });
