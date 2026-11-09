import { useState } from 'react';
import { Pressable, StyleSheet, View } from 'react-native';
import { Redirect, useRouter } from 'expo-router';
import { SymbolView } from 'expo-symbols';
import type { SFSymbol } from 'expo-symbols';
import { useTranslation } from 'react-i18next';

import { classifyPhotoRequestError } from '@/api/photoRequests';
import { PhotoDiaryStatus } from '@/api/generated';
import { planKind } from '@/api/plans';
import { getErrorStatus } from '@/api/errors';
import { Button } from '@/components/Button';
import { Card } from '@/components/Card';
import { Text } from '@/components/Text';
import { CoachHeader } from '@/components/photoRequest/CoachHeader';
import { DeclineSheet } from '@/components/photoRequest/DeclineSheet';
import { DetailRows } from '@/components/photoRequest/DetailRows';
import type { DetailRow } from '@/components/photoRequest/DetailRows';
import { RequestMessage } from '@/components/photoRequest/RequestMessage';
import { coachLabel, daysLabel, formatRequestDay } from '@/components/photoRequest/format';
import {
  useAcceptPhotoRequest,
  useDismissPhotoRequest,
  useLeaveRequest,
  usePhotoRequest,
  useRefreshPhotoRequests,
  useRequestCoach,
  useRequestId,
} from '@/components/photoRequest/hooks';
import {
  closedRoute,
  flowRoute,
  isAnswerable,
  requestDays,
  requestEndsAt,
  requestMode,
} from '@/components/photoRequest/model';
import { FlowFrame } from '@/components/questionnaire/FlowFrame';
import { useClientPlans } from '@/hooks/useClientPlans';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

