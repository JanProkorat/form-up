import { useRef, useState } from 'react';
import { ActivityIndicator, Alert, Linking, StyleSheet, View } from 'react-native';
import { Redirect, useRouter } from 'expo-router';
import { SymbolView } from 'expo-symbols';
import { useTranslation } from 'react-i18next';

import { classifyPhotoUploadError } from '@/api/photoRequests';
import type { PhotoUploadFailureKind } from '@/api/photoRequests';
import type { PickedPhoto } from '@/api/photos';
import { getErrorStatus } from '@/api/errors';
import { Button } from '@/components/Button';
import { Card } from '@/components/Card';
import { Text } from '@/components/Text';
import { PhotoNoteRow } from '@/components/photoRequest/PhotoNoteRow';
import type { PhotoRowStatus } from '@/components/photoRequest/PhotoNoteRow';
import { RequestMessage } from '@/components/photoRequest/RequestMessage';
import { coachLabel } from '@/components/photoRequest/format';
import {
  useLeaveRequest,
  usePhotoRequest,
  useRefreshPhotoRequests,
  useRequestCoach,
  useRequestId,
  useUploadRequestPhoto,
} from '@/components/photoRequest/hooks';
import { closedRoute, dailySentRoute, flowRoute, isAnswerable, requestMode } from '@/components/photoRequest/model';
import { MAX_REQUEST_PHOTOS, pickRequestPhotos } from '@/components/photoRequest/pickRequestPhotos';
import type { RequestPhotoSource } from '@/components/photoRequest/pickRequestPhotos';
import { FlowFrame } from '@/components/questionnaire/FlowFrame';
import { useTheme } from '@/theme';
import type { Theme } from '@/theme';

/** One picked photo. It stays here, with its note, until it is on the request. */
type PhotoRow = {
  key: number;
  photo: PickedPhoto;
  note: string;
  status: PhotoRowStatus;
  failure?: Exclude<PhotoUploadFailureKind, 'closed'> | undefined;
};

/**
 * Every-day request: pick today's photos, add a note to each, send them. The request is never submitted: that would
 * complete it and refuse every later upload, so the backend completes it itself once the days are over.
 */
export default function PhotoRequestDailyAddScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const theme = useTheme();
  const styles = makeStyles(theme);
  const id = useRequestId();
  const leave = useLeaveRequest();
  const query = usePhotoRequest(id);
  const coach = useRequestCoach(query.data);
  const refresh = useRefreshPhotoRequests();
  const upload = useUploadRequestPhoto();
  const [rows, setRows] = useState<PhotoRow[]>([]);
  const [sending, setSending] = useState(false);
  const [picking, setPicking] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const nextKey = useRef(1);

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
  if (!isAnswerable(current)) return <Redirect href={closedRoute(id)} />;
  const mode = requestMode(current);
  if (mode !== 'everyDay') return <Redirect href={flowRoute(current.id, mode)} />;

  const name = coachLabel(t, coach);
  const message = current.message?.trim();
  const busy = sending || picking;
  const full = rows.length >= MAX_REQUEST_PHOTOS;

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
      const result = await pickRequestPhotos(source, MAX_REQUEST_PHOTOS - rows.length);
      if (result.kind === 'denied') {
        Alert.alert(t('photos.permission.title'), t('photos.permission.body'), [
          { text: t('photos.cancel'), style: 'cancel' },
          { text: t('photos.permission.openSettings'), onPress: () => void Linking.openSettings() },
        ]);
      } else if (result.kind === 'picked') {
        const added = result.photos.map<PhotoRow>((photo) => ({ key: nextKey.current++, photo, note: '', status: 'idle' }));
        setRows((list) => [...list, ...added].slice(0, MAX_REQUEST_PHOTOS));
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
    if (busy || rows.length === 0) return;
    setSending(true);
    setNotice(null);
    try {
      // Every photo that uploads is on the request at once, so only the rows not done yet are sent again.
      let failed = false;
      for (const row of rows.filter((item) => item.status !== 'done')) {
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
          if (failure === 'network') break;
        }
      }
      // Rows left untouched by a stop are idle again.
      setRows((list) => list.map((row) => (row.status === 'uploading' ? { ...row, status: 'idle' } : row)));
      if (failed) {
        setNotice(t('photoRequest.once.errors.someFailed'));
        return;
      }

      router.replace(dailySentRoute(current.id, rows.length));
      void refresh(current.id);
    } finally {
      setSending(false);
    }
  };

  const sendLabel =
    rows.length === 0 ? t('photoRequest.once.sendNone', { name }) : t('photoRequest.once.send', { count: rows.length, name });

  return (
    <FlowFrame
      header={{ onClose: leave }}
      contentStyle={styles.content}
      footer={
        <>
          {notice ? (
            <Text variant="label" color="error" accessibilityRole="alert" style={styles.centered}>
              {notice}
            </Text>
          ) : null}
          <Button
            label={sending ? t('photoRequest.once.sending') : sendLabel}
            onPress={() => void send()}
            disabled={busy || rows.length === 0}
            icon={
              sending ? (
                <ActivityIndicator color={theme.colors.onPrimary} />
              ) : (
                <SymbolView name="arrow.up" weight="semibold" tintColor={theme.colors.onPrimary} size={theme.sizes.rowGlyph} />
              )
            }
          />
        </>
      }
    >
      <Text variant="title" accessibilityRole="header">
        {t('photoRequest.daily.title')}
      </Text>
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
          {rows.length > 0 ? (
            <Text variant="caption" color="muted">
              {t('photoRequest.once.added', { count: rows.length })}
            </Text>
          ) : null}
        </View>
        {rows.length === 0 ? (
          <Text variant="body" color="muted">
            {t('photoRequest.once.empty', { name })}
          </Text>
        ) : (
          <View>
            {rows.map((row, index) => (
              <View key={row.key} style={index > 0 ? styles.divider : undefined}>
                <PhotoNoteRow
                  uri={row.photo.uri}
                  position={index + 1}
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
        )}
        {full ? null : (
          <Button
            label={t(rows.length === 0 ? 'photoRequest.actions.addPhotos' : 'photoRequest.once.addMore')}
            onPress={askSource}
            disabled={busy}
            size={rows.length === 0 ? 'large' : 'small'}
            style={rows.length === 0 ? undefined : styles.addMore}
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
    </FlowFrame>
  );
}

const makeStyles = (theme: Theme) =>
  StyleSheet.create({
    content: {
      gap: theme.spacing.xl,
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
    },
    divider: {
      borderTopWidth: theme.sizes.border,
      borderTopColor: theme.colors.line,
    },
    addMore: {
      alignSelf: 'center',
    },
    centered: {
      textAlign: 'center',
    },
  });
