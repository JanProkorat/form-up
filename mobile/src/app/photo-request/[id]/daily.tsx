import { useRef, useState } from 'react';
import { ActivityIndicator, Alert, Linking, StyleSheet, View } from 'react-native';
import { GestureDetector } from 'react-native-gesture-handler';
import Reanimated from 'react-native-reanimated';
import { Redirect, useRouter } from 'expo-router';
import { SymbolView } from 'expo-symbols';
import { useTranslation } from 'react-i18next';

import { classifyPhotoUploadError } from '@/api/photoRequests';
import type { PhotoUploadFailureKind } from '@/api/photoRequests';
import type { PickedPhoto, ProgressPhoto } from '@/api/photos';
import { getErrorStatus } from '@/api/errors';
import { Button } from '@/components/Button';
import { Card } from '@/components/Card';
import { Text } from '@/components/Text';
import { DayPhotoGallery } from '@/components/photoRequest/DayPhotoGallery';
import { DayStrip } from '@/components/photoRequest/DayStrip';
import { PhotoNoteRow } from '@/components/photoRequest/PhotoNoteRow';
import type { PhotoRowStatus } from '@/components/photoRequest/PhotoNoteRow';
import { RequestMessage } from '@/components/photoRequest/RequestMessage';
import { coachLabel, formatClock, formatRequestDay } from '@/components/photoRequest/format';
import {
  useLeaveRequest,
  usePhotoRequest,
  useRefreshPhotoRequests,
  useRequestCoach,
  useRequestId,
  useRequestProgress,
  useUploadRequestPhoto,
} from '@/components/photoRequest/hooks';
import { closedRoute, dailySentRoute, flowRoute, isAnswerable, requestMode } from '@/components/photoRequest/model';
import { MAX_REQUEST_PHOTOS, pickRequestPhotos } from '@/components/photoRequest/pickRequestPhotos';
import type { RequestPhotoSource } from '@/components/photoRequest/pickRequestPhotos';
import { PhotoViewer } from '@/components/photos/PhotoViewer';
import { FlowFrame } from '@/components/questionnaire/FlowFrame';
import { useDeletePhoto } from '@/hooks/useDeletePhoto';
import { useProfile } from '@/hooks/useProfile';
import { useHorizontalSwipe } from '@/hooks/useHorizontalSwipe';
import type { SwipeDirection } from '@/hooks/useHorizontalSwipe';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

/** The reminder goes out at noon, so it is only worth showing before then. */
const REMINDER_HOUR = 12;

/** One picked photo. It stays here, with its note, until it is on the request. */
type PhotoRow = {
  key: number;
  photo: PickedPhoto;
  note: string;
  status: PhotoRowStatus;
  failure?: Exclude<PhotoUploadFailureKind, 'closed'> | undefined;
};

/**
 * Every-day request: day N of M and one page per day up to today. Today's page takes the photos and sends them; an
 * earlier page shows what was sent that day. The request is never submitted: that would complete it and refuse every
 * later upload, so the backend completes it itself once the days are over.
 */