/** A coach's photo request, every-day or once: what is asked, then Start (or Add photos) or Not now. */
export default function PhotoRequestScreen() {
  const { t, i18n } = useTranslation();
  const router = useRouter();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const id = useRequestId();
  const leave = useLeaveRequest();
  const query = usePhotoRequest(id);
  const request = query.data;
  const coach = useRequestCoach(request);
  const plans = useClientPlans();
  const refresh = useRefreshPhotoRequests();
  const accept = useAcceptPhotoRequest();
  const dismiss = useDismissPhotoRequest();
  const [declining, setDeclining] = useState(false);
  const [declineError, setDeclineError] = useState<string | null>(null);
  const [startError, setStartError] = useState<string | null>(null);

  if (query.isPending) return <RequestMessage kind="loading" onClose={leave} />;

  if (query.isError) {
    // A request that is gone (or not the caller's) is a closed request, not a failure.
    if (getErrorStatus(query.error) === 404) return <Redirect href={closedRoute(id)} />;
    return (
      <RequestMessage kind="retry" body={t('photoRequest.errors.load')} onRetry={() => void query.refetch()} onClose={leave} />
    );
  }

  // Narrowed once here: the handlers below run later and must not depend on the query's union.
  const current = query.data;
  if (!isAnswerable(current)) return <Redirect href={closedRoute(id)} />;

  const mode = requestMode(current);
  const everyDay = mode === 'everyDay';
  const pending = current.status === PhotoDiaryStatus.Pending;
  const name = coachLabel(t, coach);
  const days = daysLabel(t, requestDays(current));
  const endsAt = requestEndsAt(current);
  const plan = plans.data?.find((item) => item.planId?.toLowerCase() === current.planId?.toLowerCase());
  const message = current.message?.trim();

  const rows: DetailRow[] = [
    {
      key: 'howOften',
      symbol: 'arrow.triangle.2.circlepath',
      label: t('photoRequest.rows.howOften'),
      value: t(everyDay ? 'photoRequest.rows.everyDay' : 'photoRequest.rows.once'),
    },
    everyDay
      ? {
          key: 'until',
          symbol: 'calendar',
          label: t('photoRequest.rows.until'),
          value: t('photoRequest.rows.untilValue', { date: formatRequestDay(i18n.language, endsAt ?? undefined), days }),
        }
      : {
          key: 'deadline',
          symbol: 'calendar',
          label: t('photoRequest.rows.deadline'),
          value: t('photoRequest.rows.noDeadline'),
        },
  ];
  if (everyDay) {
    rows.push({
      key: 'reminder',
      symbol: 'bell',
      label: t('photoRequest.rows.reminder'),
      value: t('photoRequest.rows.reminderValue'),
    });
  }
  if (plan?.planName) {
    const planSymbol: SFSymbol = planKind(plan) === 'nutrition' ? 'leaf' : 'dumbbell';
    rows.push({ key: 'plan', symbol: planSymbol, label: t('photoRequest.rows.showsWith'), value: plan.planName });
  }

  const toClosed = async () => {
    // The request changed under the client (cancelled, ended): reload it, then explain.
    await refresh(current.id);
    router.replace(closedRoute(current.id));
  };

  const start = async () => {
    if (accept.isPending) return;
    setStartError(null);
    try {
      if (pending) await accept.mutateAsync(current.id);
      await refresh(current.id);
      router.replace(flowRoute(current.id, mode));
    } catch (error) {
      const failure = classifyPhotoRequestError(error);
      if (failure === 'closed') {
        await toClosed();
        return;
      }
      setStartError(t(failure === 'network' ? 'photoRequest.errors.network' : 'photoRequest.errors.accept'));
    }
  };

  const decline = async (reason: string) => {
    if (dismiss.isPending) return;
    setDeclineError(null);
    try {
      await dismiss.mutateAsync({ id: current.id, reason });
      await refresh(current.id);
      setDeclining(false);
      leave();
    } catch (error) {
      const failure = classifyPhotoRequestError(error);
      if (failure === 'closed') {
        setDeclining(false);
        await toClosed();
        return;
      }
      if (failure === 'invalidReason') setDeclineError(t('photoRequest.decline.errors.invalidReason'));
      else setDeclineError(t(failure === 'network' ? 'photoRequest.decline.errors.network' : 'photoRequest.decline.errors.generic'));
    }
  };

  return (
    <>
      <FlowFrame
        header={{ onClose: leave }}
        contentStyle={styles.content}
        footer={
          <>
            {startError ? (
              <Text variant="label" color="error" accessibilityRole="alert" style={styles.centered}>
                {startError}
              </Text>
            ) : null}
            {pending ? (
              <Pressable
                accessibilityRole="button"
                disabled={accept.isPending}
                onPress={() => {
                  setDeclineError(null);
                  setDeclining(true);
                }}
                style={styles.notNow}
              >
                <Text variant="bodyStrong" color="ink2">
                  {t('photoRequest.actions.notNow')}
                </Text>
              </Pressable>
            ) : null}
            <Button
              label={t(everyDay ? 'photoRequest.actions.start' : 'photoRequest.actions.addPhotos')}
              onPress={() => void start()}
              disabled={accept.isPending}
              icon={
                everyDay ? undefined : (
                  <SymbolView name="photo.badge.plus" weight="medium" tintColor={theme.colors.onPrimary} size={theme.sizes.rowGlyph} />
                )
              }
            />
          </>
        }
      >
        <CoachHeader coach={coach} />
        <View style={styles.intro}>
          <Text variant="title" accessibilityRole="header">
            {t('photoRequest.title', { name })}
          </Text>
          <Text variant="bodyLarge" color="muted">
            {t(everyDay ? 'photoRequest.subtitle.everyDay' : 'photoRequest.subtitle.once', { name, days })}
          </Text>
        </View>
        {message ? (
          <View style={styles.note}>
            <Text variant="labelStrong" color="muted">
              {t('photoRequest.note', { name })}
            </Text>
            <Text variant="bodyLarge">{message}</Text>
          </View>
        ) : null}
        <Card style={styles.card}>
          <DetailRows rows={rows} />
        </Card>
      </FlowFrame>
      <DeclineSheet
        visible={declining}
        name={name}
        busy={dismiss.isPending}
        error={declineError}
        onSubmit={(reason) => void decline(reason)}
        onClose={() => setDeclining(false)}
      />
    </>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    content: {
      gap: theme.spacing.xxl,
    },
    intro: {
      gap: theme.spacing.xs,
    },
    note: {
      gap: theme.spacing.xxs,
      padding: theme.spacing.xl,
      borderRadius: theme.radii.xl,
      backgroundColor: theme.colors.subtle,
    },
    card: {
      paddingVertical: theme.spacing.xs,
      paddingHorizontal: theme.spacing.xl,
    },
    notNow: {
      alignItems: 'center',
      justifyContent: 'center',
      minHeight: theme.sizes.buttonMedium,
    },
    centered: {
      textAlign: 'center',
    },
  });