export default function PhotoRequestDailyScreen() {
  const { t, i18n } = useTranslation();
  const router = useRouter();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const id = useRequestId();
  const leave = useLeaveRequest();
  const query = usePhotoRequest(id);
  const coach = useRequestCoach(query.data);
  // Read fresh on every visit: photos uploaded before the client left must show again as sent.
  const { progress, photos } = useRequestProgress(query.data, { fresh: true });
  const refresh = useRefreshPhotoRequests();
  const upload = useUploadRequestPhoto();
  const [rows, setRows] = useState<PhotoRow[]>([]);
  const [sending, setSending] = useState(false);
  const [picking, setPicking] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [picked, setPicked] = useState<number | null>(null);
  const nextKey = useRef(1);
  // The viewer a past day's photo opens, with the same delete rules as Progress photos.
  const [viewerId, setViewerId] = useState<string | null>(null);
  const { data: profile } = useProfile();
  const remove = useDeletePhoto();
  // A tap on the strip jumps several days at once; the slide then lands on this day instead of the next one.
  const jumpTo = useRef<number | null>(null);

  const todayDay = progress?.day ?? 1;
  const shown = Math.min(picked ?? todayDay, todayDay);
  const swipeDay = (direction: SwipeDirection) => {
    const target = jumpTo.current ?? shown + (direction === 'next' ? 1 : -1);
    jumpTo.current = null;
    setPicked(Math.min(Math.max(target, 1), todayDay));
  };
  const swipe = useHorizontalSwipe({
    onSwipe: swipeDay,
    canGoPrevious: shown > 1,
    canGoNext: shown < todayDay,
    contentKey: String(shown),
  });

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
  // The backend completes an ended request up to an hour late: until then the app's own clock decides.
  if (!isAnswerable(current) || !progress) return <Redirect href={closedRoute(id)} />;
  const mode = requestMode(current);
  if (mode !== 'everyDay') return <Redirect href={flowRoute(current.id, mode)} />;

  const name = coachLabel(t, coach);
  const message = current.message?.trim();
  const isToday = shown === progress.day;
  const sentRows = progress.today.photos;
  const pending = rows.filter((row) => row.status !== 'done');
  const busy = sending || picking;
  const full = sentRows.length + rows.length >= MAX_REQUEST_PHOTOS;
  const showReminder = !progress.sentToday && new Date().getHours() < REMINDER_HOUR;
  const summary = progress.known
    ? [
        t('photoRequest.daily.sentDays', { count: progress.sentDays }),
        progress.missedDays > 0 ? t('photoRequest.daily.missedDays', { count: progress.missedDays }) : null,
      ]
        .filter(Boolean)
        .join(' · ')
    : '';
  const shownCell = progress.cells[shown - 1];
  const pageLabel = t('photoRequest.daily.pageLabel', {
    day: shown,
    total: progress.total,
    date: shownCell ? formatRequestDay(i18n.language, shownCell.date) : '',
  });

  const selectDay = (day: number) => {
    if (day === shown || day < 1 || day > progress.day) return;
    jumpTo.current = day;
    swipe.slide(day > shown ? 'next' : 'previous');
  };

  const patchRow = (key: number, patch: Partial<Omit<PhotoRow, 'key' | 'photo'>>) =>
    setRows((list) => list.map((row) => (row.key === key ? { ...row, ...patch } : row)));

  const toClosed = async () => {
    // The request changed under the client (cancelled, ended, gone): reload it, then explain.
    await refresh(current.id);
    router.replace(closedRoute(current.id));
  };

  const pick = async (source: RequestPhotoSource) => {
    setPicking(true);
    try {
      const result = await pickRequestPhotos(source, MAX_REQUEST_PHOTOS - sentRows.length - rows.length);
      if (result.kind === 'denied') {
        Alert.alert(t('photos.permission.title'), t('photos.permission.body'), [
          { text: t('photos.cancel'), style: 'cancel' },
          { text: t('photos.permission.openSettings'), onPress: () => void Linking.openSettings() },
        ]);
      } else if (result.kind === 'picked') {
        const added = result.photos.map<PhotoRow>((photo) => ({ key: nextKey.current++, photo, note: '', status: 'idle' }));
        setRows((list) => [...list, ...added].slice(0, Math.max(MAX_REQUEST_PHOTOS - sentRows.length, 0)));
        setNotice(null);
      }
    } finally {
      setPicking(false);
    }
  };

  const askSource = () => {
    Alert.alert(t('photos.add.title'), undefined, [
      { text: t('photos.add.camera'), onPress: () => void pick('camera') },
      { text: t('photos.add.library'), onPress: () => void pick('library') },
      { text: t('photos.cancel'), style: 'cancel' },
    ]);
  };

  const send = async () => {
    if (busy || pending.length === 0) return;
    setSending(true);
    setNotice(null);
    try {
      // Every photo that uploads is on the request at once, so only the rows not done yet are sent again.
      let failed = false;
      let offline = false;
      for (const row of pending) {
        patchRow(row.key, { status: 'uploading', failure: undefined });
        try {
          await upload.mutateAsync({
            photo: row.photo,
            target: { requestId: current.id, planId: current.planId, note: row.note },
          });
          patchRow(row.key, { status: 'done' });
        } catch (error) {
          const failure = classifyPhotoUploadError(error);
          if (failure === 'closed') {
            await toClosed();
            return;
          }
          patchRow(row.key, { status: 'failed', failure });
          failed = true;
          // The connection is down: the rest would fail the same way.
          if (failure === 'network') {
            offline = true;
            break;
          }
        }
      }
      // Rows left untouched by a stop are idle again.
      setRows((list) => list.map((row) => (row.status === 'uploading' ? { ...row, status: 'idle' } : row)));
      if (failed) {
        setNotice(t('photoRequest.once.errors.someFailed'));
        if (!offline) {
          // The photos that went through now come from the request's own list, so they leave the picked rows.
          await refresh(current.id);
          setRows((list) => list.filter((row) => row.status !== 'done'));
        }
        return;
      }

      router.replace(dailySentRoute(current.id, rows.length));
      void refresh(current.id);
    } finally {
      setSending(false);
    }
  };

  const sendLabel =
    pending.length === 0
      ? t('photoRequest.once.sendNone', { name })
      : t('photoRequest.once.send', { count: pending.length, name });

  const photosError = photos.isError ? (
    <View style={styles.problem}>
      <Text variant="body" color="muted" accessibilityRole="alert">
        {t('photoRequest.daily.photosError')}
      </Text>
      <Button label={t('photoRequest.actions.retry')} size="small" onPress={() => void photos.refetch()} style={styles.retry} />
    </View>
  ) : null;

  const waiting = (
    <View accessibilityRole="progressbar" accessibilityLabel={t('photoRequest.loading')} style={styles.waiting}>
      <ActivityIndicator color={theme.colors.accent} />
    </View>
  );

  const todayPage = (
    <>
      {message ? (
        <Card style={styles.note}>
          <Text variant="captionStrong" color="muted">
            {t('photoRequest.note', { name })}
          </Text>
          <Text variant="body" color="ink2">
            {message}
          </Text>
        </Card>
      ) : null}
      <Card style={styles.photos}>
        <View style={styles.heading}>
          <Text variant="subtitle" accessibilityRole="header">
            {t('photoRequest.daily.todaysPhotos')}
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
        {photosError}
        {photos.isPending ? waiting : null}
        {sentRows.length === 0 && rows.length === 0 && !photos.isPending && !photos.isError ? (
          <Text variant="body" color="muted">
            {t('photoRequest.once.empty', { name })}
          </Text>
        ) : null}
        {sentRows.length > 0 || rows.length > 0 ? (
          <View>
            {sentRows.map((photo, index) => (
              <View key={photo.id} style={index > 0 ? styles.divider : undefined}>
                <PhotoNoteRow
                  uri={photo.displayUrl}
                  position={index + 1}
                  name={name}
                  note={photo.description ?? ''}
                  onChangeNote={() => undefined}
                  status="done"
                  editable={false}
                />
              </View>
            ))}
            {rows.map((row, index) => (
              <View key={row.key} style={sentRows.length + index > 0 ? styles.divider : undefined}>
                <PhotoNoteRow
                  uri={row.photo.uri}
                  position={sentRows.length + index + 1}
                  name={name}
                  note={row.note}
                  onChangeNote={(note) => patchRow(row.key, { note })}
                  onRemove={row.status === 'done' ? undefined : () => setRows((list) => list.filter((item) => item.key !== row.key))}
                  status={row.status}
                  error={row.failure ? t(`photoRequest.once.errors.${row.failure}`) : undefined}
                  editable={!sending}
                />
              </View>
            ))}
          </View>
        ) : null}
        {full ? null : (
          <Button
            label={t(sentRows.length + rows.length === 0 ? 'photoRequest.actions.addPhotos' : 'photoRequest.once.addMore')}
            onPress={askSource}
            disabled={busy}
            size={sentRows.length + rows.length === 0 ? 'large' : 'small'}
            style={sentRows.length + rows.length === 0 ? undefined : styles.addMore}
            icon={
              picking ? (
                <ActivityIndicator color={theme.colors.onPrimary} />
              ) : (
                <SymbolView name="photo.badge.plus" weight="medium" tintColor={theme.colors.onPrimary} size={theme.sizes.rowGlyph} />
              )
            }
          />
        )}
      </Card>
    </>
  );

  const group = progress.earlier.find((item) => item.day === shown);
  const dayPhotos: readonly ProgressPhoto[] = group?.photos ?? [];
  const isOwnPhoto = (photo: ProgressPhoto) =>
    photo.uploadedByUserId !== undefined &&
    profile?.userId !== undefined &&
    photo.uploadedByUserId.toLowerCase() === profile.userId.toLowerCase();
  const deleteViewed = (photo: ProgressPhoto) => {
    // After a delete the viewer moves to the next photo of the day, or the previous one, and closes when none is left.
    const at = dayPhotos.findIndex((item) => item.id === photo.id);
    const neighbour = dayPhotos[at + 1] ?? dayPhotos[at - 1];
    remove.mutate(photo.id, {
      onSuccess: () => setViewerId(neighbour?.id ?? null),
      onError: () => Alert.alert(t('photos.errors.delete')),
    });
  };
  const pastPage = (
    <Card style={styles.photos}>
      {photos.isPending ? (
        waiting
      ) : photos.isError ? (
        photosError
      ) : group && shownCell ? (
        <DayPhotoGallery date={shownCell.date} photos={group.photos} onOpen={(photo) => setViewerId(photo.id)} />
      ) : (
        <View style={styles.missed}>
          <SymbolView name="camera" weight="medium" tintColor={theme.colors.muted} size={theme.sizes.paymentGlyph} />
          <Text variant="label" color="muted" style={styles.centered}>
            {t('photoRequest.daily.missedDay', { date: shownCell ? formatRequestDay(i18n.language, shownCell.date) : '' })}
          </Text>
        </View>
      )}
    </Card>
  );

  return (
    <>
    <FlowFrame
      header={{ onBack: leave, title: t('photoRequest.daily.title') }}
      contentStyle={styles.content}
      footer={
        isToday ? (
          <>
            {notice ? (
              <Text variant="label" color="error" accessibilityRole="alert" style={styles.centered}>
                {notice}
              </Text>
            ) : null}
            <Button
              label={sending ? t('photoRequest.once.sending') : sendLabel}
              onPress={() => void send()}
              disabled={busy || pending.length === 0}
              icon={
                sending ? (
                  <ActivityIndicator color={theme.colors.onPrimary} />
                ) : (
                  <SymbolView name="arrow.up" weight="semibold" tintColor={theme.colors.onPrimary} size={theme.sizes.rowGlyph} />
                )
              }
            />
          </>
        ) : null
      }
    >
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
        <DayStrip cells={progress.cells} selected={shown} onSelect={selectDay} />
      </Card>

      <GestureDetector gesture={swipe.gesture}>
        <Reanimated.View accessibilityLabel={pageLabel} style={[styles.page, swipe.style]}>
          {isToday ? todayPage : pastPage}
        </Reanimated.View>
      </GestureDetector>
    </FlowFrame>
    <PhotoViewer
      photos={dayPhotos}
      selectedId={viewerId}
      canDelete={isOwnPhoto}
      deleting={remove.isPending}
      onSelect={setViewerId}
      onClose={() => setViewerId(null)}
      onDelete={deleteViewed}
    />
    </>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    content: {
      gap: theme.spacing.xl,
    },
    page: {
      gap: theme.spacing.xl,
    },
    card: {
      gap: theme.spacing.md,
      paddingVertical: theme.spacing.lg,
      paddingHorizontal: theme.spacing.roomy,
    },
    note: {
      gap: theme.spacing.xxs,
      paddingVertical: theme.spacing.md,
      paddingHorizontal: theme.spacing.roomy,
    },
    photos: {
      gap: theme.spacing.md,
      paddingVertical: theme.spacing.md,
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
    divider: {
      borderTopWidth: theme.sizes.border,
      borderTopColor: theme.colors.line,
    },
    waiting: {
      paddingVertical: theme.spacing.xl,
    },
    problem: {
      gap: theme.spacing.md,
    },
    missed: {
      alignItems: 'center',
      justifyContent: 'center',
      gap: theme.spacing.snug,
      paddingVertical: theme.spacing.huge,
      borderRadius: theme.radii.lg,
      borderWidth: theme.sizes.border,
      borderStyle: 'dashed',
      borderColor: theme.colors.muted,
    },
    addMore: {
      alignSelf: 'center',
    },
    centered: {
      textAlign: 'center',
    },
    retry: {
      alignSelf: 'flex-start',
    },
  });
